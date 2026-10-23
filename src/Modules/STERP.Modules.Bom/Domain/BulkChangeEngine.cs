using Sterp.Modules.Bom.Data;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Bom.Domain;

/// <summary>One Ws-Res row / BB-LINES entry of BOMBULK.</summary>
internal sealed class BulkLine
{
    public int Seq;
    public int BomId;
    public int NewBomId;
    public string BomRef = "";
    public string BomRev = "";
    public string NewRev = "";
    public string Parent = "";
    public string Status = "";
    public string Treatment = "";
    public int LineNum;
    /// <summary>S9(13)V9(6).</summary>
    public decimal OldQty;
    public decimal NewQty;
    /// <summary>X(10): PENDING, APPLIED, FAILED or SKIPPED.</summary>
    public string Outcome = "";
    public string Reason = "";
    public int Errors;
    /// <summary>X(120).</summary>
    public string Detail = "";

    public BulkLine Clone() => (BulkLine)MemberwiseClone();
}

/// <summary>BOMBULKCA (cobol/copy/BOMBULKCA.cpy).</summary>
internal sealed class BulkArea
{
    public string Function { get; set; } = "";
    public string Company { get; set; } = "";
    public string Site { get; set; } = "";
    public string User { get; set; } = "";
    public int BusinessDate { get; set; }
    public string CorrelationId { get; set; } = "";
    public int ReturnCode { get; set; }
    public string Reason { get; set; } = "";
    public string Message { get; set; } = "";
    public int MessageCount { get; set; }

    public string ChangeRef { get; set; } = "";
    /// <summary>BB-TITLE X(100).</summary>
    public string Title { get; set; } = "";
    public string Action { get; set; } = "";
    public string Scope { get; set; } = "";
    public string ScopeItem { get; set; } = "";
    public string OldItem { get; set; } = "";
    public string NewItem { get; set; } = "";
    public decimal NewQty { get; set; }
    public string NewUom { get; set; } = "";
    public string NewBasis { get; set; } = "";
    public decimal NewScrap { get; set; }
    public int NewLineNum { get; set; }
    public int EffDate { get; set; }
    public string IncludeDraft { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public int ChangeId { get; set; }
    public int TargetCount { get; set; }
    public int AppliedCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }
    public int NewRevCount { get; set; }
    public int AmendCount { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public string Status { get; set; } = "";
    public string MayApply { get; set; } = "";
    public string Duplicate { get; set; } = "";
    public string Completeness { get; set; } = "";
    /// <summary>BB-SUMMARY X(200).</summary>
    public string Summary { get; set; } = "";
    /// <summary>BB-REFUSAL X(200).</summary>
    public string Refusal { get; set; } = "";
    public int FromLine { get; set; }
    public int WinMax { get; set; }
    public int WinCnt { get; set; }
    public string MoreLines { get; set; } = "";
    public List<BulkLine> Lines { get; } = [];
}

/// <summary>
/// BOMBULK (cobol/online/bom/BOMBULK.cob) <c>PREVIEW</c> and <c>GETLINE</c> (FR-STERP-BOM-018): authority, request
/// validation, target selection, the change header in autocommit, one BOMBREV proposal per target inside an SQLIO BEGN
/// unit that a preview rolls back, the change lines and header update in autocommit, the summary and the decision. The
/// functions APPLY, COMMIT and RESULT have no production caller and are deferred (dispositions cobol:BOMBULK#*).
/// </summary>
internal sealed class BulkChangeEngine(ISqlRunner sql, IPermissionService permissions, ProposalBuilder proposals)
{
    private const int TgtMax = 200;

    private const string ChangeIdSql = "SELECT c.ChangeId, c.Status FROM bom.BomChange c WHERE c.CompanyCode = ? AND c.ChangeRef = ?";

