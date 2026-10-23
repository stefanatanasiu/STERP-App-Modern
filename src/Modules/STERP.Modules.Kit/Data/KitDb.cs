using Sterp.Modules.Kit.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Kit.Data;

/// <summary>
/// KITDB - all SQL for the execution kit tables (cobol/online/kit/KITDB.cob:119-1136), statement for statement on the
/// caller's SQLIO unit of work (contracts 6.5): same text, '?' parameters bound as SQLIO varchar text, columns fetched as
/// character slots of the legacy widths and converted with NUMVAL. KD-RC is 0, 100 (not found / end of data) or -1 with
/// KD-ERRMSG = the first 200 characters of the driver text. Only the functions of the implemented routes exist
/// (HDRGET, HDRSTAT, HDRFRZ, LINOPEN/LINNEXT/LINCLOS, LINGET, LINSAVE, ITEMGET, ISSINS, PCKUSE); the line cursor (SQLIO
/// cursor 5) lives in this instance, one per request or job step (no cross-request state, spec 7.4).
/// </summary>
internal sealed class KitDb(ISqlRunner sql)
{
    private List<SqlioRow>? _cursor;
    private int _cursorPos;

    // ------------------------------------------------------------------ statements (KITDB.cob)

    internal const string HeaderSelect =
        "SELECT CONVERT(varchar(20),ExecutionKitId), SiteCode," +
        " ParentItemCode, ISNULL(ParentRevision,'')," +
        " CONVERT(varchar(12),ISNULL(BomHeaderId,0))," +
        " ISNULL(BomRevision,''), ISNULL(SourceDemandType,'')," +
        " ISNULL(SourceDemandRef,''), ISNULL(JobRef,'')," +
        " ISNULL(ParentKitRef,'')," +
        " CONVERT(varchar(26),PlannedGoodQty)," +
        " CONVERT(varchar(26),CompletedGoodQty)," +
        " CONVERT(varchar(26),RejectedQty)," +
        " CONVERT(varchar(26),ISNULL(GrossStarts,0))," +
        " CONVERT(varchar(20),ISNULL(YieldFactor,1))," +
        " CONVERT(varchar(26),ISNULL(ProductionQuantum,0))," +
        " CONVERT(varchar(26),ISNULL(BatchSize,0))," +
        " CONVERT(varchar(12),RunCount)," +
        " CONVERT(varchar(12),ISNULL(BatchCount,0))," +
        " ISNULL(CONVERT(char(8),NeedDate,112),'')," +
        " ISNULL(CONVERT(char(8),PlannedStartDate,112),'')," +
        " ISNULL(CONVERT(char(8),PlannedCompletionDate,112),'')," +
        " ISNULL(CONVERT(char(8),ActualCompletionDate,112),'')," +
        " ISNULL(CONVERT(char(8),SelectionDate,112),'')," +
        " ISNULL(OwnerUser,''), Status, ISNULL(ReleasedBy,'')," +
        " CONVERT(varchar(20),ISNULL(ApprovalId,0))," +
        " CONVERT(varchar(20),WipValueGBP)," +
        " CONVERT(varchar(2),IsBackflush)" +
        " FROM kit.ExecutionKit" +
        " WHERE CompanyCode = ? AND KitRef = ?";

    private static readonly int[] HeaderWidths = [20, 10, 40, 4, 12, 4, 12, 40, 20, 30, 26, 26, 26, 26, 20, 26, 26, 12, 12, 8, 8, 8, 8, 8, 20, 14, 20, 20, 20, 2];

    private const string StatusUpdate =
        "UPDATE kit.ExecutionKit SET Status = ?," +
        " ReleasedBy = CASE WHEN ? = 'RELEASED' THEN ?" +
        "   ELSE ReleasedBy END," +
        " ReleasedAt = CASE WHEN ? = 'RELEASED' THEN SYSDATETIME()" +
        "   ELSE ReleasedAt END," +
        " ReopenedBy = CASE WHEN ? = 'REOPEN' THEN ?" +
        "   ELSE ReopenedBy END," +
        " ReopenedAt = CASE WHEN ? = 'REOPEN' THEN SYSDATETIME()" +
        "   ELSE ReopenedAt END," +
        " ReopenReason = CASE WHEN ? = 'REOPEN' THEN ?" +
        "   ELSE ReopenReason END," +
        " ModifiedBy = ?, ModifiedAt = SYSDATETIME()" +
        " WHERE CompanyCode = ? AND KitRef = ?";

