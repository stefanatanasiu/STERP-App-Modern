using Sterp.Modules.Bom.Application;
using Sterp.Modules.Bom.Contracts;
using Sterp.Modules.Bom.Domain;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel.Data;
using static Sterp.Modules.Bom.Jobs.BomReportWriter;

namespace Sterp.Modules.Bom.Jobs;

/// <summary>
/// BOMRP010 (cobol/batch/bom/BOMRP010.cob; FR-STERP-BOM-030): the indented, flattened or costed explosion listing. The
/// report calculates nothing itself - it asks BOMLEAF (INDENT/FLATTEN) or BOMCOST (COST, GETLINE) exactly as the screens
/// do - and prints with Report Writer (RD R-BOMEXP: PAGE LIMIT 60, HEADING 1, FIRST DETAIL 11, LAST DETAIL 56, FOOTING 58).
/// Return codes: 0 (also for an incomplete calculation), 8 engine refusal, 12 no ITEM or no spool.
/// </summary>
internal static class Bomrp010Program
{
    public const string Job = "BOMRP010";

    private sealed class Totals
    {
        public int Lines;
        public int Leaf;
        public decimal Material;
        public int RoundedUp;
        public decimal TotalCost;
        public decimal UnitCost;
        public decimal ConvCost;
        public decimal Recovery;
        public string Complete = "";
        public string Stop = "";
    }

    public static JobDefinition Definition() => new()
    {
        JobCode = Job,
        Slice = "STERP-BOM",
        SubmitOperationId = "bom_submitBomrp010Run",
        ParameterOrder = ["CO", "SITE", "ITEM", "REV", "QTY", "ASAT", "LEVELS", "MODE", "COSTSET", "USER"],
        SelfRegistration = pre => BomJobSubmission.JoblogStart(pre, Job),
        EndValues = BomJobSubmission.NeverEnded,
        Execute = RunAsync,
    };

    public static async Task<int> RunAsync(JobRunContext ctx)
    {
        var ct = ctx.Cancellation;
        var a = ctx.Args;
        // 0100-READ-ARGS
        var co = Pic.X(a.Co, 10);
        var site = Pic.X(a.Site, 10);
        var user = Pic.X(a.User, 20);
        var mode = Pic.X(a.Mode, 10);
        var item = Pic.X(a.Get("ITEM"), 40);
        var rev = Pic.X(a.Get("REV"), 4);
        var qty = 0m;
        var qtyText = a.Get("QTY");
        if (!Pic.Blank(qtyText)) qty = Pic.N(BomBatch.NumVal(qtyText), 13, 6);
        var costSet = Pic.X(a.Get("COSTSET"), 20);
        var levels = 0;
        var levelsText = a.Get("LEVELS");
        if (!Pic.Blank(levelsText)) levels = Pic.I((long)decimal.Truncate(BomBatch.NumVal(levelsText)), 4);
        var asat = 0;
        if (!Pic.Blank(a.AsAt)) asat = BomBatch.Date8(BomBatch.NumVal(a.AsAt));
        if (Pic.Blank(co)) co = "STC";
        if (Pic.Blank(user)) user = "ENGINEER";
        if (Pic.Blank(mode)) mode = "INDENT";
        if (qty == 0) qty = 1;
        if (levels == 0) levels = 20;
        if (asat == 0) asat = BomBatch.Today();
        var costed = mode == "COSTED";
        var rc = 0;
        if (Pic.Blank(item))
        {
            rc = 12;
            ctx.Display("STERP-E-BOM1001 ITEM= is required");
        }
        ctx.Display("STERP-I-BOM1000 BOMRP010 explosion listing for " + Pic.T(item));

        // 0300-OPEN-SPOOL
        var sql = ctx.Get<ISqlRunner>();
        var built = BomBatch.OpenSpool(ctx, Job, co, out var spool, out var spoolName);
        if (!built.IsOk)
        {
            ctx.Display("STERP-E-BOM1002 cannot build the spool path");
            return 12;
        }
        var heading = await BomBatch.HeadingAsync(sql, co, Job, costed ? "Costed explosion" : "Indented explosion",
            costed ? Pic.Str(60, "Cost set ", Pic.T(costSet), " - one value boundary per branch") : Pic.Str(60, "Structure to level ", Pic.T(item)),
            Pic.Str(30, "Effective ", BomBatch.Dmy(asat)), user, ct);
        if (spool is null)
        {
            ctx.Display("STERP-E-BOM1003 cannot open " + BomBatch.Field(spoolName, 200));
            return 12;
        }
        if (rc > 4)
        {
            // STOP RUN closes the INITIATEd report without printing anything
            spool.Dispose();
            return rc;
        }
        var rows = 0;
        using (spool)
        {
            var rw = new BomReportWriter(spool, heading, firstDetail: 11, lastDetail: 56);
            var t = new Totals();
            var hdr = new EngineHeader(co, site, user, 0, "");
            // 1000-RUN-CALCULATION / 2000-PRINT-LINES
            if (costed)
            {
                var c = await RunCostAsync(ctx, hdr, item, rev, qty, asat, levels, costSet, t);
                if (c is null) rc = 8;
                else rows = await PrintCostedAsync(ctx, rw, c, t);
            }
            else
            {
                var leaf = ctx.Get<LeafViews>();
                var l = await RunStructureAsync(ctx, leaf, hdr, mode, item, rev, qty, asat, levels, t);
                if (l is null) rc = 8;
                else rows = await PrintStructureAsync(ctx, leaf, rw, l, mode, t);
            }
            // 0400-CLOSE-SPOOL: TERMINATE R-BOMEXP
            rw.Terminate(() => rw.Group(Footing(t)));
        }
        ctx.Display("STERP-I-BOM1004 " + BomBatch.Disp(rows, 9) + " lines written to " + Pic.T(spoolName));
        ctx.RowsProcessed = rows;
        return rc;
    }