    private const string ReadBackSql =
        "SELECT l.BomHeaderId, ISNULL(l.BomRef,''), ISNULL(l.BomRevision,''), ISNULL(l.NewBomRevision,''), ISNULL(l.OldUsageQty,0), " +
        "ISNULL(l.NewUsageQty,0), ISNULL(l.Outcome,''), ISNULL(l.FailureReason,'') FROM bom.BomChangeLine l WHERE l.ChangeId = ? " +
        "ORDER BY l.LineNum";

    private const string TargetsSql =
        "SELECT h.BomHeaderId, h.BomRef, h.BomRevision, h.ParentItemCode, h.Status FROM bom.BomHeader h WHERE h.CompanyCode = ? " +
        "AND ((? = 'ITEM' AND EXISTS (SELECT 1 FROM bom.BomComponent c WHERE c.BomHeaderId = h.BomHeaderId AND c.ComponentItemCode = ?)) " +
        "OR (? = 'PARENT' AND h.ParentItemCode = ?) OR (? = 'BOMREF' AND h.BomRef = ?)) AND h.Status IN ('RELEASED','DRAFT','REVIEW') " +
        "AND (? = 'Y' OR h.Status = 'RELEASED') ORDER BY h.BomRef, h.BomRevision";

    private const string ChangeHeaderSql =
        "INSERT INTO bom.BomChange (CompanyCode, ChangeRef, Title, ChangeType, RequestedBy, EffectiveDate, Status) SELECT ?, ?, ?, ?, ?, " +
        "CONVERT(date,?,112), 'DRAFT' WHERE NOT EXISTS (SELECT 1 FROM bom.BomChange x WHERE x.CompanyCode = ? AND x.ChangeRef = ?)";

    private const string DeleteLinesSql = "DELETE FROM bom.BomChangeLine WHERE ChangeId = ?";

    private const string ChangeLineSql =
        "INSERT INTO bom.BomChangeLine (ChangeId, LineNum, BomHeaderId, BomRef, BomRevision, Action, ComponentItemCode, " +
        "NewComponentItemCode, OldUsageQty, NewUsageQty, NewBomRevision, Outcome, FailureReason) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)";

    private const string UpdateHeaderSql =
        "UPDATE bom.BomChange SET Title = ?, ChangeType = ?, RequestedBy = ?, EffectiveDate = CONVERT(date,?,112), Status = ?, " +
        "AffectedBills = ?, AppliedBills = ?, ImpactNotes = ? WHERE CompanyCode = ? AND ChangeRef = ?";

    // ------------------------------------------------------------------ working storage of the run unit

    /// <summary>Ws-Res: the outcome rows the GETLINE calls page.</summary>
    private readonly List<BulkLine> res = [];
    private string keep = "N";
    private string recordValid = "N";
    private string dead = "N";
    private string limitHit = "N";
    private string dbAction = "";
    private string foundStatus = "";
    private int would;

    public async Task CallAsync(BulkArea a, CancellationToken ct)
    {
        a.ReturnCode = 0;
        a.MessageCount = 0;
        a.Reason = "";
        a.Message = "";
        a.Action = Pic.X(Pic.Upper(a.Action), 10);
        a.Scope = Pic.X(Pic.Upper(a.Scope), 10);
        a.OldItem = Pic.X(Pic.Upper(a.OldItem), 40);
        a.NewItem = Pic.X(Pic.Upper(a.NewItem), 40);
        if (a.WinMax < 1 || a.WinMax > 25) a.WinMax = 25;
        a.WinCnt = 0;
        a.MoreLines = "N";
        a.Lines.Clear();
        await CheckAuthorityAsync(a, ct);
        if (a.ReturnCode > 4) return;
        if (a.EffDate == 0) a.EffDate = a.BusinessDate;
        if (Pic.Blank(a.RequestedBy)) a.RequestedBy = Pic.X(a.User, 20);
        switch (a.Function)
        {
            case "PREVIEW":
                keep = "N";
                await RunTheChangeAsync(a, ct);
                break;
            case "GETLINE":
                PageTheOutcome(a);
                break;
            default:
                a.ReturnCode = 8;
                a.Reason = "BOM1400";
                a.Message = Pic.Str(200, "BOMBULK does not support the function ", Pic.T(a.Function));
                a.MessageCount = Pic.I(a.MessageCount + 1, 2);
                break;
        }
    }

