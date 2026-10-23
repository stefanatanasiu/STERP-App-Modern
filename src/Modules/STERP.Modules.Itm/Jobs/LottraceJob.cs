using Sterp.Modules.Itm.Contracts;
using Sterp.Modules.Itm.Data;
using Sterp.Modules.Itm.Services;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Itm.Jobs;

/// <summary>
/// LOTTRACE (cobol/batch/itm/LOTTRACE.cob; FR-STERP-ITM-033): lot and serial traceability report - LOTSER EXPCHK banner and
/// LOTSER TRACE movements classified Quality, Forward or Backward; MODE=FORWARD/BACKWARD filter the print only.
/// </summary>
internal static class LottraceJob
{
    public static readonly JobDefinition Definition = new()
    {
        JobCode = "LOTTRACE",
        Slice = "STERP-ITM",
        SubmitOperationId = "itm_submitLottraceRun",
        ParameterOrder = ["CO", "SITE", "USER", "MODE", "ASAT", "ITEM", "LOT", "SERIAL", "FROM", "TO"],
        PreCheck = pre =>
        {
            var a = Arguments.From(pre.Args);
            // 0100-READ-ARGS refusals end RC 8 before the program's JOBLOG START (no spool)
            if (Cob.Spaces(a.Lot) && Cob.Spaces(a.Serial))
            {
                return Task.FromResult<LegacyResult?>(LegacyResult.Error("E-0101", "STERP-E-0101 LOT= or SERIAL= is required"));
            }
            if (Cob.Spaces(a.Item))
            {
                return Task.FromResult<LegacyResult?>(LegacyResult.Error("E-0102", "STERP-E-0102 ITEM= is required"));
            }
            return Task.FromResult<LegacyResult?>(null);
        },
        SelfRegistration = pre =>
        {
            var a = Arguments.From(pre.Args);
            return ItmJobSubmission.JoblogStart("LOTTRACE", a.Co, a.User, "", pre.Args.Text);
        },
        EndValues = ItmJobSubmission.NeverEnded,
        Execute = RunAsync,
    };

    private sealed record Arguments(string Co, string Site, string Item, string Lot, string Serial, string User, int AsAt, string Mode)
    {
        public static Arguments From(Sterp.Platform.Kernel.Jobs.CommandLineArgs a)
        {
            var co = Cob.X(a.Co, 10);
            var user = Cob.X(a.User, 20);
            var mode = Cob.X(a.Mode, 10);
            var item = a.Get("ITEM") is { } i ? Cob.X(i, 40) : "";
            var lot = a.Get("LOT") is { } l ? Cob.X(l, 30) : "";
            var serial = a.Get("SERIAL") is { } s ? Cob.X(s, 40) : "";
            var asat = a.AsAt.Length > 0 ? Batch.Date8(a.AsAt) : 0;
            if (Cob.Spaces(co)) co = "STC";
            if (Cob.Spaces(user)) user = "QA1";
            if (Cob.Spaces(mode)) mode = "BOTH";
            if (asat == 0) asat = Batch.Today();
            return new Arguments(co, Cob.X(a.Site, 10), item, lot, serial, user, asat, mode);
        }
    }

