using Sterp.Modules.Bom.Data;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Bom.Domain;

/// <summary>BOM-BR-CTL and BOM-BR-MSG (cobol/copy/BOMBREVC.cpy).</summary>
internal sealed class ProposalArea
{
    public string Func { get; set; } = "";
    public string Company { get; set; } = "";
    public string Site { get; set; } = "";
    public string User { get; set; } = "";
    public string ChangeRef { get; set; } = "";
    public int ChangeId { get; set; }
    public int Seq { get; set; }
    public int BomId { get; set; }
    public string Action { get; set; } = "";
    public string OldItem { get; set; } = "";
    public string NewItem { get; set; } = "";
    /// <summary>PR-NEW-QTY S9(13)V9(6).</summary>
    public decimal NewQty { get; set; }
    public string NewUom { get; set; } = "";
    public string NewBasis { get; set; } = "";
    /// <summary>PR-NEW-SCRAP S9(5)V9(6).</summary>
    public decimal NewScrap { get; set; }
    public int NewLineNum { get; set; }
    public int EffDate { get; set; }
    public string RecordValid { get; set; } = "";

    public string BomRef { get; set; } = "";
    public string BomRev { get; set; } = "";
    public string Parent { get; set; } = "";
    public string SrcStatus { get; set; } = "";
    public string Treatment { get; set; } = "";
    public string NewRev { get; set; } = "";
    public int NewBomId { get; set; }
    public int LineNum { get; set; }
    public decimal OldQty { get; set; }
    public decimal ResultQty { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    /// <summary>PR-OUTCOME: APPLIED, FAILED or SKIPPED.</summary>
    public string Outcome { get; set; } = "";
    public string Reason { get; set; } = "";
    public string SqlDead { get; set; } = "";
    /// <summary>BOM-BR-MSG X(120).</summary>
    public string Msg { get; set; } = "";
}

/// <summary>
/// BOMBREV (cobol/online/bom/BOMBREV.cob) <c>PROPOSE</c>: builds ONE proposed revision of ONE bulk-change target on the
/// caller's open unit of work and validates the whole proposed bill (spec 4.10). Not LINKable and owns no transaction:
/// BOMBULK wraps the run in BEGN and rolls a preview back.
/// </summary>
internal sealed class ProposalBuilder(ISqlRunner sql, BillReader reader, CycleChecker cycles, WhereUsedWalker whereUsed, BillValidator validator)
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private const string NewItemSql =
        "SELECT i.ItemCode, ISNULL(i.StockUomCode,'') FROM inv.Item i WHERE i.CompanyCode = ? AND i.ItemCode = ?";

    private const string RevisionSql =
        "SELECT h.BomHeaderId FROM bom.BomHeader h WHERE h.CompanyCode = ? AND h.BomRef = ? AND h.BomRevision = ?";

    private const string NewHeaderSql =
        "INSERT INTO bom.BomHeader (CompanyCode, SiteCode, BomRef, BomRevision, ParentItemCode, ParentRevision, Purpose, JobRef, " +
        "VariantCode, Description, NominalBasis, YieldFactor, ProductionQuantum, BatchSize, IsPhantom, ConsolidateRuns, CostingLotSize, " +
        "SetupCostPerRun, ConversionCostPerStart, EffectiveFrom, Status, AuthorUser, ChangeRef, ChangeReason, LossIntentNote, " +
        "SourceBomHeaderId, CreatedBy) SELECT h.CompanyCode, h.SiteCode, h.BomRef, ?, h.ParentItemCode, h.ParentRevision, h.Purpose, " +
        "h.JobRef, h.VariantCode, h.Description, h.NominalBasis, h.YieldFactor, h.ProductionQuantum, h.BatchSize, h.IsPhantom, " +
        "h.ConsolidateRuns, h.CostingLotSize, h.SetupCostPerRun, h.ConversionCostPerStart, CONVERT(date,?,112), 'DRAFT', ?, ?, ?, " +
        "h.LossIntentNote, h.BomHeaderId, ? FROM bom.BomHeader h WHERE h.BomHeaderId = ?";