    /// <summary>PREVIEW and GETLINE need BOM.VIEW through PERMCHK (INITIALIZEd area: no correlation id).</summary>
    private async Task CheckAuthorityAsync(BulkArea a, CancellationToken ct)
    {
        if (a.Function is not ("PREVIEW" or "GETLINE")) return;
        var caller = new KernelCaller(Pic.X(a.Company, 10), Pic.X(a.Site, 10), Pic.X(a.User, 20), a.BusinessDate, "");
        var perm = await permissions.CheckAsync(caller, "BOM.VIEW", ct: ct);
        if (perm.Header.ReturnCode <= 4) return;
        a.ReturnCode = perm.Header.ReturnCode;
        a.Reason = Pic.X(perm.Header.ReasonCode, 8);
        a.Message = Pic.X(perm.Header.Message, 200);
        a.MessageCount = perm.Header.MessageCount;
    }

    private async Task RunTheChangeAsync(BulkArea a, CancellationToken ct)
    {
        res.Clear();
        a.TargetCount = 0;
        a.AppliedCount = 0;
        a.FailedCount = 0;
        a.SkippedCount = 0;
        a.NewRevCount = 0;
        a.AmendCount = 0;
        a.ErrorCount = 0;
        a.WarningCount = 0;
        a.ChangeId = 0;
        would = 0;
        a.Duplicate = "N";
        dead = "N";
        limitHit = "N";
        a.MayApply = "N";
        a.Completeness = "COMPLETE";
        a.Summary = "";
        a.Refusal = "";
        a.Status = "";
        a.FromLine = 1;
        ValidateTheRequest(a);
        if (a.ReturnCode > 4) return;
        await FindTheChangeAsync(a, ct);
        if (a.ReturnCode > 4 || a.Duplicate == "Y") return;
        await FindTheTargetsAsync(a, ct);
        if (a.ReturnCode > 4) return;
        if (res.Count == 0)
        {
            a.ReturnCode = 16;
            a.Reason = "BOM1409";
            a.Status = "NONE";
            a.Message = Pic.Str(200, "No bill in scope ", Pic.T(a.Scope), " would be touched by change ", Pic.T(a.ChangeRef));
            a.MessageCount = Pic.I(a.MessageCount + 1, 2);
            return;
        }
        await RecordTheChangeHeaderAsync(a, ct);
        if (a.ReturnCode > 4) return;
        await RunTheProposalsAsync(a, ct);
        await RecordTheChangeLinesAsync(a, ct);
        SummariseTheRun(a);
        PageTheOutcome(a);
    }

