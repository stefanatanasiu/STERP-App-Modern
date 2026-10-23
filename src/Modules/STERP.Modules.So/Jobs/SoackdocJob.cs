using System.Text.Json.Nodes;
using Sterp.Modules.So.Domain;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Jobs;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.So.Jobs;

/// <summary>
/// SOACKDOC (cobol/batch/so/SOACKDOC.cob; FR-STERP-SO-031): prints the order acknowledgement (DOC=ACK), pick note (PICK) or
/// despatch note (DESP) with its version (1 + AMEND/AMENDED/REVISE audit rows) and reprint count. After the spool is
/// closed: PICK adds 1 to so.PickList.PrintCount, ACK stamps AcknowledgementIssuedAt only while it is NULL, and one PRINT
/// audit row is written - each statement autocommits; a failure warns (W-1809..1811, RC 4).
/// </summary>
internal static class SoackdocJob
{
    public const string Code = "SOACKDOC";

    public static JobDefinition Definition => new()
    {
        JobCode = Code,
        Slice = SoBatch.Slice,
        SubmitOperationId = "so_submitSoackdocRun",
        ParameterOrder = ["CO", "SITE", "DOC", "REF", "ORDER", "PICK", "DESP", "USER"],
        // 0100-READ-ARGS ends RC 8 (E-1800/E-1801) before 0200-JOB-START: the program never registers itself
        SelfRegistration = pre => Arguments.Read(pre.Args) is { Valid: true } a ? SoBatch.JoblogStart(Code, a.Co, a.User, "", pre.CommandLine) : null,
        EndValues = SoBatch.End,
        Execute = RunAsync,
    };

    /// <summary>0100-READ-ARGS / 0150-FIND-REF: CO, SITE, USER fixed fields; DOC by key; the first of REF, ORDER, PICK, DESP present.</summary>
    private sealed record Arguments(string Co, string Site, string User, string Doc, string Ref)
    {
        public bool Valid => Doc is "ACK" or "PICK" or "DESP" && !Cob.Spaces(Ref);

        public static Arguments Read(CommandLineArgs a)
        {
            var co = Cob.X(a.Co, 10);
            var user = Cob.X(a.User, 20);
            var doc = Cob.X(a.Get("DOC"), 6);
            var reference = "";
            foreach (var k in new[] { "REF", "ORDER", "PICK", "DESP" })
            {
                if (a.Get(k) is { } v)
                {
                    reference = Cob.X(v, 30);
                    break;
                }
            }
            if (Cob.Spaces(co)) co = "STC";
            if (Cob.Spaces(user)) user = "SALESREP";
            if (Cob.Spaces(doc)) doc = "ACK";
            return new Arguments(co, Cob.X(a.Site, 10), user, doc, reference);
        }
    }

    /// <summary>WS-HDR and the page-heading items of WS-DET.</summary>
    private sealed class Head
    {
        public string Ref = "", Cust = "", CustName = "", Order = "", Date = "", CustRef = "", Release = "", Fulfil = "", Billing = "";
        public string Address = "", Extra1 = "", Extra2 = "", Hold = "";
        public string HdQ1 = "", HdQ2 = "", HdQ3 = "", HdVal = "", HdNote = "", DocName = "";
        public int Version;
        public int Reprint;
        public string FirstIssue = "N";
    }

    private sealed class Det
    {
        public int Line;
        public string Item = "", Desc = "", Note = "";
        public decimal Qty1, Qty2, Qty3, Value;
    }