    private const string FreezeUpdate =
        "UPDATE kit.ExecutionKit SET BomHeaderId = NULLIF(?,0)," +
        " BomRevision = ?, ParentRevision = ?, GrossStarts = ?," +
        " YieldFactor = ?, ProductionQuantum = ?, BatchSize = ?," +
        " RunCount = ?, BatchCount = ?," +
        " SelectionDate = TRY_CONVERT(date,NULLIF(?,''),112)," +
        " SupplyChoiceNote = ?, ModifiedBy = ?," +
        " ModifiedAt = SYSDATETIME()" +
        " WHERE CompanyCode = ? AND KitRef = ?";

    internal const string LineSelect =
        "SELECT CONVERT(varchar(20),KitLineId)," +
        " CONVERT(varchar(12),LineNum), PathRef, ComponentItemCode," +
        " ISNULL(ComponentRevision,'')," +
        " CONVERT(varchar(12),ISNULL(BomComponentId,0)), UsageBasis," +
        " CONVERT(varchar(26),OriginalRequirementQty)," +
        " CONVERT(varchar(26),CurrentRequirementQty), UomCode," +
        " CONVERT(varchar(26),ConversionFactor)," +
        " CONVERT(varchar(20),ScrapPct)," +
        " CONVERT(varchar(26),ReservedQty)," +
        " CONVERT(varchar(26),PickedQty)," +
        " CONVERT(varchar(26),GrossIssuedQty)," +
        " CONVERT(varchar(26),ReturnedQty)," +
        " CONVERT(varchar(26),ScrappedQty)," +
        " CONVERT(varchar(26),NetIssuedQty)," +
        " CONVERT(varchar(26),RemainingQty)," +
        " CONVERT(varchar(26),ExcessQty)," +
        " ISNULL(SubstituteItemCode,'')," +
        " CONVERT(varchar(20),ISNULL(SubstituteApprovalId,0))," +
        " ISNULL(CONVERT(char(8),NeedDate,112),'')," +
        " ISNULL(IssueLocationCode,'')," +
        " CONVERT(varchar(26),ISNULL(PlannedUnitCostGBP,0))," +
        " CONVERT(varchar(20),IssuedValueGBP), Status" +
        " FROM kit.ExecutionKitLine WHERE ExecutionKitId = ?";

    private static readonly int[] LineWidths = [20, 12, 20, 40, 4, 12, 12, 26, 26, 10, 26, 20, 26, 26, 26, 26, 26, 26, 26, 26, 40, 20, 8, 20, 26, 20, 12];

    private const string LineSave =
        "UPDATE kit.ExecutionKitLine SET CurrentRequirementQty = ?," +
        " ReservedQty = ?, PickedQty = ?, GrossIssuedQty = ?," +
        " ReturnedQty = ?, ScrappedQty = ?, NetIssuedQty = ?," +
        " RemainingQty = ?, ExcessQty = ?," +
        " SubstituteItemCode = NULLIF(?,'')," +
        " SubstituteApprovalId = NULLIF(?,0)," +
        " DeviationReason = NULLIF(?,'')," +
        " DeviationApprovedBy = NULLIF(?,'')," +
        " IssuedValueGBP = ?, Status = ?" +
        " WHERE KitLineId = ?";

