using Sterp.Modules.Bom.Domain;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;
using static Sterp.Modules.Bom.Jobs.BomReportWriter;

namespace Sterp.Modules.Bom.Jobs;

/// <summary>
/// BOMRP030 (cobol/batch/bom/BOMRP030.cob; FR-STERP-BOM-031): MODE=WHEREUSED prints the three where-used sections through
/// WHEREUSD (ALL, TOPLEVEL, OPENUSE, GETLINE); MODE=COMPARE prints the revision comparison through BOMCOMP (BYREF, IMPACT,
/// GETLINE). A refusal of the enquiry engine is raised as a worklist exception (EXCPRAIS) as well as a console message.
/// Report Writer RD R-BOMCHG: PAGE LIMIT 60, HEADING 1, FIRST DETAIL 8, LAST DETAIL 56, FOOTING 58.
/// </summary>
internal static class Bomrp030Program
{
    public const string Job = "BOMRP030";

    private sealed class State
    {
        public string Co = "";
        public string Site = "";
        public string User = "";
        public string Item = "";
        public string BomRef = "";
        public string FromRev = "";
        public string ToRev = "";
        public string CostSet = "";
        public string Draft = "N";
        public int Asat;
        public int Levels;
        public decimal Qty;
        public bool Compare;
        public int Rc;
        public int RowsOut;
        public int SectionRows;
        public string SubjectLab = "";
        public string Subject = "";
        public string Context = "";
        /// <summary>H-Line2 X(132).</summary>
        public char[] Line2 = Blank132();
        public string Complete = "";
        public string Stop = "";
        public string MsgText = "";
        public int TLines;
        public int TParents;
        public int TPossible;
        public int TTop;
        public int TDemand;
        public int TDepth;
        public CompareArea? Cp;
    }

    public static JobDefinition Definition() => new()
    {
        JobCode = Job,
        Slice = "STERP-BOM",
        SubmitOperationId = "bom_submitBomrp030Run",
        ParameterOrder = ["CO", "SITE", "ITEM", "BOMREF", "FROMREV", "TOREV", "COSTSET", "DRAFT", "LEVELS", "QTY", "ASAT", "MODE", "USER"],
        SelfRegistration = pre => BomJobSubmission.JoblogStart(pre, Job),
        EndValues = BomJobSubmission.NeverEnded,
        Execute = RunAsync,
    };

    private static char[] Blank132() => Enumerable.Repeat(' ', 132).ToArray();

    /// <summary>MOVE value TO field(start:length) on a 132-character area.</summary>
    private static void Put(char[] area, int start, int length, string? value)
    {
        var text = Pic.X(value, length).PadRight(length);
        for (var i = 0; i < length && start - 1 + i < area.Length; i++) area[start - 1 + i] = text[i];
    }

    private static string Text(char[] area) => new string(area).TrimEnd(' ');

    /// <summary>BL-COUNT-ED PIC Z(8)9.</summary>
    private static string Count(long value) => BomBatch.Ed(value, "Z(8)9");

