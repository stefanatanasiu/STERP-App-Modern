using System.Text.Json.Nodes;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Jobs;
using static Sterp.Modules.Ar.Jobs.ArReportWriter;

namespace Sterp.Modules.Ar.Jobs;

/// <summary>
/// EINVUBL (cobol/batch/ar/EINVUBL.cob, FR-STERP-AR-065/071) - Peppol BIS Billing 3.0 (UBL 2.1) extract: one
/// &lt;CO&gt;-&lt;document&gt;.xml file per posted invoice or credit note (by INV or by document date FROM..TO) under the
/// einvoice folder, written line by line as the program does (four-space tag lines, values escaped and cut at 195
/// characters), and the extract listing. Read only.
/// </summary>
public static class EinvublJob
{
    public const string Code = "EINVUBL";

    public static JobDefinition Definition => new()
    {
        JobCode = Code,
        Slice = ArBatch.Slice,
        SubmitOperationId = "ar_runEInvoiceExportJob",
        ParameterOrder = ["CO", "INV", "FROM", "TO", "CUST", "USER"],
        // 0100-READ-ARGS: neither INV nor FROM/TO ends RC 8 before 0200-JOB-START
        SelfRegistration = pre => HasSelection(pre.Args) ? ArBatch.SelfRegistration(pre, Code) : null,
        EndValues = (ctx, rc) => HasSelection(ctx.Args) ? ArBatch.End(ctx, rc) : JobService.DefaultEnd(ctx, rc, false, null),
        Execute = RunAsync,
    };

    private static bool HasSelection(CommandLineArgs args) =>
        !CobolText.IsSpaces(CobolText.Fit(args.Get("INV"), 30)) || Date(args.From) != 0 || Date(args.To) != 0;

    private static int Date(string text) => CobolText.IsSpaces(text) ? 0 : ArBatch.Date8(text);

    /// <summary>The XML-FILE writer (LINE SEQUENTIAL, X(400) records).</summary>
    private sealed class XmlOut(StreamWriter w)
    {
        public void Rec(string text) => w.WriteLine(CobolText.Fit(text, 400).TrimEnd(' '));

        /// <summary>8560-PUT-LINE: WS-VAL(1:200).</summary>
        public void Line(string val) => Rec(CobolText.Fit(val, 200));

        /// <summary>8500-PUT-TAG: escaped WS-VAL between the tags, four spaces in.</summary>
        public void Tag(string tag, string? val) => Rec("    <" + tag + ">" + Escape(val) + "</" + tag + ">");

        /// <summary>8600-PUT-MONEY: AR-MONEY-ED of AR-AMT1 with the document currency.</summary>
        public void Money(string tag, string cur, decimal amt) =>
            Rec("    <" + tag + " currencyID=\"" + cur.Trim(' ') + "\">" + Domain.ArEdit.Money(CobolNumeric.Store(amt, 13, 2)) + "</" + tag + ">");

        /// <summary>8400-XML-ESCAPE: all 200 positions of WS-VAL, output stops once 195 positions are used; then TRIM.</summary>
        private static string Escape(string? val)
        {
            var v = CobolText.Fit(val, 200);
            var esc = Enumerable.Repeat(' ', 220).ToArray();
            var k = 1;
            foreach (var c in v)
            {
                if (k >= 196) continue;
                var s = c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", _ => c.ToString() };
                for (var i = 0; i < s.Length; i++) esc[k - 1 + i] = s[i];
                k += s.Length;
            }
            return new string(esc).Trim(' ');
        }
    }

    /// <summary>8150-ISO-DATE: yyyy-mm-dd from the first eight characters of a slot.</summary>
    private static string Iso(string slot)
    {
        var d = CobolText.Fit(slot, 8);
        return d[..4] + "-" + d[4..6] + "-" + d[6..8];
    }