    private const string CopyLinesSql =
        "INSERT INTO bom.BomComponent (BomHeaderId, LineNum, PathRef, ComponentItemCode, ComponentRevision, UsageQty, UsageBasis, " +
        "UomCode, ConversionFactor, ScrapPct, SupplyBehaviour, IsOptional, AlternateGroup, IsSelected, OptionChoiceRef, EffectiveFrom, " +
        "EffectiveTo, NeedOffsetDays, OperationRef, IssueLocationCode, QualityRestriction, Notes, CostOnlyRate, CostElementCode, " +
        "CreatedBy) SELECT ?, c.LineNum, c.PathRef, c.ComponentItemCode, c.ComponentRevision, c.UsageQty, c.UsageBasis, c.UomCode, " +
        "c.ConversionFactor, c.ScrapPct, c.SupplyBehaviour, c.IsOptional, c.AlternateGroup, c.IsSelected, c.OptionChoiceRef, " +
        "c.EffectiveFrom, c.EffectiveTo, c.NeedOffsetDays, c.OperationRef, c.IssueLocationCode, c.QualityRestriction, c.Notes, " +
        "c.CostOnlyRate, c.CostElementCode, ? FROM bom.BomComponent c WHERE c.BomHeaderId = ? AND (? = 'N' OR c.LineNum <> ?)";

    private const string SwapSql =
        "UPDATE bom.BomComponent SET ComponentItemCode = ?, ComponentRevision = NULL, UsageQty = ?, UomCode = ?, Notes = ?, " +
        "ModifiedBy = ?, ModifiedAt = SYSDATETIME() WHERE BomHeaderId = ? AND LineNum = ?";

    private const string QtyChangeSql =
        "UPDATE bom.BomComponent SET UsageQty = ?, ScrapPct = ?, UsageBasis = ?, UomCode = ?, ModifiedBy = ?, " +
        "ModifiedAt = SYSDATETIME() WHERE BomHeaderId = ? AND LineNum = ?";

    private const string AddSql =
        "INSERT INTO bom.BomComponent (BomHeaderId, LineNum, PathRef, ComponentItemCode, UsageQty, UsageBasis, UomCode, " +
        "ConversionFactor, ScrapPct, SupplyBehaviour, IsOptional, IsSelected, Notes, CreatedBy) VALUES (?,?,?,?,?,?,?,1,?," +
        "'PURCHASED',0,1,?,?)";

    private const string DropSql = "DELETE FROM bom.BomComponent WHERE BomHeaderId = ? AND LineNum = ?";

    private const string NextLineSql = "SELECT ISNULL(MAX(c.LineNum),0) + 10 FROM bom.BomComponent c WHERE c.BomHeaderId = ?";

    private sealed class Run
    {
        public ProposalArea P = null!;
        public BillRead Bill = null!;
        public string Note = "";
        public string OldUom = "";
        public string OldBasis = "";
        public decimal OldScrap;
        public string UseUom = "";
        public string UseBasis = "";
        public decimal UseQty;
        public decimal UseScrap;
        public CancellationToken Ct;
    }

    public async Task CallAsync(ProposalArea p, CancellationToken ct)
    {
        p.Msg = "";
        p.Reason = "";
        p.Treatment = "";
        p.NewRev = "";
        p.BomRef = "";
        p.BomRev = "";
        p.Parent = "";
        p.SrcStatus = "";
        p.NewBomId = 0;
        p.LineNum = 0;
        p.OldQty = 0;
        p.ResultQty = 0;
        p.ErrorCount = 0;
        p.WarningCount = 0;
        p.SqlDead = "N";
        p.Outcome = "SKIPPED";
        p.Action = Pic.X(Pic.Upper(p.Action), 10);
        if (p.Func != "PROPOSE")
        {
            p.Outcome = "FAILED";
            p.Reason = "BOM1430";
            p.Msg = "BOMBREV only supports the PROPOSE function";
            return;
        }
        await ProposeOneRevisionAsync(new Run { P = p, Ct = ct });
    }

