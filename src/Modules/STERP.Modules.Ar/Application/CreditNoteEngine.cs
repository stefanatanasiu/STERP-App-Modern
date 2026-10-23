using Sterp.Modules.Ar.Contracts;
using Sterp.Modules.Ar.Domain;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Application;

/// <summary>
/// ARCRNOTE (cobol/online/ar/ARCRNOTE.cob, AR-006) behind <see cref="IArCreditNoteService"/>: CHECK (the original invoice,
/// the lines against it, VAT, the over-credit control and the remaining creditable amount) and POST (PERMCHK, PERIODCK,
/// IDEMPOT, NUMGEN SCRN, then one unit: credit note, lines, journal, VAT records, open item, posted status). The original
/// invoice is never changed.
/// </summary>
public sealed partial class CreditNoteEngine(
    ISqlRunner sql,
    IPermissionService permchk,
    IPeriodService periodck,
    IDocumentNumberService numgen,
    IIdempotencyService idempot,
    VatWriterEngine arvatwrt,
    ArGlPostingEngine arpostgl) : IArCreditNoteService
{
    private const string Program = "ARCRNOTE";

    /// <summary>WS-VATS: the credit's VAT analysis (at most six codes; a seventh is silently not analysed).</summary>
    private sealed class VatRow
    {
        public string Code = "";
        public decimal Rate, Net, Vat;
    }

    private sealed class Ws
    {
        public required ArCtl Ctl;
        public required ArcrnoteArea An;
        public bool TranOpen;
        public string OName = "", OVatNo = "", OControl = "", OTaxPoint = "";
        public readonly List<VatRow> Vats = [];
        public int FiscalYear, PeriodNo, JournalId;
    }

    public async Task<LegacyResult> CallAsync(ArcrnoteArea an, CancellationToken ct = default)
    {
        // 0100-INITIALISE
        var c = ArCtl.Start(an.Header, Program, "AR-006");
        var w = new Ws { Ctl = c, An = an };
        an.CreditType = CobolText.Trimmed(an.CreditType, 10);
        an.ReturnStock = CobolText.Trimmed(an.ReturnStock, 1);
        an.Currency = CobolText.Trimmed(an.Currency, 3);
        an.CreditId = 0;
        an.OpenItemId = 0;
        an.JournalNo = "";
        an.Net = an.Vat = an.Gross = 0;
        an.NetGbp = an.VatGbp = an.GrossGbp = 0;
        if (an.DocDate == 0) an.DocDate = c.BusDate;
        if (an.TaxPoint == 0) an.TaxPoint = an.DocDate;
        if (an.PostingDate == 0) an.PostingDate = an.DocDate;
        if (CobolText.IsSpaces(an.CreditType)) an.CreditType = "VALUE";
        if (CobolText.IsSpaces(an.ReturnStock) || CobolText.IsLowValues(an.ReturnStock)) an.ReturnStock = "N";

        Validate(w);
        if (!c.InputBad)
        {
            await ReadOriginalAsync(w, ct);
            if (!c.RcBad) await PriceLinesAsync(w, ct);
            if (!c.RcBad && an.OrigInvoiceId != 0 && an.GrossGbp > an.CreditableRem)
            {
                // 0500-OVER-CREDIT-CHECK
                c.Rc = 8;
                c.Reason = "ARC0030";
                c.Msg = "Credit of GBP " + ArEdit.Money(an.GrossGbp) + " exceeds the remaining creditable amount of GBP " +
                    ArEdit.MoneyUk(an.CreditableRem) + " on invoice " + an.OrigInvoice.Trim(' ');
                c.MsgCnt++;
            }
            if (!c.RcBad)
            {
                switch (c.Func)
                {
                    case "CHECK":
                        c.Msg = "Remaining creditable on " + an.OrigInvoice.Trim(' ') + " after this credit: GBP " +
                            ArEdit.MoneyUk(CobolNumeric.Store(an.CreditableRem - an.GrossGbp, 13, 2));
                        break;
                    case "POST":
                        await PostCreditAsync(w, ct);
                        break;
                    default:
                        c.Rc = 8;
                        c.Reason = "ARC0098";
                        c.Msg = "ARCRNOTE does not support function [" + c.Func.Trim(' ') + "]";
                        break;
                }
            }
        }
        c.SetResult(an.Header);
        return an.Header.Outcome();
    }

    /// <summary>0200-VALIDATE.</summary>
    private static void Validate(Ws w)
    {
        var c = w.Ctl;
        var an = w.An;
        if (CobolText.IsSpaces(c.Co))
        {
            c.Reject("ARC0001", "A company code is required");
            return;
        }
        if (CobolText.IsSpaces(an.Reason))
        {
            c.Reject("ARC0002", "A credit note needs a stated reason");
            return;
        }
        if (an.ReturnStock == "Y")
        {
            c.Reject("ARC0003", "A financial credit does not return stock; raise a return authorisation in Sales");
            return;
        }
        if (an.CreditType == "GOODWILL")
        {
            if (!CobolText.IsSpaces(an.OrigInvoice))
            {
                c.Rc = 4;
                c.Reason = "ARC0004";
                c.Msg = "A goodwill credit normally has no original invoice; the link has been kept";
                c.MsgCnt++;
            }
        }
        else if (CobolText.IsSpaces(an.OrigInvoice))
        {
            c.Reject("ARC0005", "The original invoice must be identified where one exists");
        }
        if (an.LineCnt is < 1 or > 20) c.Reject("ARC0006", "A credit note needs between one and twenty lines");
    }

    /// <summary>0300-READ-ORIGINAL.</summary>
    private async Task ReadOriginalAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var an = w.An;
        if (CobolText.IsSpaces(an.OrigInvoice))
        {
            an.OrigInvoiceId = 0;
            an.CreditableRem = 0;
            return;
        }
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT CONVERT(varchar(24), i.InvoiceId), i.CustomerCode, ISNULL(i.BillToName,''), i.CurrencyCode, CONVERT(varchar(24), i.FxRate), " +
                "CONVERT(varchar(24), i.NetAmountGBP), CONVERT(varchar(24), i.VatAmountGBP), CONVERT(varchar(24), i.GrossAmountGBP), " +
                "CONVERT(varchar(24), ISNULL((SELECT SUM(c.GrossAmountGBP) FROM ar.SalesInvoice c WHERE c.CompanyCode = i.CompanyCode AND " +
                "c.DocumentType = 'CREDITNOTE' AND c.OriginalInvoiceNumber = i.InvoiceNumber AND c.Status = 'POSTED'),0)), " +
                "ISNULL(i.CustomerVatRegNo,''), ISNULL((SELECT k.ArControlAccount FROM ar.Customer k WHERE k.CompanyCode = i.CompanyCode AND " +
                "k.CustomerCode = i.CustomerCode),''), CONVERT(char(8), i.TaxPointDate, 112) FROM ar.SalesInvoice i WHERE i.CompanyCode = ? " +
                "AND i.InvoiceNumber = ? AND i.DocumentType = 'INVOICE' AND i.Status = 'POSTED'",
                [SqlioParm.Text(c.Co), SqlioParm.Text(an.OrigInvoice)], [24, 20, 120, 3, 24, 24, 24, 24, 24, 15, 12, 10], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (!row.Found)
        {
            c.Rc = 16;
            c.Reason = "ARC0010";
            c.Msg = "Posted invoice " + an.OrigInvoice.Trim(' ') + " is not held for this company";
            return;
        }
        w.OName = CobolText.Trimmed(row.Raw(2), 120);
        w.OVatNo = CobolText.Trimmed(row.Raw(9), 15);
        w.OControl = CobolText.Trimmed(row.Raw(10), 12);
        w.OTaxPoint = CobolText.Trimmed(row.Raw(11), 10);
        an.OrigInvoiceId = (int)CobolNumeric.Store(row.NumVal(0), 9, 0);
        an.Customer = CobolText.Trimmed(row.Raw(1), 20);
        an.OrigNetGbp = CobolNumeric.Store(row.NumVal(5), 11, 2);
        an.OrigVatGbp = CobolNumeric.Store(row.NumVal(6), 11, 2);
        an.OrigGrossGbp = CobolNumeric.Store(row.NumVal(7), 11, 2);
        an.PriorCredit = CobolNumeric.Store(row.NumVal(8), 11, 2);
        an.CreditableRem = CobolNumeric.Store(an.OrigGrossGbp - an.PriorCredit, 11, 2);
        if (CobolText.IsSpaces(an.Currency)) an.Currency = CobolText.Trimmed(row.Raw(3), 3);
        if (an.FxRate == 0) an.FxRate = CobolNumeric.Store(row.NumVal(4), 9, 8);
    }

    /// <summary>0400-PRICE-LINES (+0420 one line, 0440 against the original, 0460 VAT, 0480 analysis).</summary>
    private async Task PriceLinesAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var an = w.An;
        if (CobolText.IsSpaces(an.Currency)) an.Currency = "GBP";
        if (an.FxRate == 0) an.FxRate = 1;
        an.Net = an.Vat = 0;
        an.NetGbp = an.VatGbp = 0;
        w.Vats.Clear();
        for (var i = 1; i <= an.LineCnt && !c.RcBad; i++) await OneLineAsync(w, i, ct);
        if (c.RcBad) return;
        an.Gross = CobolNumeric.Store(an.Net + an.Vat, 11, 2);
        if (an.Currency == "GBP")
        {
            an.NetGbp = an.Net;
            an.VatGbp = an.Vat;
        }
        an.GrossGbp = CobolNumeric.Store(an.NetGbp + an.VatGbp, 11, 2);
    }

    private async Task OneLineAsync(Ws w, int i, CancellationToken ct)
    {
        var c = w.Ctl;
        var an = w.An;
        var l = an.Lines[i - 1];
        l.LMessage = "";
        if (CobolText.IsSpaces(l.LDesc)) l.LDesc = "Credit";
        if (l.LNet <= 0)
        {
            c.Reject("ARC0020", "Every credit line needs a positive net amount");
            return;
        }

        // 0440-LINE-AGAINST-ORIGINAL
        l.LOrigNet = 0;
        l.LOrigCredit = 0;
        l.LRemaining = 0;
        if (an.OrigInvoiceId != 0 && l.LOrigLine != 0)
        {
            SqlioRow row;
            try
            {
                row = await sql.SingleAsync(
                    "SELECT CONVERT(varchar(24), l.NetAmount), l.VatCode, CONVERT(varchar(24), l.VatRatePct), CONVERT(varchar(24), ISNULL((SELECT " +
                    "SUM(cl.NetAmount) FROM ar.SalesInvoiceLine cl JOIN ar.SalesInvoice ch ON ch.InvoiceId = cl.InvoiceId WHERE ch.CompanyCode = ? " +
                    "AND ch.DocumentType = 'CREDITNOTE' AND ch.OriginalInvoiceNumber = ? AND ch.Status = 'POSTED' AND cl.SalesOrderLine = " +
                    "l.LineNum),0)), l.Description, ISNULL(l.RevenueAccountCode,''), ISNULL(l.DimCostCentre,'') FROM ar.SalesInvoiceLine l WHERE " +
                    "l.InvoiceId = ? AND l.LineNum = ?",
                    [SqlioParm.Text(c.Co), SqlioParm.Text(an.OrigInvoice), SqlioParm.Text(ArEdit.Int(an.OrigInvoiceId)), SqlioParm.Text(ArEdit.Int(l.LOrigLine))],
                    [24, 4, 24, 24, 60, 12, 20], ct);
            }
            catch (SqlioException ex)
            {
                c.SqlFailed(ex.DriverText);
                return;
            }
            if (!row.Found)
            {
                c.Rc = 8;
                c.Reason = "ARC0021";
                c.Msg = "Line " + ArEdit.Int(l.LOrigLine) + " does not exist on invoice " + an.OrigInvoice.Trim(' ');
                l.LMessage = CobolText.Trimmed(c.Msg, 80);
                c.MsgCnt++;
                return;
            }
            l.LOrigNet = CobolNumeric.Store(row.NumVal(0), 11, 2);
            l.LOrigCredit = CobolNumeric.Store(row.NumVal(3), 11, 2);
            l.LRemaining = CobolNumeric.Store(l.LOrigNet - l.LOrigCredit, 11, 2);
            if (CobolText.IsSpaces(l.LVatCode)) l.LVatCode = CobolText.Trimmed(row.Raw(1), 4);
            if (CobolText.IsSpaces(l.LRevAccount)) l.LRevAccount = CobolText.Trimmed(row.Raw(5), 12);
            if (l.LNet > l.LRemaining)
            {
                c.Rc = 8;
                c.Reason = "ARC0022";
                c.Msg = "The credit exceeds the remaining creditable amount on that line of " + ArEdit.MoneyUk(l.LRemaining);
                l.LMessage = CobolText.Trimmed(c.Msg, 80);
                c.MsgCnt++;
            }
        }
        if (c.RcBad) return;

        // 0460-LINE-VAT
        var vw = new ArvatwrtArea { VatCode = l.LVatCode, TaxPoint = an.TaxPoint, Direction = "OUTPUT" };
        vw.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, an.TaxPoint, ""), "LOOKUP");
        await arvatwrt.CallAsync(vw, ct);
        if (vw.Header.ReturnCode > 6)
        {
            c.Rc = 8;
            c.Reason = vw.Header.ReasonCode;
            c.Msg = vw.Header.Message;
            l.LMessage = CobolText.Trimmed(c.Msg, 80);
            c.MsgCnt++;
            return;
        }
        l.LVatRate = vw.RatePct;
        l.LVatAmt = CobolNumeric.Store(CobolNumeric.Store(l.LNet * vw.RatePct / 100, 13, 2, rounded: true), 11, 2);

        an.Net = CobolNumeric.Store(an.Net + l.LNet, 11, 2);
        an.Vat = CobolNumeric.Store(an.Vat + l.LVatAmt, 11, 2);
        decimal amt2, amt3;
        if (an.Currency != "GBP" && an.FxRate > 0)
        {
            amt2 = CobolNumeric.Store(l.LNet / an.FxRate, 13, 2, rounded: true);
            amt3 = CobolNumeric.Store(l.LVatAmt / an.FxRate, 13, 2, rounded: true);
            an.NetGbp = CobolNumeric.Store(an.NetGbp + amt2, 11, 2);
            an.VatGbp = CobolNumeric.Store(an.VatGbp + amt3, 11, 2);
        }
        else
        {
            amt2 = l.LNet;
            amt3 = l.LVatAmt;
        }

        // 0480-ACCUMULATE-VAT
        var v = w.Vats.LastOrDefault(x => CobolText.Fit(x.Code, 4) == CobolText.Fit(l.LVatCode, 4));
        if (v is null)
        {
            if (w.Vats.Count >= 6) return;
            v = new VatRow { Code = CobolText.Trimmed(l.LVatCode, 4), Rate = l.LVatRate };
            w.Vats.Add(v);
        }
        v.Net = CobolNumeric.Store(v.Net + amt2, 11, 2);
        v.Vat = CobolNumeric.Store(v.Vat + amt3, 11, 2);
    }
}