    private static GroupLine[] Footing(Totals t) =>
    [
        L(2, Cols().Put(1, "Occurrences printed      ", 26).Put(27, BomBatch.Ed(t.Lines, "Z(6)9"), 7)
            .Put(40, "of which leaf demand: ", 22).Put(62, BomBatch.Ed(t.Leaf, "Z(6)9"), 7)
            .Put(76, "lines rounded up:      ", 24).Put(100, BomBatch.Ed(t.RoundedUp, "Z(6)9"), 7)),
        L(1, Cols().Put(1, "Material value           ", 26).Put(27, BomBatch.Ed(t.Material, "Z(11)9.99"), 15)
            .Put(48, "Conversion:   ", 14).Put(62, BomBatch.Ed(t.ConvCost, "Z(11)9.99"), 15)
            .Put(80, "Recovery credit:    ", 20).Put(100, BomBatch.Ed(t.Recovery, "Z(11)9.99"), 15)),
        L(1, Cols().Put(1, "Total value of the run   ", 26).Put(27, BomBatch.Ed(t.TotalCost, "Z(11)9.99"), 15)
            .Put(48, "Unit cost:    ", 14).Put(62, BomBatch.Ed(t.UnitCost, "Z(9)9.999999"), 17)),
        L(2, Cols().Put(1, "Completeness: ", 14).Put(15, t.Complete, 12).Put(30, t.Stop, 60)),
        // the VALUE literals are longer than their PIC X(62); the listing carries them whole
        L(1, "Intermediate lines are a manufacturing requirement, not purchases"),
        L(1, "Rounded quantities are the issue quantities (unrounded above)"),
    ];

    /// <summary>2300/2400-INDENT: the item at position depth * 2 + 1 of D-Indent-Item X(46) (1 when outside 1..20).</summary>
    private static string Indent(int depth, string item)
    {
        var ix = depth * 2 + 1;
        if (ix < 1 || ix > 20) ix = 1;
        return new string(' ', ix - 1) + Pic.X(item, 40);
    }

    // ------------------------------------------------------------------ costed (BOMCOST)

    private static async Task<BomCostArea?> RunCostAsync(JobRunContext ctx, EngineHeader h, string item, string rev, decimal qty, int asat,
        int levels, string costSet, Totals t)
    {
        var c = new BomCostArea
        {
            Function = "COST",
            Company = h.Company,
            Site = h.Site,
            User = h.User,
            BusinessDate = 0,
            ParentItem = item,
            ParentRev = rev,
            Purpose = "STANDARD",
            ReqQty = Pic.N(qty, 15, 6),
            EffDate = asat,
            MaxDepth = levels,
            CostSet = costSet,
            Currency = "GBP",
            CostDate = asat,
            Netted = "N",
        };
        var r = await ctx.Get<CostEngine>().LinkAsync(c, ctx.Cancellation);
        if (r.ReturnCode > 7)
        {
            ctx.Display("STERP-E-BOM1005 " + BomBatch.Field(r.Message, 200));
            return null;
        }
        t.Complete = Pic.X(c.Completeness, 12);
        t.TotalCost = Pic.N(c.TotalCost, 13, 2);
        t.UnitCost = Pic.N(c.UnitCost, 11, 6);
        t.ConvCost = Pic.N(c.ConversionCost, 13, 2);
        t.Recovery = Pic.N(c.RecoveryCredit, 13, 2);
        if (c.MissingRates > 0)
        {
            t.Stop = Pic.Str(60, "Rates missing on ", c.MissingRates.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                " line(s) - value is incomplete");
        }
        return c;
    }