    private static async Task<int> RunAsync(JobRunContext ctx)
    {
        var ct = ctx.Cancellation;
        var a = Arguments.Read(ctx.Args);
        if (a.Doc is not ("ACK" or "PICK" or "DESP"))
        {
            ctx.Display("STERP-E-1800 DOC must be ACK, PICK or DESP");
            return 8;
        }
        if (Cob.Spaces(a.Ref))
        {
            ctx.Display("STERP-E-1801 a document reference is needed");
            return 8;
        }
        var now = DateTime.Now;
        var today = CobolDate.ToYyyymmdd(now);
        var h = new Head();
        string entity;
        switch (a.Doc)
        {
            case "ACK":
                h.DocName = "Order acknowledgement";
                entity = "so.SalesOrder";
                break;
            case "PICK":
                h.DocName = "Pick note";
                entity = "so.PickList";
                break;
            default:
                h.DocName = "Despatch note";
                entity = "so.Despatch";
                break;
        }
        ctx.Display("STERP-I-1802 SOACKDOC " + Cob.Trim(a.Doc) + " " + Cob.Trim(a.Ref));

        // 0600-READ-HEADER
        var sql = ctx.Get<ISqlRunner>();
        var rc = await ReadHeaderAsync(ctx, sql, a, h, ct);
        if (rc > 4) return rc;

        // 0700-VERSION-COUNTS
        h.Version = 1;
        try
        {
            var v = await sql.SingleAsync("SELECT 1 + COUNT(*) FROM core.AuditLog WHERE EntityType = ? AND EntityKey = ? AND Action IN " +
                "('AMEND','AMENDED','REVISE')", [SqlioParm.Text(entity), SqlioParm.Text(a.Ref)], SoBatch.Widths(20, 100), ct);
            if (v.Found) h.Version = (int)CobolNumeric.Store(v.NumVal(0), 4, 0, signed: false);
        }
        catch (SqlioException)
        {
            // SQLIO-OK not set: version stays 1
        }
        if (a.Doc != "PICK")
        {
            try
            {
                var p = await sql.SingleAsync("SELECT COUNT(*) FROM core.AuditLog WHERE EntityType = ? AND EntityKey = ? AND Action = 'PRINT' " +
                    "AND FieldName = ?", [SqlioParm.Text(entity), SqlioParm.Text(a.Ref), SqlioParm.Text(a.Doc)], SoBatch.Widths(20, 100), ct);
                if (p.Found) h.Reprint = (int)CobolNumeric.Store(p.NumVal(0), 4, 0, signed: false);
            }
            catch (SqlioException)
            {
                // reprint count unchanged
            }
        }

        // 0300-OPEN-SPOOL / 0350-FILL-HEADINGS
        var heading = SoBatch.Heading(a.Co, Code, Cob.X(h.DocName + " - " + Cob.Trim(a.Ref), 60), a.User, now);
        heading.Subtitle = h.Reprint > 0 ? "REPRINT - no business state is changed" : "First issue - no business state is changed";
        heading.AsAtText = Cob.X("Version " + CobolEdit.Field(h.Version, "-(9)9").Trim(' '), 30);
        await SoBatch.ReadCompanyAsync(sql, heading, a.Co, ct);
        if (!SoBatch.TryOpenSpool(ctx, Code, a.Co, "STERP-E-1806 cannot build the spool path", "STERP-E-1807 cannot open ", out var spool))
        {
            return 12;
        }

        // 1000-PRINT-LINES
        var d = new Det();
        int lines = 0;
        decimal sQ1 = 0, sQ2 = 0, sQ3 = 0, sVal = 0;
        var items = new JsonArray();
        using (spool)
        {
            // PH-DOC: the program's own PAGE HEADING group below STRPTHDR (LINE 8-12, 14, 15)
            RwLine[] phDoc =
            [
                RwLine.At(8, () => new RwText().Put(1, h.DocName, 26).Put(28, h.Ref, 30).Lit(60, "Version ").Num(68, h.Version, "ZZZ9")
                    .Lit(76, "Reprint ").Num(84, h.Reprint, "ZZZ9").Text),
                RwLine.At(9, () => new RwText().Lit(1, "Customer ").Put(11, h.Cust, 20).Put(32, h.CustName, 40).Lit(74, "Order ").Put(80, h.Order, 30).Text),
                RwLine.At(10, () => new RwText().Lit(1, "Date     ").Put(11, h.Date, 10).Lit(24, "Your reference   ").Put(43, h.CustRef, 40).Text),
                RwLine.At(11, () => new RwText().Lit(1, "Deliver  ").Put(11, h.Address, 60).Put(73, h.Extra1, 30).Put(104, h.Extra2, 29).Text),
                RwLine.At(12, () => new RwText().Lit(1, "Status   ").Put(11, h.Release, 14).Put(26, h.Fulfil, 14).Put(41, h.Billing, 14)
                    .Put(56, h.Hold, 50).Text),
                RwLine.At(14, () => new RwText().Lit(1, "Line").Lit(7, "Item").Lit(28, "Description").Put(69, h.HdQ1, 10).Put(80, h.HdQ2, 10)
                    .Put(91, h.HdQ3, 10).Put(102, h.HdVal, 12).Put(115, h.HdNote, 18).Text),
                RwLine.At(15, () => new string('-', 132)),
            ];
            // RD R-SOACKDC: CONTROLS ARE FINAL, PAGE LIMIT 60, HEADING 1, FIRST DETAIL 16, LAST DETAIL 50
            var w = new GnuReportWriter(spool.WriteLine, heading, 60, 1, 16, 50, null,
            [
                RwLine.Plus1(() => new string('-', 132)),
                RwLine.Plus1(() => new RwText().Lit(7, "Document totals").Num(69, sQ1, Qty).Num(80, sQ2, Qty).Num(91, sQ3, Qty)
                    .Num(102, sVal, Val).Text),
                RwLine.PlusN(2, () => new RwText().Lit(1, "This print changed no business state.  No order was").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "released, no stock was reserved or picked and no").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "despatched quantity moved.  Only the reprint counter").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "and the audit trail were written by this run.").Text),
                RwLine.PlusN(2, () => new RwText().Lit(1, "A reissue reproduces the document as it stands and").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "never conceals what the customer originally accepted.").Text),
            ], pageHeading: phDoc);
            var detail = RwLine.Plus1(() => new RwText().Num(1, d.Line, "ZZZZ9").Put(7, d.Item, 20).Put(28, d.Desc, 40).Num(69, d.Qty1, Qty)
                .Num(80, d.Qty2, Qty).Num(91, d.Qty3, Qty).Num(102, d.Value, Val).Put(115, d.Note, 18).Text);
            w.Initiate();

            var statement = a.Doc switch
            {
                "ACK" => "SELECT l.LineNum, ISNULL(l.ItemCode,''), l.Description, l.AcceptedQty, l.CancelledQty, l.DespatchedQty, l.NetAmount, " +
                    "ISNULL(l.VatCode,''), ISNULL(CONVERT(char(8),(SELECT MIN(s.CurrentPromiseDate) FROM so.DeliverySchedule s WHERE " +
                    "s.SalesOrderLineId = l.SalesOrderLineId),112),'') FROM so.SalesOrderLine l WHERE l.CompanyCode = ? AND " +
                    "l.OrderNumber = ? ORDER BY l.LineNum",
                "PICK" => "SELECT pl.LineNum, pl.ItemCode, ISNULL(pl.ShortReason,''), pl.RequiredQty, pl.PickedQty, pl.ShortQty, 0, " +
                    "ISNULL(pl.LocationCode,''), ISNULL(pl.LotCode,'') FROM so.PickListLine pl JOIN so.PickList p ON p.PickListId = " +
                    "pl.PickListId WHERE p.CompanyCode = ? AND p.PickListRef = ? ORDER BY pl.LineNum",
                _ => "SELECT dl.LineNum, dl.ItemCode, ISNULL(dl.Description,''), dl.PickedQty, dl.DespatchedQty, dl.BilledQty, 0, " +
                    "ISNULL(dl.LotCode,''), ISNULL(dl.SerialNo,'') FROM so.DespatchLine dl JOIN so.Despatch d ON d.DespatchId = " +
                    "dl.DespatchId WHERE d.CompanyCode = ? AND d.DespatchRef = ? ORDER BY dl.LineNum",
            };
            IReadOnlyList<SqlioRow> rows = [];
            try
            {
                rows = await sql.FetchAllAsync(statement, [SqlioParm.Text(a.Co), SqlioParm.Text(a.Ref)], SoBatch.Widths(9, 100), ct);
            }
            catch (SqlioException ex)
            {
                rc = 12;
                ctx.Display("STERP-E-1808 " + CobolText.Fit(ex.DriverText, 100));
            }
            foreach (var row in rows)
            {
                // 1500-EMIT
                lines++;
                d.Line = (int)CobolNumeric.Store(row.NumVal(0), 5, 0, signed: false);
                d.Item = Cob.X(row.Raw(1), 20);
                d.Desc = Cob.X(row.Raw(2), 40);
                d.Qty1 = CobolNumeric.Store(row.NumVal(3), 11, 3);
                d.Qty2 = CobolNumeric.Store(row.NumVal(4), 11, 3);
                d.Qty3 = CobolNumeric.Store(row.NumVal(5), 11, 3);
                d.Value = CobolNumeric.Store(row.NumVal(6), 11, 2);
                if (a.Doc == "ACK")
                {
                    var dw = CobolText.Fit(row.Raw(8), 8);
                    d.Note = Cob.Spaces(dw)
                        ? Cob.X(row.Raw(7), 18)
                        : Cob.X(Cob.Trim(row.Raw(7)) + " " + dw[6..8] + "/" + dw[4..6] + "/" + dw[..4], 18);
                }
                else
                {
                    d.Note = Cob.X(Cob.Trim(row.Raw(7)) + " " + Cob.Trim(row.Raw(8)), 18);
                }
                sQ1 = CobolNumeric.Store(sQ1 + d.Qty1, 13, 3);
                sQ2 = CobolNumeric.Store(sQ2 + d.Qty2, 13, 3);
                sQ3 = CobolNumeric.Store(sQ3 + d.Qty3, 13, 3);
                sVal = CobolNumeric.Store(sVal + d.Value, 13, 2);
                w.Generate(detail);
                items.Add(new JsonObject
                {
                    ["line"] = d.Line, ["item"] = d.Item, ["description"] = d.Desc, ["quantity1"] = d.Qty1, ["quantity2"] = d.Qty2,
                    ["quantity3"] = d.Qty3, ["value"] = d.Value, ["note"] = d.Note,
                });
            }

            // 0400-CLOSE-SPOOL
            w.Terminate();
        }
        ctx.Display("STERP-I-1812 " + SoBatch.Num(lines, 7) + " document line(s)");
        ctx.Display("STERP-I-1813 no business state was changed");
        ctx.AnnounceSpool(spool.Path);
        ctx.RowsProcessed = lines;

        // 2000-COUNT-AND-AUDIT: autocommit statements after the spool is closed
        if (rc <= 8)
        {
            if (a.Doc == "PICK")
            {
                try
                {
                    await sql.ExecAsync("UPDATE so.PickList SET PrintCount = PrintCount + 1 WHERE CompanyCode = ? AND PickListRef = ?",
                        [SqlioParm.Text(a.Co), SqlioParm.Text(a.Ref)], ct);
                }
                catch (SqlioException)
                {
                    rc = 4;
                    ctx.Display("STERP-W-1809 reprint counter not updated");
                }
            }
            if (a.Doc == "ACK" && h.FirstIssue == "Y")
            {
                try
                {
                    await sql.ExecAsync("UPDATE so.SalesOrder SET AcknowledgementIssuedAt = SYSDATETIME() WHERE CompanyCode = ? AND " +
                        "OrderNumber = ? AND AcknowledgementIssuedAt IS NULL", [SqlioParm.Text(a.Co), SqlioParm.Text(a.Ref)], ct);
                }
                catch (SqlioException)
                {
                    rc = 4;
                    ctx.Display("STERP-W-1810 issue stamp not written");
                }
            }
            // 2300-AUDIT
            var audit = await ctx.Get<IAuditWriter>().WriteAsync(new KernelCaller(a.Co, a.Site, a.User, today, ""), new AuditlogArea
            {
                EntityType = entity,
                EntityKey = a.Ref,
                Action = "PRINT",
                FieldName = a.Doc,
                OldValue = CobolEdit.Field(h.Reprint, "-(9)9").Trim(' '),
                NewValue = CobolEdit.Field(CobolNumeric.Store(h.Reprint + 1, 4, 0), "-(9)9").Trim(' '),
                Reason = Cob.Str(200, "Document version ", CobolEdit.Field(h.Version, "-(9)9").Trim(' '), " printed to ", spool.Path.Trim()),
                Source = Code,
                Sensitive = "N",
            }, ct);
            if (audit.Header.ReturnCode > 4)
            {
                rc = 4;
                ctx.Display("STERP-W-1811 audit record not written");
            }
        }
        ctx.Result = new JsonObject
        {
            ["jobName"] = Code, ["companyCode"] = a.Co, ["document"] = a.Doc, ["reference"] = a.Ref, ["version"] = h.Version,
            ["reprint"] = h.Reprint, ["lines"] = items, ["returnCode"] = rc,
        };
        return rc;
    }

    /// <summary>0610/0620/0630: the document header (SNGL); not found or failed = RC 16.</summary>
    private static async Task<int> ReadHeaderAsync(JobRunContext ctx, ISqlRunner sql, Arguments a, Head h, CancellationToken ct)
    {
        var (statement, missing) = a.Doc switch
        {
            "ACK" => ("SELECT o.OrderNumber, o.CustomerCode, ISNULL(c.LegalName,''), ISNULL(o.CustomerOrderRef,''), " +
                "CONVERT(char(8),o.OrderDate,112), o.ReleaseStatus, o.FulfilmentStatus, o.BillingStatus, " +
                "ISNULL(o.DeliveryAddressSnapshot,''), ISNULL(o.TermsAndConditionsVersion,''), o.CommercialStatus, " +
                "ISNULL(o.HoldReason,''), o.CurrencyCode, CASE WHEN o.AcknowledgementIssuedAt IS NULL THEN 'Y' ELSE 'N' END FROM " +
                "so.SalesOrder o LEFT JOIN ar.Customer c ON c.CompanyCode = o.CompanyCode AND c.CustomerCode = o.CustomerCode WHERE " +
                "o.CompanyCode = ? AND o.OrderNumber = ?", "STERP-E-1803 no such sales order"),
            "PICK" => ("SELECT p.PickListRef, p.CustomerCode, ISNULL(c.LegalName,''), ISNULL(p.OrderNumber,''), " +
                "CONVERT(char(8),p.RequiredDate,112), p.Status, ISNULL(p.WarehouseCode,''), p.PrintCount, ISNULL(p.PickerUser,''), " +
                "p.SiteCode FROM so.PickList p LEFT JOIN ar.Customer c ON c.CompanyCode = p.CompanyCode AND c.CustomerCode = " +
                "p.CustomerCode WHERE p.CompanyCode = ? AND p.PickListRef = ?", "STERP-E-1804 no such pick list"),
            _ => ("SELECT d.DespatchRef, d.CustomerCode, ISNULL(c.LegalName,''), ISNULL(d.OrderNumber,''), " +
                "CONVERT(char(8),d.DespatchDate,112), d.Status, ISNULL(d.CarrierCode,''), ISNULL(d.DeliveryAddressSnapshot,''), " +
                "ISNULL(d.IncotermsRule,''), ISNULL(d.CarrierReference,''), d.ExportEvidenceStatus, d.SiteCode FROM so.Despatch d " +
                "LEFT JOIN ar.Customer c ON c.CompanyCode = d.CompanyCode AND c.CustomerCode = d.CustomerCode WHERE " +
                "d.CompanyCode = ? AND d.DespatchRef = ?", "STERP-E-1805 no such despatch"),
        };
        SqlioRow? row = null;
        try
        {
            row = await sql.SingleAsync(statement, [SqlioParm.Text(a.Co), SqlioParm.Text(a.Ref)], SoBatch.Widths(20, 100), ct);
        }
        catch (SqlioException)
        {
            row = null;
        }
        if (row is not { Found: true })
        {
            ctx.Display(missing);
            return 16;
        }
        h.Ref = Cob.X(row.Raw(0), 30);
        h.Cust = Cob.X(row.Raw(1), 20);
        h.CustName = Cob.X(row.Raw(2), 40);
        switch (a.Doc)
        {
            case "ACK":
                h.Order = Cob.X(row.Raw(0), 30);
                h.CustRef = Cob.X(row.Raw(3), 40);
                h.Date = DateText(row.Raw(4));
                h.Release = Cob.X(row.Raw(5), 14);
                h.Fulfil = Cob.X(row.Raw(6), 14);
                h.Billing = Cob.X(row.Raw(7), 14);
                h.Address = Cob.X(row.Raw(8), 60);
                h.Extra1 = Cob.X("T&C version " + Cob.Trim(row.Raw(9)), 30);
                h.Extra2 = Cob.X("Commercial " + Cob.Trim(row.Raw(10)), 30);
                h.Hold = Cob.X(row.Raw(11), 50);
                h.FirstIssue = Cob.X(row.Raw(13), 1);
                h.HdQ1 = "  Accepted";
                h.HdQ2 = " Cancelled";
                h.HdQ3 = "Despatched";
                h.HdVal = Cob.X("    Net value", 12);
                h.HdNote = "Promise / VAT";
                break;
            case "PICK":
                h.Order = Cob.X(row.Raw(3), 30);
                h.Date = DateText(row.Raw(4));
                h.Fulfil = Cob.X(row.Raw(5), 14);
                h.Extra1 = Cob.X("Warehouse " + Cob.Trim(row.Raw(6)), 30);
                h.Extra2 = Cob.X("Picker " + Cob.Trim(row.Raw(8)), 30);
                h.Reprint = (int)CobolNumeric.Store(row.NumVal(7), 4, 0, signed: false);
                h.Address = Cob.X(row.Raw(9), 60);
                h.HdQ1 = "  Required";
                h.HdQ2 = "    Picked";
                h.HdQ3 = "     Short";
                h.HdVal = "";
                h.HdNote = "Location / lot";
                h.Hold = "Picking does not despatch";
                break;
            default:
                h.Order = Cob.X(row.Raw(3), 30);
                h.Date = DateText(row.Raw(4));
                h.Fulfil = Cob.X(row.Raw(5), 14);
                h.Address = Cob.X(row.Raw(7), 60);
                h.Extra1 = Cob.X("Incoterm " + Cob.Trim(row.Raw(8)) + " carrier " + Cob.Trim(row.Raw(6)), 30);
                h.Extra2 = Cob.X("Consignment " + Cob.Trim(row.Raw(9)), 30);
                h.Hold = Cob.X("Export evidence " + Cob.Trim(row.Raw(10)), 50);
                h.HdQ1 = "    Picked";
                h.HdQ2 = "Despatched";
                h.HdQ3 = "    Billed";
                h.HdVal = "";
                h.HdNote = "Lot / serial";
                break;
        }
        return 0;
    }

    /// <summary>0680-DATE-TEXT: dd/mm/yyyy from the 8 characters, spaces when blank.</summary>
    private static string DateText(string raw)
    {
        var w = CobolText.Fit(raw, 8);
        return Cob.Spaces(w) ? "" : w[6..8] + "/" + w[4..6] + "/" + w[..4];
    }

    private const string Qty = "Z(5)9.999";
    private const string Val = "Z(7)9.99-";
}
