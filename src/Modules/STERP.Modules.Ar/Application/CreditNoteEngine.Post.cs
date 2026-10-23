using Sterp.Modules.Ar.Contracts;
using Sterp.Modules.Ar.Domain;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Application;

/// <summary>ARCRNOTE posting half: 1000-POST-CREDIT with its sections 1050-1950.</summary>
public sealed partial class CreditNoteEngine
{
    /// <summary>
    /// 1000-POST-CREDIT. Only RC 08-16 stops after the permission and period checks (an RC 06 ARC0040/ARC0041 or a
    /// period hold continues and the credit is posted, BR-ARCRNOTE-025); after the idempotency claim an RC 04 also stops
    /// (a replay ARC0043, or the ARC0004 goodwill warning raised by the validation).
    /// </summary>
    private async Task PostCreditAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var an = w.An;

        await PermissionAsync(w, ct);
        if (c.RcBad) return;
        await PeriodAsync(w, ct);
        if (c.RcBad) return;
        await IdempotentBeginAsync(w, ct);
        if (c.RcBad || c.RcWarn) return;
        await DrawNumberAsync(w, ct);
        if (c.RcBad) return;

        await BeginAsync(w, ct);
        foreach (var step in new Func<Ws, CancellationToken, Task>[]
                 {
                     WriteHeaderAsync, WriteLinesAsync, PostJournalAsync, WriteVatRecordsAsync, WriteOpenItemAsync, MarkPostedAsync,
                 })
        {
            await step(w, ct);
            if (c.RcBad)
            {
                await RollbackAsync(w, ct);
                return;
            }
        }
        await CommitAsync(w, ct);