    /// <summary>Nothing here needs the database (BOM1401-BOM1408, RC 8).</summary>
    private static void ValidateTheRequest(BulkArea a)
    {
        if (Pic.Blank(a.ChangeRef))
        {
            SayError(a, "BOM1401", "A change reference is required - a bulk change must be auditable");
            return;
        }
        if (Pic.Blank(a.User))
        {
            SayError(a, "BOM1408", "A bulk change must be attributable to a user");
            return;
        }
        if (a.Action is not ("REPLACE" or "ADD" or "REMOVE" or "QTYCHG"))
        {
            SayError(a, "BOM1402", "The action must be REPLACE, ADD, REMOVE or QTYCHG");
            return;
        }
        if (Pic.Blank(a.Scope)) a.Scope = a.Action == "ADD" && Pic.Blank(a.OldItem) ? "PARENT" : "ITEM";
        if (a.Action == "REPLACE" && (Pic.Blank(a.OldItem) || Pic.Blank(a.NewItem)))
        {
            SayError(a, "BOM1403", "A replacement needs the component to remove and the component to use");
            return;
        }
        if (a.Action == "ADD" && Pic.Blank(a.NewItem))
        {
            SayError(a, "BOM1404", "An addition needs the component to add");
            return;
        }
        if (a.Action is "REMOVE" or "QTYCHG" && Pic.Blank(a.OldItem))
        {
            SayError(a, "BOM1405", "The component to change must be named");
            return;
        }
        if (a.Action == "QTYCHG" && a.NewQty <= 0 && a.NewScrap <= 0 && Pic.Blank(a.NewUom) && Pic.Blank(a.NewBasis))
        {
            SayError(a, "BOM1406", "A quantity change needs a quantity, a scrap allowance, a unit or a basis");
            return;
        }
        if (a.Scope == "ITEM" && Pic.Blank(a.OldItem))
        {
            SayError(a, "BOM1407", "Scope ITEM needs the component whose bills are the targets");
            return;
        }
        if (a.Scope is "PARENT" or "BOMREF" && Pic.Blank(a.ScopeItem))
        {
            SayError(a, "BOM1407", "Scope PARENT and scope BOMREF need the parent item or bill reference");
        }
    }

    private static void SayError(BulkArea a, string reason, string message)
    {
        a.ReturnCode = 8;
        a.MessageCount = Pic.I(a.MessageCount + 1, 2);
        a.Reason = reason;
        a.Message = message;
    }

    // ------------------------------------------------------------------ COM-015 / BOM-030 duplicate change

    private async Task FindTheChangeAsync(BulkArea a, CancellationToken ct)
    {
        await ReadTheChangeIdAsync(a, ct);
        if (a.ReturnCode > 4 || a.ChangeId == 0) return;
        if (foundStatus != "APPLIED") return;
        a.Duplicate = "Y";
        a.Status = "APPLIED";
        await ReadBackTheOutcomeAsync(a, ct);
        a.ReturnCode = 4;
        a.Reason = "BOM1410";
        a.Message = Pic.Str(200, "Change ", Pic.T(a.ChangeRef), " has already been applied - the original outcome is ",
            "returned and no further revision is raised");
        a.MessageCount = Pic.I(a.MessageCount + 1, 2);
        SummariseTheRun(a);
        PageTheOutcome(a);
    }

    private async Task ReadTheChangeIdAsync(BulkArea a, CancellationToken ct)
    {
        a.ChangeId = 0;
        foundStatus = "";
        string[]? row;
        try
        {
            row = await sql.WithReaderAsync(BomSql.Q(ChangeIdSql), BomSql.Args(Pic.X(a.Company, 10), Pic.X(a.ChangeRef, 30)), async r =>
                await r.ReadAsync(ct) ? new[] { BomSql.Text(r, 0, 60), BomSql.Text(r, 1, 60) } : null, ct);
        }
        catch (SqlioException ex)
        {
            ReportSqlFailure(a, ex.DriverText);
            return;
        }
        if (row is null) return;
        a.ChangeId = (int)decimal.Truncate(BomSql.NumVal(row[0]));
        foundStatus = Pic.X(row[1], 12);
    }