    /// <summary>Everything that can refuse a target without touching the database is tested first.</summary>
    private async Task ProposeOneRevisionAsync(Run run)
    {
        var p = run.P;
        await ReadTheSourceBillAsync(run);
        if (!Pic.Blank(p.Reason)) return;
        TestTheStatus(run);
        if (!Pic.Blank(p.Reason)) return;
        FindTheAffectedLine(run);
        if (!Pic.Blank(p.Reason)) return;
        await TestTheNewItemAsync(run);
        if (!Pic.Blank(p.Reason)) return;
        await TestForANewCycleAsync(run);
        if (!Pic.Blank(p.Reason)) return;
        await DecideTheTreatmentAsync(run);
        if (!Pic.Blank(p.Reason)) return;
        if (p.Treatment == "NEWREV")
        {
            await RunTheStatementAsync(run, NewHeaderSql, p.NewRev, BomSql.D8(p.EffDate), Pic.X(p.User, 20), Pic.X(p.ChangeRef, 30),
                Pic.Str(256, "Bulk ", Pic.T(p.Action), " under change ", Pic.T(p.ChangeRef)), Pic.X(p.User, 20), p.BomId);
            if (!Pic.Blank(p.Reason)) return;
            await ReadBackTheNewIdAsync(run);
            if (!Pic.Blank(p.Reason)) return;
            await RunTheStatementAsync(run, CopyLinesSql, p.NewBomId, Pic.X(p.User, 20), p.BomId, p.Action == "REMOVE" ? "Y" : "N", p.LineNum);
            if (!Pic.Blank(p.Reason)) return;
        }
        else
        {
            p.NewBomId = p.BomId;
            p.NewRev = p.BomRev;
        }
        await ApplyTheDeltaAsync(run);
        if (!Pic.Blank(p.Reason)) return;
        await ValidateTheProposalAsync(run);
    }

    private async Task ReadTheSourceBillAsync(Run run)
    {
        var p = run.P;
        var bill = await reader.BillAsync(Pic.X(p.Company, 10), Pic.X(p.Site, 10), p.BomId, 500, run.Ct);
        run.Bill = bill;
        if (bill.Status is not ("OK" or "TOOMANY"))
        {
            p.Outcome = "FAILED";
            p.Reason = "BOM1443";
            p.Msg = Pic.X(bill.Message, 120);
            if (bill.Status == "SQLFAIL") p.SqlDead = "Y";
            return;
        }
        p.BomRef = bill.Header.BomRef;
        p.BomRev = bill.Header.BomRev;
        p.Parent = bill.Header.ParentItem;
        p.SrcStatus = bill.Header.Status;
    }

    /// <summary>A retired or superseded revision is history and is left alone (BOM-028).</summary>
    private static void TestTheStatus(Run run)
    {
        var p = run.P;
        if (p.SrcStatus is not ("RETIRED" or "SUPERSEDED")) return;
        p.Outcome = "SKIPPED";
        p.Reason = "BOM1433";
        p.Msg = Pic.Str(120, "Revision ", Pic.T(p.BomRev), " of ", Pic.T(p.BomRef), " is ", Pic.T(p.SrcStatus), " and is left as history");
    }

    /// <summary>BOM-002: only the FIRST occurrence of the component is changed and the others are noted.</summary>
    private static void FindTheAffectedLine(Run run)
    {
        var p = run.P;
        var hits = 0;
        p.LineNum = 0;
        foreach (var l in run.Bill.Lines)
        {
            if (p.Action == "ADD")
            {
                if (l.Item == p.NewItem) hits++;
                continue;
            }
            if (l.Item != p.OldItem) continue;
            hits++;
            if (p.LineNum != 0) continue;
            p.LineNum = l.LineNum;
            p.OldQty = l.UsageQty;
            run.OldUom = l.Uom;
            run.OldBasis = l.UsageBasis;
            run.OldScrap = l.ScrapPct;
        }
        if (p.Action == "ADD")
        {
            if (hits > 0)
            {
                p.Outcome = "SKIPPED";
                p.Reason = "BOM1432";
                p.Msg = Pic.Str(120, Pic.T(p.NewItem), " is already on ", Pic.T(p.BomRef), "/", Pic.T(p.BomRev), " - nothing to add");
            }
            return;
        }
        if (p.LineNum == 0)
        {
            p.Outcome = "SKIPPED";
            p.Reason = "BOM1431";
            p.Msg = Pic.Str(120, Pic.T(p.OldItem), " is not a component of ", Pic.T(p.BomRef), "/", Pic.T(p.BomRev), " - nothing to change");
            return;
        }
        if (hits > 1)
        {
            run.Note = Pic.Str(100, Pic.Z(Pic.I(hits, 4)), " occurrences of this component exist; only the", " first is changed");
        }
    }