    private const string ItemSelect =
        "SELECT i.Description, i.ItemClass, i.StockUomCode," +
        " CONVERT(varchar(2),i.IsStocked), i.TraceabilityType," +
        " CONVERT(varchar(26),ISNULL(c.StandardUnitCost,0))," +
        " CONVERT(varchar(26),ISNULL(c.AverageUnitCost,0))," +
        " i.LifecycleStatus, ISNULL(c.CostMethod,'')" +
        " FROM inv.Item i OUTER APPLY (SELECT TOP (1)" +
        " x.StandardUnitCost, x.AverageUnitCost, x.CostMethod" +
        " FROM cst.ItemCost x WHERE x.CompanyCode = i.CompanyCode" +
        "  AND x.ItemCode = i.ItemCode" +
        "  AND (x.SiteCode = '*' OR x.SiteCode = ?)" +
        " ORDER BY CASE WHEN x.SiteCode = '*' THEN 1 ELSE 0 END," +
        " CASE WHEN x.Revision = i.CurrentRevision THEN 0 ELSE 1 END)" +
        " c WHERE i.CompanyCode = ? AND i.ItemCode = ?";

    private const string IssueInsert =
        "INSERT kit.KitIssue (CompanyCode, KitRef, KitLineId," +
        " LineNum, ComponentItemCode, Revision, IssueType," +
        " Quantity, UomCode, StockQty, WarehouseCode, LocationCode," +
        " LotCode, SerialNo, ActualDate, PostingDate, OperatorUser," +
        " UnitCostGBP, ValueGBP, ReasonCode, Reason, ApprovalId," +
        " OriginalKitIssueId, MovementId, CorrelationId, CreatedBy)" +
        " VALUES (?,?,NULLIF(?,0),NULLIF(?,0),?,NULLIF(?,''),?,?," +
        " NULLIF(?,''),?,NULLIF(?,''),NULLIF(?,''),NULLIF(?,'')," +
        " NULLIF(?,''),CONVERT(date,?,112),CONVERT(date,?,112),?," +
        " ?,?,NULLIF(?,''),NULLIF(?,''),NULLIF(?,0),NULLIF(?,0)," +
        " NULLIF(?,0),NULLIF(?,''),?);" +
        " SELECT CONVERT(varchar(20),SCOPE_IDENTITY())";

    private const string PickConsume =
        "DECLARE @q decimal(19,6) = CONVERT(decimal(19,6),?);" +
        " DECLARE @p decimal(19,6); DECLARE @u decimal(19,6);" +
        " DECLARE @c decimal(19,6); DECLARE @t decimal(19,6);" +
        " DECLARE @r int;" +
        " SELECT @p = ISNULL(SUM(CASE WHEN PickType = 'PICK'" +
        " THEN PickedQty - ConsumedQty ELSE 0 END),0)," +
        " @u = ISNULL(SUM(CASE WHEN PickType = 'UNPICK'" +
        " THEN ConsumedQty - PickedQty ELSE 0 END),0)" +
        " FROM kit.KitPick WHERE CompanyCode = ? AND KitRef = ?" +
        " AND LineNum = ?;" +
        " SET @c = CASE WHEN @p - @u <= 0 THEN 0" +
        " WHEN @q < @p - @u THEN @q ELSE @p - @u END;" +
        " SET @t = @c + @u;" +
        " WITH p AS (SELECT KitPickId, PickedQty, ConsumedQty," +
        " SUM(PickedQty - ConsumedQty) OVER (ORDER BY KitPickId" +
        " ROWS UNBOUNDED PRECEDING) AS RunTot FROM kit.KitPick" +
        " WHERE CompanyCode = ? AND KitRef = ? AND LineNum = ?" +
        " AND PickType = 'PICK' AND PickedQty > ConsumedQty)" +
        " UPDATE p SET ConsumedQty = ConsumedQty + CASE" +
        " WHEN RunTot <= @t THEN PickedQty - ConsumedQty" +
        " ELSE @t - (RunTot - (PickedQty - ConsumedQty)) END" +
        " WHERE RunTot - (PickedQty - ConsumedQty) < @t;" +
        " SET @r = @@ROWCOUNT;" +
        " UPDATE kit.KitPick SET ConsumedQty = PickedQty" +
        " WHERE CompanyCode = ? AND KitRef = ? AND LineNum = ?" +
        " AND PickType = 'UNPICK' AND ConsumedQty <> PickedQty" +
        " AND @t >= @u;" +
        " SELECT CONVERT(varchar(20), @c)," +
        " CONVERT(varchar(10), @r)";

