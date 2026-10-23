using Sterp.Modules.Ar.Contracts;
using Sterp.Modules.Ar.Domain;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Application;

/// <summary>
/// ARENQ (cobol/online/ar/ARENQ.cob) - customer balance (aged profile rebuilt as at the enquiry date) and trading metrics.
/// Reads only. Functions BALANCE (no production invoker, not exposed) and METRICS.
/// </summary>
public sealed class CustomerMetricsEngine(ISqlRunner sql, IPermissionService permchk, IBusinessCalendar datecalc)
{
    private const string Program = "ARENQ";

    public async Task<LegacyResult> CallAsync(ArenqArea eq, CancellationToken ct = default)
    {
        var c = ArCtl.Start(eq.Header, Program, "AR-022");
        await InitialiseAsync(c, eq, ct);
        await ValidateAsync(c, eq, ct);
        if (!c.InputBad)
        {
            await PermissionAsync(c, ct);
            if (!(c.RcBad || c.RcHold))
            {
                var row = await ReadHeaderAsync(c, eq, ct);
                if (!c.RcBad)
                {
                    switch (c.Func)
                    {
                        case "BALANCE":
                            await BalanceAsync(c, eq, row, ct);
                            break;
                        case "METRICS":
                            await BalanceAsync(c, eq, row, ct);
                            await MetricsAsync(c, eq, ct);
                            break;
                        default:
                            c.Rc = 8;
                            c.Reason = "ENQ0009";
                            c.Msg = "ARENQ does not support function [" + CobolText.Trim(c.Func) + "]";
                            c.MsgCnt++;
                            break;
                    }
                }
            }
        }
        c.SetResult(eq.Header);
        return eq.Header.Outcome();
    }