    public static async Task<int> RunAsync(JobRunContext ctx)
    {
        var ct = ctx.Cancellation;
        var a = ctx.Args;
        var s = new State
        {
            Co = Pic.X(a.Co, 10),
            Site = Pic.X(a.Site, 10),
            User = Pic.X(a.User, 20),
            Item = Pic.X(a.Get("ITEM"), 40),
            BomRef = Pic.X(a.Get("BOMREF"), 30),
            FromRev = Pic.X(a.Get("FROMREV"), 4),
            ToRev = Pic.X(a.Get("TOREV"), 4),
            CostSet = Pic.X(a.Get("COSTSET"), 20),
        };
        var mode = Pic.X(a.Mode, 10);
        if ((a.Get("DRAFT") ?? "").StartsWith('Y')) s.Draft = "Y";
        var levelsText = a.Get("LEVELS");
        if (!Pic.Blank(levelsText)) s.Levels = Pic.I((long)decimal.Truncate(BomBatch.NumVal(levelsText)), 4);
        var qtyText = a.Get("QTY");
        if (!Pic.Blank(qtyText)) s.Qty = Pic.N(BomBatch.NumVal(qtyText), 13, 6);
        if (!Pic.Blank(a.AsAt)) s.Asat = BomBatch.Date8(BomBatch.NumVal(a.AsAt));
        // 0160-APPLY-DEFAULTS
        if (Pic.Blank(s.Co)) s.Co = "STC";
        if (Pic.Blank(s.User)) s.User = "ENGINEER";
        if (Pic.Blank(mode)) mode = "WHEREUSED";
        if (s.Levels == 0) s.Levels = 25;
        if (s.Qty == 0) s.Qty = 1;
        if (Pic.Blank(s.CostSet)) s.CostSet = "BOMDEMO";
        if (s.Asat == 0) s.Asat = BomBatch.Today();
        s.Compare = mode == "COMPARE";
        // 0170-CHECK-ARGS: a report that cannot name its subject prints nothing and says why
        if (s.Compare)
        {
            if (Pic.Blank(s.BomRef) || Pic.Blank(s.FromRev) || Pic.Blank(s.ToRev))
            {
                s.Rc = 12;
                ctx.Display("STERP-E-BOM1501 BOMREF=, FROMREV= and TOREV= are required for MODE=COMPARE");
            }
            ctx.Display("STERP-I-BOM1500 BOMRP030 revision comparison " + Pic.T(s.BomRef) + " " + Pic.T(s.FromRev) + " to " + Pic.T(s.ToRev));
        }
        else
        {
            if (Pic.Blank(s.Item))
            {
                s.Rc = 12;
                ctx.Display("STERP-E-BOM1502 ITEM= is required for MODE=WHEREUSED");
            }
            ctx.Display("STERP-I-BOM1500 BOMRP030 where used listing for " + Pic.T(s.Item));
        }

        // 0300-OPEN-SPOOL
        var sql = ctx.Get<ISqlRunner>();
        var built = BomBatch.OpenSpool(ctx, Job, s.Co, out var spool, out var spoolName);
        if (!built.IsOk)
        {
            ctx.Display("STERP-E-BOM1503 cannot build the spool path");
            return 12;
        }
        var heading = s.Compare
            ? await BomBatch.HeadingAsync(sql, s.Co, "BOMRP040", "Revision comparison",
                Pic.Str(60, "Cost set ", Pic.T(s.CostSet), " - sequence changes shown apart"), Pic.Str(30, "Effective ", BomBatch.Dmy(s.Asat)), s.User, ct)
            : await BomBatch.HeadingAsync(sql, s.Co, Job, "Where used - direct and indirect",
                s.Draft == "Y" ? "Released and draft bills; possible uses are labelled" : "Released bills only; possible uses are labelled",
                Pic.Str(30, "Effective ", BomBatch.Dmy(s.Asat)), s.User, ct);
        if (spool is null)
        {
            ctx.Display("STERP-E-BOM1504 cannot open " + BomBatch.Field(spoolName, 200));
            return 12;
        }
        if (s.Rc > 4)
        {
            spool.Dispose();
            return s.Rc;
        }
        using (spool)
        {
            var rw = new BomReportWriter(spool, heading, firstDetail: 8, lastDetail: 56);
            // 1000-RUN-ENQUIRY
            if (s.Compare) await PrintComparisonAsync(ctx, rw, s);
            else await PrintWhereUsedAsync(ctx, rw, s);
            // 3000-FILL-FOOTER / 0400-CLOSE-SPOOL
            var foot = s.Compare ? CompareFooter(s) : WhereUsedFooter(s);
            rw.Terminate(() => rw.Group(
                L(2, Text(foot[0])),
                L(1, Text(foot[1])),
                L(1, Text(foot[2])),
                L(2, Cols().Put(1, "Completeness: ", 14).Put(15, s.Complete, 12).Put(30, s.Stop, 70)),
                L(1, Text(foot[3]))));
        }
        ctx.Display("STERP-I-BOM1505 " + Pic.T(Count(s.RowsOut)) + " lines written");
        ctx.AnnounceSpool(Pic.T(spoolName));
        ctx.RowsProcessed = s.RowsOut;
        return s.Rc;
    }

    // ------------------------------------------------------------------ subject and section groups

    private static void GenerateSubject(BomReportWriter rw, State s)
    {
        s.RowsOut++;
        rw.Generate(
            L(1, Cols().Put(1, s.SubjectLab, 16).Put(17, s.Subject, 44).Put(63, s.Context, 70)),
            L(1, Text(s.Line2)),
            L(1, new string('=', 132)));
    }