    // ------------------------------------------------------------------ entry (Main-Entry)

    /// <summary>CALL "KITDB" USING KIT-DB-CTL, KIT-HEADER-REC, KIT-LINE-REC, KIT-ITEM-REC with KD-FUNC set.</summary>
    public async Task CallAsync(KitDbCtl kd, KitHeader kh, KitLine kl, KitItem ki, CancellationToken ct)
    {
        kd.Rc = 0;
        kd.Rows = 0;
        kd.ErrMsg = "";
        try
        {
            switch (kd.Func)
            {
                case "HDRGET": await ReadHeaderAsync(kd, kh, ct); break;
                case "HDRSTAT": await UpdateStatusAsync(kd, ct); break;
                case "HDRFRZ": await FreezeAsync(kd, kh, ct); break;
                case "LINOPEN": await OpenLinesAsync(kd, ct); break;
                case "LINNEXT": FetchLine(kd, kl); break;
                case "LINCLOS": _cursor = null; _cursorPos = 0; break;
                case "LINGET": await ReadOneLineAsync(kd, kl, ct); break;
                case "LINSAVE": await SaveLineAsync(kd, kl, ct); break;
                case "ITEMGET": await ReadItemAsync(kd, ki, ct); break;
                case "ISSINS": await InsertIssueAsync(kd, kl, ct); break;
                case "PCKUSE": await ConsumePicksAsync(kd, ct); break;
                default:
                    // HDRINS, HDRQTY, LININS, GENINS, PCKINS, SUPINS, SUPSUM, SUPFREE serve deferred programs only
                    kd.Rc = -1;
                    kd.ErrMsg = "KITDB function not supported";
                    break;
            }
        }
        catch (SqlioException ex)
        {
            // Set-Sql-Result: SQLCODE -1, KD-ERRMSG = SQLIO-ERRMSG (1:200)
            kd.Rc = -1;
            kd.ErrMsg = Pic.X(ex.DriverText, 200);
        }
    }

    // ------------------------------------------------------------------ HDRGET

    private async Task ReadHeaderAsync(KitDbCtl kd, KitHeader kh, CancellationToken ct)
    {
        var row = await sql.SingleAsync(HeaderSelect, [SqlioParm.Text(Pic.Pad(kd.Company, 10)), SqlioParm.Text(Pic.Pad(kd.KitRef, 30))], HeaderWidths, ct);
        kd.Rc = row.SqlCode;
        if (!row.Found) return;
        // Move-Header-In
        kh.Company = kd.Company;
        kh.KitRef = kd.KitRef;
        kh.ExecKitId = (long)Pic.N(row.NumVal(0), 17, 0);
        kd.KitId = kh.ExecKitId;
        kh.Site = row.Text(1);
        kh.ParentItem = row.Text(2);
        kh.ParentRev = row.Text(3);
        kh.BomId = (int)decimal.Truncate(row.NumVal(4));
        kh.BomRev = row.Text(5);
        kh.SourceType = row.Text(6);
        kh.SourceRef = row.Text(7);
        kh.JobRef = row.Text(8);
        kh.ParentKitRef = row.Text(9);
        kh.PlannedQty = Pic.Q(row.NumVal(10));
        kh.CompletedQty = Pic.Q(row.NumVal(11));
        kh.RejectedQty = Pic.Q(row.NumVal(12));
        kh.GrossStarts = Pic.Q(row.NumVal(13));
        kh.Yield = Pic.N(row.NumVal(14), 5, 6);
        kh.Quantum = Pic.Q(row.NumVal(15));
        kh.BatchSize = Pic.Q(row.NumVal(16));
        kh.RunCount = (int)decimal.Truncate(row.NumVal(17));
        kh.BatchCount = (int)decimal.Truncate(row.NumVal(18));
        kh.NeedDate = Pic.DateSlot(row.Raw(19));
        kh.StartDate = Pic.DateSlot(row.Raw(20));
        kh.PlanCompDate = Pic.DateSlot(row.Raw(21));
        kh.ActCompDate = Pic.DateSlot(row.Raw(22));
        kh.SelectionDate = Pic.DateSlot(row.Raw(23));
        kh.OwnerUser = row.Text(24);
        kh.Status = row.Text(25);
        kh.ReleasedBy = row.Text(26);
        kh.ApprovalId = (long)Pic.N(row.NumVal(27), 17, 0);
        kh.WipValue = Pic.M(row.NumVal(28));
        kh.Backflush = row.Raw(29).StartsWith('1') ? "Y" : "N";
    }