    private async Task InitialiseAsync(ArCtl c, ArenqArea eq, CancellationToken ct)
    {
        if (eq.AsAt == 0) eq.AsAt = c.BusDate;
        if (eq.ToDate == 0) eq.ToDate = eq.AsAt;
        if (eq.FromDate == 0)
        {
            // 0150-YEAR-BACK: the default trading window is the twelve months to the enquiry date
            var dc = new DatecalcArea { Date1 = eq.ToDate, Number = -12 };
            dc.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, c.BusDate, ""), "ADDMONTH");
            await datecalc.ExecuteAsync(dc, ct);
            eq.FromDate = dc.Header.ReturnCode > 4 ? eq.ToDate : dc.ResultDate;
        }
        eq.Balance = eq.Debits = eq.Credits = eq.Deposits = 0m;
        eq.NotDue = eq.Bucket1 = eq.Bucket2 = eq.Bucket3 = 0m;
        eq.Bucket4 = eq.OverdueTotal = eq.Disputed = 0m;
        eq.OpenItems = eq.OldestDue = eq.WorstDays = 0;
        eq.CreditLimit = eq.Exposure = eq.UtilisedPct = 0m;
        eq.TurnoverInv = eq.OrderValue = eq.CreditSales = 0m;
        eq.ReceiptsValue = 0m;
        eq.ReceiptCount = eq.DishonourCount = eq.DisputeCount = 0;
        eq.AvgDaysPaid = eq.PeriodDays = 0;
        eq.Dso = 0m;
        eq.HoldFlag = "N";
        eq.DsoDefined = "N";
        eq.DsoBasis = "Closing VAT inclusive receivables / VAT inclusive credit sales x period days";
    }

    private async Task ValidateAsync(ArCtl c, ArenqArea eq, CancellationToken ct)
    {
        if (CobolText.IsSpaces(c.Co))
        {
            c.Reject("ENQ0001", "A company code is required");
            return;
        }
        if (CobolText.IsSpaces(eq.Customer) || CobolText.IsLowValues(eq.Customer))
        {
            c.Reject("ENQ0002", "A customer account is required");
            return;
        }
        if (eq.FromDate > eq.ToDate)
        {
            c.Reject("ENQ0003", "The period start is later than the period end");
            return;
        }
        // 0250-PERIOD-DAYS
        var dc = new DatecalcArea { Date1 = eq.FromDate, Date2 = eq.ToDate };
        dc.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, c.BusDate, ""), "DAYSBTWN");
        await datecalc.ExecuteAsync(dc, ct);
        var days = dc.Header.ReturnCode > 4 ? 365 : (int)CobolNumeric.Store(dc.ResultNum + 1m, 7, 0);
        if (days < 1) days = 1;
        eq.PeriodDays = (int)CobolNumeric.Store(days, 5, 0);
    }

    /// <summary>0300: COM-019 - a customer financial enquiry is a sensitive read.</summary>
    private async Task PermissionAsync(ArCtl c, CancellationToken ct)
    {
        var pk = new PermchkArea { Permission = "AR.CUSTOMER.ENQUIRE" };
        pk.Header.Prepare(new KernelCaller(c.Co, c.Site, c.UserId, c.BusDate, ""), "CHECK");
        await permchk.ExecuteAsync(pk, ct);
        if (pk.Header.ReturnCode > 4)
        {
            c.Rc = 6;
            c.Reason = "ENQ0004";
            c.Msg = pk.Header.Message;
            c.MsgCnt++;
        }
    }

    /// <summary>0400: AR-021 - a retired customer with open items is still shown. Returns WS-LIMIT.</summary>
    private async Task<decimal> ReadHeaderAsync(ArCtl c, ArenqArea eq, CancellationToken ct)
    {
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT c.LegalName, c.Status, c.CurrencyCode, CONVERT(varchar(24), ISNULL(k.CreditLimitGBP,0)), " +
                "CONVERT(varchar(4), ISNULL(k.HoldFlag,0)) FROM ar.Customer c LEFT JOIN ar.CustomerCredit k " +
                "ON k.CompanyCode = c.CompanyCode AND k.CustomerCode = c.CustomerCode WHERE c.CompanyCode = ? AND c.CustomerCode = ?",
                [SqlioParm.Text(c.Co), SqlioParm.Text(eq.Customer)], [120, 12, 4, 24, 4], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return 0m;
        }
        if (!row.Found)
        {
            c.Rc = 16;
            c.Reason = "ENQ0010";
            c.Msg = "Customer " + CobolText.Trim(eq.Customer) + " is not on the sales ledger";
            c.MsgCnt++;
            return 0m;
        }
        eq.CustomerName = row.Text(0);
        eq.Status = CobolText.Trimmed(row.Raw(1), 10);
        eq.Currency = CobolText.Trimmed(row.Raw(2), 3);
        var limit = CobolNumeric.Store(row.NumVal(3), 11, 2);
        eq.CreditLimit = limit;
        eq.HoldFlag = row.Raw(4).StartsWith('1') ? "Y" : "N";
        return limit;
    }

    // ------------------------------------------------------------------ 1000 BALANCE

    private async Task BalanceAsync(ArCtl c, ArenqArea eq, decimal limit, CancellationToken ct)
    {
        SqlioRow row;
        var asAt = SqlioParm.Digits(eq.AsAt, 8);
        try
        {
            row = await sql.SingleAsync(
                "SELECT CONVERT(varchar(24), ISNULL(SUM(x.Res),0)), " +
                "CONVERT(varchar(24), ISNULL(SUM(CASE WHEN x.Res > 0 THEN x.Res ELSE 0 END),0)), " +
                "CONVERT(varchar(24), ISNULL(SUM(CASE WHEN x.Res < 0 AND x.T <> 'DEPOSIT' THEN x.Res ELSE 0 END),0)), " +
                "CONVERT(varchar(24), ISNULL(SUM(CASE WHEN x.T = 'DEPOSIT' THEN x.Res ELSE 0 END),0)), " +
                "CONVERT(varchar(24), ISNULL(SUM(CASE WHEN x.Res > 0 AND x.D >= x.AsAt THEN x.Res ELSE 0 END),0)), " +
                "CONVERT(varchar(24), ISNULL(SUM(CASE WHEN x.Res > 0 AND DATEDIFF(day, x.D, x.AsAt) BETWEEN 1 AND 30 THEN x.Res ELSE 0 END),0)), " +
                "CONVERT(varchar(24), ISNULL(SUM(CASE WHEN x.Res > 0 AND DATEDIFF(day, x.D, x.AsAt) BETWEEN 31 AND 60 THEN x.Res ELSE 0 END),0)), " +
                "CONVERT(varchar(24), ISNULL(SUM(CASE WHEN x.Res > 0 AND DATEDIFF(day, x.D, x.AsAt) BETWEEN 61 AND 90 THEN x.Res ELSE 0 END),0)), " +
                "CONVERT(varchar(24), ISNULL(SUM(CASE WHEN x.Res > 0 AND DATEDIFF(day, x.D, x.AsAt) > 90 THEN x.Res ELSE 0 END),0)), " +
                "CONVERT(varchar(24), ISNULL(SUM(CASE WHEN x.Res > 0 AND x.D < x.AsAt THEN x.Res ELSE 0 END),0)), " +
                "CONVERT(varchar(24), ISNULL(SUM(CASE WHEN x.P = 1 AND x.Res > 0 THEN x.Res ELSE 0 END),0)), " +
                "CONVERT(varchar(24), SUM(CASE WHEN ABS(x.Res) >= 0.005 THEN 1 ELSE 0 END)), " +
                "ISNULL(CONVERT(varchar(10), MIN(CASE WHEN x.Res > 0 AND x.D < x.AsAt THEN x.D END), 112),''), " +
                "CONVERT(varchar(24), ISNULL(MAX(CASE WHEN x.Res > 0 THEN DATEDIFF(day, x.D, x.AsAt) END),0)) FROM (" +
                "SELECT CONVERT(date, ?) AS AsAt, o.ItemType AS T, o.DueDate AS D, o.IsDisputed AS P, o.OriginalAmountGBP" +
                " - ISNULL((SELECT SUM(a.AppliedAmountGBP + a.DiscountAmountGBP + a.WriteOffAmountGBP) FROM ar.Allocation a " +
                "WHERE a.Status = 'ACTIVE' AND a.EffectiveDate <= ? AND a.TargetOpenItemId = o.OpenItemId),0)" +
                " + ISNULL((SELECT SUM(b.AppliedAmountGBP) FROM ar.Allocation b WHERE b.Status = 'ACTIVE' " +
                "AND b.EffectiveDate <= ? AND b.SourceOpenItemId = o.OpenItemId),0) AS Res " +
                "FROM ar.OpenItem o WHERE o.CompanyCode = ? AND o.CustomerCode = ? AND o.PostingDate <= ?) x",
                [asAt, asAt, asAt, SqlioParm.Text(c.Co), SqlioParm.Text(eq.Customer), asAt],
                [24, 24, 24, 24, 24, 24, 24, 24, 24, 24, 24, 24, 10, 24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        UnpackAge(c, eq, row);
        Exposure(c, eq, limit);
        c.Msg = "Balance GBP " + ArEdit.MoneyUk(eq.Balance) + " for " + CobolText.Trim(eq.Customer) + " as at " + ArEdit.Dmy(eq.AsAt);
    }

    /// <summary>1200: the residual is the original sterling amount less what was applied to it, plus what it settled.</summary>
    private static void UnpackAge(ArCtl c, ArenqArea eq, SqlioRow row)
    {
        decimal M(int i) => CobolNumeric.Store(row.NumVal(i), 11, 2);
        eq.Balance = M(0);
        eq.Debits = M(1);
        eq.Credits = M(2);
        eq.Deposits = M(3);
        eq.NotDue = M(4);
        eq.Bucket1 = M(5);
        eq.Bucket2 = M(6);
        eq.Bucket3 = M(7);
        eq.Bucket4 = M(8);
        eq.OverdueTotal = M(9);
        eq.Disputed = M(10);
        eq.OpenItems = (int)CobolNumeric.Truncated(row.NumVal(11), 0);
        eq.WorstDays = (int)CobolNumeric.Store(row.NumVal(13), 5, 0);
        eq.OldestDue = CobolText.IsSpaces(row.Raw(12)) ? 0 : Digits(row.Raw(12)[..8]);
        // the bucket totals must agree with the same date debit balance, otherwise the report is not reconcilable
        var amt1 = eq.NotDue + eq.OverdueTotal;
        if (Math.Abs(amt1 - eq.Debits) > 0.005m)
        {
            c.Rc = 4;
            c.Reason = "ENQ0020";
            c.Msg = "The ageing buckets do not reconcile to the same date debit balance";
            c.MsgCnt++;
        }
    }

    private static int Digits(string text)
    {
        var v = 0;
        foreach (var ch in text) v = v * 10 + (char.IsAsciiDigit(ch) ? ch - '0' : 0);
        return v;
    }

    /// <summary>1300: D-06 - disputes stay in exposure, cleared deposits reduce it.</summary>
    private static void Exposure(ArCtl c, ArenqArea eq, decimal limit)
    {
        var expos = CobolNumeric.Store(eq.Debits + eq.Credits + eq.Deposits, 11, 2);
        eq.Exposure = expos;
        eq.UtilisedPct = limit > 0 ? CobolNumeric.Store(expos / limit * 100, 5, 2, rounded: true) : 0m;
        if (limit > 0 && expos > limit)
        {
            c.Rc = 4;
            c.Reason = "ENQ0021";
            c.Msg = "Exposure is " + ArEdit.MoneyUk(eq.UtilisedPct) + " per cent of the approved credit limit";
            c.MsgCnt++;
        }
    }

    // ------------------------------------------------------------------ 2000 METRICS

    private async Task MetricsAsync(ArCtl c, ArenqArea eq, CancellationToken ct)
    {
        await TradingAsync(c, eq, ct);
        if (c.RcBad) return;
        await OrderValueAsync(c, eq, ct);
        if (c.RcBad) return;
        await ReceiptsAsync(c, eq, ct);
        if (c.RcBad) return;
        await DaysToPayAsync(c, eq, ct);
        if (c.RcBad) return;
        Dso(c, eq);
    }

    private async Task TradingAsync(ArCtl c, ArenqArea eq, CancellationToken ct)
    {
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT CONVERT(varchar(24), ISNULL(SUM(CASE WHEN i.DocumentType = 'CREDITNOTE' THEN -i.NetAmountGBP ELSE i.NetAmountGBP " +
                "END),0)), CONVERT(varchar(24), ISNULL(SUM(CASE WHEN i.DocumentType = 'CREDITNOTE' THEN -i.GrossAmountGBP ELSE i.GrossAmountGBP " +
                "END),0)) FROM ar.SalesInvoice i WHERE i.CompanyCode = ? AND i.CustomerCode = ? AND i.Status = 'POSTED' " +
                "AND i.DocumentType <> 'PROFORMA' AND i.PostingDate BETWEEN ? AND ?",
                [SqlioParm.Text(c.Co), SqlioParm.Text(eq.Customer), SqlioParm.Digits(eq.FromDate, 8), SqlioParm.Digits(eq.ToDate, 8)],
                [24, 24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        eq.TurnoverInv = CobolNumeric.Store(row.NumVal(0), 11, 2);
        eq.CreditSales = CobolNumeric.Store(row.NumVal(1), 11, 2);
    }

    /// <summary>2200: order value is what the customer has asked for; it never feeds revenue or DSO.</summary>
    private async Task OrderValueAsync(ArCtl c, ArenqArea eq, CancellationToken ct)
    {
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT CONVERT(varchar(24), ISNULL(SUM(s.GrossAmountGBP),0)) FROM so.SalesOrder s WHERE s.CompanyCode = ? " +
                "AND s.CustomerCode = ? AND s.CommercialStatus <> 'CANCELLED' AND s.OrderDate BETWEEN ? AND ?",
                [SqlioParm.Text(c.Co), SqlioParm.Text(eq.Customer), SqlioParm.Digits(eq.FromDate, 8), SqlioParm.Digits(eq.ToDate, 8)],
                [24], ct);
        }
        catch (SqlioException)
        {
            // distribution may not be installed on this company; the enquiry still answers the ledger figures
            eq.OrderValue = 0m;
            return;
        }
        eq.OrderValue = CobolNumeric.Store(row.NumVal(0), 11, 2);
    }

    private async Task ReceiptsAsync(ArCtl c, ArenqArea eq, CancellationToken ct)
    {
        SqlioRow row;
        var from = SqlioParm.Digits(eq.FromDate, 8);
        var to = SqlioParm.Digits(eq.ToDate, 8);
        try
        {
            row = await sql.SingleAsync(
                "SELECT CONVERT(varchar(24), ISNULL(SUM(CASE WHEN r.ConfirmationState = 'CONFIRMED' THEN r.GrossReceivedGBP ELSE 0 END),0)), " +
                "CONVERT(varchar(24), SUM(CASE WHEN r.ConfirmationState = 'CONFIRMED' THEN 1 ELSE 0 END)), " +
                "CONVERT(varchar(24), SUM(CASE WHEN r.ConfirmationState IN ('DISHONOURED','REVERSED') THEN 1 ELSE 0 END)), " +
                "(SELECT CONVERT(varchar(24), COUNT(*)) FROM ar.CollectionCase k WHERE k.CompanyCode = r.CompanyCode " +
                "AND k.CustomerCode = ? AND k.CaseType = 'DISPUTE' AND CONVERT(date, k.OpenedAt) BETWEEN ? AND ?) " +
                "FROM ar.Receipt r WHERE r.CompanyCode = ? AND r.CustomerCode = ? AND r.ReceiptDate BETWEEN ? AND ? GROUP BY r.CompanyCode",
                [SqlioParm.Text(eq.Customer), from, to, SqlioParm.Text(c.Co), SqlioParm.Text(eq.Customer), from, to],
                [24, 24, 24, 24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (!row.Found)
        {
            eq.ReceiptsValue = 0m;
            eq.ReceiptCount = eq.DishonourCount = eq.DisputeCount = 0;
            return;
        }
        eq.ReceiptsValue = CobolNumeric.Store(row.NumVal(0), 11, 2);
        eq.ReceiptCount = (int)CobolNumeric.Truncated(row.NumVal(1), 0);
        eq.DishonourCount = (int)CobolNumeric.Truncated(row.NumVal(2), 0);
        eq.DisputeCount = (int)CobolNumeric.Truncated(row.NumVal(3), 0);
    }

    /// <summary>2400: average days to pay - settlement date less invoice date, over the invoices settled inside the period.</summary>
    private async Task DaysToPayAsync(ArCtl c, ArenqArea eq, CancellationToken ct)
    {
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT CONVERT(varchar(24), ISNULL(AVG(CONVERT(decimal(18,4), DATEDIFF(day, o.DocumentDate, a.EffectiveDate))),0)) " +
                "FROM ar.Allocation a INNER JOIN ar.OpenItem o ON o.OpenItemId = a.TargetOpenItemId WHERE a.CompanyCode = ? " +
                "AND a.CustomerCode = ? AND a.Status = 'ACTIVE' AND o.ItemType = 'INVOICE' AND a.EffectiveDate BETWEEN ? AND ?",
                [SqlioParm.Text(c.Co), SqlioParm.Text(eq.Customer), SqlioParm.Digits(eq.FromDate, 8), SqlioParm.Digits(eq.ToDate, 8)],
                [24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        eq.AvgDaysPaid = (int)CobolNumeric.Store(row.NumVal(0), 5, 0, rounded: true);
    }

    /// <summary>2500: with no credit sales in the period DSO is undefined rather than zero (AR-022).</summary>
    private static void Dso(ArCtl c, ArenqArea eq)
    {
        if (eq.CreditSales <= 0)
        {
            eq.Dso = 0m;
            eq.DsoDefined = "N";
            c.Rc = 4;
            c.Reason = "ENQ0030";
            c.Msg = "Days sales outstanding is undefined: there were no credit sales in the stated period";
            c.MsgCnt++;
            return;
        }
        eq.Dso = CobolNumeric.Store(eq.Balance / eq.CreditSales * eq.PeriodDays, 7, 2, rounded: true);
        eq.DsoDefined = "Y";
        if (c.RcOk)
        {
            c.Msg = "DSO " + ArEdit.MoneyUk(eq.Dso) + " days over a period of " + ArEdit.Int(eq.PeriodDays) + " days, " +
                    CobolText.Trim(eq.DsoBasis);
        }
    }
}