    private static void GenerateSection(BomReportWriter rw, State s, string section, char[] caption)
    {
        s.RowsOut++;
        rw.Generate(
            L(2, Pic.X(section, 132)),
            L(1, Text(caption)),
            L(1, new string('-', 132)));
    }

    // ------------------------------------------------------------------ MODE=WHEREUSED

    private static async Task PrintWhereUsedAsync(JobRunContext ctx, BomReportWriter rw, State s)
    {
        var walker = ctx.Get<WhereUsedWalker>();
        s.SubjectLab = "Changed part   ";
        s.Subject = s.Item;
        // 1110-ALL-PARENTS
        var wu = SetUpRequest(s, "ALL");
        await walker.CallAsync(wu, ctx.Cancellation);
        if (wu.ReturnCode > 7 && wu.ReturnCode != 16) await ReportRefusalAsync(ctx, s, wu.Message, true);
        s.TParents = wu.ParentCount;
        s.TDepth = wu.DepthReached;
        s.Complete = Pic.X(wu.Completeness, 12);
        s.Line2 = Blank132();
        Put(s.Line2, 1, 132, "Ancestor uses found " + Pic.T(Count(wu.ParentCount)));
        Put(s.Line2, 34, 13, "deepest level");
        Put(s.Line2, 48, 9, Count(wu.DepthReached));
        Put(s.Line2, 60, 12, "completeness");
        Put(s.Line2, 73, 12, wu.Completeness);
        var dmy = BomBatch.Dmy(s.Asat);
        Put(s.Line2, 88, 9, "effective");
        Put(s.Line2, 98, 2, dmy[..2]);
        Put(s.Line2, 100, 1, "/");
        Put(s.Line2, 101, 2, dmy[3..5]);
        Put(s.Line2, 103, 1, "/");
        Put(s.Line2, 104, 4, dmy[6..]);
        s.Context = Pic.Str(70, "Levels requested ", Pic.T(Count(s.Levels)), "   scope ", Pic.T(wu.Purpose), " bills for company ", Pic.T(s.Co));
        GenerateSubject(rw, s);
        await PrintWindowsAsync(ctx, rw, s, walker, wu, "DIRECT AND INDIRECT PARENTS (level 1 is a direct use)");
        // 1120-TOP-LEVEL
        wu = SetUpRequest(s, "TOPLEVEL", wu);
        await walker.CallAsync(wu, ctx.Cancellation);
        await PrintWindowsAsync(ctx, rw, s, walker, wu, "TOP LEVEL ASSEMBLIES AT THE END OF EACH PATH");
        s.TTop = s.SectionRows;
        // 1130-OPEN-DEMAND
        wu = SetUpRequest(s, "OPENUSE", wu);
        await walker.CallAsync(wu, ctx.Cancellation);
        await PrintWindowsAsync(ctx, rw, s, walker, wu, "OPEN EXECUTION DEMAND THAT NEEDS AN EXPLICIT DECISION");
        s.TDemand = s.SectionRows;
    }

    /// <summary>1105-SET-UP-REQUEST on the (only once INITIALIZEd) where-used area.</summary>
    private static WhereUsedArea SetUpRequest(State s, string function, WhereUsedArea? previous = null)
    {
        var wu = previous ?? new WhereUsedArea();
        wu.Function = function;
        wu.Company = s.Co;
        wu.Site = s.Site;
        wu.User = s.User;
        wu.BusinessDate = s.Asat;
        wu.Item = s.Item;
        wu.EffDate = s.Asat;
        wu.Purpose = "STANDARD";
        wu.MaxDepth = s.Levels;
        wu.IncludeDraft = s.Draft;
        wu.IncludeOpt = "Y";
        wu.FromLine = 1;
        wu.WinMax = 25;
        return wu;
    }

    private static char[] WhereUsedCaption()
    {
        var c = Blank132();
        Put(c, 2, 3, "Lvl");
        Put(c, 6, 11, "Parent item");
        Put(c, 39, 3, "Rev");
        Put(c, 44, 11, "Description");
        Put(c, 65, 14, "Bill reference");
        Put(c, 88, 3, "Rev");
        Put(c, 93, 6, "Status");
        Put(c, 104, 4, "Line");
        Put(c, 110, 14, "Qty per parent");
        Put(c, 125, 4, "Unit");
        return c;
    }