    public static async Task<int> RunAsync(JobRunContext ctx)
    {
        var sql = ctx.Get<ISqlRunner>();
        var ct = ctx.Cancellation;

        // 0100-READ-ARGS
        var co = CobolText.Trimmed(ctx.Args.Co, 10);
        var user = CobolText.Trimmed(ctx.Args.User, 20);
        var inv = CobolText.Trimmed(ctx.Args.Get("INV"), 30);
        var from = Date(ctx.Args.From);
        var to = Date(ctx.Args.To);
        if (co.Length == 0) co = "STC";
        if (user.Length == 0) user = "ARCLERK1";
        if (inv.Length == 0 && from == 0 && to == 0)
        {
            ctx.Display("STERP-E-0104 give INV= or FROM= and TO=");
            return 8;
        }
        if (inv.Length == 0)
        {
            if (from == 0) from = 19900101;
            if (to == 0) to = 20991231;
        }
        ctx.Display("STERP-I-0100 EINVUBL e-invoice extract for " + co);
        ctx.RowsProcessed = 0;

        // 0300-OPEN-SPOOL / 0350-FILL-HEADINGS (RUNPATH BASE for the XML folder)
        var heading = ArBatch.Heading(co, Code, "Peppol e-invoice extract (UBL 2.1)", user, DateTime.Now);
        heading.Subtitle = inv.Length > 0
            ? CobolText.Trimmed("Document " + inv, 60)
            : "Document date " + ArBatch.Uk(from) + " to " + ArBatch.Uk(to);
        await ArBatch.ReadCompanyAsync(sql, heading, co, ct);
        var baseDir = CobolText.Trimmed(ctx.Paths.Base, 160);
        if (!ArBatch.TryOpenSpool(ctx, Code, co, out var spool)) return 12;

        var rc = 0;
        var rows = 0;
        int tDocs = 0, skipped = 0;
        decimal tNet = 0, tVat = 0;
        var documents = new JsonArray();

        using (spool)
        {
            var rw = new ArReportWriter(spool, heading, firstDetail: 8, lastDetail: 54);

            // 0370-READ-SUPPLIER
            string sName = "", sVat = "", sRegNo = "", sLine1 = "", sTown = "", sPost = "", sCountry = "GB", sJuris = "";
            try
            {
                var s = await sql.SingleAsync(
                    "SELECT Name, ISNULL(VatRegNo,''), ISNULL(RegisteredNumber,''), ISNULL(RegOfficeLine1,''), ISNULL(RegOfficeTown,''), " +
                    "ISNULL(RegOfficePostcode,''), CASE WHEN LEN(ISNULL(RegOfficeCountry,'')) = 2 THEN RegOfficeCountry ELSE 'GB' END, " +
                    "Jurisdiction FROM core.Company WHERE CompanyCode = ?",
                    [SqlioParm.Text(co)], ArBatch.Widths(26, 200), ct);
                if (!s.Found) throw new InvalidOperationException();
                sName = CobolText.Trimmed(s.Raw(0), 100);
                sVat = CobolText.Trimmed(s.Raw(1), 15);
                sRegNo = CobolText.Trimmed(s.Raw(2), 10);
                sLine1 = CobolText.Trimmed(s.Raw(3), 60);
                sTown = CobolText.Trimmed(s.Raw(4), 40);
                sPost = CobolText.Trimmed(s.Raw(5), 10);
                sCountry = CobolText.Fit(s.Raw(6), 2);
                sJuris = CobolText.Trimmed(s.Raw(7), 30);
            }
            catch (Exception ex) when (ex is SqlioException or InvalidOperationException)
            {
                rc = 12;
                ctx.Display("STERP-E-0371 company not found");
            }

            // 1000-EXTRACT-DOCS
            var stmt = "SELECT CONVERT(varchar(24),i.InvoiceId), i.DocumentType, i.InvoiceNumber, CONVERT(char(8),i.DocumentDate,112), " +
                "CONVERT(char(8),i.TaxPointDate,112), CONVERT(char(8),i.DueDate,112), i.CurrencyCode, CONVERT(varchar(24),i.NetAmount), " +
                "CONVERT(varchar(24),i.VatAmount), CONVERT(varchar(24),i.GrossAmount), CONVERT(varchar(24),i.VatAmountGBP), i.CustomerCode, " +
                "ISNULL(i.BillToName,c.LegalName), ISNULL(i.BillToAddress,''), ISNULL(i.CustomerVatRegNo,''), ISNULL(i.SpecialWording,''), " +
                "ISNULL(i.CustomerOrderRef,''), ISNULL(i.OriginalInvoiceNumber,''), ISNULL(i.TermsSnapshot,''), CONVERT(varchar(24)," +
                "ISNULL(i.SettlementDiscountPct,0)), CONVERT(varchar(24),i.DepositAppliedGBP), CASE WHEN LEN(ISNULL(c.CountryCode,'')) = 2 " +
                "THEN c.CountryCode ELSE 'GB' END, ISNULL(i.SalesOrderNumber,''), CONVERT(varchar(24),i.FxRate) FROM ar.SalesInvoice i JOIN " +
                "ar.Customer c ON c.CompanyCode = i.CompanyCode AND c.CustomerCode = i.CustomerCode WHERE i.CompanyCode = ? AND " +
                "i.Status = 'POSTED' AND i.DocumentType IN ('INVOICE','CREDITNOTE')";
            var parms = new List<SqlioParm> { SqlioParm.Text(co) };
            if (inv.Length > 0)
            {
                parms.Add(SqlioParm.Text(inv));
                stmt += " AND i.InvoiceNumber = ?";
            }
            else
            {
                parms.Add(SqlioParm.Text(ArBatch.D8(from)));
                parms.Add(SqlioParm.Text(ArBatch.D8(to)));
                stmt += " AND i.DocumentDate BETWEEN ? AND ?";
            }
            stmt += " ORDER BY i.InvoiceNumber";
            IReadOnlyList<SqlioRow> docs = [];
            try
            {
                docs = await sql.FetchAllAsync(stmt, parms, ArBatch.Widths(26, 200), ct);
            }
            catch (SqlioException ex)
            {
                rc = 12;
                ctx.Display("STERP-E-1001 " + CobolText.Fit(ex.DriverText, 100));
            }
            foreach (var b in docs)
            {
                // 1100-ONE-DOCUMENT
                var invId = CobolText.Trimmed(b.Raw(0), 20);
                var dType = CobolText.Trimmed(b.Raw(1), 12);
                var dNumber = CobolText.Trimmed(b.Raw(2), 30);
                var dCust = CobolText.Trimmed(b.Raw(11), 20);
                var cur = CobolText.Fit(b.Raw(6), 3);
                var dNet = CobolNumeric.Store(b.NumVal(7), 13, 2);
                var dVat = CobolNumeric.Store(b.NumVal(8), 13, 2);
                var dGross = CobolNumeric.Store(b.NumVal(9), 13, 2);
                var dVatGbp = CobolNumeric.Store(b.NumVal(10), 13, 2);
                var dDate = ArBatch.Uk(ArBatch.Date8(b.Raw(3)[..8]));
                var credit = CobolText.Fit(dType, 10) == "CREDITNOTE";
                var root = credit ? "CreditNote" : "Invoice";
                var lineTag = credit ? "cac:CreditNoteLine" : "cac:InvoiceLine";
                var qtyTag = credit ? "cbc:CreditedQuantity" : "cbc:InvoicedQuantity";

                // 1150-BUILD-NAME
                var xmlName = CobolText.Trimmed(baseDir.Trim(' ') + "\\einvoice\\" + co + "-" + dNumber.Trim(' ') + ".xml", 200);
                var dFile = CobolText.Trimmed(co + "-" + dNumber.Trim(' ') + ".xml", 60);
                StreamWriter w;
                try
                {
                    ctx.Paths.Folder("einvoice");
                    w = new StreamWriter(new FileStream(xmlName, FileMode.Create, FileAccess.Write, FileShare.Read), CobolText.Ansi) { NewLine = "\r\n" };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    skipped++;
                    rc = 4;
                    ctx.Display("STERP-W-1101 cannot write " + CobolText.Fit(xmlName, 200));
                    continue;
                }
                var lines = 0;
                using (w)
                {
                    var x = new XmlOut(w);
                    // 2000-WRITE-HEADER
                    x.Rec("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                    x.Rec("<" + root + " xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:" + root + "-2\"");
                    x.Rec("    xmlns:cac=\"urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2\"");
                    x.Rec("    xmlns:cbc=\"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2\">");
                    x.Rec("  <cbc:CustomizationID>urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0</cbc:CustomizationID>");
                    x.Rec("  <cbc:ProfileID>urn:fdc:peppol.eu:2017:poacc:billing:01:1.0</cbc:ProfileID>");
                    x.Tag("cbc:ID", dNumber);
                    x.Tag("cbc:IssueDate", Iso(b.Raw(3)));
                    x.Tag("cbc:DueDate", Iso(b.Raw(5)));
                    if (credit) x.Tag("cbc:CreditNoteTypeCode", "381");
                    else x.Tag("cbc:InvoiceTypeCode", "380");
                    x.Tag("cbc:TaxPointDate", Iso(b.Raw(4)));
                    if (!CobolText.IsSpaces(b.Raw(15))) x.Tag("cbc:Note", b.Raw(15));
                    x.Tag("cbc:DocumentCurrencyCode", cur);
                    x.Tag("cbc:TaxCurrencyCode", "GBP");
                    if (!CobolText.IsSpaces(b.Raw(16))) x.Tag("cbc:BuyerReference", b.Raw(16));
                    // 2100-WRITE-REFS
                    if (!CobolText.IsSpaces(b.Raw(22)))
                    {
                        x.Line("  <cac:OrderReference>");
                        x.Tag("cbc:ID", b.Raw(22));
                        x.Line("  </cac:OrderReference>");
                    }
                    if (!CobolText.IsSpaces(b.Raw(17)))
                    {
                        x.Line("  <cac:BillingReference>");
                        x.Line("    <cac:InvoiceDocumentReference>");
                        x.Tag("cbc:ID", b.Raw(17));
                        x.Line("    </cac:InvoiceDocumentReference>");
                        x.Line("  </cac:BillingReference>");
                    }

                    // 2200-WRITE-PARTIES (+2300-WRITE-ADDRESS)
                    void Address(string street, string city, string post, string country)
                    {
                        x.Line("      <cac:PostalAddress>");
                        x.Tag("cbc:StreetName", street);
                        if (!CobolText.IsSpaces(city)) x.Tag("cbc:CityName", city);
                        if (!CobolText.IsSpaces(post)) x.Tag("cbc:PostalZone", CobolText.Fit(post, 10));
                        x.Line("        <cac:Country>");
                        x.Tag("cbc:IdentificationCode", CobolText.Fit(country, 2));
                        x.Line("        </cac:Country>");
                        x.Line("      </cac:PostalAddress>");
                    }
                    x.Line("  <cac:AccountingSupplierParty>");
                    x.Line("    <cac:Party>");
                    x.Tag("cbc:EndpointID", sVat);
                    x.Line("      <cac:PartyName>");
                    x.Tag("cbc:Name", sName);
                    x.Line("      </cac:PartyName>");
                    Address(sLine1, sTown, sPost, sCountry);
                    x.Line("      <cac:PartyTaxScheme>");
                    x.Tag("cbc:CompanyID", sVat);
                    x.Line("        <cac:TaxScheme><cbc:ID>VAT</cbc:ID>");
                    x.Line("        </cac:TaxScheme>");
                    x.Line("      </cac:PartyTaxScheme>");
                    x.Line("      <cac:PartyLegalEntity>");
                    x.Tag("cbc:RegistrationName", sName);
                    x.Tag("cbc:CompanyID", sRegNo);
                    x.Tag("cbc:CompanyLegalForm", "Registered in " + sJuris.Trim(' '));
                    x.Line("      </cac:PartyLegalEntity>");
                    x.Line("    </cac:Party>");
                    x.Line("  </cac:AccountingSupplierParty>");
                    // 2250-WRITE-BUYER
                    x.Line("  <cac:AccountingCustomerParty>");
                    x.Line("    <cac:Party>");
                    x.Tag("cbc:EndpointID", CobolText.IsSpaces(b.Raw(14)) ? dCust : b.Raw(14));
                    x.Line("      <cac:PartyName>");
                    x.Tag("cbc:Name", b.Raw(12));
                    x.Line("      </cac:PartyName>");
                    Address(b.Raw(13), "", "", CobolText.Fit(b.Raw(21), 2));
                    if (!CobolText.IsSpaces(b.Raw(14)))
                    {
                        x.Line("      <cac:PartyTaxScheme>");
                        x.Tag("cbc:CompanyID", b.Raw(14));
                        x.Line("        <cac:TaxScheme><cbc:ID>VAT</cbc:ID>");
                        x.Line("        </cac:TaxScheme>");
                        x.Line("      </cac:PartyTaxScheme>");
                    }
                    x.Line("      <cac:PartyLegalEntity>");
                    x.Tag("cbc:RegistrationName", b.Raw(12));
                    x.Line("      </cac:PartyLegalEntity>");
                    x.Line("    </cac:Party>");
                    x.Line("  </cac:AccountingCustomerParty>");

                    // 2400-WRITE-PAYMENT
                    x.Line("  <cac:PaymentMeans>");
                    x.Tag("cbc:PaymentMeansCode", "31");
                    x.Tag("cbc:PaymentID", dNumber);
                    x.Line("  </cac:PaymentMeans>");
                    x.Line("  <cac:PaymentTerms>");
                    var discPct = CobolNumeric.Store(b.NumVal(19), 5, 6);
                    x.Tag("cbc:Note", discPct > 0
                        ? b.Raw(18).Trim(' ') + " - prompt payment discount " + Domain.ArEdit.Rate(discPct) + " per cent"
                        : b.Raw(18));
                    x.Line("  </cac:PaymentTerms>");

                    // 2500-WRITE-TAX (+2550/2560 subtotals)
                    x.Line("  <cac:TaxTotal>");
                    x.Money("cbc:TaxAmount", cur, dVat);
                    IReadOnlyList<SqlioRow>? vats = null;
                    try
                    {
                        vats = await sql.FetchAllAsync(
                            "SELECT v.VatCode, CONVERT(varchar(24),v.RatePct), CONVERT(varchar(24),v.NetAmount), CONVERT(varchar(24),v.VatAmount) " +
                            "FROM ar.SalesInvoiceVat v WHERE v.InvoiceId = ? ORDER BY v.VatCode",
                            [SqlioParm.Text(invId)], ArBatch.Widths(26, 200), ct);
                    }
                    catch (SqlioException)
                    {
                        vats = null;
                    }
                    foreach (var v in vats ?? [])
                    {
                        var pct = CobolNumeric.Store(v.NumVal(1), 5, 6);
                        var pctText = CobolEdit.Format(pct, "-(5)9.99");
                        x.Line("    <cac:TaxSubtotal>");
                        x.Money("cbc:TaxableAmount", cur, v.NumVal(2));
                        x.Money("cbc:TaxAmount", cur, v.NumVal(3));
                        x.Line("      <cac:TaxCategory>");
                        x.Tag("cbc:ID", pct > 0 ? "S" : "Z");
                        x.Tag("cbc:Percent", pctText);
                        x.Line("        <cac:TaxScheme><cbc:ID>VAT</cbc:ID>");
                        x.Line("        </cac:TaxScheme>");
                        x.Line("      </cac:TaxCategory>");
                        x.Line("    </cac:TaxSubtotal>");
                    }
                    x.Line("  </cac:TaxTotal>");
                    if (cur != "GBP")
                    {
                        x.Line("  <cac:TaxTotal>");
                        x.Rec("    <cbc:TaxAmount currencyID=\"GBP\">" + Domain.ArEdit.Money(dVatGbp) + "</cbc:TaxAmount>");
                        x.Line("  </cac:TaxTotal>");
                    }

                    // 2600-WRITE-TOTALS
                    x.Line("  <cac:LegalMonetaryTotal>");
                    x.Money("cbc:LineExtensionAmount", cur, dNet);
                    x.Money("cbc:TaxExclusiveAmount", cur, dNet);
                    x.Money("cbc:TaxInclusiveAmount", cur, dGross);
                    var prepaid = CobolNumeric.Store(b.NumVal(20), 13, 2);
                    x.Money("cbc:PrepaidAmount", cur, prepaid);
                    x.Money("cbc:PayableAmount", cur, dGross - prepaid);
                    x.Line("  </cac:LegalMonetaryTotal>");

                    // 2700-WRITE-LINES (+2750/2780/2790)
                    IReadOnlyList<SqlioRow>? lineRows = null;
                    try
                    {
                        lineRows = await sql.FetchAllAsync(
                            "SELECT CONVERT(varchar(12),l.LineNum), ISNULL(l.ItemCode,''), l.Description, CONVERT(varchar(24),l.Quantity), " +
                            "ISNULL(l.UomCode,'EA'), CONVERT(varchar(24),l.UnitPrice), CONVERT(varchar(24),l.NetAmount), l.VatCode, " +
                            "CONVERT(varchar(24),l.VatRatePct), CONVERT(varchar(24),l.AllowanceAmount), ISNULL(l.VatMechanism,''), " +
                            "ISNULL(l.VatLiability,'') FROM ar.SalesInvoiceLine l WHERE l.InvoiceId = ? ORDER BY l.LineNum",
                            [SqlioParm.Text(invId)], ArBatch.Widths(26, 200), ct);
                    }
                    catch (SqlioException)
                    {
                        lineRows = null;
                    }
                    foreach (var l in lineRows ?? [])
                    {
                        lines++;
                        var pct = CobolNumeric.Store(l.NumVal(8), 5, 6);
                        var pctText = CobolEdit.Format(pct, "-(5)9.99");
                        var taxCat = CobolText.Fit(l.Raw(10), 7) == "REVERSE" ? "AE"
                            : CobolText.Fit(l.Raw(11), 6) == "EXEMPT" ? "E"
                            : pct > 0 ? "S" : "Z";
                        x.Line("  <" + lineTag + ">");
                        x.Tag("cbc:ID", l.Raw(0));
                        var qty = CobolNumeric.Store(l.NumVal(3), 9, 6);
                        x.Rec("    <" + qtyTag + " unitCode=\"" + l.Raw(4).Trim(' ') + "\">" + Domain.ArEdit.Qty(qty) + "</" + qtyTag + ">");
                        x.Money("cbc:LineExtensionAmount", cur, l.NumVal(6));
                        var allowance = CobolNumeric.Store(l.NumVal(9), 13, 2);
                        if (allowance != 0)
                        {
                            x.Line("    <cac:AllowanceCharge>");
                            x.Tag("cbc:ChargeIndicator", "false");
                            x.Tag("cbc:AllowanceChargeReason", "Discount");
                            x.Money("cbc:Amount", cur, allowance);
                            x.Line("    </cac:AllowanceCharge>");
                        }
                        x.Line("    <cac:Item>");
                        x.Tag("cbc:Description", l.Raw(2));
                        x.Tag("cbc:Name", l.Raw(2));
                        if (!CobolText.IsSpaces(l.Raw(1)))
                        {
                            x.Line("      <cac:SellersItemIdentification>");
                            x.Tag("cbc:ID", l.Raw(1));
                            x.Line("      </cac:SellersItemIdentification>");
                        }
                        x.Line("      <cac:ClassifiedTaxCategory>");
                        x.Tag("cbc:ID", taxCat);
                        x.Tag("cbc:Percent", pctText);
                        x.Line("        <cac:TaxScheme><cbc:ID>VAT</cbc:ID>");
                        x.Line("        </cac:TaxScheme>");
                        x.Line("      </cac:ClassifiedTaxCategory>");
                        x.Line("    </cac:Item>");
                        x.Line("    <cac:Price>");
                        x.Money("cbc:PriceAmount", cur, l.NumVal(5));
                        x.Line("    </cac:Price>");
                        x.Line("  </" + lineTag + ">");
                    }
                    x.Rec("</" + root + ">");
                }
                tDocs++;
                rows++;
                tNet += dNet;
                tVat += dVat;
                rw.Begin();
                rw.Detail(
                    L(1, Cols().Put(1, dType, 11).Put(13, dNumber, 30).Put(45, dDate, 10).Put(57, dCust, 20).Put(78, cur, 3)
                        .Put(82, ArBatch.M13(dNet), 13).Put(97, ArBatch.M13(dVat), 13).Put(112, ArBatch.M13(dGross), 13)
                        .Put(128, CobolEdit.Field(lines, "ZZZZ9"), 5)),
                    L(1, Cols().Put(5, "XML file", 10).Put(16, dFile, 60).Put(80, "VAT in sterling", 16).Put(98, ArBatch.M13(dVatGbp), 13)));
                rw.End();
                documents.Add(new JsonObject
                {
                    ["documentType"] = dType, ["documentNumber"] = dNumber, ["documentDate"] = dDate, ["customerCode"] = dCust,
                    ["currencyCode"] = cur, ["netAmount"] = dNet, ["vatAmount"] = dVat, ["grossAmount"] = dGross, ["vatAmountGbp"] = dVatGbp,
                    ["lines"] = lines, ["xmlFile"] = xmlName,
                });
            }

            // 0400-CLOSE-SPOOL: TERMINATE R-EINVUBL
            rw.Terminate(() => rw.Group(
                L(2, Cols().Put(1, "Documents extracted", 22).Put(24, CobolEdit.Field(tDocs, "ZZZ,ZZ9"), 7).Put(36, "Not extracted", 20)
                    .Put(57, CobolEdit.Field(skipped, "ZZZ,ZZ9"), 7)),
                L(1, Cols().Put(1, "Total net", 22).Put(24, ArBatch.M17(tNet), 17).Put(48, "Total VAT", 16).Put(65, ArBatch.M17(tVat), 17)),
                L(2, Cols().Put(1, "Peppol BIS Billing 3.0, UBL 2.1.  The structured", 48)
                    .Put(50, "and readable copies carry identical values.", 46))));
        }
        ctx.Display("STERP-I-0401 " + ArBatch.Num(tDocs, 7) + " e-invoice document(s)");
        ctx.AnnounceSpool(spool.Path);
        ctx.RowsProcessed = rows;
        ctx.Result = new JsonObject
        {
            ["jobName"] = Code, ["companyCode"] = co, ["documentNumber"] = inv, ["from"] = inv.Length > 0 ? null : ArBatch.D8(from),
            ["to"] = inv.Length > 0 ? null : ArBatch.D8(to), ["documents"] = documents, ["documentsExtracted"] = tDocs, ["notExtracted"] = skipped,
            ["totalNet"] = tNet, ["totalVat"] = tVat, ["returnCode"] = rc,
        };
        return rc;
    }
}