    private static async Task<int> PrintCostedAsync(JobRunContext ctx, BomReportWriter rw, BomCostArea c, Totals t)
    {
        var rows = 0;
        var engine = ctx.Get<CostEngine>();
        c.FromLine = 0;
        c.WinMax = 25;
        c.MoreLines = "Y";
        while (c.MoreLines == "Y")
        {
            c.Function = "GETLINE";
            var r = await engine.LinkAsync(c, ctx.Cancellation);
            if (r.ReturnCode > 7) break;
            for (var w = 0; w < c.WinCnt; w++)
            {
                var l = c.Lines[w];
                var ext = Pic.N(l.ExtCost, 11, 2);
                if (l.Element == "MAT" || l.NodeType == "LEAF") t.Material = Pic.N(t.Material + l.ExtCost, 13, 2);
                t.Lines++;
                rows++;
                rw.Generate(L(1, Cols().Put(2, BomBatch.Ed(l.Depth, "Z(2)9"), 3).Put(6, Indent(l.Depth, l.Item), 46).Put(52, l.Desc, 20)
                    .Put(73, l.Source, 9).Put(83, BomBatch.Ed(Pic.N(l.ReqQty, 11, 6), "Z(5)9.999999"), 13).Put(97, l.Uom, 5)
                    .Put(103, BomBatch.Ed(Pic.N(l.UnitCost, 9, 6), "Z(5)9.999999"), 13).Put(118, BomBatch.Ed(ext, "Z(9)9.99"), 13)));
                if (!Pic.Blank(l.Warning)) rw.Generate(Unrounded(0m, l.Warning));
            }
        }
        return rows;
    }

    private static GroupLine Unrounded(decimal unrounded, string note) =>
        L(1, Cols().Put(10, "unrounded requirement was ", 26).Put(36, BomBatch.Ed(unrounded, "Z(7)9.999999"), 15).Put(52, note, 20));

    // ------------------------------------------------------------------ structure (BOMLEAF)

    private static async Task<LeafArea?> RunStructureAsync(JobRunContext ctx, LeafViews leaf, EngineHeader h, string mode, string item, string rev,
        decimal qty, int asat, int levels, Totals t)
    {
        var l = new LeafArea
        {
            Function = mode == "LEAF" ? "FLATTEN" : "INDENT",
            Company = h.Company,
            Site = h.Site,
            User = h.User,
            ParentItem = item,
            ParentRev = rev,
            Purpose = "STANDARD",
            ReqQty = Pic.N(qty, 15, 6),
            EffDate = asat,
            MaxDepth = levels,
            Reexplode = "Y",
            FromLine = 0,
            WinMax = 25,
        };
        await leaf.CallAsync(l, ctx.Cancellation);
        if (l.ReturnCode > 7)
        {
            ctx.Display("STERP-E-BOM1006 " + BomBatch.Field(l.Message, 200));
            return null;
        }
        t.Complete = Pic.X(l.Completeness, 12);
        t.Stop = Pic.X(l.StopSummary, 60);
        return l;
    }

    private static async Task<int> PrintStructureAsync(JobRunContext ctx, LeafViews leaf, BomReportWriter rw, LeafArea l, string mode, Totals t)
    {
        var rows = 0;
        l.FromLine = 0;
        l.WinMax = 25;
        l.MoreLines = "Y";
        while (l.MoreLines == "Y")
        {
            l.Function = mode == "LEAF" ? "FLATTEN" : "INDENT";
            l.Reexplode = "N";
            await leaf.CallAsync(l, ctx.Cancellation);
            if (l.ReturnCode > 7) break;
            for (var w = 0; w < l.WinCnt; w++)
            {
                var x = l.Lines[w];
                var req = Pic.N(x.ReqQty, 11, 6);
                var unrounded = Pic.N(x.Unrounded, 11, 6);
                t.Lines++;
                rows++;
                if (x.NodeType == "LEAF") t.Leaf++;
                rw.Generate(L(1, Cols().Put(2, BomBatch.Ed(x.Depth, "Z(2)9"), 3).Put(6, Indent(x.Depth, x.Item), 46).Put(52, x.Desc, 24)
                    .Put(77, x.NodeType, 9).Put(86, x.Basis, 9).Put(95, BomBatch.Ed(Pic.N(x.Usage, 11, 6), "Z(5)9.999999"), 13)
                    .Put(110, x.Uom, 5).Put(116, BomBatch.Ed(req, "Z(7)9.999999"), 15)));
                if (unrounded != req)
                {
                    t.RoundedUp++;
                    rw.Generate(Unrounded(unrounded, x.Stop));
                }
            }
        }
        return rows;
    }
}