    private static async Task PrintWindowsAsync(JobRunContext ctx, BomReportWriter rw, State s, WhereUsedWalker walker, WhereUsedArea wu, string section)
    {
        s.SectionRows = 0;
        GenerateSection(rw, s, section, WhereUsedCaption());
        PrintWindow(rw, s, wu);
        while (wu.MoreLines == "Y")
        {
            wu.Function = "GETLINE";
            await walker.CallAsync(wu, ctx.Cancellation);
            if (wu.ReturnCode > 7) break;
            PrintWindow(rw, s, wu);
        }
        if (s.SectionRows != 0) return;
        s.RowsOut++;
        rw.Generate(PathLine("nothing to report for this section", "", "", ""));
    }

    private static GroupLine PathLine(string path, string basis, string cert, string demand) =>
        L(1, Cols().Put(8, "path: ", 6).Put(14, path, 70).Put(85, basis, 12).Put(98, cert, 10).Put(109, demand, 24));

    private static void PrintWindow(BomReportWriter rw, State s, WhereUsedArea wu)
    {
        for (var w = 0; w < wu.WinCnt; w++)
        {
            var l = wu.Lines[w];
            if (l.Certainty != "CONFIRMED") s.TPossible++;
            s.TLines++;
            s.RowsOut++;
            s.SectionRows++;
            rw.Generate(L(1, Cols().Put(2, BomBatch.Ed(l.Level, "Z(2)9"), 3).Put(6, l.Parent, 32).Put(39, l.ParentRev, 4).Put(44, l.Desc, 20)
                .Put(65, l.BomRef, 22).Put(88, l.BomRev, 4).Put(93, l.Status, 10).Put(104, BomBatch.Ed(l.LineNum, "Z(3)9"), 4)
                .Put(109, BomBatch.Ed(Pic.N(l.Usage, 11, 6), "Z(7)9.999999"), 15).Put(125, l.Uom, 8)));
            var path = Pic.X(l.Path, 70);
            var demand = Pic.X(l.Demand, 24);
            if (Pic.Blank(path) && Pic.Blank(demand)) continue;
            s.RowsOut++;
            rw.Generate(PathLine(path, l.Basis, l.Certainty, demand));
        }
    }

    /// <summary>1190-REPORT-REFUSAL / the comparison refusal: console, H-Stop and one worklist exception (0600-RAISE-EXCEPTION).</summary>
    private static async Task ReportRefusalAsync(JobRunContext ctx, State s, string message, bool whereUsed)
    {
        s.Stop = Pic.X(message, 70);
        s.MsgText = Pic.X(message, 120);
        s.Complete = "INCOMPLETE";
        if (whereUsed)
        {
            s.Rc = 4;
            ctx.Display("STERP-W-BOM1506 " + Pic.T(message));
        }
        else
        {
            s.Rc = 8;
            ctx.Display("STERP-E-BOM1507 " + Pic.T(message));
        }
        var body = new ExcpraisArea
        {
            Module = "BOM",
            Severity = "WARNING",
            Category = "BOM.CHANGE.REPORT",
            DocumentType = "REPORT",
            DocumentRef = Job,
            OwnerUser = s.User,
            OwnerRole = "ENGINEER",
            Priority = 5,
            MessageText = s.MsgText,
        };
        await ctx.Get<IExceptionRaiser>().RaiseAsync(new KernelCaller(s.Co, s.Site, s.User, s.Asat, ""), body, ctx.Cancellation);
    }

    private static char[][] WhereUsedFooter(State s)
    {
        var f = new[] { Blank132(), Blank132(), Blank132(), Blank132() };
        Put(f[0], 1, 12, "Rows printed");
        Put(f[0], 20, 9, Count(s.TLines));
        Put(f[0], 32, 20, "ancestor uses listed");
        Put(f[0], 54, 9, Count(s.TParents));
        Put(f[0], 67, 21, "deepest level reached");
        Put(f[0], 90, 9, Count(s.TDepth));
        Put(f[1], 1, 20, "Top level assemblies");
        Put(f[1], 22, 9, Count(s.TTop));
        Put(f[1], 34, 16, "open demand rows");
        Put(f[1], 52, 9, Count(s.TDemand));
        Put(f[1], 64, 27, "uses that are only POSSIBLE");
        Put(f[1], 93, 9, Count(s.TPossible));
        Put(f[2], 1, 132, "A POSSIBLE use is an unselected optional or alternate line: a permitted configuration could activate it, so it is reported.");
        Put(f[3], 1, 132, "It is never counted as confirmed usage, and released history is listed and never deleted (BOM-023, BOM-028).");
        return f;
    }