    private static async Task<int> RunAsync(JobRunContext ctx)
    {
        var ct = ctx.Cancellation;
        var a = Arguments.From(ctx.Args);
        ctx.Display("STERP-I-0100 LOTTRACE genealogy for " + Cob.Trim(a.Item));
        var sql = ctx.Get<Sqlio>();
        var lotSer = ctx.Get<ILotSerialService>();
        var rc = 0;

        // 1000-READ-TRACE: LOTSER EXPCHK for the banner, then TRACE
        var call = new ItmCall(a.Co, a.Site, a.User, Cob.Date(a.AsAt));
        var ls = Reset(a);
        var exp = await lotSer.ExecuteAsync("EXPCHK", ls, call, ct);
        string status;
        if (exp.ReturnCode == 0) status = "Usable at the as at date";
        else if (exp.ReasonCode == "LOT0008")
        {
            status = "EXPIRED";
            rc = 4;
        }
        else if (exp.ReasonCode == "LOT0009")
        {
            status = "Inside minimum shelf life";
            rc = 4;
        }
        else status = Cob.X(exp.Message, 30);
        var banner = new Banner
        {
            Status = Cob.X(status, 30),
            Days = ls.DaysRemaining,
            Expiry = ls.ExpiryDate is { } e ? e.ToString("dd'/'MM'/'yyyy", System.Globalization.CultureInfo.InvariantCulture) : "none held",
            Supplier = Cob.X(ls.SupplierCode, 20),
            SupLot = Cob.X(ls.SupplierLot, 40),
            Cert = Cob.X(ls.CertificateRef, 40),
        };
        ls = Reset(a);
        var trace = await lotSer.ExecuteAsync("TRACE", ls, call, ct);
        if (trace.ReturnCode > 8)
        {
            ctx.Display("STERP-E-1001 " + Batch.Field(trace.Message, 200));
            return 12;
        }
        if (ls.Trace.Count == 0)
        {
            rc = 4;
            ctx.Display("STERP-W-1002 no movements for that identity");
        }
        banner.Identity = Cob.X(!Cob.Spaces(a.Serial) ? "Serial " + Cob.Trim(a.Serial) : "Lot " + Cob.Trim(a.Lot), 44);
        banner.Item = Cob.X(a.Item, 40);
        var desc = await sql.SingleAsync("SELECT Description FROM inv.Item WHERE CompanyCode = @p1 AND ItemCode = @p2", [a.Co, a.Item], ct);
        if (desc.SqlCode == 0) banner.Desc = Cob.X(Col.Text(desc.Row, 0), 40);
        if (rc > 4) return rc;

        // 0300-OPEN-SPOOL: RUNPATH, 0350 headings, OPEN OUTPUT, INITIATE
        var built = Batch.OpenSpool(ctx, "LOTTRACE", a.Co, out var opened, out var spoolName);
        if (built.ReturnCode > 4)
        {
            ctx.Display("STERP-E-0301 cannot build the spool path");
            return 12;
        }
        var heading = await Batch.HeadingAsync(sql, a.Co, "LOTTRACE", "Lot and serial traceability report",
            "Trace direction " + Cob.Trim(a.Mode), Batch.AsAtText(a.AsAt), a.User, ct);
        if (opened is null)
        {
            ctx.Display("STERP-E-0302 cannot open " + Batch.Field(spoolName, 200));
            return 12;
        }
        var rowsOut = 0;
        using (opened)
        {
            var l = new TraceLine();
            var t = new Totals();
            var rw = Report(opened, heading, banner, t);
            rw.Initiate();
            var line = RwLine.Plus1(() => new RwText().Ed(1, l.Seq, "ZZ9").Src(5, l.Direction, 8).Src(15, l.Type, 14).Src(30, l.Date, 10)
                .Ed(42, l.Qty, "Z(10)9.999-").Src(60, l.Site, 10).Src(71, l.Location, 18).Src(90, l.DocType, 16).Src(107, l.DocRef, 13)
                .Src(121, l.Who, 12).Text);
            // 1200-PRINT-TRACE
            for (var tx = 0; tx < ls.Trace.Count; tx++)
            {
                var tr = ls.Trace[tx];
                l.Seq = tx + 1;
                l.Type = Cob.X(tr.Type, 14);
                l.Qty = CobolNumeric.Store(tr.Qty, 11, 3);
                l.Site = Cob.X(tr.Site, 10);
                l.Location = Cob.X(tr.Location, 18);
                l.DocType = Cob.X(tr.DocType, 16);
                l.DocRef = Cob.X(tr.DocRef, 13);
                l.Who = Cob.X(tr.User, 12);
                l.Date = tr.Date is { } td ? td.ToString("dd'/'MM'/'yyyy", System.Globalization.CultureInfo.InvariantCulture) : "00/00/0000";
                if (tr.Type is "HOLD" or "RELEASE")
                {
                    l.Direction = "Quality";
                    t.Holds++;
                }
                else if (tr.Qty < 0)
                {
                    l.Direction = "Forward";
                    t.Out = CobolNumeric.Store(t.Out - tr.Qty, 13, 3);
                }
                else
                {
                    l.Direction = "Backward";
                    t.In = CobolNumeric.Store(t.In + tr.Qty, 13, 3);
                }
                t.Moves++;
                if (a.Mode == "FORWARD" && l.Direction == "Backward") continue;
                if (a.Mode == "BACKWARD" && l.Direction == "Forward") continue;
                rowsOut++;
                rw.Generate(line);
            }
            // 0400-CLOSE-SPOOL
            rw.Terminate();
        }
        ctx.Display("STERP-I-0401 " + Batch.Disp(rowsOut, 9) + " traced movement(s)");
        ctx.AnnounceSpool(spoolName.Trim(' '));
        return rc;
    }