        // 1950-IDEMPOTENT-END (result not inspected)
        var end = new IdempotArea
        {
            Operation = "ar.SalesCredit.Post",
            Status = "COMPLETED",
            ResultRef = an.CreditNo,
            ResultMessage = "Sales credit note posted",
        };
        end.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, an.PostingDate, c.Correl), "END");
        await idempot.ExecuteAsync(end, ct);
        c.Msg = "Credit note " + an.CreditNo.Trim(' ') + " posted, " + an.Currency.Trim(' ') + " " + ArEdit.MoneyUk(an.Gross) +
            "; the original invoice is unchanged";
    }

    /// <summary>1050-PERMISSION: AR.CREDITNOTE.ENTER, then the independent approval above GBP 1,000.00 (both RC 06).</summary>
    private async Task PermissionAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var an = w.An;
        var pk = new PermchkArea { Permission = "AR.CREDITNOTE.ENTER" };
        pk.Header.Prepare(new KernelCaller(c.Co, c.Site, c.UserId, an.PostingDate, ""), "CHECK");
        await permchk.ExecuteAsync(pk, ct);
        if (pk.Header.ReturnCode > 4)
        {
            c.Rc = 6;
            c.Reason = "ARC0040";
            c.Msg = pk.Header.Message;
            c.MsgCnt++;
            return;
        }
        if (an.GrossGbp > 1000 && an.ApprovalId == 0 && CobolText.IsSpaces(an.ApprovedBy))
        {
            c.Rc = 6;
            c.Reason = "ARC0041";
            c.Msg = "A credit note above GBP 1,000.00 needs an independent approval before posting";
            c.MsgCnt++;
        }
    }

    /// <summary>1100-PERIOD: PERIODCK POST on the posting date, document date and tax point.</summary>
    private async Task PeriodAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var an = w.An;
        var pc = new PeriodckArea { PostingDate = an.PostingDate, DocumentDate = an.DocDate, TaxPoint = an.TaxPoint, Action = "POST" };
        pc.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, an.PostingDate, ""), "CHECK");
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

    /// <summary>1150-IDEMPOTENT-BEGIN: ar.SalesCredit.Post on the correlation id (a replay returns the earlier credit number).</summary>
    private async Task IdempotentBeginAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var an = w.An;
        if (CobolText.IsSpaces(c.Correl))
        {
            c.Rc = 8;
            c.Reason = "ARC0042";
            c.Msg = "A correlation id is required to post a credit";
            return;
        }
        var id = new IdempotArea { Operation = "ar.SalesCredit.Post" };
        id.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, an.PostingDate, c.Correl), "BEGIN");
        await idempot.ExecuteAsync(id, ct);
        switch (id.Header.ReturnCode)
        {
            case 0:
                break;
            case 4:
                an.CreditNo = CobolText.Trimmed(id.ResultRef, 30);
                c.Rc = 4;
                c.Reason = "ARC0043";
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

    /// <summary>1200-DRAW-NUMBER: series SCRN unless the caller supplied the credit number.</summary>
    private async Task DrawNumberAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var an = w.An;
        if (!CobolText.IsSpaces(an.CreditNo)) return;
        var ng = new NumgenArea { SeriesCode = "SCRN" };
        ng.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, an.DocDate, c.Correl), "NEXT");
        await numgen.ExecuteAsync(ng, ct);
        if (ng.Header.ReturnCode > 4)
        {
            c.Rc = ng.Header.ReturnCode;
            c.Reason = ng.Header.ReasonCode;
            c.Msg = ng.Header.Message;
            c.MsgCnt++;
            return;
        }
        an.CreditNo = CobolText.Trimmed(ng.DocumentNumber, 30);
    }

    /// <summary>1300-WRITE-HEADER: ar.SalesInvoice CREDITNOTE as DRAFT.</summary>
    private async Task WriteHeaderAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var an = w.An;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SET NOCOUNT ON; INSERT ar.SalesInvoice (CompanyCode, DocumentType, InvoiceNumber, CustomerCode, DocumentDate, TaxPointDate, " +
                "PostingDate, DueDate, FiscalYear, PeriodNo, CurrencyCode, FxRate, NetAmount, VatAmount, GrossAmount, NetAmountGBP, VatAmountGBP, " +
                "GrossAmountGBP, BillToName, CustomerVatRegNo, OriginalInvoiceNumber, CreditReason, CreditableRemainingGBP, Status, ApprovedBy, " +
                "CorrelationId, CreatedBy) VALUES (?, 'CREDITNOTE', ?, ?, ?, ?, ?, ?, NULLIF(?,0), NULLIF(?,0), ?, ?, ?, ?, ?, ?, ?, ?, " +
                "NULLIF(?,''), NULLIF(?,''), NULLIF(?,''), ?, ?, 'DRAFT', NULLIF(?,''), ?, ?); SELECT CONVERT(varchar(24), SCOPE_IDENTITY()), ''",
                [
                    SqlioParm.Text(c.Co), SqlioParm.Text(an.CreditNo), SqlioParm.Text(an.Customer), SqlioParm.Digits(an.DocDate, 8),
                    SqlioParm.Digits(an.TaxPoint, 8), SqlioParm.Digits(an.PostingDate, 8), SqlioParm.Digits(an.DocDate, 8),
                    SqlioParm.Text(ArEdit.Int(w.FiscalYear)), SqlioParm.Text(ArEdit.Int(w.PeriodNo)), SqlioParm.Text(an.Currency),
                    SqlioParm.Text(ArEdit.Rate(an.FxRate)), SqlioParm.Text(ArEdit.Money(an.Net)), SqlioParm.Text(ArEdit.Money(an.Vat)),
                    SqlioParm.Text(ArEdit.Money(an.Gross)), SqlioParm.Text(ArEdit.Money(an.NetGbp)), SqlioParm.Text(ArEdit.Money(an.VatGbp)),
                    SqlioParm.Text(ArEdit.Money(an.GrossGbp)), SqlioParm.Text(w.OName), SqlioParm.Text(w.OVatNo), SqlioParm.Text(an.OrigInvoice),
                    SqlioParm.Text(an.Reason), SqlioParm.Text(ArEdit.Money(CobolNumeric.Store(an.CreditableRem - an.GrossGbp, 13, 2))),
                    SqlioParm.Text(an.ApprovedBy), SqlioParm.Text(c.Correl), SqlioParm.Text(c.UserId),
                ], [24, 24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        an.CreditId = (int)CobolNumeric.Store(row.NumVal(0), 9, 0);
    }

    /// <summary>1400-WRITE-LINES: a quantity of zero becomes one; the unit price is net / quantity rounded to pence.</summary>
    private async Task WriteLinesAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var an = w.An;
        for (var i = 1; i <= an.LineCnt && !c.RcBad; i++)
        {
            var l = an.Lines[i - 1];
            if (l.LQty == 0) l.LQty = 1;
            var price = CobolNumeric.Store(l.LNet / l.LQty, 13, 2, rounded: true);
            decimal amt1, amt2;
            if (an.Currency == "GBP" || an.FxRate == 0)
            {
                amt1 = l.LNet;
                amt2 = l.LVatAmt;
            }
            else
            {
                amt1 = CobolNumeric.Store(l.LNet / an.FxRate, 13, 2, rounded: true);
                amt2 = CobolNumeric.Store(l.LVatAmt / an.FxRate, 13, 2, rounded: true);
            }
            try
            {
                await sql.ExecAsync(
                    "INSERT ar.SalesInvoiceLine (InvoiceId, LineNum, ItemCode, Description, Quantity, UnitPrice, PricePerQty, NetAmount, VatCode, " +
                    "VatRatePct, VatAmount, NetAmountGBP, VatAmountGBP, RevenueAccountCode, SalesOrderLine) VALUES (?, ?, NULLIF(?,''), ?, ?, ?, 1, " +
                    "?, ?, ?, ?, ?, ?, NULLIF(?,''), NULLIF(?,0))",
                    [
                        SqlioParm.Text(ArEdit.Int(an.CreditId)), SqlioParm.Text(ArEdit.Int(i)), SqlioParm.Text(l.LItem), SqlioParm.Text(l.LDesc),
                        SqlioParm.Text(ArEdit.Qty(l.LQty)), SqlioParm.Text(ArEdit.Qty(price)), SqlioParm.Text(ArEdit.Money(l.LNet)),
                        SqlioParm.Text(l.LVatCode), SqlioParm.Text(ArEdit.Money(l.LVatRate)), SqlioParm.Text(ArEdit.Money(l.LVatAmt)),
                        SqlioParm.Text(ArEdit.Money(amt1)), SqlioParm.Text(ArEdit.Money(amt2)), SqlioParm.Text(l.LRevAccount),
                        SqlioParm.Text(ArEdit.Int(l.LOrigLine)),
                    ], ct);
            }
            catch (SqlioException ex)
            {
                c.SqlFailed(ex.DriverText);
            }
        }
    }

    /// <summary>1600-POST-JOURNAL: ARPOSTGL AR.CREDITNOTE - DR RETURNS per line, DR VAT, CR DEBTOR (document currency).</summary>
    private async Task PostJournalAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var an = w.An;
        var pg = new ArpostglArea
        {
            EventCode = "AR.CREDITNOTE",
            JournalType = "AR",
            SourceModule = "AR",
            SourceRef = CobolText.Trimmed(an.CreditNo, 40),
            DocDate = an.DocDate,
            PostingDate = an.PostingDate,
            Currency = an.Currency,
            FxRate = an.FxRate,
            OwnTran = "N",
            Description = CobolText.Trimmed("Credit note " + an.CreditNo.Trim(' ') + " against " + an.OrigInvoice.Trim(' '), 200),
        };
        var leg = 0;
        for (var i = 1; i <= an.LineCnt; i++)
        {
            var l = an.Lines[i - 1];
            var r = pg.Legs[leg++];
            r.GLegCode = "RETURNS";
            r.GDebit = l.LNet;
            r.GNarrative = CobolText.Trimmed(l.LDesc, 100);
            r.GVatCode = CobolText.Trimmed(l.LVatCode, 4);
        }
        if (an.Vat != 0)
        {
            var v = pg.Legs[leg++];
            v.GLegCode = "VAT";
            v.GDebit = an.Vat;
            v.GNarrative = CobolText.Trimmed("Output VAT credited on " + an.CreditNo.Trim(' '), 100);
        }
        var d = pg.Legs[leg++];
        d.GLegCode = "DEBTOR";
        d.GAccount = CobolText.Trimmed(w.OControl, 12);
        d.GCredit = an.Gross;
        d.GSubledger = "AR";
        d.GSubref = CobolText.Trimmed(an.Customer, 40);
        d.GNarrative = CobolText.Trimmed(an.Reason, 100);
        pg.LegCnt = leg;
        pg.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, an.PostingDate, c.Correl), "POST");
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
        an.JournalNo = CobolText.Trimmed(pg.JournalNo, 30);
    }

    /// <summary>
    /// 1700-WRITE-VAT-RECORDS: ARVATWRT WRITE per VAT code, negative, evidence = the original invoice. The analysis holds
    /// sterling, and the document-currency net/VAT are given the same sterling values (as coded).
    /// </summary>
    private async Task WriteVatRecordsAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var an = w.An;
        for (var i = 1; i <= w.Vats.Count && !c.RcBad; i++)
        {
            var v = w.Vats[i - 1];
            var net = CobolNumeric.Store(0 - v.Net, 11, 2);
            var vat = CobolNumeric.Store(0 - v.Vat, 11, 2);
            var vw = new ArvatwrtArea
            {
                SourceModule = "AR",
                DocType = "SALESCREDIT",
                DocRef = CobolText.Trimmed(an.CreditNo, 40),
                PartyType = "CUSTOMER",
                PartyCode = an.Customer,
                PartyVatNo = w.OVatNo,
                TaxPoint = an.TaxPoint,
                PostingDate = an.PostingDate,
                VatCode = v.Code,
                Direction = "OUTPUT",
                Currency = an.Currency,
                FxRate = an.FxRate,
                JournalId = w.JournalId,
                Net = net,
                Vat = vat,
                NetGbp = net,
                VatGbp = vat,
                EvidenceRef = CobolText.Trimmed(an.OrigInvoice, 60),
            };
            vw.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, an.TaxPoint, ""), "WRITE");
            await arvatwrt.CallAsync(vw, ct);
            if (vw.Header.ReturnCode > 4)
            {
                c.Rc = vw.Header.ReturnCode;
                c.Reason = vw.Header.ReasonCode;
                c.Msg = vw.Header.Message;
                c.MsgCnt++;
            }
        }
    }

    /// <summary>1800-WRITE-OPEN-ITEM: a negative CREDIT item for the gross (18 parameters for 17 markers, as bound).</summary>
    private async Task WriteOpenItemAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var an = w.An;
        var gross = SqlioParm.Text(ArEdit.Money(CobolNumeric.Store(0 - an.Gross, 13, 2)));
        var grossGbp = SqlioParm.Text(ArEdit.Money(CobolNumeric.Store(0 - an.GrossGbp, 13, 2)));
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SET NOCOUNT ON; INSERT ar.OpenItem (CompanyCode, CustomerCode, ItemType, DocumentNumber, DocumentDate, DueDate, PostingDate, " +
                "FiscalYear, PeriodNo, CurrencyCode, FxRate, OriginalAmount, OriginalAmountGBP, ResidualAmount, ResidualAmountGBP, VatAmountGBP, " +
                "SourceInvoiceId, CreatedBy) VALUES (?, ?, 'CREDIT', ?, ?, ?, ?, NULLIF(?,0), NULLIF(?,0), ?, ?, ?, ?, ?, ?, ?, ?, ?); SELECT " +
                "CONVERT(varchar(24), SCOPE_IDENTITY()), ''",
                [
                    SqlioParm.Text(c.Co), SqlioParm.Text(an.Customer), SqlioParm.Text(an.CreditNo), SqlioParm.Digits(an.DocDate, 8),
                    SqlioParm.Digits(an.DocDate, 8), SqlioParm.Digits(an.PostingDate, 8), SqlioParm.Text(ArEdit.Int(w.FiscalYear)),
                    SqlioParm.Text(ArEdit.Int(w.PeriodNo)), SqlioParm.Text(an.Currency), SqlioParm.Text(ArEdit.Rate(an.FxRate)), gross, grossGbp,
                    gross, grossGbp, SqlioParm.Text(ArEdit.Money(CobolNumeric.Store(0 - an.VatGbp, 13, 2))),
                    SqlioParm.Text(ArEdit.Int(an.CreditId)), SqlioParm.Text(c.UserId), SqlioParm.Text(c.UserId),
                ], [24, 24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        an.OpenItemId = (int)CobolNumeric.Store(row.NumVal(0), 9, 0);
    }

    /// <summary>1900-MARK-POSTED: the credit note POSTED with its journal; the original's CreditableRemainingGBP reduced.</summary>
    private async Task MarkPostedAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var an = w.An;
        var remaining = CobolNumeric.Store(an.CreditableRem - an.GrossGbp, 13, 2);
        try
        {
            await sql.ExecAsync(
                "UPDATE ar.SalesInvoice SET Status = 'POSTED', PostedBy = ?, PostedAt = SYSDATETIME(), IssuedAt = SYSDATETIME(), JournalId = " +
                "(SELECT TOP (1) j.JournalId FROM fin.Journal j WHERE j.CompanyCode = ? AND j.JournalNumber = ?) WHERE InvoiceId = ?; " +
                "UPDATE ar.SalesInvoice SET CreditableRemainingGBP = ? WHERE CompanyCode = ? AND InvoiceNumber = ? AND DocumentType = 'INVOICE'",
                [
                    SqlioParm.Text(c.UserId), SqlioParm.Text(c.Co), SqlioParm.Text(an.JournalNo), SqlioParm.Text(ArEdit.Int(an.CreditId)),
                    SqlioParm.Text(ArEdit.Money(remaining)), SqlioParm.Text(c.Co), SqlioParm.Text(an.OrigInvoice),
                ], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        an.CreditableRem = CobolNumeric.Store(remaining, 11, 2);
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