    // ------------------------------------------------------------------ MODE=COMPARE

    private static async Task PrintComparisonAsync(JobRunContext ctx, BomReportWriter rw, State s)
    {
        var comparer = ctx.Get<RevisionComparer>();
        var cp = new CompareArea
        {
            Function = "BYREF",
            Company = s.Co,
            Site = s.Site,
            User = s.User,
            BusinessDate = s.Asat,
            BomRef = s.BomRef,
            FromRev = s.FromRev,
            ToRev = s.ToRev,
            EffDate = s.Asat,
            ReqQty = Pic.N(s.Qty, 15, 6),
            FromLine = 1,
            WinMax = 25,
        };
        s.Cp = cp;
        await comparer.CallAsync(cp, ctx.Cancellation);
        if (cp.ReturnCode > 7)
        {
            var message = cp.Message;
            await ReportRefusalAsync(ctx, s, message, false);
            // 2400-REFUSAL-PAGE: a refused comparison still produces a page
            s.SubjectLab = "Bill reference ";
            s.Subject = s.BomRef;
            s.Context = Pic.Str(70, "Revision ", Pic.T(s.FromRev), " to ", Pic.T(s.ToRev), " - refused");
            s.Line2 = Blank132();
            Put(s.Line2, 1, 120, message);
            GenerateSubject(rw, s);
            GenerateSection(rw, s, "NO COMPARISON WAS PRODUCED", Blank132());
            s.RowsOut++;
            rw.Generate(NoteLine(Pic.X(message, 100), cp.Reason));
            return;
        }
        cp.Function = "IMPACT";
        cp.FromLine = 1;
        await comparer.CallAsync(cp, ctx.Cancellation);
        FillCompareHead(rw, s, cp);
        await PrintCompareLinesAsync(ctx, rw, s, comparer, cp);
    }

    private static GroupLine NoteLine(string detail, string status) =>
        L(1, Cols().Put(11, "changed: ", 9).Put(20, detail, 100).Put(121, status, 12));

    private static void FillCompareHead(BomReportWriter rw, State s, CompareArea cp)
    {
        s.SubjectLab = "Bill reference ";
        s.Subject = s.BomRef;
        s.Context = Pic.Str(70, "Parent ", Pic.T(cp.ParentItem), "   revision ", Pic.T(s.FromRev), " to ", Pic.T(s.ToRev));
        s.Complete = Pic.X(cp.Completeness, 12);
        s.Line2 = Blank132();
        if (Pic.Blank(cp.HeaderDiff))
        {
            Put(s.Line2, 1, 16, "Header unchanged");
        }
        else
        {
            Put(s.Line2, 1, 8, "Header: ");
            Put(s.Line2, 9, 110, cp.HeaderDiff.PadRight(110)[..110]);
        }
        if (cp.MaterialChange == "Y") Put(s.Line2, 118, 15, "MATERIAL CHANGE");
        else Put(s.Line2, 118, 13, "SEQUENCE ONLY");
        GenerateSubject(rw, s);
    }

    private static char[] CompareCaption()
    {
        var c = Blank132();
        Put(c, 2, 6, "Action");
        Put(c, 11, 14, "Component item");
        Put(c, 40, 3, "Rev");
        Put(c, 45, 11, "Description");
        Put(c, 66, 4, "From");
        Put(c, 71, 2, "To");
        Put(c, 79, 10, "Qty before");
        Put(c, 93, 9, "Qty after");
        Put(c, 106, 10, "Difference");
        Put(c, 118, 4, "Unit");
        Put(c, 123, 5, "Basis");
        return c;
    }