    /// <summary>BOM-010 / BOM-029: a replacement naming an item the item file does not hold refuses.</summary>
    private async Task TestTheNewItemAsync(Run run)
    {
        var p = run.P;
        if (p.Action is not ("REPLACE" or "ADD")) return;
        string[]? row;
        try
        {
            row = await sql.WithReaderAsync(BomSql.Q(NewItemSql), BomSql.Args(Pic.X(p.Company, 10), p.NewItem), async r =>
                await r.ReadAsync(run.Ct) ? new[] { BomSql.Text(r, 0, 60), BomSql.Text(r, 1, 60) } : null, run.Ct);
        }
        catch (SqlioException ex)
        {
            ReportSqlFailure(p, ex);
            return;
        }
        if (row is null)
        {
            p.Outcome = "FAILED";
            p.Reason = "BOM1435";
            p.Msg = Pic.Str(120, "Item ", Pic.T(p.NewItem), " is not held in the item file for company ", Pic.T(p.Company));
            return;
        }
        if (Pic.Blank(run.OldUom)) run.OldUom = Pic.X(row[1], 10);
    }

    /// <summary>
    /// BOM-013: CYCLECHK EDGE answers "is this item below itself"; the where-used walk of the parent answers "is the new
    /// component already an ancestor of this bill's parent".
    /// </summary>
    private async Task TestForANewCycleAsync(Run run)
    {
        var p = run.P;
        if (p.Action is not ("REPLACE" or "ADD")) return;
        var c = new CycleArea
        {
            Function = "EDGE",
            Company = Pic.X(p.Company, 10),
            Site = Pic.X(p.Site, 10),
            User = Pic.X(p.User, 20),
            BusinessDate = p.EffDate,
            RootItem = p.Parent,
            ChildItem = p.NewItem,
            EffDate = p.EffDate,
            Purpose = "STANDARD",
            MaxDepth = 25,
            IncludeOpt = "Y",
        };
        await cycles.CallAsync(c, run.Ct);
        if (c.CycleFound == "Y")
        {
            p.Outcome = "FAILED";
            p.Reason = "BOM1434";
            p.Msg = Pic.Str(120, "Cycle refused: ", Slice(c.CyclePath, 105));
            return;
        }
        await TestTheAncestryAsync(run);
    }

    private async Task TestTheAncestryAsync(Run run)
    {
        var p = run.P;
        var wu = new WhereUsedArea
        {
            Function = "ALL",
            Company = Pic.X(p.Company, 10),
            Site = Pic.X(p.Site, 10),
            User = Pic.X(p.User, 20),
            BusinessDate = p.EffDate,
            Item = p.Parent,
            EffDate = p.EffDate,
            Purpose = "STANDARD",
            MaxDepth = 25,
            IncludeDraft = "Y",
            IncludeOpt = "Y",
            FromLine = 1,
            WinMax = 25,
        };
        await whereUsed.CallAsync(wu, run.Ct);
        var path = ScanOneWindow(wu, p.NewItem);
        while (wu.MoreLines == "Y" && path is null)
        {
            wu.Function = "GETLINE";
            await whereUsed.CallAsync(wu, run.Ct);
            if (wu.ReturnCode > 7) break;
            path = ScanOneWindow(wu, p.NewItem);
        }
        if (path is null) return;
        p.Outcome = "FAILED";
        p.Reason = "BOM1434";
        p.Msg = Pic.Str(120, "Cycle refused: ", Pic.T(p.NewItem), " is already above ", Pic.T(p.Parent), " - ", Slice(path, 60));
    }

    private static string? ScanOneWindow(WhereUsedArea wu, string newItem)
    {
        for (var i = 0; i < wu.WinCnt; i++)
        {
            if (wu.Lines[i].Parent == newItem) return wu.Lines[i].Path;
        }
        return null;
    }

    /// <summary>BOM-004: a draft is amended; a released revision is copied to the first free revision letter.</summary>
    private async Task DecideTheTreatmentAsync(Run run)
    {
        var p = run.P;
        if (p.SrcStatus is "DRAFT" or "REVIEW")
        {
            p.Treatment = "AMEND";
            return;
        }
        p.Treatment = "NEWREV";
        await PickAFreeRevisionAsync(run);
        if (Pic.Blank(p.NewRev) && Pic.Blank(p.Reason))
        {
            p.Outcome = "FAILED";
            p.Reason = "BOM1436";
            p.Msg = Pic.Str(120, "Every revision letter of ", Pic.T(p.BomRef), " is used - no proposal can be raised");
        }
    }

    private async Task PickAFreeRevisionAsync(Run run)
    {
        var p = run.P;
        p.NewRev = "";
        for (var n = 1; n <= 26 && Pic.Blank(p.NewRev) && Pic.Blank(p.Reason); n++)
        {
            var tryRev = Alphabet[n - 1].ToString();
            var (free, _) = await LookForThatRevisionAsync(run, tryRev);
            if (free) p.NewRev = tryRev;
        }
    }

