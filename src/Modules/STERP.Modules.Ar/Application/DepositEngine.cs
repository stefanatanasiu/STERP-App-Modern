using Sterp.Modules.Ar.Contracts;
using Sterp.Modules.Ar.Domain;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Application;

/// <summary>
/// ARDEPOS (cobol/online/ar/ARDEPOS.cob, AR-013) behind <see cref="IArDepositService"/>: DEPOSIT (customer deposit open item,
/// GL posting and output VAT on the advance in one unit), ENQUIRE, APPLY (allocation of the deposit to a document) and
/// REFUND (residual reduction). Runs on the caller's engine connection and unit of work.
/// </summary>
public sealed class DepositEngine(
    ISqlRunner sql,
    IPermissionService permchk,
    IPeriodService periodck,
    IDocumentNumberService numgen,
    VatWriterEngine arvatwrt,
    ArGlPostingEngine arpostgl) : IArDepositService
{
    private const string Program = "ARDEPOS";

    private sealed class Ws
    {
        public required ArCtl Ctl;
        public required ArdeposArea Dp;
        public bool TranOpen;
        public int FiscalYear;
        public int PeriodNo;
        public int JournalId;
    }

    public async Task<LegacyResult> CallAsync(ArdeposArea dp, CancellationToken ct = default)
    {
        // 0100-INITIALISE
        var c = ArCtl.Start(dp.Header, Program, "AR-013");
        var w = new Ws { Ctl = c, Dp = dp };
        dp.Residual = 0;
        dp.AppliedTotal = 0;
        dp.RefundedTotal = 0;
        if (dp.DocDate == 0) dp.DocDate = c.BusDate;
        if (dp.PostingDate == 0) dp.PostingDate = dp.DocDate;
        if (CobolText.IsSpaces(dp.Currency)) dp.Currency = "GBP";
        if (dp.FxRate == 0) dp.FxRate = 1;
        if (CobolText.IsSpaces(dp.Purpose)) dp.Purpose = "ADVANCE";
        if (CobolText.IsSpaces(dp.Cleared) || CobolText.IsLowValues(dp.Cleared)) dp.Cleared = "Y";

        // 0200-VALIDATE
        if (CobolText.IsSpaces(c.Co) || CobolText.IsSpaces(dp.Customer))
        {
            c.Reject("DEP0001", "A company and a customer are required");
        }
        else
        {
            if (c.Func == "DEPOSIT" && dp.Amount <= 0) c.Reject("DEP0002", "A deposit must be for a positive amount");
            if (c.Func != "DEPOSIT" && CobolText.IsSpaces(dp.DepositRef)) c.Reject("DEP0003", "A deposit reference is required");
        }

        if (!c.InputBad)
        {
            switch (c.Func)
            {
                case "DEPOSIT":
                    await TakeDepositAsync(w, ct);
                    break;
                case "ENQUIRE":
                    await ReadDepositAsync(w, ct);
                    if (!c.RcBad)
                    {
                        c.Msg = "Deposit " + dp.DepositRef.Trim(' ') + ": applied " + ArEdit.Money(dp.AppliedTotal) + ", residual " +
                            ArEdit.MoneyUk(dp.Residual);
                    }
                    break;
                case "APPLY":
                    await ReadDepositAsync(w, ct);
                    await ApplyAsync(w, ct);
                    break;
                case "REFUND":
                    await ReadDepositAsync(w, ct);
                    await RefundAsync(w, ct);
                    break;
                default:
                    c.Rc = 8;
                    c.Reason = "DEP0098";
                    c.Msg = "ARDEPOS does not support function [" + c.Func.Trim(' ') + "]";
                    break;
            }
        }
        c.SetResult(dp.Header);
        return dp.Header.Outcome();
    }

    /// <summary>0300-READ-DEPOSIT.</summary>
    private async Task ReadDepositAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var dp = w.Dp;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT CONVERT(varchar(24), o.OpenItemId), o.CustomerCode, o.CurrencyCode, CONVERT(varchar(24), -o.OriginalAmount), " +
                "CONVERT(varchar(24), -o.ResidualAmount), o.DocumentNumber, CONVERT(varchar(24), -o.VatAmountGBP), o.SettlementStatus " +
                "FROM ar.OpenItem o WHERE o.CompanyCode = ? AND o.DocumentNumber = ? AND o.ItemType = 'DEPOSIT'",
                [SqlioParm.Text(c.Co), SqlioParm.Text(dp.DepositRef)], [24, 20, 3, 24, 24, 30, 24, 12], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (!row.Found)
        {
            c.Rc = 16;
            c.Reason = "DEP0004";
            c.Msg = "Deposit " + dp.DepositRef.Trim(' ') + " is not held";
            return;
        }
        dp.OpenItemId = (int)CobolNumeric.Store(row.NumVal(0), 9, 0);
        dp.Currency = CobolText.Trimmed(row.Raw(2), 3);
        dp.Amount = CobolNumeric.Store(row.NumVal(3), 11, 2);
        dp.Residual = CobolNumeric.Store(row.NumVal(4), 11, 2);
        dp.AppliedTotal = CobolNumeric.Store(dp.Amount - dp.Residual, 11, 2);
    }

    /// <summary>1000-TAKE-DEPOSIT.</summary>
    private async Task TakeDepositAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var dp = w.Dp;

        // 1050-PERMISSION: a refusal is RC 06, which does not stop the deposit (AR-RC-BAD is 08 thru 16)
        var pk = new PermchkArea { Permission = "AR.RECEIPT.POST" };
        pk.Header.Prepare(new KernelCaller(c.Co, c.Site, c.UserId, dp.PostingDate, ""), "CHECK");
        await permchk.ExecuteAsync(pk, ct);
        if (pk.Header.ReturnCode > 4)
        {
            c.Rc = 6;
            c.Reason = "DEP0005";
            c.Msg = pk.Header.Message;
            c.MsgCnt++;
        }
        if (c.RcBad) return;

        // 1100-PERIOD
        var pc = new PeriodckArea { PostingDate = dp.PostingDate, DocumentDate = dp.DocDate, Action = "POST" };
        pc.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, dp.PostingDate, ""), "CHECK");
        await periodck.ExecuteAsync(pc, ct);
        w.FiscalYear = pc.FiscalYear;
        w.PeriodNo = pc.PeriodNo;
        if (pc.Header.ReturnCode > 4)
        {
            c.Rc = pc.Header.ReturnCode;
            c.Reason = pc.Header.ReasonCode;
            c.Msg = pc.Header.Message;
            c.MsgCnt++;
        }
        if (c.RcBad) return;

        // 1150-DRAW-NUMBER
        if (CobolText.IsSpaces(dp.DepositRef))
        {
            var ng = new NumgenArea { SeriesCode = "ARRC" };
            ng.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, dp.DocDate, c.Correl), "NEXT");
            await numgen.ExecuteAsync(ng, ct);
            if (ng.Header.ReturnCode > 4)
            {
                c.Rc = ng.Header.ReturnCode;
                c.Reason = ng.Header.ReasonCode;
                c.Msg = ng.Header.Message;
            }
            else
            {
                dp.DepositRef = CobolText.Trimmed("DEP-" + ng.DocumentNumber.Trim(' '), 30);
            }
        }
        if (c.RcBad) return;

        // 1200-SPLIT-VAT
        await SplitVatAsync(w, ct);
        if (c.RcBad) return;

        await BeginAsync(w, ct);
        await WriteOpenItemAsync(w, ct);
        if (c.RcBad)
        {
            await RollbackAsync(w, ct);
            return;
        }
        await PostDepositAsync(w, ct);
        if (c.RcBad)
        {
            await RollbackAsync(w, ct);
            return;
        }
        if (dp.VatGbp != 0)
        {
            await VatRecordAsync(w, ct);
            if (c.RcBad)
            {
                await RollbackAsync(w, ct);
                return;
            }
        }
        await CommitAsync(w, ct);
        dp.Residual = dp.Amount;
        c.Msg = "Deposit " + dp.DepositRef.Trim(' ') + " of " + dp.Currency.Trim(' ') + " " + ArEdit.MoneyUk(dp.Amount) +
            " received; output VAT GBP " + ArEdit.Money(dp.VatGbp) + " accounted on the advance";
    }

    /// <summary>1200-SPLIT-VAT: the advance is VAT-inclusive; output VAT = gross x rate / (100 + rate), ROUNDED.</summary>
    private async Task SplitVatAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var dp = w.Dp;
        dp.AmountGbp = dp.Currency == "GBP" || dp.FxRate == 0
            ? CobolNumeric.Store(dp.Amount, 11, 2)
            : CobolNumeric.Store(dp.Amount / dp.FxRate, 11, 2, rounded: true);
        dp.VatGbp = 0;
        dp.NetGbp = dp.AmountGbp;
        if (CobolText.IsSpaces(dp.VatCode)) return;
        var vw = new ArvatwrtArea { VatCode = dp.VatCode, TaxPoint = dp.DocDate, Direction = "OUTPUT" };
        vw.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, dp.DocDate, ""), "LOOKUP");
        await arvatwrt.CallAsync(vw, ct);
        if (vw.Header.ReturnCode > 6)
        {
            c.Rc = 8;
            c.Reason = vw.Header.ReasonCode;
            c.Msg = vw.Header.Message;
            return;
        }
        dp.VatRate = vw.RatePct;
        if (vw.RatePct == 0) return;
        var amt1 = CobolNumeric.Store(dp.AmountGbp * vw.RatePct / (100 + vw.RatePct), 13, 2, rounded: true);
        dp.VatGbp = CobolNumeric.Store(amt1, 11, 2);
        dp.NetGbp = CobolNumeric.Store(dp.AmountGbp - dp.VatGbp, 11, 2);
    }

    /// <summary>1300-WRITE-OPEN-ITEM: the deposit as a negative DEPOSIT open item.</summary>
    private async Task WriteOpenItemAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var dp = w.Dp;
        var amt = ArEdit.Money(0 - dp.Amount);
        var amtGbp = ArEdit.Money(0 - dp.AmountGbp);
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SET NOCOUNT ON; INSERT ar.OpenItem (CompanyCode, CustomerCode, ItemType, DocumentNumber, DocumentDate, DueDate, " +
                "PostingDate, FiscalYear, PeriodNo, CurrencyCode, FxRate, OriginalAmount, OriginalAmountGBP, ResidualAmount, " +
                "ResidualAmountGBP, VatAmountGBP, CreatedBy) VALUES (?, ?, 'DEPOSIT', ?, ?, ?, ?, NULLIF(?,0), NULLIF(?,0), ?, ?, ?, ?, ?, " +
                "?, ?, ?); SELECT CONVERT(varchar(24), SCOPE_IDENTITY()), ''",
                [
                    SqlioParm.Text(c.Co), SqlioParm.Text(dp.Customer), SqlioParm.Text(dp.DepositRef), SqlioParm.Digits(dp.DocDate, 8),
                    SqlioParm.Digits(dp.DocDate, 8), SqlioParm.Digits(dp.PostingDate, 8), SqlioParm.Text(ArEdit.Int(w.FiscalYear)),
                    SqlioParm.Text(ArEdit.Int(w.PeriodNo)), SqlioParm.Text(dp.Currency), SqlioParm.Text(ArEdit.Rate(dp.FxRate)),
                    SqlioParm.Text(amt), SqlioParm.Text(amtGbp), SqlioParm.Text(amt), SqlioParm.Text(amtGbp),
                    SqlioParm.Text(ArEdit.Money(0 - dp.VatGbp)), SqlioParm.Text(c.UserId),
                ], [24, 24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        dp.OpenItemId = (int)CobolNumeric.Store(row.NumVal(0), 9, 0);
    }

    /// <summary>1400-POST-DEPOSIT: ARPOSTGL POST in the caller's unit (PG-OWN-TRAN "N").</summary>
    private async Task PostDepositAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var dp = w.Dp;
        var pg = new ArpostglArea
        {
            EventCode = "AR.DEPOSIT",
            JournalType = "AR",
            SourceModule = "AR",
            SourceRef = dp.DepositRef,
            DocDate = dp.DocDate,
            PostingDate = dp.PostingDate,
            Currency = "GBP",
            FxRate = 1,
            OwnTran = "N",
            Description = CobolText.Trimmed("Customer deposit " + dp.DepositRef.Trim(' ') + " " + dp.Customer.Trim(' '), 200),
            LegCnt = 3,
        };
        pg.Legs[0].GLegCode = "BANK";
        pg.Legs[0].GDebit = dp.AmountGbp;
        pg.Legs[0].GBankAcct = CobolText.Trimmed(dp.BankAccount, 20);
        pg.Legs[0].GNarrative = CobolText.Trimmed(dp.DepositRef, 100);
        pg.Legs[1].GLegCode = "DEFERRED";
        pg.Legs[1].GCredit = dp.NetGbp;
        pg.Legs[1].GSubledger = "AR";
        pg.Legs[1].GSubref = CobolText.Trimmed(dp.Customer, 40);
        pg.Legs[1].GNarrative = "Customer deposit liability";
        pg.Legs[2].GLegCode = "VAT";
        pg.Legs[2].GAccount = "2040";
        pg.Legs[2].GCredit = dp.VatGbp;
        pg.Legs[2].GNarrative = "Output VAT on advance";
        pg.Legs[2].GVatCode = CobolText.Trimmed(dp.VatCode, 4);
        pg.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, dp.PostingDate, c.Correl), "POST");
        await arpostgl.CallAsync(pg, ct);
        w.JournalId = pg.JournalId;
        if (pg.Header.ReturnCode > 4)
        {
            c.Rc = pg.Header.ReturnCode;
            c.Reason = pg.Header.ReasonCode;
            c.Msg = pg.Header.Message;
            c.MsgCnt++;
            return;
        }
        dp.JournalNo = CobolText.Trimmed(pg.JournalNo, 30);
    }

    /// <summary>1500-VAT-RECORD: ARVATWRT WRITE of the output VAT on the advance.</summary>
    private async Task VatRecordAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var dp = w.Dp;
        var vw = new ArvatwrtArea
        {
            SourceModule = "AR",
            DocType = "CUSTOMERDEPOSIT",
            DocRef = dp.DepositRef,
            PartyType = "CUSTOMER",
            PartyCode = dp.Customer,
            TaxPoint = dp.DocDate,
            PostingDate = dp.PostingDate,
            VatCode = dp.VatCode,
            Direction = "OUTPUT",
            Currency = "GBP",
            FxRate = 1,
            Net = dp.NetGbp,
            NetGbp = dp.NetGbp,
            Vat = dp.VatGbp,
            VatGbp = dp.VatGbp,
            JournalId = w.JournalId,
        };
        vw.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, dp.DocDate, ""), "WRITE");
        await arvatwrt.CallAsync(vw, ct);
        if (vw.Header.ReturnCode > 4)
        {
            c.Rc = vw.Header.ReturnCode;
            c.Reason = vw.Header.ReasonCode;
            c.Msg = vw.Header.Message;
            c.MsgCnt++;
        }
    }

    /// <summary>2000-APPLY-DEPOSIT: allocation from the deposit to a document (no unit of its own).</summary>
    private async Task ApplyAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var dp = w.Dp;
        if (c.RcBad) return;
        if (dp.ApplyAmount <= 0)
        {
            c.Rc = 8;
            c.Reason = "DEP0010";
            c.Msg = "A positive amount is required to apply";
            return;
        }
        if (dp.ApplyAmount > dp.Residual)
        {
            c.Rc = 8;
            c.Reason = "DEP0011";
            c.Msg = "Only " + ArEdit.MoneyUk(dp.Residual) + " remains on deposit " + dp.DepositRef.Trim(' ');
            return;
        }
        var amt = SqlioParm.Text(ArEdit.Money(dp.ApplyAmount));
        var item = SqlioParm.Text(ArEdit.Int(dp.OpenItemId));
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SET NOCOUNT ON; INSERT ar.Allocation (CompanyCode, CustomerCode, SourceType, SourceOpenItemId, TargetOpenItemId, " +
                "EffectiveDate, CurrencyCode, AppliedAmount, AppliedAmountGBP, Status, CreatedBy) SELECT ?, ?, 'DEPOSIT', ?, t.OpenItemId, ?, " +
                "?, ?, ?, 'ACTIVE', ? FROM ar.OpenItem t WHERE t.CompanyCode = ? AND t.DocumentNumber = ?; UPDATE ar.OpenItem SET " +
                "ResidualAmount = ResidualAmount + ?, ResidualAmountGBP = ResidualAmountGBP + ? WHERE OpenItemId = ?; UPDATE ar.OpenItem " +
                "SET ResidualAmount = ResidualAmount - ?, ResidualAmountGBP = ResidualAmountGBP - ?, SettlementStatus = CASE WHEN " +
                "ResidualAmount - ? = 0 THEN 'SETTLED' ELSE 'PARTIAL' END WHERE CompanyCode = ? AND DocumentNumber = ?; SELECT " +
                "CONVERT(varchar(24), SCOPE_IDENTITY()), ''",
                [
                    SqlioParm.Text(c.Co), SqlioParm.Text(dp.Customer), item, SqlioParm.Digits(dp.PostingDate, 8), SqlioParm.Text(dp.Currency),
                    amt, amt, SqlioParm.Text(c.UserId), SqlioParm.Text(c.Co), SqlioParm.Text(dp.ApplyTo), amt, amt, item, amt, amt, amt,
                    SqlioParm.Text(c.Co), SqlioParm.Text(dp.ApplyTo),
                ], [24, 24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        dp.AllocId = (int)CobolNumeric.Store(row.NumVal(0), 9, 0);
        dp.Residual = CobolNumeric.Store(dp.Residual - dp.ApplyAmount, 11, 2);
        dp.AppliedTotal = CobolNumeric.Store(dp.AppliedTotal + dp.ApplyAmount, 11, 2);
        c.Msg = ArEdit.MoneyUk(dp.ApplyAmount) + " of deposit " + dp.DepositRef.Trim(' ') + " applied to " + dp.ApplyTo.Trim(' ') +
            "; residual " + ArEdit.Money(dp.Residual);
    }

    /// <summary>3000-REFUND-DEPOSIT.</summary>
    private async Task RefundAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var dp = w.Dp;
        if (c.RcBad) return;
        if (dp.RefundAmount <= 0)
        {
            c.Rc = 8;
            c.Reason = "DEP0020";
            c.Msg = "A positive refund amount is required";
            return;
        }
        if (dp.RefundAmount > dp.Residual)
        {
            c.Rc = 8;
            c.Reason = "DEP0021";
            c.Msg = "A refund of " + ArEdit.Money(dp.RefundAmount) + " exceeds the deposit residual of " + ArEdit.MoneyUk(dp.Residual) +
                " - the balance has already been applied";
            c.MsgCnt++;
            return;
        }
        if (CobolText.IsSpaces(dp.Reason))
        {
            c.Rc = 8;
            c.Reason = "DEP0022";
            c.Msg = "A refund needs an approved reason";
            return;
        }
        var amt = SqlioParm.Text(ArEdit.Money(dp.RefundAmount));
        try
        {
            await sql.ExecAsync(
                "UPDATE ar.OpenItem SET ResidualAmount = ResidualAmount + ?, ResidualAmountGBP = ResidualAmountGBP + ?, SettlementStatus = " +
                "CASE WHEN ResidualAmount + ? = 0 THEN 'SETTLED' ELSE 'PARTIAL' END, ModifiedBy = ?, ModifiedAt = SYSDATETIME() WHERE " +
                "OpenItemId = ?",
                [amt, amt, amt, SqlioParm.Text(c.UserId), SqlioParm.Text(ArEdit.Int(dp.OpenItemId))], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        dp.Residual = CobolNumeric.Store(dp.Residual - dp.RefundAmount, 11, 2);
        dp.RefundedTotal = CobolNumeric.Store(dp.RefundedTotal + dp.RefundAmount, 11, 2);
        c.Msg = "Refund of " + ArEdit.MoneyUk(dp.RefundAmount) + " approved against deposit " + dp.DepositRef.Trim(' ');
    }

    private async Task BeginAsync(Ws w, CancellationToken ct)
    {
        try
        {
            await sql.UnitOfWork.BeginAsync(ct);
        }
        catch (SqlioException)
        {
            // SQLIO BEGN: result not inspected
        }
        w.TranOpen = true;
    }

    private async Task CommitAsync(Ws w, CancellationToken ct)
    {
        try
        {
            await sql.UnitOfWork.CommitAsync(ct);
        }
        catch (SqlioException)
        {
            // SQLIO COMT: result not inspected
        }
        w.TranOpen = false;
    }

    /// <summary>9500-ROLLBACK.</summary>
    private async Task RollbackAsync(Ws w, CancellationToken ct)
    {
        if (!w.TranOpen) return;
        try
        {
            await sql.UnitOfWork.RollbackAsync(ct);
        }
        catch (SqlioException)
        {
            // SQLIO ROLB: result not inspected
        }
        w.TranOpen = false;
    }
}