    private static async Task PrintCompareLinesAsync(JobRunContext ctx, BomReportWriter rw, State s, RevisionComparer comparer, CompareArea cp)
    {
        s.SectionRows = 0;
        GenerateSection(rw, s, "COMPONENT DIFFERENCES BETWEEN THE TWO REVISIONS", CompareCaption());
        cp.FromLine = 1;
        cp.Function = "GETLINE";
        await comparer.CallAsync(cp, ctx.Cancellation);
        PrintCompareWindow(rw, s, cp);
        while (cp.MoreLines == "Y")
        {
            cp.Function = "GETLINE";
            await comparer.CallAsync(cp, ctx.Cancellation);
            if (cp.ReturnCode > 7) break;
            PrintCompareWindow(rw, s, cp);
        }
        if (s.SectionRows != 0) return;
        s.RowsOut++;
        rw.Generate(NoteLine("the two revisions have identical component lines", ""));
    }

    private static void PrintCompareWindow(BomReportWriter rw, State s, CompareArea cp)
    {
        for (var w = 0; w < cp.WinCnt; w++)
        {
            var l = cp.Lines[w];
            var uom = Pic.Blank(l.ToUom) ? l.FromUom : l.ToUom;
            var basis = Pic.Blank(l.ToBasis) ? l.FromBasis : l.ToBasis;
            var status = l.Action == "SEQUENCE" ? "not material" : "";
            s.TLines++;
            s.RowsOut++;
            s.SectionRows++;
            rw.Generate(L(1, Cols().Put(2, l.Action, 8).Put(11, l.Item, 28).Put(40, l.Rev, 4).Put(45, l.Desc, 20)
                .Put(66, BomBatch.Ed(l.FromLine, "Z(3)9"), 4).Put(71, BomBatch.Ed(l.ToLine, "Z(3)9"), 4)
                .Put(76, BomBatch.Ed(Pic.N(l.FromQty, 11, 6), "Z(5)9.999999"), 13).Put(90, BomBatch.Ed(Pic.N(l.ToQty, 11, 6), "Z(5)9.999999"), 13)
                .Put(104, BomBatch.Ed(Pic.N(l.QtyDelta, 11, 6), "-(5)9.999999"), 13).Put(118, uom, 4).Put(123, basis, 10)));
            if (Pic.Blank(l.Detail)) continue;
            s.RowsOut++;
            rw.Generate(NoteLine(l.Detail, status));
        }
    }

    private static char[][] CompareFooter(State s)
    {
        var cp = s.Cp ?? new CompareArea();
        var f = new[] { Blank132(), Blank132(), Blank132(), Blank132() };
        Put(f[0], 1, 16, "Components added");
        Put(f[0], 20, 9, Count(cp.AddedCount));
        Put(f[0], 32, 7, "removed");
        Put(f[0], 42, 9, Count(cp.RemovedCount));
        Put(f[0], 54, 7, "changed");
        Put(f[0], 64, 9, Count(cp.ChangedCount));
        Put(f[0], 76, 16, "resequenced only");
        Put(f[0], 94, 9, Count(cp.SeqOnlyCount));
        Put(f[0], 106, 9, "unchanged");
        Put(f[0], 117, 9, Count(cp.SameCount));
        Put(f[1], 1, 16, "Cost of revision");
        Put(f[1], 18, 4, s.FromRev);
        Put(f[1], 23, 13, BomBatch.Ed(cp.FromCost, "-(9)9.99"));
        Put(f[1], 38, 8, "revision");
        Put(f[1], 47, 4, s.ToRev);
        Put(f[1], 52, 13, BomBatch.Ed(cp.ToCost, "-(9)9.99"));
        Put(f[1], 67, 20, "impact of the change");
        Put(f[1], 88, 13, BomBatch.Ed(cp.CostDelta, "-(9)9.99"));
        Put(f[1], 102, 7, "GBP for");
        Put(f[1], 110, 13, BomBatch.Ed(s.Qty, "-(5)9.9(6)"));
        Put(f[1], 124, 7, "unit(s)");
        Put(f[2], 1, 132, "A line that only moved in the sequence is reported as SEQUENCE and is not a material change (BOM-025).");
        Put(f[3], 1, 132, cp.CostDelta == 0 && cp.MaterialChange == "Y"
            ? "The roll-up values the effective revision of the parent, so a draft revision can show no cost movement even for a material change."
            : "Costs come from the revision roll-up, so a missing rate makes the impact incomplete rather than zero.");
        return f;
    }
}