    private async Task ReadBackTheOutcomeAsync(BulkArea a, CancellationToken ct)
    {
        res.Clear();
        List<string[]> rows;
        try
        {
            rows = await sql.WithReaderAsync(BomSql.Q(ReadBackSql), BomSql.Args(a.ChangeId), async r =>
            {
                var list = new List<string[]>();
                while (list.Count < TgtMax && await r.ReadAsync(ct))
                {
                    list.Add(Enumerable.Range(0, 8).Select(i => BomSql.Text(r, i, 60)).ToArray());
                }
                return list;
            }, ct);
        }
        catch (SqlioException ex)
        {
            ReportSqlFailure(a, ex.DriverText);
            return;
        }
        foreach (var c in rows)
        {
            res.Add(new BulkLine
            {
                BomId = (int)decimal.Truncate(BomSql.NumVal(c[0])),
                BomRef = Pic.X(c[1], 30),
                BomRev = Pic.X(c[2], 4),
                NewRev = Pic.X(c[3], 4),
                OldQty = Pic.N(BomSql.NumVal(c[4]), 13, 6),
                NewQty = Pic.N(BomSql.NumVal(c[5]), 13, 6),
                Outcome = Pic.X(c[6], 10),
                Detail = Pic.X(c[7], 120),
            });
            var head = c[6].PadRight(7)[..7];
            if (head == "APPLIED") a.AppliedCount = Pic.I(a.AppliedCount + 1, 4);
            else if (head == "FAILED ") a.FailedCount = Pic.I(a.FailedCount + 1, 4);
            else a.SkippedCount = Pic.I(a.SkippedCount + 1, 4);
        }
        a.TargetCount = res.Count;
    }

    // ------------------------------------------------------------------ BOM-023 targets

    private async Task FindTheTargetsAsync(BulkArea a, CancellationToken ct)
    {
        res.Clear();
        List<string[]> rows;
        try
        {
            rows = await sql.WithReaderAsync(BomSql.Q(TargetsSql), BomSql.Args(Pic.X(a.Company, 10), a.Scope, a.OldItem, a.Scope,
                Pic.X(a.ScopeItem, 40), a.Scope, Pic.X(a.ScopeItem, 40), a.IncludeDraft == "N" ? "N" : "Y"), async r =>
            {
                var list = new List<string[]>();
                while (await r.ReadAsync(ct)) list.Add(Enumerable.Range(0, 5).Select(i => BomSql.Text(r, i, 60)).ToArray());
                return list;
            }, ct);
        }
        catch (SqlioException ex)
        {
            ReportSqlFailure(a, ex.DriverText);
            return;
        }
        foreach (var c in rows)
        {
            if (res.Count >= TgtMax)
            {
                limitHit = "Y";
                continue;
            }
            res.Add(new BulkLine
            {
                BomId = (int)decimal.Truncate(BomSql.NumVal(c[0])),
                BomRef = Pic.X(c[1], 30),
                BomRev = Pic.X(c[2], 4),
                Parent = Pic.X(c[3], 40),
                Status = Pic.X(c[4], 12),
                Outcome = "PENDING",
            });
        }
        a.TargetCount = res.Count;
        if (limitHit != "Y") return;
        a.Completeness = "INCOMPLETE";
        a.ReturnCode = 4;
        a.Reason = "BOM1413";
        a.Message = "More bills use this component than one bulk run can carry - narrow the scope";
        a.MessageCount = Pic.I(a.MessageCount + 1, 2);
    }

    /// <summary>The change set is recorded outside the proposal transaction (autocommit), so a preview leaves its record.</summary>
    private async Task RecordTheChangeHeaderAsync(BulkArea a, CancellationToken ct)
    {
        if (Pic.Blank(a.Title)) a.Title = Pic.Str(100, "Bulk ", Pic.T(a.Action), " of ", Pic.T(a.OldItem));
        // the database calls a quantity change QTYCHANGE (CR2442)
        dbAction = a.Action == "QTYCHG" ? "QTYCHANGE" : Pic.X(a.Action, 12);
        await RunOneStatementAsync(a, ChangeHeaderSql, ct, Pic.X(a.Company, 10), Pic.X(a.ChangeRef, 30), Pic.X(a.Title, 100), dbAction,
            Pic.X(a.RequestedBy, 20), BomSql.D8(a.EffDate), Pic.X(a.Company, 10), Pic.X(a.ChangeRef, 30));
        if (a.ReturnCode > 4) return;
        await ReadTheChangeIdAsync(a, ct);
    }

    // ------------------------------------------------------------------ BOM-024, BOM-030: one unit, one decision