    /// <summary>Doubles as the read-back of a revision just inserted: when the row is there, its identity is returned.</summary>
    private async Task<(bool Free, string Id)> LookForThatRevisionAsync(Run run, string tryRev)
    {
        var p = run.P;
        try
        {
            var id = await sql.WithReaderAsync(BomSql.Q(RevisionSql), BomSql.Args(Pic.X(p.Company, 10), p.BomRef, tryRev), async r =>
                await r.ReadAsync(run.Ct) ? BomSql.Text(r, 0, 60) : null, run.Ct);
            return (id is null, id ?? "");
        }
        catch (SqlioException ex)
        {
            ReportSqlFailure(p, ex);
            return (false, "");
        }
    }

    private async Task ReadBackTheNewIdAsync(Run run)
    {
        var p = run.P;
        p.NewBomId = 0;
        var (free, id) = await LookForThatRevisionAsync(run, p.NewRev);
        if (!Pic.Blank(p.Reason)) return;
        if (free)
        {
            p.Outcome = "FAILED";
            p.Reason = "BOM1437";
            p.Msg = "The proposed revision could not be read back";
            return;
        }
        p.NewBomId = (int)decimal.Truncate(BomSql.NumVal(id));
    }

    // ------------------------------------------------------------------ the delta

    /// <summary>"Keep what was there unless the request names something else" is decided here, not in the SQL.</summary>
    private async Task ApplyTheDeltaAsync(Run run)
    {
        var p = run.P;
        run.UseUom = run.OldUom;
        run.UseBasis = run.OldBasis;
        run.UseQty = p.OldQty;
        run.UseScrap = run.OldScrap;
        if (!Pic.Blank(p.NewUom)) run.UseUom = Pic.X(p.NewUom, 10);
        if (!Pic.Blank(p.NewBasis)) run.UseBasis = Pic.X(p.NewBasis, 12);
        if (p.NewQty > 0) run.UseQty = p.NewQty;
        if (p.NewScrap >= 0) run.UseScrap = p.NewScrap;
        if (Pic.Blank(run.UseBasis)) run.UseBasis = "PROPORTION";
        switch (p.Action)
        {
            case "REPLACE":
                await RunTheStatementAsync(run, SwapSql, p.NewItem, run.UseQty, run.UseUom,
                    Pic.Str(256, Pic.T(p.OldItem), " replaced by ", Pic.T(p.NewItem), " under ", Pic.T(p.ChangeRef)),
                    Pic.X(p.User, 20), p.NewBomId, p.LineNum);
                p.ResultQty = run.UseQty;
                break;
            case "QTYCHG":
                await ChangeTheQuantityAsync(run);
                break;
            case "ADD":
                await AddTheComponentAsync(run);
                break;
            case "REMOVE":
                p.ResultQty = 0;
                if (p.Treatment == "NEWREV") break;
                await RunTheStatementAsync(run, DropSql, p.NewBomId, p.LineNum);
                break;
            default:
                p.Outcome = "FAILED";
                p.Reason = "BOM1444";
                p.Msg = "Unsupported bulk change action";
                break;
        }
    }

    /// <summary>BOM-009: the scrap allowance travels with the quantity on one request.</summary>
    private async Task ChangeTheQuantityAsync(Run run)
    {
        var p = run.P;
        if (p.NewQty <= 0 && p.NewScrap < 0)
        {
            p.Outcome = "FAILED";
            p.Reason = "BOM1442";
            p.Msg = "A quantity change needs a new quantity or a new scrap allowance";
            return;
        }
        await RunTheStatementAsync(run, QtyChangeSql, run.UseQty, run.UseScrap, run.UseBasis, run.UseUom, Pic.X(p.User, 20), p.NewBomId, p.LineNum);
        p.ResultQty = run.UseQty;
    }

    private async Task AddTheComponentAsync(Run run)
    {
        var p = run.P;
        await NextFreeLineNumberAsync(run);
        if (!Pic.Blank(p.Reason)) return;
        if (run.UseQty <= 0) run.UseQty = 1;
        var pathRef = Pic.Str(20, Pic.T(p.BomRef), "/", Pic.Z(Pic.I(p.LineNum, 4)));
        await RunTheStatementAsync(run, AddSql, p.NewBomId, p.LineNum, pathRef, p.NewItem, run.UseQty, run.UseBasis, run.UseUom, run.UseScrap,
            Pic.Str(256, "Added by bulk change ", Pic.T(p.ChangeRef)), Pic.X(p.User, 20));
        p.OldQty = 0;
        p.ResultQty = run.UseQty;
    }