    // ------------------------------------------------------------------ HDRSTAT / HDRFRZ

    private async Task UpdateStatusAsync(KitDbCtl kd, CancellationToken ct)
    {
        var status = SqlioParm.Text(Pic.Pad(kd.NewStatus, 14));
        var func = SqlioParm.Text(Pic.Pad(kd.Func, 8));
        var user = SqlioParm.Text(Pic.Pad(kd.User, 20));
        await RunUpdateAsync(kd, StatusUpdate,
        [
            status, status, user, status, func, user, func, func, SqlioParm.Text(Pic.Pad(kd.Reason, 200)), user,
            SqlioParm.Text(Pic.Pad(kd.Company, 10)), SqlioParm.Text(Pic.Pad(kd.KitRef, 30)),
        ], ct);
    }

    private async Task FreezeAsync(KitDbCtl kd, KitHeader kh, CancellationToken ct)
    {
        await RunUpdateAsync(kd, FreezeUpdate,
        [
            SqlioParm.Integer(kh.BomId),
            SqlioParm.Text(Pic.Pad(kh.BomRev, 4)),
            SqlioParm.Text(Pic.Pad(kh.ParentRev, 4)),
            SqlioParm.Number(kh.GrossStarts),
            SqlioParm.Number(kh.Yield),
            SqlioParm.Number(kh.Quantum),
            SqlioParm.Number(kh.BatchSize),
            SqlioParm.Integer(kh.RunCount),
            SqlioParm.Integer(kh.BatchCount),
            kh.SelectionDate > 0 ? SqlioParm.Digits(kh.SelectionDate, 8) : SqlioParm.Text(""),
            SqlioParm.Text(Pic.Pad(kd.Reason, 200)),
            SqlioParm.Text(Pic.Pad(kd.User, 20)),
            SqlioParm.Text(Pic.Pad(kd.Company, 10)),
            SqlioParm.Text(Pic.Pad(kd.KitRef, 30)),
        ], ct);
    }

    /// <summary>Run-Update: EXEC on the single cursor, then CLOS.</summary>
    private async Task RunUpdateAsync(KitDbCtl kd, string statement, IReadOnlyList<SqlioParm> parms, CancellationToken ct)
    {
        var r = await sql.ExecAsync(statement, parms, ct);
        kd.Rc = r.SqlCode;
        kd.Rows = r.RowCount;
    }

    // ------------------------------------------------------------------ LINOPEN / LINNEXT / LINGET / LINSAVE

    private async Task OpenLinesAsync(KitDbCtl kd, CancellationToken ct)
    {
        var stmt = LineSelect + (kd.OnlyOpen == "Y"
            ? " AND Status NOT IN ('CLOSED','CANCELLED') ORDER BY LineNum"
            : " ORDER BY LineNum");
        _cursor = null;
        _cursorPos = 0;
        var rows = await sql.FetchAllAsync(stmt, [SqlioParm.Integer(kd.KitId)], LineWidths, ct);
        _cursor = [.. rows];
        kd.Rc = 0;
    }

    private void FetchLine(KitDbCtl kd, KitLine kl)
    {
        if (_cursor is null || _cursorPos >= _cursor.Count)
        {
            kd.Rc = 100;
            return;
        }
        MoveLineIn(_cursor[_cursorPos++], kl);
        kd.Rc = 0;
    }