    private async Task RunTheProposalsAsync(BulkArea a, CancellationToken ct)
    {
        recordValid = keep == "Y" ? "Y" : "N";
        await sql.UnitOfWork.BeginAsync(ct);
        for (var t = 1; t <= res.Count && dead != "Y"; t++)
        {
            await ProposeOneTargetAsync(a, t, ct);
        }
        // a preview never keeps a proposal (APPLY, the only keeping route, is deferred)
        await sql.UnitOfWork.RollbackAsync(ct);
        a.Status = "PREVIEW";
        DowngradeAppliedLines(a);
    }

    private async Task ProposeOneTargetAsync(BulkArea a, int t, CancellationToken ct)
    {
        var w = res[t - 1];
        var p = new ProposalArea
        {
            Func = "PROPOSE",
            Company = Pic.X(a.Company, 10),
            Site = Pic.X(a.Site, 10),
            User = Pic.X(a.User, 20),
            ChangeRef = Pic.X(a.ChangeRef, 30),
            ChangeId = a.ChangeId,
            Seq = t,
            BomId = w.BomId,
            Action = a.Action,
            OldItem = a.OldItem,
            NewItem = a.NewItem,
            NewQty = a.NewQty,
            NewUom = Pic.X(a.NewUom, 10),
            NewBasis = Pic.X(a.NewBasis, 12),
            NewScrap = a.NewScrap,
            NewLineNum = a.NewLineNum,
            EffDate = a.EffDate,
            RecordValid = recordValid,
        };
        await proposals.CallAsync(p, ct);
        if (!Pic.Blank(p.BomRef))
        {
            w.BomRef = p.BomRef;
            w.BomRev = p.BomRev;
            w.Parent = p.Parent;
            w.Status = p.SrcStatus;
        }
        w.NewBomId = p.NewBomId;
        w.NewRev = p.NewRev;
        w.Treatment = p.Treatment;
        w.LineNum = p.LineNum;
        w.OldQty = p.OldQty;
        w.NewQty = p.ResultQty;
        w.Outcome = p.Outcome;
        w.Reason = p.Reason;
        w.Errors = p.ErrorCount;
        w.Detail = p.Msg;
        a.ErrorCount = Pic.I(a.ErrorCount + p.ErrorCount, 4);
        a.WarningCount = Pic.I(a.WarningCount + p.WarningCount, 4);
        switch (p.Outcome)
        {
            case "APPLIED":
                a.AppliedCount = Pic.I(a.AppliedCount + 1, 4);
                if (p.Treatment == "NEWREV") a.NewRevCount = Pic.I(a.NewRevCount + 1, 4);
                else a.AmendCount = Pic.I(a.AmendCount + 1, 4);
                break;
            case "FAILED":
                a.FailedCount = Pic.I(a.FailedCount + 1, 4);
                break;
            default:
                a.SkippedCount = Pic.I(a.SkippedCount + 1, 4);
                break;
        }
        if (p.SqlDead == "Y") dead = "Y";
    }

    /// <summary>Nothing was kept, so nothing may claim to have been applied (BOM-024).</summary>
    private void DowngradeAppliedLines(BulkArea a)
    {
        would = a.AppliedCount;
        a.AppliedCount = 0;
        foreach (var w in res.Where(w => w.Outcome == "APPLIED"))
        {
            w.Outcome = "PENDING";
            w.NewBomId = 0;
        }
    }

    // ------------------------------------------------------------------ the change lines (autocommit)