    /// <summary>1050-RESET-LS.</summary>
    private static LotserCommarea Reset(Arguments a) => new()
    {
        ItemCode = a.Item,
        LotCode = a.Lot,
        SerialNo = a.Serial,
        Site = a.Site,
        AsAt = Cob.Date(a.AsAt),
    };

    /// <summary>
    /// R-LOTTRCE: PAGE LIMIT 60, HEADING 1, FIRST DETAIL 15, LAST DETAIL 56, CONTROLS ARE FINAL; RH-BANNER (REPORT HEADING, LINE
    /// 8-11, 13, 14), DE-LINE and CF-FINAL.
    /// </summary>
    private static GnuReportWriter Report(SpoolWriter spool, ReportHeading heading, Banner b, Totals t) =>
        new(spool.WriteLine, heading, 60, 1, 15, 56, controls: null,
            finalFooting:
            [
                RwLine.PlusN(2, () => new RwText().Lit(1, "Traced movements:").Ed(20, t.Moves, "Z(4)9").Lit(30, "Quantity in:").Ed(44, t.In, "Z(12)9.999")
                    .Lit(62, "Quantity out:").Ed(77, t.Out, "Z(12)9.999").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "Hold actions:").Ed(20, t.Holds, "Z(4)9").Text),
                RwLine.PlusN(2, () => new RwText().Lit(1, "Backward lines brought the identity into stock").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "and forward lines consumed or despatched it.").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "A hold changes availability, deleting nothing.").Text),
            ],
            reportHeading:
            [
                RwLine.At(8, () => new RwText().Lit(1, "Identity: ").Src(11, b.Identity, 44).Lit(58, "Item: ").Src(64, b.Item, 40).Text),
                RwLine.At(9, () => new RwText().Lit(1, "Description: ").Src(14, b.Desc, 40).Lit(58, "Supplier ").Src(67, b.Supplier, 20)
                    .Lit(90, "Sup lot: ").Src(99, b.SupLot, 34).Text),
                RwLine.At(10, () => new RwText().Lit(1, "Expiry: ").Src(10, b.Expiry, 10).Lit(23, "Days remaining: ").Ed(39, b.Days, "-(6)9")
                    .Lit(50, "Status: ").Src(58, b.Status, 30).Text),
                RwLine.At(11, () => new RwText().Lit(1, "Certificate: ").Src(14, b.Cert, 40).Text),
                RwLine.At(13, () => new RwText().Lit(1, "Seq").Lit(5, "Direction").Lit(15, "Movement ").Lit(30, "Date").Lit(45, "Quantity")
                    .Lit(60, "Site").Lit(71, "Location").Lit(90, "Document").Lit(107, "Reference").Lit(121, "Actor ").Text),
                RwLine.At(14, () => new RwText().Lit(1, new string('-', 66)).Lit(67, new string('-', 66)).Text),
            ]);

    private sealed class Banner
    {
        public string Identity = "";
        public string Item = "";
        public string Desc = "";
        public string Expiry = "";
        public int Days;
        public string Status = "";
        public string Supplier = "";
        public string SupLot = "";
        public string Cert = "";
    }

    /// <summary>WS-LINE (L- fields of DE-LINE).</summary>
    private sealed class TraceLine
    {
        public int Seq;
        public string Direction = "";
        public string Type = "";
        public string Date = "";
        public decimal Qty;
        public string Site = "";
        public string Location = "";
        public string DocType = "";
        public string DocRef = "";
        public string Who = "";
    }

    /// <summary>WS-TOT.</summary>
    private sealed class Totals
    {
        public decimal In;
        public decimal Out;
        public int Holds;
        public int Moves;
    }
}