    private async Task ReadOneLineAsync(KitDbCtl kd, KitLine kl, CancellationToken ct)
    {
        var row = await sql.SingleAsync(LineSelect + " AND LineNum = ?",
            [SqlioParm.Integer(kd.KitId), SqlioParm.Integer(kd.LineNum)], LineWidths, ct);
        kd.Rc = row.SqlCode;
        if (row.Found) MoveLineIn(row, kl);
    }

    private static void MoveLineIn(SqlioRow row, KitLine kl)
    {
        kl.LineId = (long)Pic.N(row.NumVal(0), 17, 0);
        kl.LineNum = (int)decimal.Truncate(row.NumVal(1));
        kl.PathRef = row.Text(2);
        kl.ItemCode = row.Text(3);
        kl.Revision = row.Text(4);
        kl.BomCompId = (int)decimal.Truncate(row.NumVal(5));
        kl.UsageBasis = row.Text(6);
        kl.OriginalReq = Pic.Q(row.NumVal(7));
        kl.CurrentReq = Pic.Q(row.NumVal(8));
        kl.UomCode = row.Text(9);
        kl.Factor = Pic.N(row.NumVal(10), 9, 9);
        kl.ScrapPct = Pic.N(row.NumVal(11), 5, 6);
        kl.ReservedQty = Pic.Q(row.NumVal(12));
        kl.PickedQty = Pic.Q(row.NumVal(13));
        kl.GrossIssued = Pic.Q(row.NumVal(14));
        kl.ReturnedQty = Pic.Q(row.NumVal(15));
        kl.ScrappedQty = Pic.Q(row.NumVal(16));
        kl.NetIssued = Pic.Q(row.NumVal(17));
        kl.RemainingQty = Pic.Q(row.NumVal(18));
        kl.ExcessQty = Pic.Q(row.NumVal(19));
        kl.Substitute = row.Text(20);
        kl.SubApproval = (long)Pic.N(row.NumVal(21), 17, 0);
        kl.NeedDate = Pic.DateSlot(row.Raw(22));
        kl.Location = row.Text(23);
        kl.PlanUnitCost = Pic.N(row.NumVal(24), 11, 6);
        kl.IssuedValue = Pic.M(row.NumVal(25));
        kl.Status = row.Text(26);
    }

    private async Task SaveLineAsync(KitDbCtl kd, KitLine kl, CancellationToken ct)
    {
        await RunUpdateAsync(kd, LineSave,
        [
            SqlioParm.Number(kl.CurrentReq),
            SqlioParm.Number(kl.ReservedQty),
            SqlioParm.Number(kl.PickedQty),
            SqlioParm.Number(kl.GrossIssued),
            SqlioParm.Number(kl.ReturnedQty),
            SqlioParm.Number(kl.ScrappedQty),
            SqlioParm.Number(kl.NetIssued),
            SqlioParm.Number(kl.RemainingQty),
            SqlioParm.Number(kl.ExcessQty),
            SqlioParm.Text(Pic.Pad(kl.Substitute, 40)),
            SqlioParm.Integer(kl.SubApproval),
            SqlioParm.Text(Pic.Pad(kl.Deviation, 300)[..200]),
            SqlioParm.Text(Pic.Pad(kl.DevApprover, 20)),
            SqlioParm.Number(kl.IssuedValue),
            SqlioParm.Text(Pic.Pad(kl.Status, 12)),
            SqlioParm.Integer(kl.LineId),
        ], ct);
    }

    // ------------------------------------------------------------------ ITEMGET