    private async Task NextFreeLineNumberAsync(Run run)
    {
        var p = run.P;
        if (p.NewLineNum > 0)
        {
            p.LineNum = p.NewLineNum;
            return;
        }
        p.LineNum = 0;
        try
        {
            var next = await sql.WithReaderAsync(BomSql.Q(NextLineSql), BomSql.Args(p.NewBomId), async r =>
                await r.ReadAsync(run.Ct) ? BomSql.Text(r, 0, 60) : "", run.Ct);
            p.LineNum = Pic.I((long)decimal.Truncate(BomSql.NumVal(next)), 4);
        }
        catch (SqlioException ex)
        {
            ReportSqlFailure(p, ex);
        }
    }

    private async Task RunTheStatementAsync(Run run, string statement, params object?[] values)
    {
        try
        {
            await sql.ExecuteAsync(BomSql.Q(statement), BomSql.Args(values), run.Ct);
        }
        catch (SqlioException ex)
        {
            ReportSqlFailure(run.P, ex);
        }
    }

    // ------------------------------------------------------------------ validation of the whole proposed bill

    /// <summary>BOM-024, BOM-029: the proposal is validated as a whole bill (a preview only reads the answer back).</summary>
    private async Task ValidateTheProposalAsync(Run run)
    {
        var p = run.P;
        var v = new ValidationArea
        {
            Function = p.RecordValid == "Y" ? "VALIDATE" : "CHECK",
            Company = Pic.X(p.Company, 10),
            Site = Pic.X(p.Site, 10),
            User = Pic.X(p.User, 20),
            BusinessDate = p.EffDate,
            BomId = p.NewBomId,
            EffDate = p.EffDate,
            ValidRef = Pic.Str(30, Pic.T(Pic.X(p.ChangeRef, 20)), "/", Pic.Z(Pic.I(p.Seq, 4)), "/", Pic.T(p.NewRev)),
        };
        await validator.CallAsync(v, run.Ct);
        p.ErrorCount = v.ErrorCount;
        p.WarningCount = v.WarningCount;
        if (v.ReturnCode == 12)
        {
            // BOMBREV reports its own SQLIO-ERRMSG, which its last (successful) SQLIO call has cleared
            ReportSqlFailure(p, "");
            return;
        }
        if (v.ErrorCount > 0 || v.Outcome == "FAIL")
        {
            p.Outcome = "FAILED";
            p.Reason = "BOM1440";
            if (v.FindCount > 1)
            {
                p.Msg = Pic.Str(120, Slice(v.Findings[0].Message, 45), " [", Pic.T(v.Findings[0].Reason), "]; ",
                    Slice(v.Findings[1].Message, 45), " [", Pic.T(v.Findings[1].Reason), "]");
            }
            else if (v.FindCount == 1)
            {
                p.Msg = Pic.Str(120, "Proposed revision ", Pic.T(p.NewRev), " fails validation: ", Slice(v.Findings[0].Message, 60), " [",
                    Pic.T(v.Findings[0].Reason), "]");
            }
            else
            {
                p.Msg = Pic.X(v.Message, 120);
            }
            return;
        }
        p.Outcome = "APPLIED";
        p.Reason = "";
        p.Msg = p.Treatment == "NEWREV"
            ? Pic.Str(120, "New draft revision ", Pic.T(p.NewRev), " of ", Pic.T(p.BomRef), "; the released revision is untouched. ", Pic.T(run.Note))
            : Pic.Str(120, "Draft revision ", Pic.T(p.BomRev), " of ", Pic.T(p.BomRef), " amended in place. ", Pic.T(run.Note));
    }

    /// <summary>A reference modification (1:n) of a PIC X field: exactly n characters, space padded.</summary>
    private static string Slice(string? value, int n) => (value ?? "").PadRight(n)[..n];

    private static void ReportSqlFailure(ProposalArea p, SqlioException ex) => ReportSqlFailure(p, ex.DriverText);

    private static void ReportSqlFailure(ProposalArea p, string? errMsg)
    {
        p.Outcome = "FAILED";
        p.Reason = "BOM1441";
        p.SqlDead = "Y";
        p.Msg = Pic.Str(120, "Bill database access failed - ", Slice(errMsg, 85));
    }
}