    private async Task RecordTheChangeLinesAsync(BulkArea a, CancellationToken ct)
    {
        if (a.ChangeId == 0) return;
        await RunOneStatementAsync(a, DeleteLinesSql, ct, a.ChangeId);
        for (var t = 1; t <= res.Count; t++)
        {
            var w = res[t - 1];
            var failure = Pic.Blank(w.Reason) ? w.Detail : Pic.Str(256, "STERP-E-", Pic.T(w.Reason), ": ", w.Detail.PadRight(120));
            await RunOneStatementAsync(a, ChangeLineSql, ct, a.ChangeId, t, w.BomId, w.BomRef, w.BomRev, dbAction,
                Pic.Blank(a.OldItem) ? null : a.OldItem, Pic.Blank(a.NewItem) ? null : a.NewItem, w.OldQty, w.NewQty,
                Pic.Blank(w.NewRev) ? null : w.NewRev, w.Outcome, failure);
        }
        await RunOneStatementAsync(a, UpdateHeaderSql, ct, Pic.X(a.Title, 100), dbAction, Pic.X(a.RequestedBy, 20), BomSql.D8(a.EffDate),
            Pic.X(a.Status, 12), a.TargetCount, a.AppliedCount, Pic.X(a.Summary, 200), Pic.X(a.Company, 10), Pic.X(a.ChangeRef, 30));
    }

    private async Task RunOneStatementAsync(BulkArea a, string statement, CancellationToken ct, params object?[] values)
    {
        try
        {
            await sql.ExecuteAsync(BomSql.Q(statement), BomSql.Args(values), ct);
        }
        catch (SqlioException ex)
        {
            ReportSqlFailure(a, ex.DriverText);
        }
    }

    // ------------------------------------------------------------------ summary and decision

    private void SummariseTheRun(BulkArea a)
    {
        if (a.Duplicate == "Y") would = a.AppliedCount;
        if (keep == "Y" && a.FailedCount == 0 && dead == "N") would = a.AppliedCount;
        a.Summary = Pic.Str(200, Pic.Z(a.TargetCount), " bill(s) in scope; ", Pic.Z(Pic.I(would, 4)), " to change (",
            Pic.Z(a.NewRevCount), " as a new revision, ", Pic.Z(a.AmendCount), " as a draft amendment); ",
            Pic.Z(a.SkippedCount), " skipped; ", Pic.Z(a.FailedCount), " failed validation");
        DecideTheReturnCode(a);
    }

    private void DecideTheReturnCode(BulkArea a)
    {
        if (a.Duplicate == "Y") return;
        if (dead == "Y")
        {
            // the run's own SQLIO-ERRMSG, cleared by its last (successful) SQLIO call
            ReportSqlFailure(a, "");
            return;
        }
        if (a.FailedCount > 0)
        {
            a.MayApply = "N";
            a.Refusal = Pic.Str(200, "STERP-E-BOM1411: ", Pic.Z(a.FailedCount), " proposed revision(s) fail validation, so no bill",
                " is changed at all - correct them and preview ", "again");
            a.ReturnCode = 8;
            a.Reason = "BOM1411";
            a.Message = a.Refusal;
            a.MessageCount = Pic.I(a.MessageCount + 1, 2);
            return;
        }
        a.MayApply = would > 0 ? "Y" : "N";
        a.Reason = "BOM1415";
        a.Message = Pic.Str(200, "STERP-I-BOM1415: preview only - nothing has been ", "changed. ", Pic.T(a.Summary));
    }

    private void PageTheOutcome(BulkArea a)
    {
        if (a.FromLine < 1) a.FromLine = 1;
        a.WinCnt = 0;
        a.MoreLines = "N";
        a.Lines.Clear();
        var i = a.FromLine;
        while (!(i > res.Count || a.WinCnt >= a.WinMax))
        {
            a.WinCnt++;
            var line = res[i - 1].Clone();
            line.Seq = i;
            a.Lines.Add(line);
            i++;
        }
        a.FromLine = i;
        if (i <= res.Count) a.MoreLines = "Y";
    }

    private static void ReportSqlFailure(BulkArea a, string? errMsg)
    {
        a.ReturnCode = 12;
        a.Reason = "BOM1412";
        a.Message = Pic.Str(200, "STERP-E-BOM1412: bulk change database access failed ", "- ", (errMsg ?? "").PadRight(120)[..120]);
        a.MessageCount = Pic.I(a.MessageCount + 1, 2);
    }
}
