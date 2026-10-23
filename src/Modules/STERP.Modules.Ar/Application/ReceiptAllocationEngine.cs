using Sterp.Modules.Ar.Contracts;
using Sterp.Modules.Ar.Domain;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Application;

/// <summary>
/// RCPTALOC (cobol/online/ar/RCPTALOC.cob) - receipts and allocation: RECEIPT (cash effect), CONFIRM, PREVIEW (no
/// business effect), ALLOCATE (no second cash effect) and UNALLOC (authorised correction, history preserved).
/// </summary>
public sealed class ReceiptAllocationEngine(
    ISqlRunner sql,
    IParameterService parmget,
    IPermissionService permchk,
    IPeriodService periodck,
    IDocumentNumberService numgen,
    IIdempotencyService idempot,
    IExceptionRaiser excprais,
    ArGlPostingEngine arpostgl)
{
    private const string Program = "RCPTALOC";

    /// <summary>WS-SRC / WS-TGT row areas (10 and 12 slots) as the SNGL fetches left them.</summary>
    private sealed class Row
    {
        public string Id = "";
        public string Customer = "";
        public string Currency = "";
        public string Rate = "";
        public string Residual = "";
        public string ResidGbp = "";
        public string Type = "";
        public string DocNo = "";
        public string Confirm = "";
        public string Control = "";
        public string DiscDate = "";
        public string DiscAmt = "";
        public string DueDate = "";
        public string Disputed = "";

        public void Fill(SqlioRow r, bool target)
        {
            Id = r.Raw(0);
            Customer = r.Raw(1);
            Currency = r.Raw(2);
            Rate = r.Raw(3);
            Residual = r.Raw(4);
            ResidGbp = r.Raw(5);
            Type = r.Raw(6);
            DocNo = r.Raw(7);
            if (target)
            {
                DiscDate = r.Raw(8);
                DiscAmt = r.Raw(9);
                DueDate = r.Raw(10);
                Disputed = r.Raw(11);
            }
            else
            {
                Confirm = r.Raw(8);
                Control = r.Raw(9);
            }
        }
    }

    private sealed class Ws
    {
        public required ArCtl Ctl;
        public required RcptalocArea Ra;
        public Row Src = new();
        public Row Tgt = new();
        public bool TranOpen;
        public bool Preview;
        public bool CrossCcy;
        public decimal SrcLeft;
        public decimal SrcRate;
        public decimal TgtRate;
        public decimal ApplyGbp;
        public decimal DiscGbp;
        public decimal WoGbp;
        public int FiscalYear;
        public int PeriodNo;
    }

    public async Task<LegacyResult> CallAsync(RcptalocArea ra, CancellationToken ct = default)
    {
        var w = new Ws { Ctl = ArCtl.Start(ra.Header, Program, "AR-010"), Ra = ra };
        var c = w.Ctl;
        await InitialiseAsync(w, ct);
        Validate(w);
        if (!c.InputBad)
        {
            switch (c.Func)
            {
                case "RECEIPT":
                    await RecordReceiptAsync(w, ct);
                    break;
                case "CONFIRM":
                    await ConfirmReceiptAsync(w, ct);
                    break;
                case "PREVIEW":
                    w.Preview = true;
                    await AllocateAsync(w, ct);
                    break;
                case "ALLOCATE":
                    await AllocateAsync(w, ct);
                    break;
                case "UNALLOC":
                    await UnallocateAsync(w, ct);
                    break;
                default:
                    c.Rc = 8;
                    c.Reason = "RCP0098";
                    c.Msg = "RCPTALOC does not support function [" + CobolText.Trim(c.Func) + "]";
                    break;
            }
        }
        c.SetResult(ra.Header);
        return ra.Header.Outcome();
    }

    // ------------------------------------------------------------------ 0100 / 0150 / 0200

    private async Task InitialiseAsync(Ws w, CancellationToken ct)
    {
        var ra = w.Ra;
        var c = w.Ctl;
        ra.TotApplied = ra.TotDiscount = ra.TotWriteoff = ra.TotFx = ra.Residual = 0m;
        ra.ReversedCnt = 0;
        if (ra.ReceiptDate == 0) ra.ReceiptDate = c.BusDate;
        if (ra.PostingDate == 0) ra.PostingDate = ra.ReceiptDate;
        if (ra.EffectiveDate == 0) ra.EffectiveDate = ra.PostingDate;
        if (CobolText.IsSpaces(ra.Currency)) ra.Currency = "GBP";
        if (ra.FxRate == 0) ra.FxRate = 1m;
        if (CobolText.IsSpaces(ra.ConfirmState)) ra.ConfirmState = "CONFIRMED";
        if (CobolText.IsSpaces(ra.SourceType)) ra.SourceType = "RECEIPT";
        // 0150-READ-LIMITS: D-22 small difference limit and the discount grace
        var wo = await GetNumAsync(w, "AR.WRITEOFF.AUTO.GBP", "5.00", ct);
        ra.WriteoffLimit = CobolNumeric.Store(wo.ValueNum, 11, 2);
        if (ra.WriteoffLimit == 0) ra.WriteoffLimit = 5m;
        var grace = await GetNumAsync(w, "AR.DISCOUNT.GRACE.DAYS", "2", ct);
        ra.DiscGraceDays = (int)CobolNumeric.Store(grace.ValueNum, 5, 0);
    }

    private async Task<ParmgetArea> GetNumAsync(Ws w, string code, string defaultText, CancellationToken ct)
    {
        var pm = new ParmgetArea { ParamCode = code, UseDefault = "Y", DefaultText = defaultText };
        pm.Header.Prepare(new KernelCaller(w.Ctl.Co, "", w.Ctl.UserId, w.Ra.PostingDate, ""), "GETNUM");
        await parmget.ExecuteAsync(pm, ct);
        return pm;
    }

    private static void Validate(Ws w)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        if (CobolText.IsSpaces(c.Co))
        {
            c.Reject("RCP0001", "A company code is required");
            return;
        }
        if (c.Func == "RECEIPT")
        {
            if (ra.Gross <= 0)
            {
                c.Reject("RCP0002", "A receipt must be for a positive amount");
                return;
            }
            if (CobolText.IsSpaces(ra.BankAccount))
            {
                c.Reject("RCP0003", "A bank or cash account is required so the money can be identified");
                return;
            }
            if (CobolText.IsSpaces(ra.Method)) ra.Method = "BACS";
        }
        if ((c.Func == "ALLOCATE" || c.Func == "PREVIEW") && (ra.AllocCnt < 1 || ra.AllocCnt > 20))
        {
            c.Reject("RCP0004", "An allocation instruction needs between one and twenty target items");
        }
    }

    // ------------------------------------------------------------------ 1000 RECEIPT

    private async Task RecordReceiptAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        await PermissionAsync(w, ct);
        if (c.RcBad) return;
        await PeriodAsync(w, ct);
        if (c.RcBad) return;
        await IdempotentBeginAsync(w, ct);
        if (c.RcBad || c.RcWarn) return;
        await DrawNumberAsync(w, ct);
        if (c.RcBad) return;
        if (ra.Currency == "GBP")
        {
            ra.GrossGbp = ra.Gross;
            ra.FxRate = 1m;
        }
        else
        {
            if (ra.FxRate <= 0)
            {
                c.Rc = 8;
                c.Reason = "RCP0005";
                c.Msg = "A foreign receipt needs an approved rate before it can be valued in sterling";
                return;
            }
            ra.GrossGbp = CobolNumeric.Store(ra.Gross / ra.FxRate, 11, 2, rounded: true);
        }
        await BeginAsync(w, ct);
        await WriteReceiptAsync(w, ct);
        if (c.RcBad)
        {
            await RollbackAsync(w, ct);
            return;
        }
        if (ra.Confirmed)
        {
            await OpenItemAsync(w, ct);
            if (c.RcBad)
            {
                await RollbackAsync(w, ct);
                return;
            }
            await PostCashAsync(w, ct);
            if (c.RcBad)
            {
                await RollbackAsync(w, ct);
                return;
            }
        }
        if (ra.Unidentified == "Y") await UnidentifiedQueueAsync(w, ct);
        await CommitAsync(w, ct);
        await IdempotentEndAsync(w, ct);
        c.Msg = "Receipt " + CobolText.Trim(ra.ReceiptNo) + " " + CobolText.Trim(ra.Currency) + " " + ArEdit.MoneyUk(ra.Gross) +
                " recorded (" + CobolText.Trim(ra.ConfirmState) + ")";
    }

    private async Task PermissionAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var pk = new PermchkArea { Permission = c.Func is "RECEIPT" or "CONFIRM" ? "AR.RECEIPT.POST" : "AR.ALLOCATE.RUN" };
        pk.Header.Prepare(new KernelCaller(c.Co, c.Site, c.UserId, w.Ra.PostingDate, ""), "CHECK");
        await permchk.ExecuteAsync(pk, ct);
        if (pk.Header.ReturnCode > 4)
        {
            c.Rc = 6;
            c.Reason = "RCP0006";
            c.Msg = pk.Header.Message;
            c.MsgCnt++;
        }
    }

    private async Task PeriodAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        var pc = new PeriodckArea { PostingDate = ra.PostingDate, DocumentDate = ra.ReceiptDate, Action = "POST" };
        pc.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ra.PostingDate, ""), "CHECK");
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
    }

    private async Task IdempotentBeginAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        if (CobolText.IsSpaces(c.Correl))
        {
            c.Rc = 8;
            c.Reason = "RCP0007";
            c.Msg = "A correlation id is required so a repeated bank receipt does not increase cash twice";
            return;
        }
        var id = new IdempotArea { Operation = "ar.Receipt.Post" };
        id.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ra.PostingDate, c.Correl), "BEGIN");
        await idempot.ExecuteAsync(id, ct);
        switch (id.Header.ReturnCode)
        {
            case 0:
                break;
            case 4:
                ra.ReceiptNo = CobolText.Trimmed(id.ResultRef, 30);
                c.Rc = 4;
                c.Reason = "RCP0008";
                c.Msg = id.Header.Message;
                c.MsgCnt++;
                break;
            default:
                c.Rc = id.Header.ReturnCode;
                c.Reason = id.Header.ReasonCode;
                c.Msg = id.Header.Message;
                c.MsgCnt++;
                break;
        }
    }

    private async Task DrawNumberAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        if (!CobolText.IsSpaces(ra.ReceiptNo)) return;
        var ng = new NumgenArea { SeriesCode = "ARRC" };
        ng.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ra.ReceiptDate, c.Correl), "NEXT");
        await numgen.ExecuteAsync(ng, ct);
        if (ng.Header.ReturnCode > 4)
        {
            c.Rc = ng.Header.ReturnCode;
            c.Reason = ng.Header.ReasonCode;
            c.Msg = ng.Header.Message;
            c.MsgCnt++;
            return;
        }
        ra.ReceiptNo = CobolText.Trimmed(ng.DocumentNumber, 30);
    }

    private async Task WriteReceiptAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SET NOCOUNT ON; INSERT ar.Receipt (CompanyCode, ReceiptNumber, CustomerCode, PayerCustomerCode, PayerName, " +
                "PaymentMethod, BankAccountCode, ReceiptDate, ValueDate, PostingDate, CurrencyCode, FxRate, GrossReceived, " +
                "GrossReceivedGBP, BankFee, AllocatedAmount, UnallocatedAmount, BankReference, ExternalReference, ConfirmationState, " +
                "Status, CorrelationId, CreatedBy) VALUES (?, ?, NULLIF(?,''), NULLIF(?,''), NULLIF(?,''), ?, ?, ?, NULLIF(?,''), " +
                "?, ?, ?, ?, ?, ?, 0, ?, NULLIF(?,''), NULLIF(?,''), ?, 'POSTED', ?, ?); " +
                "SELECT CONVERT(varchar(24), SCOPE_IDENTITY()), ''",
                [SqlioParm.Text(c.Co), SqlioParm.Text(ra.ReceiptNo), SqlioParm.Text(ra.Customer), SqlioParm.Text(ra.Payer),
                 SqlioParm.Text(ra.PayerName), SqlioParm.Text(ra.Method), SqlioParm.Text(ra.BankAccount),
                 SqlioParm.Digits(ra.ReceiptDate, 8), ra.ValueDate == 0 ? SqlioParm.Text("") : SqlioParm.Digits(ra.ValueDate, 8),
                 SqlioParm.Digits(ra.PostingDate, 8), SqlioParm.Text(ra.Currency), SqlioParm.Text(ArEdit.Rate(ra.FxRate)),
                 SqlioParm.Text(ArEdit.Money(ra.Gross)), SqlioParm.Text(ArEdit.Money(ra.GrossGbp)), SqlioParm.Text(ArEdit.Money(ra.BankFee)),
                 SqlioParm.Text(ArEdit.Money(ra.Gross)), SqlioParm.Text(ra.BankRef), SqlioParm.Text(ra.ExternalRef),
                 SqlioParm.Text(ra.ConfirmState), SqlioParm.Text(c.Correl), SqlioParm.Text(c.UserId)],
                [24, 24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        ra.ReceiptId = (int)CobolNumeric.Truncated(row.NumVal(0), 0);
        ra.Unallocated = ra.Gross;
    }

    /// <summary>1400: confirmed money sits on the account as an unapplied credit (and is named as the next source).</summary>
    private async Task OpenItemAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        var negGross = ArEdit.Money(CobolNumeric.Store(0 - ra.Gross, 13, 2));
        var negGbp = ArEdit.Money(CobolNumeric.Store(0 - ra.GrossGbp, 13, 2));
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SET NOCOUNT ON; INSERT ar.OpenItem (CompanyCode, CustomerCode, ItemType, DocumentNumber, DocumentDate, DueDate, " +
                "PostingDate, FiscalYear, PeriodNo, CurrencyCode, FxRate, OriginalAmount, OriginalAmountGBP, ResidualAmount, " +
                "ResidualAmountGBP, SourceReceiptId, CreatedBy) VALUES (?, ?, 'RECEIPT', ?, ?, ?, ?, NULLIF(?,0), NULLIF(?,0), ?, ?, " +
                "?, ?, ?, ?, ?, ?); SELECT CONVERT(varchar(24), SCOPE_IDENTITY()), ''",
                [SqlioParm.Text(c.Co), SqlioParm.Text(CobolText.IsSpaces(ra.Customer) ? ra.Payer : ra.Customer),
                 SqlioParm.Text(ra.ReceiptNo), SqlioParm.Digits(ra.ReceiptDate, 8), SqlioParm.Digits(ra.ReceiptDate, 8),
                 SqlioParm.Digits(ra.PostingDate, 8), SqlioParm.Text(ArEdit.Int(w.FiscalYear)), SqlioParm.Text(ArEdit.Int(w.PeriodNo)),
                 SqlioParm.Text(ra.Currency), SqlioParm.Text(ArEdit.Rate(ra.FxRate)), SqlioParm.Text(negGross), SqlioParm.Text(negGbp),
                 SqlioParm.Text(negGross), SqlioParm.Text(negGbp), SqlioParm.Text(ArEdit.Int(ra.ReceiptId)), SqlioParm.Text(c.UserId)],
                [24, 24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        ra.SourceItemId = (int)CobolNumeric.Truncated(row.NumVal(0), 0);
        ra.SourceDoc = ra.ReceiptNo;
        ra.SourceType = "RECEIPT";
        ra.SourceAvail = ra.Gross;
    }

    /// <summary>1500: DR bank, CR trade debtors; the bank fee is never netted off the customer's settlement (AR-014).</summary>
    private async Task PostCashAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        var pg = new ArpostglArea
        {
            EventCode = ra.Method == "DD" ? "AR.DDCOLLECT" : "AR.RECEIPT",
            JournalType = "AR",
            SourceModule = "AR",
            SourceRef = ra.ReceiptNo,
            DocDate = ra.ReceiptDate,
            PostingDate = ra.PostingDate,
            Currency = ra.Currency,
            FxRate = ra.FxRate,
            OwnTran = "N",
            Description = CobolText.Trimmed("Customer receipt " + CobolText.Trim(ra.ReceiptNo) + " " + CobolText.Trim(ra.PayerName), 200),
        };
        pg.Header.Function = "POST";
        pg.Header.Company = c.Co;
        pg.Header.User = c.UserId;
        pg.Header.CorrelationId = c.Correl;
        pg.Header.BusinessDate = ra.PostingDate;
        var bank = pg.Legs[0];
        bank.GLegCode = "BANK";
        bank.GDebit = ra.Gross;
        bank.GBankAcct = ra.BankAccount;
        bank.GNarrative = CobolText.Trimmed(ra.BankRef, 100);
        var debtor = pg.Legs[1];
        debtor.GLegCode = "DEBTOR";
        debtor.GCredit = ra.Gross;
        debtor.GSubledger = "AR";
        debtor.GSubref = ra.Customer;
        debtor.GNarrative = ra.ReceiptNo;
        pg.LegCnt = 2;
        await arpostgl.CallAsync(pg, ct);
        if (pg.Header.ReturnCode > 4)
        {
            c.Rc = pg.Header.ReturnCode;
            c.Reason = pg.Header.ReasonCode;
            c.Msg = pg.Header.Message;
            c.MsgCnt++;
            return;
        }
        ra.JournalNo = CobolText.Trimmed(pg.JournalNo, 30);
        // 1550-LINK-JOURNAL
        try
        {
            await sql.ExecAsync(
                "UPDATE ar.Receipt SET JournalId = (SELECT TOP (1) j.JournalId FROM fin.Journal j WHERE j.CompanyCode = ? " +
                "AND j.JournalNumber = ?) WHERE ReceiptId = ?",
                [SqlioParm.Text(c.Co), SqlioParm.Text(ra.JournalNo), SqlioParm.Text(ArEdit.Int(ra.ReceiptId))], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
        }
    }

    /// <summary>1600: unidentified money stays in an owned exception queue.</summary>
    private async Task UnidentifiedQueueAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        var ex = new ExcpraisArea
        {
            Module = "AR",
            Severity = "WARNING",
            Category = "UNIDENTIFIED-CASH",
            MessageText = "Unidentified receipt of GBP " + ArEdit.MoneyUk(ra.GrossGbp) + " awaiting identification",
            DocumentType = "RECEIPT",
            DocumentRef = ra.ReceiptNo,
            OwnerRole = "ARCLERK",
            Priority = 2,
            AmountGbp = ra.GrossGbp,
        };
        ex.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ra.PostingDate, ""), "RAISE");
        await excprais.ExecuteAsync(ex, ct);
        ra.ExceptionId = (int)ex.ExceptionId;
    }

    private async Task IdempotentEndAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        var id = new IdempotArea
        {
            Operation = "ar.Receipt.Post",
            Status = "COMPLETED",
            ResultRef = ra.ReceiptNo,
            ResultMessage = "Customer receipt posted",
        };
        id.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ra.PostingDate, c.Correl), "END");
        await idempot.ExecuteAsync(id, ct);
    }

    // ------------------------------------------------------------------ 1800 CONFIRM

    private async Task ConfirmReceiptAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        await PermissionAsync(w, ct);
        if (c.RcBad) return;
        await ReadReceiptAsync(w, ct);
        if (c.RcBad) return;
        if (CobolText.EqualsPadded(w.Src.Confirm, "CONFIRMED"))
        {
            c.Rc = 4;
            c.Reason = "RCP0010";
            c.Msg = "Receipt " + CobolText.Trim(ra.ReceiptNo) + " is already confirmed";
            c.MsgCnt++;
            return;
        }
        await PeriodAsync(w, ct);
        if (c.RcBad) return;
        await BeginAsync(w, ct);
        try
        {
            await sql.ExecAsync(
                "UPDATE ar.Receipt SET ConfirmationState = 'CONFIRMED', ModifiedBy = ?, ModifiedAt = SYSDATETIME() WHERE ReceiptId = ?",
                [SqlioParm.Text(c.UserId), SqlioParm.Text(ArEdit.Int(ra.ReceiptId))], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            await RollbackAsync(w, ct);
            return;
        }
        ra.ConfirmState = "CONFIRMED";
        await OpenItemAsync(w, ct);
        if (c.RcBad)
        {
            await RollbackAsync(w, ct);
            return;
        }
        await PostCashAsync(w, ct);
        if (c.RcBad)
        {
            await RollbackAsync(w, ct);
            return;
        }
        await CommitAsync(w, ct);
        c.Msg = "Receipt " + CobolText.Trim(ra.ReceiptNo) + " confirmed and posted to cash";
    }

    private async Task ReadReceiptAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        var id = ra.ReceiptId == 0 ? "" : ArEdit.Int(ra.ReceiptId);
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT CONVERT(varchar(24), r.ReceiptId), ISNULL(r.CustomerCode,''), r.CurrencyCode, CONVERT(varchar(24), r.FxRate), " +
                "CONVERT(varchar(24), r.UnallocatedAmount), CONVERT(varchar(24), r.GrossReceivedGBP), r.PaymentMethod, r.ReceiptNumber, " +
                "r.ConfirmationState, r.BankAccountCode FROM ar.Receipt r WHERE r.CompanyCode = ? AND (r.ReceiptNumber = ? OR " +
                "r.ReceiptId = CASE WHEN ? = '' THEN -1 ELSE CONVERT(bigint, ?) END)",
                [SqlioParm.Text(c.Co), SqlioParm.Text(ra.ReceiptNo), SqlioParm.Text(id), SqlioParm.Text(id)],
                [24, 20, 3, 24, 24, 24, 12, 30, 12, 12], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (!row.Found)
        {
            c.Rc = 16;
            c.Reason = "RCP0011";
            c.Msg = "That receipt is not held for this company";
            return;
        }
        w.Src.Fill(row, target: false);
        ra.ReceiptId = (int)CobolNumeric.Truncated(row.NumVal(0), 0);
        ra.ReceiptNo = row.Text(7);
        ra.Customer = row.Text(1);
        ra.Currency = row.Text(2);
        ra.FxRate = CobolNumeric.Store(row.NumVal(3), 9, 8);
        ra.Gross = CobolNumeric.Store(row.NumVal(4), 11, 2);
        ra.GrossGbp = CobolNumeric.Store(row.NumVal(5), 11, 2);
        ra.Method = CobolText.Trimmed(row.Raw(6), 10);
        ra.BankAccount = CobolText.Trimmed(row.Raw(9), 20);
    }

    // ------------------------------------------------------------------ 2000 PREVIEW / ALLOCATE

    private async Task AllocateAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        await PermissionAsync(w, ct);
        if (c.RcStop) return;
        await ReadSourceAsync(w, ct);
        if (c.RcStop) return;
        if (!w.Preview)
        {
            await PeriodAsync(w, ct);
            if (c.RcStop) return;
            await BeginAsync(w, ct);
        }
        w.SrcLeft = ra.SourceAvail;
        for (var i = 0; i < ra.AllocCnt && i < RcptalocArea.MaxAllocs && !c.RcStop; i++)
        {
            await OneAllocationAsync(w, i, ct);
        }
        if (c.RcStop)
        {
            await RollbackAsync(w, ct);
            return;
        }
        ra.Residual = w.SrcLeft;
        if (w.Preview)
        {
            c.Msg = "Preview only: " + ArEdit.MoneyUk(ra.TotApplied) + " would be applied leaving " + ArEdit.Money(w.SrcLeft) + " unapplied";
            return;
        }
        await PostAdjustmentsAsync(w, ct);
        if (c.RcStop)
        {
            await RollbackAsync(w, ct);
            return;
        }
        await UpdateSourceAsync(w, ct);
        if (c.RcStop)
        {
            await RollbackAsync(w, ct);
            return;
        }
        await CommitAsync(w, ct);
        c.Msg = "Allocated " + ArEdit.MoneyUk(ra.TotApplied) + " from " + CobolText.Trim(ra.SourceDoc) + "; unapplied residual " +
                ArEdit.Money(w.SrcLeft);
    }

    /// <summary>2050: only posted, confirmed, usable money is an eligible source.</summary>
    private async Task ReadSourceAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        if (ra.SourceItemId == 0 && CobolText.IsSpaces(ra.SourceDoc))
        {
            // CR2616 - PREVIEW alone may value the keyed money for the keyed customer as its source
            if (w.Preview && ra.Gross > 0 && !CobolText.IsSpaces(ra.Customer))
            {
                w.Src = new Row { Customer = ra.Customer, Currency = ra.Currency, Confirm = ra.ConfirmState };
                w.SrcRate = ra.Currency == "GBP" ? 1m : ra.FxRate;
                ra.SourceAvail = ra.Gross;
                ra.SourceType = "RECEIPT";
                ra.SourceDoc = "NEW RECEIPT";
                CheckSource(w);
                return;
            }
            c.Rc = 16;
            c.Reason = "RCP0023";
            c.Msg = "Name the receipt or credit the money is to come from before it is applied";
            return;
        }
        var id = ArEdit.Int(ra.SourceItemId);
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT CONVERT(varchar(24), o.OpenItemId), o.CustomerCode, o.CurrencyCode, CONVERT(varchar(24), o.FxRate), " +
                "CONVERT(varchar(24), -o.ResidualAmount), CONVERT(varchar(24), -o.ResidualAmountGBP), o.ItemType, o.DocumentNumber, " +
                "ISNULL((SELECT r.ConfirmationState FROM ar.Receipt r WHERE r.ReceiptId = o.SourceReceiptId),'CONFIRMED'), " +
                "ISNULL((SELECT k.ArControlAccount FROM ar.Customer k WHERE k.CompanyCode = o.CompanyCode AND k.CustomerCode = o.CustomerCode),'') " +
                "FROM ar.OpenItem o WHERE o.CompanyCode = ? AND o.OpenItemId = CASE WHEN ? = '0' THEN o.OpenItemId ELSE CONVERT(bigint, ?) END " +
                "AND (? = '' OR o.DocumentNumber = ?) AND o.SettlementStatus <> 'REVERSED'",
                [SqlioParm.Text(c.Co), SqlioParm.Text(id), SqlioParm.Text(id), SqlioParm.Text(ra.SourceDoc), SqlioParm.Text(ra.SourceDoc)],
                [24, 20, 3, 24, 24, 24, 12, 30, 12, 12], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (!row.Found)
        {
            c.Rc = 16;
            c.Reason = "RCP0020";
            c.Msg = "The source receipt or credit is not held";
            return;
        }
        w.Src.Fill(row, target: false);
        ra.SourceItemId = (int)CobolNumeric.Truncated(row.NumVal(0), 0);
        ra.SourceDoc = row.Text(7);
        ra.Customer = row.Text(1);
        ra.Currency = row.Text(2);
        w.SrcRate = CobolNumeric.Store(row.NumVal(3), 9, 8);
        ra.SourceAvail = CobolNumeric.Store(row.NumVal(4), 11, 2);
        ra.SourceType = CobolText.Trimmed(row.Raw(6), 10);
        CheckSource(w);
    }

    /// <summary>2050-CHECK.</summary>
    private static void CheckSource(Ws w)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        if (!CobolText.EqualsPadded(w.Src.Confirm, "CONFIRMED"))
        {
            c.Rc = 8;
            c.Reason = "RCP0021";
            c.Msg = "Unconfirmed evidence is not funds and cannot settle an invoice";
            return;
        }
        if (ra.SourceAvail <= 0)
        {
            c.Rc = 8;
            c.Reason = "RCP0022";
            c.Msg = "There is nothing left to allocate from " + CobolText.Trim(ra.SourceDoc);
        }
    }

    private async Task OneAllocationAsync(Ws w, int i, CancellationToken ct)
    {
        var c = w.Ctl;
        var a = w.Ra.Allocs[i];
        a.AMessage = "";
        a.AFxRealised = 0m;
        a.AAllocId = 0;
        await ReadTargetAsync(w, a, ct);
        if (c.RcStop) return;
        CheckSameDebtor(w, a);
        if (c.RcStop) return;
        CrossCurrency(w, a);
        if (c.RcStop) return;
        CheckDiscount(w, a);
        if (c.RcStop) return;
        await CheckWriteoffAsync(w, a, ct);
        if (c.RcStop) return;
        CheckLimits(w, a);
        if (c.RcStop) return;
        Classify(w, a);
        if (w.Preview) return;
        await WriteAllocationAsync(w, a, ct);
        if (c.RcStop) return;
        await UpdateTargetAsync(w, a, ct);
    }

    private void LineRefusal(Ws w, RcptalocAlloc a, int rc, string reason, string message)
    {
        var c = w.Ctl;
        c.Rc = rc;
        c.Reason = reason;
        c.Msg = message;
        a.AMessage = CobolText.Trimmed(c.Msg, 80);
        c.MsgCnt++;
    }

    private async Task ReadTargetAsync(Ws w, RcptalocAlloc a, CancellationToken ct)
    {
        var c = w.Ctl;
        var id = ArEdit.Int(a.AItemId);
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT CONVERT(varchar(24), o.OpenItemId), o.CustomerCode, o.CurrencyCode, CONVERT(varchar(24), o.FxRate), " +
                "CONVERT(varchar(24), o.ResidualAmount), CONVERT(varchar(24), o.ResidualAmountGBP), o.ItemType, o.DocumentNumber, " +
                "ISNULL(CONVERT(char(8), o.DiscountDueDate, 112),''), CONVERT(varchar(24), ISNULL(o.DiscountAmount,0)), " +
                "CONVERT(char(8), o.DueDate, 112), CONVERT(char(1), o.IsDisputed) FROM ar.OpenItem o WHERE o.CompanyCode = ? " +
                "AND (o.OpenItemId = CASE WHEN ? = '0' THEN o.OpenItemId ELSE CONVERT(bigint, ?) END) " +
                "AND (? = '' OR o.DocumentNumber = ?) AND o.SettlementStatus <> 'REVERSED'",
                [SqlioParm.Text(c.Co), SqlioParm.Text(id), SqlioParm.Text(id), SqlioParm.Text(a.ADocNo), SqlioParm.Text(a.ADocNo)],
                [24, 20, 3, 24, 24, 24, 12, 30, 10, 24, 10, 4], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (!row.Found)
        {
            LineRefusal(w, a, 16, "RCP0023", "Target open item " + CobolText.Trim(a.ADocNo) + " is not held");
            return;
        }
        w.Tgt.Fill(row, target: true);
        a.AItemId = (int)CobolNumeric.Truncated(row.NumVal(0), 0);
        a.ADocNo = row.Text(7);
        a.ACurrency = row.Text(2);
        w.TgtRate = CobolNumeric.Store(row.NumVal(3), 9, 8);
        a.AResidBefore = CobolNumeric.Store(row.NumVal(4), 11, 2);
    }

    /// <summary>2200: no automatic offset against another customer's balance.</summary>
    private void CheckSameDebtor(Ws w, RcptalocAlloc a)
    {
        if (CobolText.EqualsPadded(w.Tgt.Customer, w.Src.Customer)) return;
        LineRefusal(w, a, 8, "RCP0024", "Source " + CobolText.Trim(w.Ra.SourceDoc) + " belongs to " + CobolText.Trim(w.Src.Customer) +
            " and cannot settle a debt of " + CobolText.Trim(w.Tgt.Customer));
    }

    /// <summary>2250: AR-019 - cross currency allocation needs an explicit conversion instruction.</summary>
    private void CrossCurrency(Ws w, RcptalocAlloc a)
    {
        w.CrossCcy = false;
        if (CobolText.EqualsPadded(a.ACurrency, w.Ra.Currency)) return;
        if (w.Ra.CrossCcyRate <= 0)
        {
            LineRefusal(w, a, 8, "RCP0025", "Allocating " + CobolText.Trim(w.Ra.Currency) + " money to a " + CobolText.Trim(a.ACurrency) +
                " item needs an explicit conversion instruction");
            return;
        }
        w.CrossCcy = true;
    }

    /// <summary>2300: AR-012 - a settlement discount is only valid inside the stated terms plus the approved grace (D-22).</summary>
    private void CheckDiscount(Ws w, RcptalocAlloc a)
    {
        if (a.ADiscount == 0) return;
        if (CobolText.IsSpaces(w.Tgt.DiscDate))
        {
            LineRefusal(w, a, 8, "RCP0026", "No prompt payment discount was offered on " + CobolText.Trim(a.ADocNo));
            return;
        }
        var disc = CobolText.Fit(w.Tgt.DiscDate, 10);
        var numDate = 0;
        foreach (var ch in disc[..8]) numDate = numDate * 10 + (char.IsAsciiDigit(ch) ? ch - '0' : 0);
        var days = (int)CobolNumeric.Store(w.Ra.EffectiveDate - numDate, 7, 0);
        if (days > w.Ra.DiscGraceDays)
        {
            LineRefusal(w, a, 8, "RCP0027", "The discount period on " + CobolText.Trim(a.ADocNo) + " expired on " +
                disc[6..8] + "/" + disc[4..6] + "/" + disc[..4]);
        }
    }

    /// <summary>2350: D-22 - up to the auto limit per receipt may be written off by the clerk with a reason.</summary>
    private async Task CheckWriteoffAsync(Ws w, RcptalocAlloc a, CancellationToken ct)
    {
        var c = w.Ctl;
        if (a.AWriteoff == 0) return;
        if (CobolText.IsSpaces(a.AWoReason))
        {
            LineRefusal(w, a, 8, "RCP0028", "A write off needs a reason; a short payment is not silently written off");
            return;
        }
        if (a.AWriteoff > w.Ra.WriteoffLimit)
        {
            var pk = new PermchkArea { Permission = "AR.WRITEOFF.APPROVE" };
            pk.Header.Prepare(new KernelCaller(c.Co, c.Site, c.UserId, w.Ra.PostingDate, ""), "CHECK");
            await permchk.ExecuteAsync(pk, ct);
            if (pk.Header.ReturnCode > 4)
            {
                LineRefusal(w, a, 6, "RCP0029", "A write off above GBP " + ArEdit.MoneyUk(w.Ra.WriteoffLimit) +
                    " needs an authorised approval (D-22)");
            }
        }
    }

    /// <summary>2400: neither side of an allocation may be exceeded.</summary>
    private void CheckLimits(Ws w, RcptalocAlloc a)
    {
        if (a.AApply <= 0)
        {
            LineRefusal(w, a, 8, "RCP0030", "Each allocation needs a positive applied amount");
            return;
        }
        if (a.AApply > w.SrcLeft)
        {
            LineRefusal(w, a, 8, "RCP0031", "The allocation exceeds the available amount of " + ArEdit.MoneyUk(w.SrcLeft) + " on " +
                CobolText.Trim(w.Ra.SourceDoc));
            return;
        }
        var amt1 = a.AApply + a.ADiscount + a.AWriteoff;
        if (amt1 > a.AResidBefore)
        {
            LineRefusal(w, a, 8, "RCP0032", "Over allocation: " + CobolText.Trim(a.ADocNo) + " has only " +
                ArEdit.MoneyUk(a.AResidBefore) + " outstanding");
        }
    }

    /// <summary>2450: classify, value in sterling and work out the realised FX.</summary>
    private static void Classify(Ws w, RcptalocAlloc a)
    {
        var ra = w.Ra;
        var amt1 = CobolNumeric.Store(a.AApply + a.ADiscount + a.AWriteoff, 13, 2);
        a.AResidAfter = CobolNumeric.Store(a.AResidBefore - amt1, 11, 2);
        a.AKind = a.AWriteoff != 0 ? "WRITEOFF"
            : a.ADiscount != 0 ? "DISCOUNT"
            : w.CrossCcy ? "CROSSCCY"
            : a.AResidAfter == 0 ? "EXACT"
            : "PARTIAL";
        if (w.SrcRate > 0)
        {
            w.ApplyGbp = CobolNumeric.Store(a.AApply / w.SrcRate, 11, 2, rounded: true);
            w.DiscGbp = CobolNumeric.Store(a.ADiscount / w.SrcRate, 11, 2, rounded: true);
            w.WoGbp = CobolNumeric.Store(a.AWriteoff / w.SrcRate, 11, 2, rounded: true);
        }
        else
        {
            w.ApplyGbp = a.AApply;
            w.DiscGbp = a.ADiscount;
            w.WoGbp = a.AWriteoff;
        }
        // realised exchange difference on a foreign invoice
        if (w.TgtRate > 0 && w.SrcRate > 0 && w.TgtRate != w.SrcRate)
        {
            var amt2 = CobolNumeric.Store(a.AApply / w.TgtRate, 13, 2, rounded: true);
            a.AFxRealised = CobolNumeric.Store(amt2 - w.ApplyGbp, 11, 2);
        }
        w.SrcLeft = CobolNumeric.Store(w.SrcLeft - a.AApply, 11, 2);
        ra.TotApplied = CobolNumeric.Store(ra.TotApplied + a.AApply, 11, 2);
        ra.TotDiscount = CobolNumeric.Store(ra.TotDiscount + a.ADiscount, 11, 2);
        ra.TotWriteoff = CobolNumeric.Store(ra.TotWriteoff + a.AWriteoff, 11, 2);
        ra.TotFx = CobolNumeric.Store(ra.TotFx + a.AFxRealised, 11, 2);
    }

    private async Task WriteAllocationAsync(Ws w, RcptalocAlloc a, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SET NOCOUNT ON; INSERT ar.Allocation (CompanyCode, CustomerCode, SourceType, SourceOpenItemId, TargetOpenItemId, " +
                "EffectiveDate, CurrencyCode, AppliedAmount, AppliedAmountGBP, DiscountAmountGBP, WriteOffAmountGBP, WriteOffReason, " +
                "RealisedFxGBP, IsCrossCurrency, ConversionRate, Status, CreatedBy) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, " +
                "NULLIF(?,''), ?, ?, NULLIF(CONVERT(decimal(18,8),?),0), 'ACTIVE', ?); SELECT CONVERT(varchar(24), SCOPE_IDENTITY()), ''",
                [SqlioParm.Text(c.Co), SqlioParm.Text(ra.Customer), SqlioParm.Text(ra.SourceType), SqlioParm.Text(ArEdit.Int(ra.SourceItemId)),
                 SqlioParm.Text(ArEdit.Int(a.AItemId)), SqlioParm.Digits(ra.EffectiveDate, 8), SqlioParm.Text(ra.Currency),
                 SqlioParm.Text(ArEdit.Money(a.AApply)), SqlioParm.Text(ArEdit.Money(w.ApplyGbp)), SqlioParm.Text(ArEdit.Money(w.DiscGbp)),
                 SqlioParm.Text(ArEdit.Money(w.WoGbp)), SqlioParm.Text(a.AWoReason), SqlioParm.Text(ArEdit.Money(a.AFxRealised)),
                 SqlioParm.Text(w.CrossCcy ? "1" : "0"), SqlioParm.Text(ArEdit.Rate(ra.CrossCcyRate)), SqlioParm.Text(c.UserId)],
                [24, 24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        a.AAllocId = (int)CobolNumeric.Truncated(row.NumVal(0), 0);
    }

    private async Task UpdateTargetAsync(Ws w, RcptalocAlloc a, CancellationToken ct)
    {
        var c = w.Ctl;
        var amt1 = ArEdit.Money(CobolNumeric.Store(a.AApply + a.ADiscount + a.AWriteoff, 13, 2));
        var amt2 = ArEdit.Money(CobolNumeric.Store(w.ApplyGbp + w.DiscGbp + w.WoGbp, 13, 2));
        try
        {
            await sql.ExecAsync(
                "UPDATE ar.OpenItem SET ResidualAmount = ResidualAmount - ?, ResidualAmountGBP = ResidualAmountGBP - ?, " +
                "SettlementStatus = CASE WHEN ResidualAmount - ? = 0 THEN 'SETTLED' ELSE 'PARTIAL' END, WrittenOff = CASE WHEN " +
                "CONVERT(decimal(18,2), ?) > 0 THEN 1 ELSE WrittenOff END, ModifiedBy = ?, ModifiedAt = SYSDATETIME() WHERE OpenItemId = ?",
                [SqlioParm.Text(amt1), SqlioParm.Text(amt2), SqlioParm.Text(amt1), SqlioParm.Text(ArEdit.Money(w.WoGbp)),
                 SqlioParm.Text(c.UserId), SqlioParm.Text(ArEdit.Int(a.AItemId))], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
        }
    }

    /// <summary>2600: discounts, write offs and realised FX are the only parts of an allocation that touch the nominal.</summary>
    private async Task PostAdjustmentsAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        if (ra.TotDiscount == 0 && ra.TotWriteoff == 0 && ra.TotFx == 0) return;
        var stem = CobolText.Fit(c.Correl, 36)[..30];
        var pg = new ArpostglArea
        {
            JournalType = "AR",
            SourceModule = "AR",
            SourceRef = CobolText.Trimmed(ra.SourceDoc, 40),
            DocDate = ra.EffectiveDate,
            PostingDate = ra.PostingDate,
            Currency = "GBP",
            FxRate = 1m,
            OwnTran = "N",
            Description = CobolText.Trimmed("Allocation adjustments for " + CobolText.Trim(ra.SourceDoc), 200),
        };
        pg.Header.Function = "POST";
        pg.Header.Company = c.Co;
        pg.Header.User = c.UserId;
        pg.Header.CorrelationId = CobolText.Trimmed(CobolText.Trim(stem) + "-ALLOC", 36);
        pg.Header.BusinessDate = ra.PostingDate;
        var leg = 0;
        if (ra.TotDiscount != 0)
        {
            pg.EventCode = "AR.DISCOUNT";
            leg++;
            pg.Legs[leg - 1].GLegCode = "DISCOUNT";
            pg.Legs[leg - 1].GDebit = ra.TotDiscount;
            pg.Legs[leg - 1].GNarrative = "Settlement discount allowed";
            leg++;
            pg.Legs[leg - 1].GLegCode = "DEBTOR";
            pg.Legs[leg - 1].GCredit = ra.TotDiscount;
            pg.Legs[leg - 1].GSubledger = "AR";
            pg.Legs[leg - 1].GSubref = ra.Customer;
        }
        if (ra.TotWriteoff != 0)
        {
            pg.EventCode = "AR.WRITEOFF";
            leg++;
            pg.Legs[leg - 1].GLegCode = "BADDEBT";
            pg.Legs[leg - 1].GDebit = ra.TotWriteoff;
            pg.Legs[leg - 1].GNarrative = "Receivable written off";
            leg++;
            pg.Legs[leg - 1].GLegCode = "DEBTOR";
            pg.Legs[leg - 1].GCredit = ra.TotWriteoff;
            pg.Legs[leg - 1].GSubledger = "AR";
            pg.Legs[leg - 1].GSubref = ra.Customer;
        }
        if (ra.TotFx != 0)
        {
            leg += 2;
            if (ra.TotFx > 0)
            {
                pg.EventCode = "AR.FXGAIN";
                leg--;
                pg.Legs[leg - 1].GLegCode = "DEBTOR";
                pg.Legs[leg - 1].GDebit = ra.TotFx;
                pg.Legs[leg - 1].GSubledger = "AR";
                pg.Legs[leg - 1].GSubref = ra.Customer;
                leg++;
                pg.Legs[leg - 1].GLegCode = "FX";
                pg.Legs[leg - 1].GCredit = ra.TotFx;
                pg.Legs[leg - 1].GNarrative = "Realised exchange gain";
            }
            else
            {
                pg.EventCode = "AR.FXLOSS";
                leg--;
                var amt1 = CobolNumeric.Store(0 - ra.TotFx, 13, 2);
                pg.Legs[leg - 1].GLegCode = "FX";
                pg.Legs[leg - 1].GDebit = CobolNumeric.Store(amt1, 11, 2);
                pg.Legs[leg - 1].GNarrative = "Realised exchange loss";
                leg++;
                pg.Legs[leg - 1].GLegCode = "DEBTOR";
                pg.Legs[leg - 1].GCredit = CobolNumeric.Store(amt1, 11, 2);
                pg.Legs[leg - 1].GSubledger = "AR";
                pg.Legs[leg - 1].GSubref = ra.Customer;
            }
        }
        pg.LegCnt = leg;
        await arpostgl.CallAsync(pg, ct);
        if (pg.Header.ReturnCode > 4)
        {
            c.Rc = pg.Header.ReturnCode;
            c.Reason = pg.Header.ReasonCode;
            c.Msg = pg.Header.Message;
            c.MsgCnt++;
            return;
        }
        ra.JournalNo = CobolText.Trimmed(pg.JournalNo, 30);
    }

    private async Task UpdateSourceAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        var applied = ArEdit.Money(ra.TotApplied);
        var amt1 = w.SrcRate > 0 ? CobolNumeric.Store(ra.TotApplied / w.SrcRate, 13, 2, rounded: true) : ra.TotApplied;
        var srcId = ArEdit.Int(ra.SourceItemId);
        try
        {
            await sql.ExecAsync(
                "UPDATE ar.OpenItem SET ResidualAmount = ResidualAmount + ?, ResidualAmountGBP = ResidualAmountGBP + ?, " +
                "SettlementStatus = CASE WHEN ResidualAmount + ? = 0 THEN 'SETTLED' ELSE 'PARTIAL' END, ModifiedBy = ?, " +
                "ModifiedAt = SYSDATETIME() WHERE OpenItemId = ?; UPDATE ar.Receipt SET AllocatedAmount = AllocatedAmount + ?, " +
                "UnallocatedAmount = UnallocatedAmount - ? WHERE CompanyCode = ? AND ReceiptId = (SELECT o.SourceReceiptId FROM " +
                "ar.OpenItem o WHERE o.OpenItemId = ?)",
                [SqlioParm.Text(applied), SqlioParm.Text(ArEdit.Money(amt1)), SqlioParm.Text(applied), SqlioParm.Text(c.UserId),
                 SqlioParm.Text(srcId), SqlioParm.Text(applied), SqlioParm.Text(applied), SqlioParm.Text(c.Co), SqlioParm.Text(srcId)], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        ra.Unallocated = w.SrcLeft;
    }

    // ------------------------------------------------------------------ 3000 UNALLOC

    private async Task UnallocateAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        await PermissionAsync(w, ct);
        if (c.RcStop) return;
        if (ra.AllocId == 0)
        {
            c.Rc = 8;
            c.Reason = "RCP0040";
            c.Msg = "An allocation reference is required";
            return;
        }
        if (CobolText.IsSpaces(ra.CorrectReason))
        {
            c.Rc = 8;
            c.Reason = "RCP0041";
            c.Msg = "An allocation correction needs a reason";
            return;
        }
        if (ra.CorrectDate == 0) ra.CorrectDate = c.BusDate;
        var (targetId, appliedGbp) = await ReadAllocationAsync(w, ct);
        if (c.RcBad) return;
        await BeginAsync(w, ct);
        await ReverseAllocationAsync(w, targetId, appliedGbp, ct);
        if (c.RcBad)
        {
            await RollbackAsync(w, ct);
            return;
        }
        await CommitAsync(w, ct);
        ra.ReversedCnt = 1;
        c.Msg = "Allocation reversed; " + ArEdit.MoneyUk(ra.TotApplied) + " is unapplied again. The receipt itself is untouched.";
    }

    private async Task<(long TargetId, decimal AppliedGbp)> ReadAllocationAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT CONVERT(varchar(24), a.SourceOpenItemId), a.CustomerCode, a.CurrencyCode, CONVERT(varchar(24), a.TargetOpenItemId), " +
                "CONVERT(varchar(24), a.AppliedAmount), CONVERT(varchar(24), a.AppliedAmountGBP), a.Status, '', '', '' FROM ar.Allocation a " +
                "WHERE a.AllocationId = ? AND a.CompanyCode = ?",
                [SqlioParm.Text(ArEdit.Int(ra.AllocId)), SqlioParm.Text(c.Co)],
                [24, 20, 3, 24, 24, 24, 12, 30, 12, 12], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return (0, 0m);
        }
        if (!row.Found)
        {
            c.Rc = 16;
            c.Reason = "RCP0042";
            c.Msg = "That allocation is not held";
            return (0, 0m);
        }
        w.Src.Fill(row, target: false);
        if (CobolText.EqualsPadded(row.Raw(6), "REVERSED"))
        {
            c.Rc = 8;
            c.Reason = "RCP0043";
            c.Msg = "That allocation has already been reversed";
            return (0, 0m);
        }
        ra.SourceItemId = (int)CobolNumeric.Truncated(row.NumVal(0), 0);
        ra.Customer = row.Text(1);
        ra.Currency = row.Text(2);
        var target = (long)CobolNumeric.Store(row.NumVal(3), 9, 0);
        ra.TotApplied = CobolNumeric.Store(row.NumVal(4), 11, 2);
        var appliedGbp = CobolNumeric.Store(row.NumVal(5), 13, 2);
        return (target, appliedGbp);
    }

    /// <summary>3200: the original row stays in history as REVERSED with a linked contra row carrying the correction date.</summary>
    private async Task ReverseAllocationAsync(Ws w, long targetId, decimal appliedGbp, CancellationToken ct)
    {
        var c = w.Ctl;
        var ra = w.Ra;
        var src = SqlioParm.Text(ArEdit.Int(ra.SourceItemId));
        var tgt = SqlioParm.Text(ArEdit.Int(targetId));
        var alloc = SqlioParm.Text(ArEdit.Int(ra.AllocId));
        var user = SqlioParm.Text(c.UserId);
        var applied = SqlioParm.Text(ArEdit.Money(ra.TotApplied));
        var gbp = SqlioParm.Text(ArEdit.Money(appliedGbp));
        try
        {
            await sql.SingleAsync(
                "SET NOCOUNT ON; INSERT ar.Allocation (CompanyCode, CustomerCode, SourceType, SourceOpenItemId, TargetOpenItemId, " +
                "EffectiveDate, CurrencyCode, AppliedAmount, AppliedAmountGBP, WriteOffReason, Status, ReversalOfAllocationId, " +
                "ReversedAt, ReversedBy, CreatedBy) VALUES (?, ?, 'ADJUST', ?, ?, ?, ?, ?, ?, ?, 'REVERSED', ?, SYSDATETIME(), ?, ?); " +
                "UPDATE ar.Allocation SET Status = 'REVERSED', ReversedAt = SYSDATETIME(), ReversedBy = ? WHERE AllocationId = ?; " +
                "UPDATE ar.OpenItem SET ResidualAmount = ResidualAmount + ?, ResidualAmountGBP = ResidualAmountGBP + ?, " +
                "SettlementStatus = CASE WHEN ABS(ResidualAmount + ?) >= ABS(OriginalAmount) THEN 'OPEN' ELSE 'PARTIAL' END, " +
                "ModifiedBy = ?, ModifiedAt = SYSDATETIME() WHERE OpenItemId = ?; " +
                "UPDATE ar.OpenItem SET ResidualAmount = ResidualAmount - ?, ResidualAmountGBP = ResidualAmountGBP - ?, " +
                "SettlementStatus = CASE WHEN ABS(ResidualAmount - ?) >= ABS(OriginalAmount) THEN 'OPEN' ELSE 'PARTIAL' END, " +
                "ModifiedBy = ?, ModifiedAt = SYSDATETIME() WHERE OpenItemId = ?; SELECT CONVERT(varchar(24), ?), ''",
                [SqlioParm.Text(c.Co), SqlioParm.Text(ra.Customer), src, tgt, SqlioParm.Digits(ra.CorrectDate, 8),
                 SqlioParm.Text(ra.Currency), SqlioParm.Text(ArEdit.Money(CobolNumeric.Store(0 - ra.TotApplied, 13, 2))),
                 SqlioParm.Text(ArEdit.Money(CobolNumeric.Store(0 - appliedGbp, 13, 2))), SqlioParm.Text(ra.CorrectReason), alloc,
                 user, user, user, alloc, applied, gbp, applied, user, tgt, applied, gbp, applied, user, src, alloc],
                [24, 24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
        }
    }

    // ------------------------------------------------------------------ unit of work

    private async Task BeginAsync(Ws w, CancellationToken ct)
    {
        // SQLIO BEGN: result not inspected
        try
        {
            await sql.UnitOfWork.BeginAsync(ct);
        }
        catch (SqlioException)
        {
        }
        w.TranOpen = true;
    }

    private async Task CommitAsync(Ws w, CancellationToken ct)
    {
        // SQLIO COMT: result not inspected
        try
        {
            await sql.UnitOfWork.CommitAsync(ct);
        }
        catch (SqlioException)
        {
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
        }
        w.TranOpen = false;
    }
}
