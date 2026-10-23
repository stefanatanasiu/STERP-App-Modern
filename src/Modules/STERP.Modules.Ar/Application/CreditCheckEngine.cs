using Sterp.Modules.Ar.Contracts;
using Sterp.Modules.Ar.Domain;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Application;

/// <summary>
/// CREDCHK (cobol/online/ar/CREDCHK.cob) - credit exposure and the release decision for one customer; scoped override
/// request and independent approval. Runs on the caller's engine connection (scoped <see cref="ISqlRunner"/>).
/// </summary>
public sealed class CreditCheckEngine(
    ISqlRunner sql,
    IParameterService parmget,
    IPermissionService permchk,
    IExceptionRaiser excprais,
    IAuditWriter auditlog,
    IBusinessCalendar datecalc) : ICreditCheckService
{
    private const string Program = "CREDCHK";

    private sealed class Ws
    {
        public required ArCtl Ctl;
        public required CredchkArea Ca;
        public bool SoPresent = true;
        public bool BlockSet;
        public bool HoldSet;
        public bool UowOwn;
    }

    public async Task<LegacyResult> CallAsync(CredchkArea ca, CancellationToken ct = default)
    {
        var w = new Ws { Ctl = ArCtl.Start(ca.Header, Program, "AR-007"), Ca = ca };
        var c = w.Ctl;
        Initialise(w);
        Validate(w);
        if (!c.InputBad)
        {
            await ReadCustomerAsync(w, ct);
            if (!c.RcBad)
            {
                switch (c.Func)
                {
                    case CreditCheckFunctions.Exposure:
                        await ComputeExposureAsync(w, ct);
                        break;
                    case CreditCheckFunctions.Check:
                    case CreditCheckFunctions.Release:
                        await ComputeExposureAsync(w, ct);
                        await DecideAsync(w, ct);
                        break;
                    case CreditCheckFunctions.Override:
                        await ComputeExposureAsync(w, ct);
                        await RequestOverrideAsync(w, ct);
                        break;
                    case CreditCheckFunctions.Approve:
                        await ApproveOverrideAsync(w, ct);
                        break;
                    default:
                        // 9100-BAD-FUNCTION
                        c.Rc = 8;
                        c.Reason = "CRD0008";
                        c.Msg = "CREDCHK does not support function [" + CobolText.Trim(c.Func) + "]";
                        break;
                }
            }
        }
        c.SetResult(ca.Header);
        return ca.Header.Outcome();
    }

    // ------------------------------------------------------------------ 0100 / 0200 / 0300 / 0400

    private static void Initialise(Ws w)
    {
        var ca = w.Ca;
        ca.Customer = CobolText.Upper(CobolText.Trimmed(ca.Customer, 20));
        if (ca.EvalDate == 0) ca.EvalDate = w.Ctl.BusDate;
        if (ca.EvalDate == 0) ca.EvalDate = ArEdit.Today();
        ca.OpenDebt = ca.OpenCredits = ca.FirmOrders = 0m;
        ca.ShippedUnbill = ca.Deposits = ca.Exposure = 0m;
        ca.Limit = ca.Headroom = ca.Excess = ca.UtilisedPct = 0m;
        ca.OverdueGbp = 0m;
        ca.OverdueDays = 0;
        ca.DisputedGbp = 0m;
        ca.OldestDueDate = 0;
        ca.OrderValueGbp = 0m;
        ca.Decision = ca.DecisionText = "";
        ca.HoldReason = ca.AccountStatus = "";
        ca.HoldFlag = ca.ProformaOnly = ca.OverrideActive = "N";
        if (CobolText.IsSpaces(ca.Currency) || CobolText.IsLowValues(ca.Currency)) ca.Currency = "GBP";
        if (ca.FxRate == 0) ca.FxRate = 1m;
    }

    private static void Validate(Ws w)
    {
        var c = w.Ctl;
        var ca = w.Ca;
        if (CobolText.IsSpaces(c.Co) || CobolText.IsLowValues(c.Co))
        {
            c.Reject("CRD0001", "A company code is required for a credit check");
            return;
        }
        if (CobolText.IsSpaces(ca.Customer) || CobolText.IsLowValues(ca.Customer))
        {
            c.Reject("CRD0002", "A customer account is required");
            return;
        }
        if (ca.OrderValue < 0) c.Reject("CRD0003", "A proposed commitment cannot be negative");
    }

    private async Task ReadCustomerAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ca = w.Ca;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT c.LegalName, c.Status, c.SanctionsStatus, c.CurrencyCode, ISNULL(c.InsolvencyType,''), " +
                "ISNULL(cc.CreditLimitGBP,0), ISNULL(cc.OverdueThresholdGBP,0), ISNULL(cc.OverdueDaysTrigger,30), " +
                "ISNULL(CONVERT(char(1),cc.HoldFlag),'0'), ISNULL(cc.HoldReason,''), " +
                "ISNULL(CONVERT(char(1),cc.ProFormaOnly),'0'), ISNULL(CONVERT(char(1),cc.RequiresPrepayment),'0') " +
                "FROM ar.Customer c LEFT JOIN ar.CustomerCredit cc ON cc.CompanyCode = c.CompanyCode " +
                "AND cc.CustomerCode = c.CustomerCode WHERE c.CompanyCode = ? AND c.CustomerCode = ?",
                [SqlioParm.Text(c.Co), SqlioParm.Text(ca.Customer)],
                [120, 10, 12, 3, 16, 20, 20, 10, 2, 200, 2, 2], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (!row.Found)
        {
            c.Rc = 16;
            c.Reason = "CRD0004";
            c.Msg = "Customer " + CobolText.Trim(ca.Customer) + " is not held for company " + CobolText.Trim(c.Co);
            return;
        }
        ca.AccountStatus = row.Text(1);
        ca.Sanctions = row.Text(2);
        ca.Limit = CobolNumeric.Store(row.NumVal(5), 11, 2);
        ca.OverdueThresh = CobolNumeric.Store(row.NumVal(6), 11, 2);
        ca.OverdueTrigger = (int)CobolNumeric.Store(row.NumVal(7), 5, 0);
        if (row.Raw(8).StartsWith('1'))
        {
            ca.HoldFlag = "Y";
            ca.HoldReason = row.Text(9);
        }
        if (row.Raw(10).StartsWith('1') || row.Raw(11).StartsWith('1')) ca.ProformaOnly = "Y";
        await ReadParametersAsync(w, ct);
    }

    /// <summary>0400: D-06 defaults fill in anything the account does not state.</summary>
    private async Task ReadParametersAsync(Ws w, CancellationToken ct)
    {
        var ca = w.Ca;
        if (ca.OverdueTrigger == 0)
        {
            var pm = await GetNumAsync(w, "AR.HOLD.OVERDUE.DAYS", "30", ct);
            ca.OverdueTrigger = (int)CobolNumeric.Store(pm.ValueNum, 5, 0);
        }
        if (ca.OverdueTrigger == 0) ca.OverdueTrigger = 30;
    }

    /// <summary>PARMGET GETNUM with PM-USE-DEFAULT "Y" (header: company, user, CK-EVAL-DATE).</summary>
    private async Task<ParmgetArea> GetNumAsync(Ws w, string code, string defaultText, CancellationToken ct)
    {
        var pm = new ParmgetArea { ParamCode = code, UseDefault = "Y", DefaultText = defaultText };
        pm.Header.Prepare(new KernelCaller(w.Ctl.Co, "", w.Ctl.UserId, w.Ca.EvalDate, ""), "GETNUM");
        await parmget.ExecuteAsync(pm, ct);
        return pm;
    }

    // ------------------------------------------------------------------ 1000 EXPOSURE

    private async Task ComputeExposureAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ca = w.Ca;
        await OpenItemsAsync(w, ct);
        if (c.RcSevere) return;
        await FirmOrdersAsync(w, ct);
        await ShippedUnbilledAsync(w, ct);
        ConvertCandidate(w);
        var amt1 = ca.OpenDebt - ca.OpenCredits + ca.FirmOrders + ca.ShippedUnbill - ca.Deposits;
        if (amt1 < 0) amt1 = 0;
        ca.Exposure = CobolNumeric.Store(amt1, 11, 2);
        ca.Headroom = CobolNumeric.Store(ca.Limit - ca.Exposure - ca.OrderValueGbp, 11, 2);
        ca.Excess = ca.Headroom < 0 ? CobolNumeric.Store(0 - ca.Headroom, 11, 2) : 0m;
        ca.UtilisedPct = ca.Limit > 0
            ? CobolNumeric.Store((ca.Exposure + ca.OrderValueGbp) * 100 / ca.Limit, 5, 2, rounded: true)
            : 0m;
        await StoreExposureAsync(w, ct);
    }

    private async Task OpenItemsAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ca = w.Ca;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT ISNULL(SUM(CASE WHEN o.ItemType IN ('INVOICE','ADJUSTMENT','INTEREST') AND o.ResidualAmountGBP > 0 " +
                "THEN o.ResidualAmountGBP ELSE 0 END),0), " +
                "ISNULL(SUM(CASE WHEN o.ItemType IN ('CREDIT','RECEIPT') AND o.ResidualAmountGBP < 0 " +
                "AND (o.SourceReceiptId IS NULL OR r.ConfirmationState = 'CONFIRMED') THEN -o.ResidualAmountGBP ELSE 0 END),0), " +
                "ISNULL(SUM(CASE WHEN o.ItemType = 'DEPOSIT' AND o.ResidualAmountGBP < 0 THEN -o.ResidualAmountGBP ELSE 0 END),0), " +
                "ISNULL(SUM(CASE WHEN o.ResidualAmountGBP > 0 AND o.DueDate < ? THEN o.ResidualAmountGBP ELSE 0 END),0) " +
                "FROM ar.OpenItem o LEFT JOIN ar.Receipt r ON r.ReceiptId = o.SourceReceiptId " +
                "WHERE o.CompanyCode = ? AND o.CustomerCode = ? AND o.SettlementStatus <> 'REVERSED' AND o.PostingDate <= ?",
                [SqlioParm.Digits(ca.EvalDate, 8), SqlioParm.Text(c.Co), SqlioParm.Text(ca.Customer), SqlioParm.Digits(ca.EvalDate, 8)],
                [24, 24, 24, 24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (row.Found)
        {
            ca.OpenDebt = CobolNumeric.Store(row.NumVal(0), 11, 2);
            ca.OpenCredits = CobolNumeric.Store(row.NumVal(1), 11, 2);
            ca.Deposits = CobolNumeric.Store(row.NumVal(2), 11, 2);
            ca.OverdueGbp = CobolNumeric.Store(row.NumVal(3), 11, 2);
        }
        await OldestOverdueAsync(w, ct);
    }

    private async Task OldestOverdueAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ca = w.Ca;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT TOP (1) CONVERT(char(8), o.DueDate, 112), DATEDIFF(day, o.DueDate, ?), " +
                "ISNULL((SELECT SUM(d.DisputedAmountGBP) FROM ar.OpenItem d WHERE d.CompanyCode = o.CompanyCode " +
                "AND d.CustomerCode = o.CustomerCode AND d.IsDisputed = 1 AND d.SettlementStatus <> 'REVERSED'),0), '' " +
                "FROM ar.OpenItem o WHERE o.CompanyCode = ? AND o.CustomerCode = ? AND o.DueDate < ? " +
                "AND o.ResidualAmountGBP > 0 AND o.SettlementStatus <> 'REVERSED' ORDER BY o.DueDate",
                [SqlioParm.Digits(ca.EvalDate, 8), SqlioParm.Text(c.Co), SqlioParm.Text(ca.Customer), SqlioParm.Digits(ca.EvalDate, 8)],
                [24, 24, 24, 24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (!row.Found) return;
        if (ArEdit.TryDate8(row.Raw(0), out var due)) ca.OldestDueDate = due;
        ca.OverdueDays = (int)CobolNumeric.Store(row.NumVal(1), 5, 0);
        ca.DisputedGbp = CobolNumeric.Store(row.NumVal(2), 11, 2);
    }

    private async Task FirmOrdersAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ca = w.Ca;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT ISNULL(SUM(ROUND(CASE WHEN l.AcceptedQty > 0 AND (l.AcceptedQty - l.CancelledQty - l.DespatchedQty) > 0 " +
                "THEN (l.AcceptedQty - l.CancelledQty - l.DespatchedQty) / l.AcceptedQty * (l.NetAmount + l.VatAmount) / o.FxRate " +
                "ELSE 0 END, 2)),0), '0', '0', '0' " +
                "FROM so.SalesOrder o JOIN so.SalesOrderLine l ON l.SalesOrderId = o.SalesOrderId " +
                "WHERE o.CompanyCode = ? AND o.CustomerCode = ? AND o.CommercialStatus IN ('ACCEPTED','AMENDED') " +
                "AND l.Status = 'OPEN' AND o.OrderDate <= ? AND (? = '' OR o.OrderNumber <> ?)",
                [SqlioParm.Text(c.Co), SqlioParm.Text(ca.Customer), SqlioParm.Digits(ca.EvalDate, 8),
                 SqlioParm.Text(ca.ExcludeRef), SqlioParm.Text(ca.ExcludeRef)],
                [24, 24, 24, 24], ct);
        }
        catch (SqlioException)
        {
            // the sales order module may not be deployed yet; an unavailable leg is reported, never silently zero
            w.SoPresent = false;
            c.Rc = 4;
            c.Reason = "CRD0010";
            c.Msg = "Order book exposure is unavailable - the sales order module is not deployed";
            c.MsgCnt++;
            return;
        }
        if (row.Found) ca.FirmOrders = CobolNumeric.Store(row.NumVal(0), 11, 2);
    }

    private async Task ShippedUnbilledAsync(Ws w, CancellationToken ct)
    {
        if (!w.SoPresent) return;
        var c = w.Ctl;
        var ca = w.Ca;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT ISNULL(SUM(ROUND(CASE WHEN l.AcceptedQty > 0 AND (d.DespatchedQty - d.BilledQty) > 0 " +
                "THEN (d.DespatchedQty - d.BilledQty) / l.AcceptedQty * (l.NetAmount + l.VatAmount) / o.FxRate ELSE 0 END, 2)),0), " +
                "'0', '0', '0' FROM so.Despatch h JOIN so.DespatchLine d ON d.DespatchId = h.DespatchId " +
                "JOIN so.SalesOrderLine l ON l.SalesOrderLineId = d.SalesOrderLineId JOIN so.SalesOrder o " +
                "ON o.SalesOrderId = l.SalesOrderId WHERE h.CompanyCode = ? AND h.CustomerCode = ? " +
                "AND h.Status = 'CONFIRMED' AND h.DespatchDate <= ?",
                [SqlioParm.Text(c.Co), SqlioParm.Text(ca.Customer), SqlioParm.Digits(ca.EvalDate, 8)],
                [24, 24, 24, 24], ct);
        }
        catch (SqlioException)
        {
            w.SoPresent = false;
            return;
        }
        if (row.Found) ca.ShippedUnbill = CobolNumeric.Store(row.NumVal(0), 11, 2);
    }

    /// <summary>1400: the candidate commitment arrives in the order currency (approved credit evaluation rate).</summary>
    private static void ConvertCandidate(Ws w)
    {
        var c = w.Ctl;
        var ca = w.Ca;
        if (ca.OrderValue == 0)
        {
            ca.OrderValueGbp = 0m;
            return;
        }
        if (ca.Currency == "GBP")
        {
            ca.OrderValueGbp = ca.OrderValue;
            ca.FxRate = 1m;
            return;
        }
        if (ca.FxRate > 0 && ca.FxRate != 1)
        {
            ca.OrderValueGbp = CobolNumeric.Store(ca.OrderValue / ca.FxRate, 11, 2, rounded: true);
            return;
        }
        c.Rc = 8;
        c.Reason = "CRD0011";
        c.Msg = "A credit evaluation rate is required to assess a " + CobolText.Trim(ca.Currency) + " commitment";
    }

    /// <summary>1500: LastExposureGBP / LastExposureAt (autocommit or the caller's unit; failures ignored).</summary>
    private async Task StoreExposureAsync(Ws w, CancellationToken ct)
    {
        try
        {
            await sql.ExecAsync(
                "UPDATE ar.CustomerCredit SET LastExposureGBP = ?, LastExposureAt = SYSDATETIME() WHERE CompanyCode = ? AND CustomerCode = ?",
                [SqlioParm.Text(ArEdit.Money(w.Ca.Exposure)), SqlioParm.Text(w.Ctl.Co), SqlioParm.Text(w.Ca.Customer)], ct);
        }
        catch (SqlioException)
        {
            // SQLIO EXEC result not inspected (CREDCHK.cob:1500-STORE-EXPOSURE)
        }
    }

    // ------------------------------------------------------------------ 2000 DECIDE

    private async Task DecideAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ca = w.Ca;
        await ActiveOverrideAsync(w, ct);
        ca.Decision = "RELEASE";
        ca.DecisionText = "";
        HardRefusals(w);
        if (w.BlockSet) return;
        CommercialRefusals(w);
        if (w.HoldSet && ca.OverrideActive == "Y") CommercialOverride(w);
        if (w.HoldSet)
        {
            await RaiseHoldAsync(w, ct);
        }
        else if (c.Reason != "CRD0040")
        {
            // CR2027 - a release granted by an override keeps its warning, reason and evidence
            c.Rc = 0;
            c.Reason = "";
            c.Msg = "Credit available: GBP " + ArEdit.MoneyUk(ca.Headroom);
            ca.DecisionText = c.Msg;
        }
    }

    private async Task ActiveOverrideAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ca = w.Ca;
        ca.OverrideActive = "N";
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT TOP (1) CONVERT(varchar(20), v.OverrideId), CONVERT(varchar(24), v.OverrideAmountGBP), " +
                "CONVERT(char(8), v.ExpiresOn, 112), v.Status FROM ar.CreditOverride v WHERE v.CompanyCode = ? " +
                "AND v.CustomerCode = ? AND v.Status = 'ACTIVE' AND v.ApprovedBy IS NOT NULL AND v.ApprovedBy <> v.RequestedBy " +
                "AND v.ValidFrom <= ? AND v.ExpiresOn >= ? AND (v.Scope = 'ACCOUNT' OR (v.DocumentRef = ? AND (v.Scope = 'ORDER' OR " +
                "(v.Scope = 'DESPATCH' AND ? = 'DESPATCH')))) ORDER BY v.OverrideAmountGBP DESC",
                [SqlioParm.Text(c.Co), SqlioParm.Text(ca.Customer), SqlioParm.Digits(ca.EvalDate, 8), SqlioParm.Digits(ca.EvalDate, 8),
                 SqlioParm.Text(ca.DocumentRef), SqlioParm.Text(ca.CheckPoint)],
                [24, 24, 24, 24], ct);
        }
        catch (SqlioException)
        {
            return;
        }
        if (!row.Found) return;
        ca.OverrideId = (int)CobolNumeric.Truncated(row.NumVal(0), 0);
        ca.OverrideAmt = CobolNumeric.Store(row.NumVal(1), 11, 2);
        if (ArEdit.TryDate8(row.Raw(2), out var expiry)) ca.OverrideExpiry = expiry;
        ca.OverrideStatus = CobolText.Trimmed(row.Raw(3), 10);
        ca.OverrideActive = "Y";
    }

    private static void HardRefusals(Ws w)
    {
        var ca = w.Ca;
        if (ca.Sanctions is "POTENTIAL" or "MATCH")
        {
            ca.Decision = "BLOCK";
            w.Ctl.Reason = "CRD0020";
            ca.DecisionText = "Release is blocked until the sanctions screening match has an authorised decision";
            SetBlock(w);
            return;
        }
        if (ca.AccountStatus == "RETIRED")
        {
            ca.Decision = "BLOCK";
            w.Ctl.Reason = "CRD0021";
            ca.DecisionText = "The account is retired and cannot take new demand";
            SetBlock(w);
            return;
        }
        if (ca.AccountStatus == "SUSPENDED")
        {
            ca.Decision = "BLOCK";
            w.Ctl.Reason = "CRD0022";
            ca.DecisionText = "The account is suspended; new release needs reinstatement, not a credit override";
            SetBlock(w);
        }
    }

    private static void CommercialRefusals(Ws w)
    {
        var ca = w.Ca;
        var c = w.Ctl;
        if (ca.HoldFlag == "Y")
        {
            c.Reason = "CRD0030";
            ca.DecisionText = CobolText.Trimmed("Account on credit hold: " + CobolText.Trim(ca.HoldReason), 200);
            SetHold(w);
        }
        if (ca.ProformaOnly == "Y" && ca.OrderValueGbp > 0)
        {
            c.Reason = "CRD0031";
            ca.DecisionText = "The account trades on pro forma or prepayment until an approved credit limit exists (D-06)";
            SetHold(w);
        }
        if (ca.OverdueDays >= ca.OverdueTrigger && ca.OverdueGbp > 0)
        {
            c.Reason = "CRD0032";
            ca.DecisionText = CobolText.Trimmed("Overdue debt of GBP " + ArEdit.MoneyUk(ca.OverdueGbp) + " is " +
                ArEdit.Int(ca.OverdueDays) + " days past due and holds new release", 200);
            SetHold(w);
        }
        if (ca.OverdueThresh > 0 && ca.OverdueGbp > ca.OverdueThresh)
        {
            c.Reason = "CRD0033";
            ca.DecisionText = CobolText.Trimmed("Overdue debt of GBP " + ArEdit.MoneyUk(ca.OverdueGbp) +
                " exceeds the approved overdue threshold", 200);
            SetHold(w);
        }
        if (ca.Excess > 0) LimitRefusal(w);
    }

    /// <summary>2250: a zero approved limit is not an unlimited limit (D-06).</summary>
    private static void LimitRefusal(Ws w)
    {
        var ca = w.Ca;
        var c = w.Ctl;
        if (ca.OverrideActive == "Y" && ca.OverrideAmt >= ca.Excess)
        {
            c.Rc = 4;
            c.Reason = "CRD0040";
            ca.DecisionText = CobolText.Trimmed("Released under credit override " + ArEdit.Int(ca.OverrideId) + " expiring " +
                ArEdit.Dmy(ca.OverrideExpiry), 200);
            c.Msg = ca.DecisionText;
            c.MsgCnt++;
            return;
        }
        c.Reason = "CRD0034";
        ca.DecisionText = "Credit limit exceeded by GBP " + ArEdit.MoneyUk(ca.Excess);
        SetHold(w);
    }

    /// <summary>2270: commercial holds require cover for the held commitment and overdue balance as well as any excess.</summary>
    private static void CommercialOverride(Ws w)
    {
        var ca = w.Ca;
        var amt1 = Math.Max(ca.Excess, Math.Max(ca.OverdueGbp, ca.OrderValueGbp));
        if (ca.OverrideAmt >= amt1)
        {
            w.HoldSet = false;
            ca.Decision = "RELEASE";
            LimitRefusal(w);
        }
    }

    /// <summary>2300: a hold is an owned exception, not just a message (COM-017).</summary>
    private async Task RaiseHoldAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ca = w.Ca;
        c.Rc = 6;
        c.Msg = ca.DecisionText;
        c.MsgCnt++;
        var ex = new ExcpraisArea
        {
            Module = "AR",
            Severity = "HOLD",
            Category = "CREDIT",
            MessageText = ca.DecisionText,
            DocumentType = "ORDER",
            DocumentRef = CobolText.IsSpaces(ca.DocumentRef) ? ca.Customer : ca.DocumentRef,
            OwnerRole = "CREDMGR",
            OwnerUser = "CREDMGR",
            Priority = 2,
            AmountGbp = ca.Exposure,
        };
        ex.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ca.EvalDate, ""), "RAISE");
        await excprais.ExecuteAsync(ex, ct);
        ca.ExceptionId = (int)ex.ExceptionId;
    }

    private static void SetBlock(Ws w)
    {
        w.BlockSet = true;
        w.Ctl.Rc = 8;
        w.Ctl.Msg = w.Ca.DecisionText;
        w.Ctl.MsgCnt++;
    }

    private static void SetHold(Ws w)
    {
        w.HoldSet = true;
        w.Ca.Decision = "HOLD";
    }

    // ------------------------------------------------------------------ 3000 OVERRIDE

    private async Task RequestOverrideAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ca = w.Ca;
        ca.ApproverPerm = "SO.ORDER.ENTER";
        await CheckAuthorityAsync(w, ct);
        if (c.RcStop)
        {
            c.Rc = 0;
            ca.ApproverPerm = "AR.CREDIT.OVERRIDE";
            await CheckAuthorityAsync(w, ct);
        }
        if (c.RcStop) return;
        ca.OverrideBy = c.UserId;
        if (CobolText.IsSpaces(ca.OverrideReason) || CobolText.IsLowValues(ca.OverrideReason))
        {
            c.Reject("CRD0050", "A credit override needs a stated reason");
            return;
        }
        if (ca.OverrideAmt <= 0) ca.OverrideAmt = ca.Excess;
        if (ca.OverrideAmt <= 0)
        {
            c.Reject("CRD0051", "There is no excess to override");
            return;
        }
        if (CobolText.IsSpaces(ca.OverrideScope)) ca.OverrideScope = "ORDER";
        if (ca.OverrideScope is not ("ORDER" or "DESPATCH" or "ACCOUNT"))
        {
            c.Reject("CRD0057", "Scope must be ORDER, DESPATCH or ACCOUNT");
            return;
        }
        if (ca.OverrideScope != "ACCOUNT" && CobolText.IsSpaces(ca.DocumentRef))
        {
            c.Reject("CRD0058", "A scoped override needs a document reference");
            return;
        }
        await ExpiryDateAsync(w, ct);
        if (c.RcStop) return;
        await RequiredAuthorityAsync(w, ct);
        await BeginAsync(w, ct);
        if (c.RcStop) return;
        await InsertOverrideAsync(w, ct);
        await FinishAsync(w, ct);
    }

    /// <summary>3100: overrides expire after a maximum of five working days (D-06).</summary>
    private async Task ExpiryDateAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ca = w.Ca;
        ca.OverrideFrom = ca.EvalDate;
        var pm = await GetNumAsync(w, "AR.OVERRIDE.MAX.WORKDAYS", "5", ct);
        var days = (int)CobolNumeric.Store(pm.ValueNum, 7, 0);
        if (days <= 0) days = 5;
        var dc = new DatecalcArea { Date1 = ca.EvalDate, Number = days, Calendar = "ENG" };
        dc.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ca.EvalDate, ""), "ADDBUS");
        await datecalc.ExecuteAsync(dc, ct);
        if (dc.Header.ReturnCode < 8)
        {
            ca.OverrideExpiry = dc.ResultDate;
        }
        else
        {
            c.Rc = 12;
            c.Reason = "CRD0059";
            c.Msg = "Cannot calculate the override expiry";
        }
    }

    /// <summary>3200: granted by the credit manager, or the financial controller above GBP 50,000 (D-06).</summary>
    private async Task RequiredAuthorityAsync(Ws w, CancellationToken ct)
    {
        var ca = w.Ca;
        var pm = await GetNumAsync(w, "AR.OVERRIDE.FC.THRESHOLD", "50000", ct);
        var amt1 = CobolNumeric.Store(pm.ValueNum, 13, 2);
        ca.ApproverPerm = ca.OverrideAmt > amt1 ? "FIN.CONTROL.OVERRIDE" : "AR.CREDIT.OVERRIDE";
    }

    private async Task InsertOverrideAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ca = w.Ca;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "INSERT ar.CreditOverride (CompanyCode, CustomerCode, Scope, DocumentRef, OverrideAmountGBP, ExcessGBP, Reason, " +
                "RequestedBy, ValidFrom, ExpiresOn, Status) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'PENDING'); " +
                "SELECT CONVERT(varchar(24), SCOPE_IDENTITY())",
                [SqlioParm.Text(c.Co), SqlioParm.Text(ca.Customer), SqlioParm.Text(ca.OverrideScope), SqlioParm.Text(ca.DocumentRef),
                 SqlioParm.Text(ArEdit.Money(ca.OverrideAmt)), SqlioParm.Text(ArEdit.Money(ca.Excess)), SqlioParm.Text(ca.OverrideReason),
                 SqlioParm.Text(CobolText.IsSpaces(ca.OverrideBy) ? c.UserId : ca.OverrideBy),
                 SqlioParm.Digits(ca.OverrideFrom, 8), SqlioParm.Digits(ca.OverrideExpiry, 8)],
                [24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        ca.OverrideId = (int)CobolNumeric.Truncated(row.NumVal(0), 0);
        ca.OverrideStatus = "PENDING";
        await AuditOverrideAsync(w, ct);
        if (c.RcStop) return;
        c.Rc = 4;
        c.Reason = "CRD0052";
        c.Msg = "Override requested; approval by " + CobolText.Trim(ca.ApproverPerm) + " is required";
        c.MsgCnt++;
    }

    // ------------------------------------------------------------------ 3500 APPROVE

    private async Task ApproveOverrideAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ca = w.Ca;
        await ApproveBodyAsync(w, ct);
        // 3500-EXIT
        if (c.InTransaction) await FinishAsync(w, ct);
    }

    private async Task ApproveBodyAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ca = w.Ca;
        if (ca.OverrideId == 0)
        {
            c.Reject("CRD0053", "An override reference is required");
            return;
        }
        await ReadOverrideAsync(w, ct);
        if (c.RcStop) return;
        if (CobolText.EqualsPadded(ca.OverrideBy, c.UserId))
        {
            c.Rc = 6;
            c.Reason = "CRD0054";
            c.Msg = "An override cannot be approved by the user who requested it";
            return;
        }
        if (ca.OverrideExpiry < ca.EvalDate || ca.OverrideFrom > ca.EvalDate)
        {
            c.Rc = 8;
            c.Reason = "CRD0060";
            c.Msg = "That override is not valid on the business date";
            return;
        }
        await RequiredAuthorityAsync(w, ct);
        await CheckAuthorityAsync(w, ct);
        if (c.RcStop) return;
        await BeginAsync(w, ct);
        if (c.RcStop) return;
        SqlioExecResult exec;
        try
        {
            exec = await sql.ExecAsync(
                "UPDATE ar.CreditOverride SET Status = 'ACTIVE', ApprovedBy = ?, ApprovedAt = SYSDATETIME() WHERE OverrideId = ? " +
                "AND CompanyCode = ? AND CustomerCode = ? AND Status = 'PENDING' AND RequestedBy <> ? AND ExpiresOn >= ? AND ValidFrom <= ?",
                [SqlioParm.Text(c.UserId), SqlioParm.Text(ArEdit.Int(ca.OverrideId)), SqlioParm.Text(c.Co), SqlioParm.Text(ca.Customer),
                 SqlioParm.Text(c.UserId), SqlioParm.Digits(ca.EvalDate, 8), SqlioParm.Digits(ca.EvalDate, 8)], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (exec.RowCount == 0)
        {
            c.Rc = 8;
            c.Reason = "CRD0055";
            c.Msg = "That override is not awaiting approval";
            return;
        }
        ca.OverrideStatus = "ACTIVE";
        ca.OverrideActive = "Y";
        ca.OverrideAppr = c.UserId;
        await AuditOverrideAsync(w, ct);
        if (c.RcStop) return;
        c.Rc = 0;
        c.Msg = "Credit override approved to " + ArEdit.Dmy(ca.OverrideExpiry);
    }

    private async Task ReadOverrideAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ca = w.Ca;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT v.CustomerCode, v.RequestedBy, CONVERT(char(8), v.ExpiresOn, 112), v.Status, v.OverrideAmountGBP, v.Reason, " +
                "v.Scope, ISNULL(v.DocumentRef,''), CONVERT(char(8), v.ValidFrom, 112) FROM ar.CreditOverride v " +
                "WHERE v.OverrideId = ? AND v.CompanyCode = ? AND v.CustomerCode = ?",
                [SqlioParm.Text(ArEdit.Int(ca.OverrideId)), SqlioParm.Text(c.Co), SqlioParm.Text(ca.Customer)],
                [20, 20, 8, 10, 24, 300, 12, 40, 8], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (!row.Found)
        {
            c.Rc = 16;
            c.Reason = "CRD0056";
            c.Msg = "That credit override is not held";
            return;
        }
        ca.Customer = row.Text(0);
        ca.OverrideBy = row.Text(1);
        ca.OverrideExpiry = Digits8(row.Raw(2));
        ca.OverrideFrom = Digits8(row.Raw(8));
        ca.OverrideStatus = row.Text(3);
        ca.OverrideAmt = CobolNumeric.Store(row.NumVal(4), 11, 2);
        ca.OverrideReason = row.Text(5);
        ca.OverrideScope = row.Text(6);
        ca.DocumentRef = row.Text(7);
    }

    /// <summary>MOVE X(8) TO 9(8): the digits of the text (non-digits count as zero).</summary>
    private static int Digits8(string raw)
    {
        var v = 0;
        foreach (var ch in CobolText.Fit(raw, 8)) v = v * 10 + (char.IsAsciiDigit(ch) ? ch - '0' : 0);
        return v;
    }

    private async Task AuditOverrideAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ca = w.Ca;
        var au = new AuditlogArea
        {
            EntityType = "ar.CreditOverride",
            EntityKey = ArEdit.Int(ca.OverrideId),
            FieldName = "Status",
            Reason = ca.OverrideReason,
            Source = "COBOL",
            AuthorityRef = ca.ApproverPerm,
        };
        if (c.Func == CreditCheckFunctions.Override)
        {
            au.Action = "REQUEST";
            au.NewValue = "PENDING";
        }
        else
        {
            au.Action = "APPROVE";
            au.OldValue = "PENDING";
            au.NewValue = "ACTIVE";
        }
        au.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ca.EvalDate, c.Correl), "WRITE");
        await auditlog.ExecuteAsync(au, ct);
        if (au.Header.ReturnCode > 4)
        {
            c.Rc = 12;
            c.Msg = au.Header.Message;
        }
    }

    private async Task CheckAuthorityAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var pk = new PermchkArea { Permission = w.Ca.ApproverPerm };
        pk.Header.Prepare(new KernelCaller(c.Co, c.Site, c.UserId, c.BusDate, ""), "CHECK");
        await permchk.ExecuteAsync(pk, ct);
        if (pk.Header.ReturnCode > 4)
        {
            c.Rc = 6;
            c.Reason = "CRD0061";
            c.Msg = pk.Header.Message;
        }
        else
        {
            c.Reason = "";
            c.Msg = "";
        }
    }

    /// <summary>3900: own unit when the caller is in autocommit, savepoint CREDCHK inside the caller's unit.</summary>
    private async Task BeginAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var uow = sql.UnitOfWork;
        if (uow.TransactionState == 1)
        {
            try
            {
                await uow.BeginAsync(ct);
            }
            catch (SqlioException ex)
            {
                c.SqlFailed(ex.DriverText);
                return;
            }
            w.UowOwn = true;
        }
        c.InTransaction = true;
        try
        {
            await sql.ExecAsync("IF @@TRANCOUNT = 0 BEGIN TRANSACTION; SAVE TRANSACTION CREDCHK", [], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            await FinishAsync(w, ct);
        }
    }

    private async Task FinishAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var uow = sql.UnitOfWork;
        if (w.UowOwn)
        {
            try
            {
                if (c.RcStop) await uow.RollbackAsync(ct);
                else await uow.CommitAsync(ct);
            }
            catch (SqlioException ex)
            {
                c.SqlFailed(ex.DriverText);
            }
        }
        else if (c.RcStop)
        {
            try
            {
                await sql.ExecAsync("IF XACT_STATE() = 1 ROLLBACK TRANSACTION CREDCHK", [], ct);
            }
            catch (SqlioException ex)
            {
                c.SqlFailed(ex.DriverText);
            }
        }
        c.InTransaction = false;
    }
}
