using Sterp.Modules.Ar.Contracts;
using Sterp.Modules.Ar.Domain;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Application;

/// <summary>
/// ARINVPST (cobol/online/ar/ARINVPST.cob, AR-004) behind <see cref="IArInvoicePostingService"/>: VALIDATE (customer,
/// pricing, VAT, terms, VAT-invoice content and source controls), PROFORMA (a pro forma document in its own unit) and
/// POST (PERMCHK, PERIODCK, IDEMPOT, NUMGEN, then one unit: invoice, lines, VAT analysis, journal, VAT records, open
/// item, posted status). Runs on the caller's engine connection.
/// </summary>
public sealed partial class InvoicePostingEngine(
    ISqlRunner sql,
    IParameterService parmget,
    IPaymentTermsCalculator termcalc,
    IPermissionService permchk,
    IPeriodService periodck,
    IDocumentNumberService numgen,
    IIdempotencyService idempot,
    VatWriterEngine arvatwrt,
    ArGlPostingEngine arpostgl) : IArInvoicePostingService
{
    private const string Program = "ARINVPST";

    private sealed class Ws
    {
        public required ArCtl Ctl;
        public required ArinvpstArea Ai;
        public decimal SimpleLimit = 250;
        public int MaxTermDays = 60;
        public bool PostMode;
        public bool TranOpen;
        public string CuStatus = "", CuCurrency = "", CuControl = "", CuEinv = "", CuPoReq = "";
        public string VwLiability = "";
        public int FiscalYear, PeriodNo, JournalId;
        public decimal TcDiscountAmount;
    }

    public async Task<LegacyResult> CallAsync(ArinvpstArea ai, CancellationToken ct = default)
    {
        var c = ArCtl.Start(ai.Header, Program, "AR-004");
        var w = new Ws { Ctl = c, Ai = ai };
        await InitialiseAsync(w, ct);
        ValidateHeader(w);
        if (!c.InputBad)
        {
            await ReadCustomerAsync(w, ct);
            if (!c.RcBad) await PriceTheLinesAsync(w, ct);
            if (!c.RcBad) await TermsAndDueDateAsync(w, ct);
            if (!c.RcBad)
            {
                VatInvoiceContent(w);
                await SourceControlsAsync(w, ct);
                if (!c.RcBad)
                {
                    switch (c.Func)
                    {
                        case "VALIDATE":
                            if (!c.RcBad) c.Msg = "Invoice validated: " + ai.Currency.Trim(' ') + " " + ArEdit.MoneyUk(ai.Gross) + " due " + Uk(ai.DueDate);
                            break;
                        case "PROFORMA":
                            await WriteProformaAsync(w, ct);
                            break;
                        case "POST":
                            await PostInvoiceAsync(w, ct);
                            break;
                        default:
                            c.Rc = 8;
                            c.Reason = "ARI0098";
                            c.Msg = "ARINVPST does not support function [" + c.Func.Trim(' ') + "]";
                            break;
                    }
                }
            }
        }
        c.SetResult(ai.Header);
        return ai.Header.Outcome();
    }

    /// <summary>AI-DUE-DATE(7:2) "/" (5:2) "/" (1:4).</summary>
    private static string Uk(int yyyymmdd) => ArEdit.Dmy(yyyymmdd);

    /// <summary>0100-INITIALISE (+0150-READ-LIMITS).</summary>
    private async Task InitialiseAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        ai.MissingCnt = 0;
        ai.VatCnt = 0;
        ai.InvoiceId = 0;
        ai.OpenItemId = 0;
        ai.JournalNo = "";
        ai.Duplicate = "N";
        ai.Customer = CobolText.Trimmed(ai.Customer.ToUpperInvariant(), 20);
        if (CobolText.IsSpaces(ai.DocType)) ai.DocType = "INVOICE";
        if (ai.DocDate == 0) ai.DocDate = c.BusDate;
        if (ai.TaxPoint == 0) ai.TaxPoint = ai.DocDate;
        if (ai.PostingDate == 0) ai.PostingDate = ai.DocDate;
        if (CobolText.IsSpaces(ai.Currency)) ai.Currency = "GBP";
        if (ai.FxRate == 0) ai.FxRate = 1;
        if (CobolText.IsSpaces(ai.Source)) ai.Source = "DIRECT";
        if (CobolText.IsSpaces(ai.Simplified) || CobolText.IsLowValues(ai.Simplified)) ai.Simplified = "N";

        var pm = new ParmgetArea { ParamCode = "AR.SIMPLIFIED.VAT.LIMIT", UseDefault = "Y", DefaultText = "250" };
        pm.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ai.DocDate, ""), "GETNUM");
        await parmget.ExecuteAsync(pm, ct);
        if (pm.ValueNum > 0) w.SimpleLimit = CobolNumeric.Store(pm.ValueNum, 9, 2);
        pm = new ParmgetArea { ParamCode = "TERMS.MAX.DAYS", UseDefault = "Y", DefaultText = "60" };
        pm.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ai.DocDate, ""), "GETNUM");
        await parmget.ExecuteAsync(pm, ct);
        if (pm.ValueNum > 0) w.MaxTermDays = (int)CobolNumeric.Store(pm.ValueNum, 5, 0);
    }

    /// <summary>0200-VALIDATE-HEADER.</summary>
    private static void ValidateHeader(Ws w)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        if (CobolText.IsSpaces(c.Co))
        {
            c.Reject("ARI0001", "A company code is required");
            return;
        }
        if (CobolText.IsSpaces(ai.Customer))
        {
            c.Reject("ARI0002", "A customer account is required");
            return;
        }
        if (ai.LineCnt is < 1 or > 25)
        {
            c.Reject("ARI0003", "An invoice needs between one and twenty five lines");
            return;
        }
        if (CobolDate.TestDate(ai.DocDate) != 0)
        {
            c.Reject("ARI0004", "The invoice date is not a calendar date");
            return;
        }
        if (CobolDate.TestDate(ai.TaxPoint) != 0) c.Reject("ARI0005", "The tax point is not a calendar date");
        if (c.Func == "POST" && CobolText.IsSpaces(c.Correl))
        {
            c.Reject("ARI0006", "A correlation id is required so a repeated instruction cannot bill the same work twice");
        }
    }

    /// <summary>0300-READ-CUSTOMER.</summary>
    private async Task ReadCustomerAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT c.LegalName, c.Status, ISNULL(c.VatRegNo,''), c.VatLocation, c.CurrencyCode, ISNULL(c.TermsCode,''), " +
                "ISNULL(c.ArControlAccount,''), ISNULL(c.InvoiceCustomerCode, c.CustomerCode), ISNULL((SELECT TOP (1) CASE WHEN " +
                "LTRIM(RTRIM(ISNULL(a.Line1,''))) = '' THEN '' ELSE RTRIM(a.Line1) + ', ' + ISNULL(RTRIM(a.Town) + ', ','') + " +
                "ISNULL(a.Postcode,'') END FROM ar.CustomerAddress a WHERE a.CompanyCode = c.CompanyCode AND a.CustomerCode = " +
                "c.CustomerCode AND a.AddressRole = 'BILLING'),''), CONVERT(char(1), c.EInvoiceEnabled), CONVERT(char(1), " +
                "c.RequiresPurchaseOrderRef), ISNULL((SELECT TOP (1) y.VatRegNo FROM core.Company y WHERE y.CompanyCode = " +
                "c.CompanyCode),'') FROM ar.Customer c WHERE c.CompanyCode = ? AND c.CustomerCode = ?",
                [SqlioParm.Text(c.Co), SqlioParm.Text(ai.Customer)], [120, 10, 15, 3, 3, 12, 12, 20, 300, 2, 2, 15], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (!row.Found)
        {
            c.Rc = 16;
            c.Reason = "ARI0007";
            c.Msg = "Customer " + ai.Customer.Trim(' ') + " is not held for this company";
            return;
        }
        w.CuStatus = CobolText.Trimmed(row.Raw(1), 10);
        w.CuCurrency = CobolText.Trimmed(row.Raw(4), 3);
        w.CuControl = CobolText.Trimmed(row.Raw(6), 12);
        w.CuEinv = CobolText.Fit(row.Raw(9), 2);
        w.CuPoReq = CobolText.Fit(row.Raw(10), 2);
        if (CobolText.IsSpaces(ai.BillName)) ai.BillName = CobolText.Trimmed(row.Raw(0), 120);
        if (CobolText.IsSpaces(ai.BillAddress)) ai.BillAddress = CobolText.Trimmed(row.Raw(8), 300);
        if (CobolText.IsSpaces(ai.CustVatNo)) ai.CustVatNo = CobolText.Trimmed(row.Raw(2), 15);
        if (CobolText.IsSpaces(ai.OwnVatNo)) ai.OwnVatNo = CobolText.Trimmed(row.Raw(11), 15);
        if (CobolText.IsSpaces(ai.VatLocation)) ai.VatLocation = CobolText.Trimmed(row.Raw(3), 3);
        if (CobolText.IsSpaces(ai.InvoiceCust)) ai.InvoiceCust = CobolText.Trimmed(row.Raw(7), 20);
        if (CobolText.IsSpaces(ai.TermsCode)) ai.TermsCode = CobolText.Trimmed(row.Raw(5), 12);
        if (w.CuStatus is "RETIRED" or "SUSPENDED")
        {
            c.Rc = 8;
            c.Reason = "ARI0008";
            c.Msg = "Customer " + ai.Customer.Trim(' ') + " is " + w.CuStatus + " and cannot be invoiced";
            return;
        }
        if (w.CuPoReq[0] == '1' && CobolText.IsSpaces(ai.OrderRef))
        {
            c.Rc = 8;
            c.Reason = "ARI0009";
            c.Msg = "This customer requires its own order reference on every invoice";
        }
    }

    /// <summary>0500-PRICE-THE-LINES (+0520 one line, 0540 line VAT, 0560 VAT analysis).</summary>
    private async Task PriceTheLinesAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        ai.Net = ai.Vat = ai.Gross = 0;
        ai.NetGbp = ai.VatGbp = ai.GrossGbp = 0;
        ai.VatCnt = 0;
        for (var i = 1; i <= ai.LineCnt && !c.RcBad; i++) await OneLineAsync(w, i, ct);
        if (c.RcBad) return;
        ai.Gross = CobolNumeric.Store(ai.Net + ai.Vat, 11, 2);
        if (ai.Currency == "GBP")
        {
            ai.NetGbp = ai.Net;
            ai.VatGbp = ai.Vat;
            ai.GrossGbp = ai.Gross;
        }
        else
        {
            ai.GrossGbp = CobolNumeric.Store(ai.NetGbp + ai.VatGbp, 11, 2);
        }
    }

    private async Task OneLineAsync(Ws w, int i, CancellationToken ct)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        var l = ai.Lines[i - 1];
        l.LMessage = "";
        if (CobolText.IsSpaces(l.LDesc))
        {
            c.Reject("ARI0020", "Every line needs a description sufficient to identify the goods or services");
            return;
        }
        if (l.LQty == 0) l.LQty = 1;
        if (l.LNet == 0)
        {
            var qty1 = CobolNumeric.Store(l.LQty, 9, 6);
            var price1 = CobolNumeric.Store(l.LPrice, 9, 6);
            var amt1 = CobolNumeric.Store(qty1 * price1, 13, 2, rounded: true);
            if (l.LDiscPct != 0)
            {
                var pct1 = CobolNumeric.Store(l.LDiscPct, 5, 6);
                amt1 = CobolNumeric.Store(amt1 * (100 - pct1) / 100, 13, 2, rounded: true);
            }
            amt1 = CobolNumeric.Store(amt1 + l.LCharge, 13, 2);
            l.LNet = CobolNumeric.Store(amt1, 11, 2);
        }
        if (l.LAvailQty > 0 && l.LQty > l.LAvailQty)
        {
            var msg = "Line " + (Math.Abs(i) % 10000).ToString("D4", System.Globalization.CultureInfo.InvariantCulture) +
                " exceeds the uninvoiced quantity of " + ArEdit.Qty(l.LAvailQty);
            c.Reason = "ARI0021";
            c.Msg = msg;
            l.LMessage = CobolText.Trimmed(c.Msg, 80);
            c.Reject("ARI0021", msg);
            return;
        }
        // 0540-LINE-VAT
        var vw = new ArvatwrtArea { VatCode = l.LVatCode, TaxPoint = ai.TaxPoint, Direction = "OUTPUT" };
        vw.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ai.TaxPoint, ""), "LOOKUP");
        await arvatwrt.CallAsync(vw, ct);
        w.VwLiability = vw.Liability;
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
        if (!CobolText.IsSpaces(vw.Wording) && CobolText.IsSpaces(ai.SpecialWording)) ai.SpecialWording = CobolText.Trimmed(vw.Wording, 300);

        ai.Net = CobolNumeric.Store(ai.Net + l.LNet, 11, 2);
        ai.Vat = CobolNumeric.Store(ai.Vat + l.LVatAmt, 11, 2);
        decimal amt2, amt3;
        if (ai.Currency != "GBP" && ai.FxRate > 0)
        {
            amt2 = CobolNumeric.Store(l.LNet / ai.FxRate, 13, 2, rounded: true);
            ai.NetGbp = CobolNumeric.Store(ai.NetGbp + amt2, 11, 2);
            amt3 = CobolNumeric.Store(l.LVatAmt / ai.FxRate, 13, 2, rounded: true);
            ai.VatGbp = CobolNumeric.Store(ai.VatGbp + amt3, 11, 2);
        }
        else
        {
            amt2 = l.LNet;
            amt3 = l.LVatAmt;
        }
        // 0560-ACCUMULATE-VAT
        var ix = 0;
        for (var j = 1; j <= ai.VatCnt; j++)
        {
            if (CobolText.Fit(ai.Vats[j - 1].VCode, 4) == CobolText.Fit(l.LVatCode, 4)) ix = j;
        }
        if (ix == 0)
        {
            if (ai.VatCnt >= 6)
            {
                c.Reject("ARI0022", "An invoice may carry at most six VAT codes");
                return;
            }
            ai.VatCnt++;
            ix = ai.VatCnt;
            var v = ai.Vats[ix - 1];
            v.VCode = CobolText.Trimmed(l.LVatCode, 4);
            v.VRate = l.LVatRate;
            v.VLiability = CobolText.Trimmed(w.VwLiability, 12);
            v.VNetGbp = 0;
            v.VVatGbp = 0;
        }
        ai.Vats[ix - 1].VNetGbp = CobolNumeric.Store(ai.Vats[ix - 1].VNetGbp + amt2, 11, 2);
        ai.Vats[ix - 1].VVatGbp = CobolNumeric.Store(ai.Vats[ix - 1].VVatGbp + amt3, 11, 2);
    }

    /// <summary>0400-TERMS-AND-DUE-DATE.</summary>
    private async Task TermsAndDueDateAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        var tc = new TermcalcArea { TermsCode = ai.TermsCode, InvoiceDate = ai.DocDate, SupplyDate = ai.TaxPoint, NetAmount = ai.Net, VatAmount = ai.Vat };
        tc.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, ai.DocDate, ""), "DUEDATE");
        await termcalc.ExecuteAsync(tc, ct);
        if (tc.Header.ReturnCode > 6)
        {
            c.Rc = 8;
            c.Reason = tc.Header.ReasonCode;
            c.Msg = tc.Header.Message;
            c.MsgCnt++;
            return;
        }
        if (ai.DueDate == 0) ai.DueDate = tc.DueDate;
        ai.DiscountDate = tc.DiscountDate;
        ai.DiscountPct = CobolNumeric.Store(tc.DiscountPct, 3, 4);
        ai.TermsSnapshot = CobolText.Trimmed(tc.TermsDescription, 100);
        ai.TermsSource = CobolText.Trimmed(tc.Source, 20);
        if (tc.DiscountPct > 0)
        {
            tc.Header.Function = "DISCOUNT";
            await termcalc.ExecuteAsync(tc, ct);
        }
        w.TcDiscountAmount = tc.DiscountAmount;
        if (tc.TermDays > w.MaxTermDays)
        {
            c.Rc = 4;
            c.Reason = "ARI0010";
            c.Msg = "Payment terms of " + ArEdit.Int(tc.TermDays) + " days exceed the agreed maximum (D-25)";
            c.MsgCnt++;
        }
    }

    /// <summary>0600-VAT-INVOICE-CONTENT (+0650..0670).</summary>
    private static void VatInvoiceContent(Ws w)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        ai.MissingCnt = 0;
        void Missing(string what)
        {
            ai.MissingCnt++;
            if (ai.MissingCnt <= ArinvpstArea.MaxMissing) ai.Missing[ai.MissingCnt - 1] = what;
        }
        if (CobolText.IsSpaces(ai.BillName)) Missing("Customer name");
        if (CobolText.IsSpaces(ai.BillAddress)) Missing("Customer address");
        if (CobolText.IsSpaces(ai.OwnVatNo)) Missing("Supplier VAT registration number");
        if (ai.TaxPoint == 0) Missing("Tax point");
        if (ai.DocType == "PROFORMA")
        {
            ai.SpecialWording = "PRO FORMA - THIS IS NOT A VAT INVOICE";
            return;
        }
        if (ai.Simplified == "Y" && ai.GrossGbp > w.SimpleLimit)
        {
            c.Rc = 8;
            c.Reason = "ARI0031";
            c.Msg = "A simplified VAT invoice is only permitted up to GBP " + ArEdit.MoneyUk(w.SimpleLimit) + " including VAT; this document is GBP " +
                ArEdit.Money(ai.GrossGbp);
            c.MsgCnt++;
        }
        if (ai.DepositApply > 0)
        {
            ai.SpecialWording = CobolText.Trimmed("Deposit of GBP " + ArEdit.MoneyUk(ai.DepositApply) + " already accounted for on an earlier invoice", 300);
        }
        if (ai.Currency != "GBP")
        {
            var text2 = CobolText.Trimmed("VAT total GBP " + ArEdit.MoneyUk(ai.VatGbp) + " at " + ArEdit.Rate(ai.FxRate) + " " + ai.Currency.Trim(' ') + " per GBP", 200);
            var scratch = CobolText.Trimmed(text2.Trim(' ') + ". " + ai.SpecialWording.Trim(' '), 256);
            ai.SpecialWording = CobolText.Trimmed(scratch, 300);
        }
        if (ai.MissingCnt > 0)
        {
            c.Rc = 8;
            c.Reason = "ARI0030";
            c.Msg = "The document is not a valid VAT invoice: " + ai.Missing[0].Trim(' ') + " is missing";
            c.MsgCnt++;
        }
    }

    /// <summary>0700-SOURCE-CONTROLS (+0750 despatch already billed).</summary>
    private async Task SourceControlsAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ai = w.Ai;
        if (ai.DocType == "PROFORMA") return;
        if (ai.Source is "DIRECT" or "SERVICE" && CobolText.IsSpaces(ai.EvidenceRef) && CobolText.IsSpaces(ai.OrderRef))
        {
            c.Rc = 4;
            c.Reason = "ARI0040";
            c.Msg = "A direct invoice should carry evidence of the charge or the customer reference";
            c.MsgCnt++;
        }
        if (CobolText.IsSpaces(ai.DespatchRef)) return;
        if (ai.Source == "DIRECT")
        {
            c.Rc = 8;
            c.Reason = "ARI0041";
            c.Msg = "A direct financial invoice may not quote a despatch; invoice the despatch itself";
            return;
        }
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT TOP (1) i.InvoiceNumber, '' FROM ar.SalesInvoice i WHERE i.CompanyCode = ? AND i.DespatchRef = ? AND " +
                "i.DocumentType = 'INVOICE' AND i.Status IN ('APPROVED','POSTED')",
                [SqlioParm.Text(c.Co), SqlioParm.Text(ai.DespatchRef)], [24, 24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (row.Found)
        {
            c.Rc = 8;
            c.Reason = "ARI0042";
            c.Msg = "Despatch " + ai.DespatchRef.Trim(' ') + " has already been invoiced as " + row.Raw(0).Trim(' ');
            ai.Duplicate = "Y";
            c.MsgCnt++;
        }
    }
}