    private async Task ReadItemAsync(KitDbCtl kd, KitItem ki, CancellationToken ct)
    {
        var row = await sql.SingleAsync(ItemSelect,
            [SqlioParm.Text(Pic.Pad(kd.Site, 10)), SqlioParm.Text(Pic.Pad(kd.Company, 10)), SqlioParm.Text(Pic.Pad(kd.ItemCode, 40))],
            [40, 12, 10, 2, 8, 26, 26, 10, 4], ct);
        kd.Rc = row.SqlCode;
        if (!row.Found) return;
        ki.ItemCode = kd.ItemCode;
        ki.Description = row.Text(0);
        ki.ItemClass = row.Text(1);
        ki.StockUom = row.Text(2);
        ki.Status = row.Text(7);
        ki.IsStocked = row.Raw(3).StartsWith('1') ? "Y" : "N";
        ki.LotControl = "N";
        ki.SerialControl = "N";
        ki.Traced = "N";
        switch (row.Raw(4)[..6])
        {
            case "LOT   ":
                ki.LotControl = "Y";
                ki.Traced = "Y";
                break;
            case "SERIAL":
                ki.SerialControl = "Y";
                ki.Traced = "Y";
                break;
        }
        ki.StdCost = Pic.N(row.NumVal(5), 11, 6);
        ki.AvgCost = Pic.N(row.NumVal(6), 11, 6);
        ki.CostMethod = row.Text(8);
    }

    // ------------------------------------------------------------------ ISSINS / PCKUSE

    private async Task InsertIssueAsync(KitDbCtl kd, KitLine kl, CancellationToken ct)
    {
        var row = await sql.SingleAsync(IssueInsert,
        [
            SqlioParm.Text(Pic.Pad(kd.Company, 10)),
            SqlioParm.Text(Pic.Pad(kd.KitRef, 30)),
            SqlioParm.Integer(kl.LineId),
            SqlioParm.Integer(kl.LineNum),
            SqlioParm.Text(Pic.Pad(kd.IItem, 40)),
            SqlioParm.Text(Pic.Pad(kd.IRev, 4)),
            SqlioParm.Text(Pic.Pad(kd.IType, 12)),
            SqlioParm.Number(kd.IQty),
            SqlioParm.Text(Pic.Pad(kd.IUom, 10)),
            SqlioParm.Number(kd.IStockQty),
            SqlioParm.Text(Pic.Pad(kd.IWarehouse, 20)),
            SqlioParm.Text(Pic.Pad(kd.ILocation, 20)),
            SqlioParm.Text(Pic.Pad(kd.ILot, 30)),
            SqlioParm.Text(Pic.Pad(kd.ISerial, 40)),
            SqlioParm.Digits(kd.IActDate, 8),
            SqlioParm.Digits(kd.IPostDate, 8),
            SqlioParm.Text(Pic.Pad(kd.User, 20)),
            SqlioParm.Number(kd.IUnitCost),
            SqlioParm.Number(kd.IValue),
            SqlioParm.Text(Pic.Pad(kd.IReasonCode, 20)),
            SqlioParm.Text(Pic.Pad(kd.IReason, 200)),
            SqlioParm.Integer(kd.IApproval),
            SqlioParm.Integer(kd.IOrigIssue),
            SqlioParm.Integer(kd.IMovement),
            SqlioParm.Text(Pic.Pad(kd.Reason, 200)[..64]),
            SqlioParm.Text(Pic.Pad(kd.User, 20)),
        ], [20], ct);
        kd.Rc = row.SqlCode;
        // Run-Identity-Insert
        kd.NewId = row.Found ? (long)Pic.N(row.NumVal(0), 17, 0) : 0;
    }

    private async Task ConsumePicksAsync(KitDbCtl kd, CancellationToken ct)
    {
        var co = SqlioParm.Text(Pic.Pad(kd.Company, 10));
        var kit = SqlioParm.Text(Pic.Pad(kd.KitRef, 30));
        var line = SqlioParm.Integer(kd.LineNum);
        var row = await sql.SingleAsync(PickConsume, [SqlioParm.Number(kd.PQty), co, kit, line, co, kit, line, co, kit, line], [20, 10], ct);
        kd.Rc = row.SqlCode;
        kd.PConsumed = 0;
        kd.Rows = 0;
        if (!row.Found) return;
        if (!Pic.Blank(row.Raw(0))) kd.PConsumed = Pic.Q(row.NumVal(0));
        if (!Pic.Blank(row.Raw(1))) kd.Rows = (int)decimal.Truncate(row.NumVal(1));
    }
}
