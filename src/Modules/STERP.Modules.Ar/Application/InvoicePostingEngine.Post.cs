using Sterp.Modules.Ar.Contracts;
using Sterp.Modules.Ar.Domain;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Application;

/// <summary>ARINVPST posting half: 1000-POST-INVOICE and 3000-WRITE-PROFORMA with their sections.</summary>
public sealed partial class InvoicePostingEngine
{
    /// <summary>1000-POST-INVOICE.</summary>
    private async Task PostInvoiceAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        w.PostMode = true;

        // 1050-CHECK-PERMISSION
        var pk = new PermchkArea { Permission = "AR.INVOICE.POST" };
        pk.Header.Prepare(new KernelCaller(c.Co, c.Site, c.UserId, ai.PostingDate, ""), "CHECK");
        await permchk.ExecuteAsync(pk, ct);
        if (pk.Header.ReturnCode > 4)
        {
            c.Rc = 6;
            c.Reason = "ARI0050";
            c.Msg = pk.Header.Message;
            c.MsgCnt++;
        }
        if (c.RcBad || c.Rc == 6) return;

        // 1100-CHECK-PERIOD
        var pc = new PeriodckArea { PostingDate = ai.PostingDate, DocumentDate = ai.DocDate, TaxPoint = ai.TaxPoint, Action = "POST" };
        pc.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ai.PostingDate, ""), "CHECK");
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

        // 1150-IDEMPOTENT-BEGIN
        var id = new IdempotArea { Operation = "ar.SalesInvoice.Post" };
        id.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ai.PostingDate, c.Correl), "BEGIN");
        await idempot.ExecuteAsync(id, ct);
        switch (id.Header.ReturnCode)
        {
            case 0:
                break;
            case 4:
                ai.Duplicate = "Y";
                ai.InvoiceNo = CobolText.Trimmed(id.ResultRef, 30);
                c.Rc = 4;
                c.Reason = "ARI0051";
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
        if (c.RcBad || ai.Duplicate == "Y") return;

        await DrawNumberAsync(w, ct);
        if (c.RcBad) return;

        await BeginAsync(w, ct);
        foreach (var step in new Func<Ws, CancellationToken, Task>[]
                 {
                     WriteHeaderAsync, WriteLinesAsync, WriteVatAnalysisAsync, PostJournalAsync, WriteVatRecordsAsync, WriteOpenItemAsync,
                     MarkPostedAsync,
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

        // 1950-IDEMPOTENT-END
        var end = new IdempotArea
        {
            Operation = "ar.SalesInvoice.Post",
            Status = "COMPLETED",
            ResultRef = ai.InvoiceNo,
            ResultMessage = "Sales invoice posted",
        };
        end.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ai.PostingDate, c.Correl), "END");
        await idempot.ExecuteAsync(end, ct);
        c.Msg = "Invoice " + ai.InvoiceNo.Trim(' ') + " posted, " + ai.Currency.Trim(' ') + " " + ArEdit.MoneyUk(ai.Gross) + " due " + Uk(ai.DueDate);
    }

    /// <summary>1200-DRAW-NUMBER (series SINV).</summary>
    private async Task DrawNumberAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        if (!CobolText.IsSpaces(ai.InvoiceNo)) return;
        var ng = new NumgenArea { SeriesCode = "SINV" };
        ng.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ai.DocDate, c.Correl), "NEXT");
        await numgen.ExecuteAsync(ng, ct);
        if (ng.Header.ReturnCode > 4)
        {
            c.Rc = ng.Header.ReturnCode;
            c.Reason = ng.Header.ReasonCode;
            c.Msg = ng.Header.Message;
            c.MsgCnt++;
            return;
        }
        ai.InvoiceNo = CobolText.Trimmed(ng.DocumentNumber, 30);
    }

    /// <summary>1300-WRITE-HEADER: ar.SalesInvoice as DRAFT.</summary>
    private async Task WriteHeaderAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SET NOCOUNT ON; INSERT ar.SalesInvoice (CompanyCode, DocumentType, InvoiceNumber, CustomerCode, InvoiceCustomerCode, " +
                "DocumentDate, TaxPointDate, PostingDate, DueDate, FiscalYear, PeriodNo, CurrencyCode, FxRate, NetAmount, VatAmount, " +
                "GrossAmount, NetAmountGBP, VatAmountGBP, GrossAmountGBP, SettlementDiscountPct, DepositAppliedGBP, TermsCode, TermsSnapshot, " +
                "TermsSource, BillToName, BillToAddress, CustomerVatRegNo, CompanyVatRegNo, VatLocation, IsSimplifiedInvoice, SpecialWording, " +
                "CustomerOrderRef, SalesOrderNumber, DespatchRef, SalespersonCode, Status, CorrelationId, CreatedBy) VALUES (?, ?, ?, ?, ?, " +
                "?, ?, ?, ?, NULLIF(?,0), NULLIF(?,0), ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, NULLIF(?,''), NULLIF(?,''), NULLIF(?,''), ?, ?, " +
                "NULLIF(?,''), NULLIF(?,''), ?, ?, NULLIF(?,''), NULLIF(?,''), NULLIF(?,''), NULLIF(?,''), NULLIF(?,''), 'DRAFT', ?, ?); " +
                "SELECT CONVERT(varchar(24), SCOPE_IDENTITY()), ''",
                [
                    SqlioParm.Text(c.Co), SqlioParm.Text(ai.DocType), SqlioParm.Text(ai.InvoiceNo), SqlioParm.Text(ai.Customer),
                    SqlioParm.Text(ai.InvoiceCust), SqlioParm.Digits(ai.DocDate, 8), SqlioParm.Digits(ai.TaxPoint, 8),
                    SqlioParm.Digits(ai.PostingDate, 8), SqlioParm.Digits(ai.DueDate, 8), SqlioParm.Text(ArEdit.Int(w.FiscalYear)),
                    SqlioParm.Text(ArEdit.Int(w.PeriodNo)), SqlioParm.Text(ai.Currency), SqlioParm.Text(ArEdit.Rate(ai.FxRate)),
                    SqlioParm.Text(ArEdit.Money(ai.Net)), SqlioParm.Text(ArEdit.Money(ai.Vat)), SqlioParm.Text(ArEdit.Money(ai.Gross)),
                    SqlioParm.Text(ArEdit.Money(ai.NetGbp)), SqlioParm.Text(ArEdit.Money(ai.VatGbp)), SqlioParm.Text(ArEdit.Money(ai.GrossGbp)),
                    SqlioParm.Text(ArEdit.Money(ai.DiscountPct)), SqlioParm.Text(ArEdit.Money(ai.DepositApply)), SqlioParm.Text(ai.TermsCode),
                    SqlioParm.Text(ai.TermsSnapshot), SqlioParm.Text(ai.TermsSource), SqlioParm.Text(ai.BillName), SqlioParm.Text(ai.BillAddress),
                    SqlioParm.Text(ai.CustVatNo), SqlioParm.Text(ai.OwnVatNo), SqlioParm.Text(ai.VatLocation),
                    SqlioParm.Text(ai.Simplified == "Y" ? "1" : "0"), SqlioParm.Text(ai.SpecialWording), SqlioParm.Text(ai.OrderRef),
                    SqlioParm.Text(ai.SoNumber), SqlioParm.Text(ai.DespatchRef), SqlioParm.Text(ai.Salesperson), SqlioParm.Text(c.Correl),
                    SqlioParm.Text(c.UserId),
                ], [24, 24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        ai.InvoiceId = (int)CobolNumeric.Store(row.NumVal(0), 9, 0);
    }

    /// <summary>1400-WRITE-LINES (+1450 one line row).</summary>
    private async Task WriteLinesAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        for (var i = 1; i <= ai.LineCnt && !c.RcBad; i++)
        {
            var l = ai.Lines[i - 1];
            decimal amt1, amt2;
            if (ai.Currency == "GBP" || ai.FxRate == 0)
            {
                amt1 = l.LNet;
                amt2 = l.LVatAmt;
            }
            else
            {
                amt1 = CobolNumeric.Store(l.LNet / ai.FxRate, 13, 2, rounded: true);
                amt2 = CobolNumeric.Store(l.LVatAmt / ai.FxRate, 13, 2, rounded: true);
            }
            try
            {
                await sql.ExecAsync(
                    "INSERT ar.SalesInvoiceLine (InvoiceId, LineNum, ItemCode, Description, Quantity, UomCode, UnitPrice, PricePerQty, " +
                    "DiscountPct1, NetAmount, VatCode, VatRatePct, VatAmount, NetAmountGBP, VatAmountGBP, DimCostCentre, DimProject, " +
                    "RevenueAccountCode, SalesOrderLine) VALUES (?, ?, NULLIF(?,''), ?, ?, NULLIF(?,''), ?, 1, ?, ?, ?, ?, ?, ?, ?, NULLIF(?,''), " +
                    "NULLIF(?,''), NULLIF(?,''), NULLIF(?,0))",
                    [
                        SqlioParm.Text(ArEdit.Int(ai.InvoiceId)), SqlioParm.Text(ArEdit.Int(i)), SqlioParm.Text(l.LItem), SqlioParm.Text(l.LDesc),
                        SqlioParm.Text(ArEdit.Qty(l.LQty)), SqlioParm.Text(l.LUom), SqlioParm.Text(ArEdit.Qty(l.LPrice)),
                        SqlioParm.Text(ArEdit.Money(l.LDiscPct)), SqlioParm.Text(ArEdit.Money(l.LNet)), SqlioParm.Text(l.LVatCode),
                        SqlioParm.Text(ArEdit.Money(l.LVatRate)), SqlioParm.Text(ArEdit.Money(l.LVatAmt)), SqlioParm.Text(ArEdit.Money(amt1)),
                        SqlioParm.Text(ArEdit.Money(amt2)), SqlioParm.Text(l.LCostCentre), SqlioParm.Text(l.LProject), SqlioParm.Text(l.LRevAccount),
                        SqlioParm.Text(ArEdit.Int(l.LSoLine)),
                    ], ct);
            }
            catch (SqlioException ex)
            {
                c.SqlFailed(ex.DriverText);
            }
        }
    }

    /// <summary>1500-WRITE-VAT-ANALYSIS: ar.SalesInvoiceVat per VAT code.</summary>
    private async Task WriteVatAnalysisAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        for (var i = 1; i <= ai.VatCnt && !c.RcBad; i++)
        {
            var v = ai.Vats[i - 1];
            decimal amt1, amt2;
            if (ai.Currency == "GBP" || ai.FxRate == 0)
            {
                amt1 = v.VNetGbp;
                amt2 = v.VVatGbp;
            }
            else
            {
                amt1 = CobolNumeric.Store(v.VNetGbp * ai.FxRate, 13, 2, rounded: true);
                amt2 = CobolNumeric.Store(v.VVatGbp * ai.FxRate, 13, 2, rounded: true);
            }
            try
            {
                await sql.ExecAsync(
                    "INSERT ar.SalesInvoiceVat (InvoiceId, VatCode, RatePct, NetAmount, VatAmount, NetAmountGBP, VatAmountGBP) VALUES " +
                    "(?, ?, ?, ?, ?, ?, ?)",
                    [
                        SqlioParm.Text(ArEdit.Int(ai.InvoiceId)), SqlioParm.Text(v.VCode), SqlioParm.Text(ArEdit.Money(v.VRate)),
                        SqlioParm.Text(ArEdit.Money(amt1)), SqlioParm.Text(ArEdit.Money(amt2)), SqlioParm.Text(ArEdit.Money(v.VNetGbp)),
                        SqlioParm.Text(ArEdit.Money(v.VVatGbp)),
                    ], ct);
            }
            catch (SqlioException ex)
            {
                c.SqlFailed(ex.DriverText);
            }
        }
    }

    /// <summary>1600-POST-JOURNAL: debtor, one revenue leg per line, output VAT (document currency, ARPOSTGL in this unit).</summary>
    private async Task PostJournalAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        var pg = new ArpostglArea
        {
            EventCode = "AR.INVOICE",
            JournalType = "AR",
            SourceModule = "AR",
            SourceRef = CobolText.Trimmed(ai.InvoiceNo, 40),
            DocDate = ai.DocDate,
            PostingDate = ai.PostingDate,
            Currency = ai.Currency,
            FxRate = ai.FxRate,
            OwnTran = "N",
            Description = CobolText.Trimmed("Sales invoice " + ai.InvoiceNo.Trim(' ') + " " + ai.BillName.Trim(' '), 200),
        };
        var leg = 0;
        var d = pg.Legs[leg++];
        d.GLegCode = "DEBTOR";
        d.GAccount = CobolText.Trimmed(w.CuControl, 12);
        d.GDebit = ai.Gross;
        d.GSubledger = "AR";
        d.GSubref = CobolText.Trimmed(ai.Customer, 40);
        d.GNarrative = CobolText.Trimmed(ai.BillName, 100);
        for (var i = 1; i <= ai.LineCnt; i++)
        {
            var l = ai.Lines[i - 1];
            var r = pg.Legs[leg++];
            r.GLegCode = "REVENUE";
            r.GAccount = CobolText.Trimmed(l.LRevAccount, 12);
            r.GCredit = l.LNet;
            r.GNarrative = CobolText.Trimmed(l.LDesc, 100);
            r.GCostCentre = CobolText.Trimmed(l.LCostCentre, 20);
            r.GProject = CobolText.Trimmed(l.LProject, 20);
            r.GVatCode = CobolText.Trimmed(l.LVatCode, 4);
        }
        if (ai.Vat != 0)
        {
            var v = pg.Legs[leg++];
            v.GLegCode = "VAT";
            v.GCredit = ai.Vat;
            v.GNarrative = CobolText.Trimmed("Output VAT on " + ai.InvoiceNo.Trim(' '), 100);
        }
        pg.LegCnt = leg;
        pg.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ai.PostingDate, c.Correl), "POST");
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
        ai.JournalNo = CobolText.Trimmed(pg.JournalNo, 30);
    }

    /// <summary>1700-WRITE-VAT-RECORDS: ARVATWRT WRITE per VAT code.</summary>
    private async Task WriteVatRecordsAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        for (var i = 1; i <= ai.VatCnt && !c.RcBad; i++)
        {
            var v = ai.Vats[i - 1];
            var vw = new ArvatwrtArea
            {
                SourceModule = "AR",
                DocType = "SALESINVOICE",
                DocRef = CobolText.Trimmed(ai.InvoiceNo, 40),
                PartyType = "CUSTOMER",
                PartyCode = ai.Customer,
                PartyVatNo = ai.CustVatNo,
                TaxPoint = ai.TaxPoint,
                PostingDate = ai.PostingDate,
                VatCode = v.VCode,
                Direction = "OUTPUT",
                Currency = ai.Currency,
                FxRate = ai.FxRate,
                JournalId = w.JournalId,
                NetGbp = v.VNetGbp,
                VatGbp = v.VVatGbp,
                EvidenceRef = ai.EvidenceRef,
            };
            if (ai.Currency == "GBP" || ai.FxRate == 0)
            {
                vw.Net = v.VNetGbp;
                vw.Vat = v.VVatGbp;
            }
            else
            {
                vw.Net = CobolNumeric.Store(v.VNetGbp * ai.FxRate, 11, 2, rounded: true);
                vw.Vat = CobolNumeric.Store(v.VVatGbp * ai.FxRate, 11, 2, rounded: true);
            }
            vw.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ai.TaxPoint, ""), "WRITE");
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

    /// <summary>1800-WRITE-OPEN-ITEM: the receivable net of any deposit applied (21 parameters for 20 markers, as bound).</summary>
    private async Task WriteOpenItemAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        var orig = SqlioParm.Text(ArEdit.Money(CobolNumeric.Store(ai.Gross - ai.DepositApply, 13, 2)));
        var origGbp = SqlioParm.Text(ArEdit.Money(CobolNumeric.Store(ai.GrossGbp - ai.DepositApply, 13, 2)));
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SET NOCOUNT ON; INSERT ar.OpenItem (CompanyCode, CustomerCode, ItemType, DocumentNumber, DocumentDate, DueDate, PostingDate, " +
                "FiscalYear, PeriodNo, CurrencyCode, FxRate, OriginalAmount, OriginalAmountGBP, ResidualAmount, ResidualAmountGBP, VatAmountGBP, " +
                "TermsCode, DiscountDueDate, DiscountAmount, SourceInvoiceId, CreatedBy) VALUES (?, ?, 'INVOICE', ?, ?, ?, ?, NULLIF(?,0), " +
                "NULLIF(?,0), ?, ?, ?, ?, ?, ?, ?, NULLIF(?,''), NULLIF(?,''), ?, ?, ?); SELECT CONVERT(varchar(24), SCOPE_IDENTITY()), ''",
                [
                    SqlioParm.Text(c.Co), SqlioParm.Text(CobolText.IsSpaces(ai.InvoiceCust) ? ai.Customer : ai.InvoiceCust),
                    SqlioParm.Text(ai.InvoiceNo), SqlioParm.Digits(ai.DocDate, 8), SqlioParm.Digits(ai.DueDate, 8),
                    SqlioParm.Digits(ai.PostingDate, 8), SqlioParm.Text(ArEdit.Int(w.FiscalYear)), SqlioParm.Text(ArEdit.Int(w.PeriodNo)),
                    SqlioParm.Text(ai.Currency), SqlioParm.Text(ArEdit.Rate(ai.FxRate)), orig, origGbp, orig, origGbp,
                    SqlioParm.Text(ArEdit.Money(ai.VatGbp)), SqlioParm.Text(ai.TermsCode),
                    ai.DiscountDate == 0 ? SqlioParm.Text("") : SqlioParm.Digits(ai.DiscountDate, 8),
                    SqlioParm.Text(ArEdit.Money(w.TcDiscountAmount)), SqlioParm.Text(ArEdit.Int(ai.InvoiceId)), SqlioParm.Text(c.UserId),
                    SqlioParm.Text(c.UserId),
                ], [24, 24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        ai.OpenItemId = (int)CobolNumeric.Store(row.NumVal(0), 9, 0);
    }

    /// <summary>1900-MARK-POSTED: invoice POSTED with its journal, customer trading dates.</summary>
    private async Task MarkPostedAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        try
        {
            await sql.ExecAsync(
                "UPDATE ar.SalesInvoice SET Status = 'POSTED', PostedBy = ?, PostedAt = SYSDATETIME(), IssuedAt = SYSDATETIME(), JournalId = " +
                "(SELECT TOP (1) j.JournalId FROM fin.Journal j WHERE j.CompanyCode = ? AND j.JournalNumber = ?) WHERE InvoiceId = ?; " +
                "UPDATE ar.Customer SET LastTransactionDate = ?, FirstTradedDate = ISNULL(FirstTradedDate, ?) WHERE CompanyCode = ? AND " +
                "CustomerCode = ?",
                [
                    SqlioParm.Text(c.UserId), SqlioParm.Text(c.Co), SqlioParm.Text(ai.JournalNo), SqlioParm.Text(ArEdit.Int(ai.InvoiceId)),
                    SqlioParm.Digits(ai.DocDate, 8), SqlioParm.Digits(ai.DocDate, 8), SqlioParm.Text(c.Co), SqlioParm.Text(ai.Customer),
                ], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
        }
    }

    /// <summary>3000-WRITE-PROFORMA: header and lines in an own unit, RC 4 ARI0060.</summary>
    private async Task WriteProformaAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        ai.DocType = "PROFORMA";
        await DrawNumberAsync(w, ct);
        if (c.RcBad) return;
        w.FiscalYear = 0;
        w.PeriodNo = 0;
        await BeginAsync(w, ct);
        await WriteHeaderAsync(w, ct);
        if (c.RcBad)
        {
            await RollbackAsync(w, ct);
            return;
        }
        await WriteLinesAsync(w, ct);
        if (c.RcBad)
        {
            await RollbackAsync(w, ct);
            return;
        }
        await CommitAsync(w, ct);
        c.Rc = 4;
        c.Reason = "ARI0060";
        c.Msg = "Pro forma " + ai.InvoiceNo.Trim(' ') + " produced - this is not a VAT invoice and creates no receivable";
        c.MsgCnt++;
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
