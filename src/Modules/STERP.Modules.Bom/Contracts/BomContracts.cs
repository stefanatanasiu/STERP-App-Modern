using Sterp.Platform.Kernel;

namespace Sterp.Modules.Bom.Contracts;

/// <summary>
/// IC-BOM-BOMEXPL (FR-STERP-BOM-040): the legacy BOMEXPL nested explosion engine as an in-process contract, executed on
/// the caller's unit of work (the scoped <c>ISqlRunner</c>/<c>IUnitOfWork</c> of the request or job step). It mirrors
/// <c>CALL "BOMEXPL" USING BOMEXPLCA</c>: the caller fills <see cref="BomExplosionArea"/> (header, request fields, paging
/// window), calls <see cref="LinkAsync"/> with <see cref="BomExplosionArea.Function"/> set, and reads the outcome
/// (<see cref="BomExplosionArea.Result"/> = STCAHDR return/reason/message) and the output fields back from the same area.
/// Functions: <c>EXPLODE</c> computes and retains one result in <see cref="BomExplosionArea.Calculation"/>, <c>GETLINE</c>
/// pages that retained result, <c>LEAFQTY</c> totals one item over it; any other function is RC 8 <c>BOM0501</c>
/// (<c>NODE</c> is the engine's internal recursion; <c>STORE</c> is deferred, SME-015). The result belongs to the area that
/// computed it (BC-BOM-01: no process-wide pool).
/// </summary>
public interface IBomExplosionEngine
{
    /// <summary>CALL BOMEXPL with <see cref="BomExplosionArea.Function"/>; returns the outcome also stored in the area.</summary>
    Task<LegacyResult> LinkAsync(BomExplosionArea area, CancellationToken ct = default);
}

/// <summary>
/// IC-BOM-BOMCOST (FR-STERP-BOM-041): the legacy BOMCOST costed explosion as an in-process contract on the caller's unit of
/// work, mirroring <c>CALL "BOMCOST" USING BOMCOSTCA</c>. Functions: <c>COST</c> explodes (BOMEXPL, GROSS, or NET when
/// <see cref="BomCostArea.Netted"/> = "Y"), values every occurrence once and retains the valued result in
/// <see cref="BomCostArea.Calculation"/>; <c>GETLINE</c> pages it; <c>BRANCH</c> values the sub-tree rooted at
/// <see cref="BomCostArea.BranchSeq"/>; any other function is RC 8 <c>BOM0801</c>. The material-rate cache lives for one
/// <c>COST</c> call (CS-BOM-05).
/// </summary>
public interface IBomCostEngine
{
    /// <summary>CALL BOMCOST with <see cref="BomCostArea.Function"/>; returns the outcome also stored in the area.</summary>
    Task<LegacyResult> LinkAsync(BomCostArea area, CancellationToken ct = default);
}

/// <summary>One option choice of BX-OPTIONS (BX-O-GROUP X(10), BX-O-CHOICE X(40)).</summary>
public sealed record BomOptionChoice(string Group, string Choice);

/// <summary>
/// The BOMEXPLCA COMMAREA (cobol/copy/BOMEXPLCA.cpy). Text fields are carried right-trimmed and are cut to their PIC X(n)
/// length by the engine; dates are PIC 9(8) yyyymmdd integers (0 = not given); quantities are <see cref="decimal"/> with the
/// legacy scale. The engine writes the defaults it applies back into the request fields exactly as BOMEXPL did
/// (parent item upper-cased, effective date, purpose, calc type, max depth, expand-phantom, stock source).
/// </summary>
public sealed class BomExplosionArea
{
    // ---- STCAHDR (cobol/copy/STCAHDR.cpy) ------------------------------------------------------------------------
    /// <summary>CA-FUNCTION: EXPLODE, GETLINE, LEAFQTY.</summary>
    public string Function { get; set; } = "";
    /// <summary>CA-COMPANY X(10).</summary>
    public string Company { get; set; } = "";
    /// <summary>CA-SITE X(10) (blank = every site, SME-1814).</summary>
    public string Site { get; set; } = "";
    /// <summary>CA-USER X(20).</summary>
    public string User { get; set; } = "";
    /// <summary>CA-BUSINESS-DATE 9(8) yyyymmdd (default of a zero effective date).</summary>
    public int BusinessDate { get; set; }
    /// <summary>CA-CORRELATION-ID X(36).</summary>
    public string CorrelationId { get; set; } = "";
    /// <summary>CA-PROGRAM (set to BOMEXPL by the engine).</summary>
    public string Program { get; set; } = "";
    /// <summary>CA-RETURN-CODE, CA-REASON-CODE, CA-MESSAGE, CA-MSG-COUNT, CA-REQ-REF after the call.</summary>
    public LegacyResult Result { get; set; } = LegacyResult.Ok();

    // ---- request (BX-BODY) ------------------------------------------------------------------------------------------
    /// <summary>BX-CALC-TYPE X(10): blank -> GROSS, NET -> net mode, any other value (KIT sends EXECUTION) -> gross.</summary>
    public string CalcType { get; set; } = "";
    /// <summary>BX-CALC-REF X(30) (used only by the deferred STORE).</summary>
    public string CalcRef { get; set; } = "";
    /// <summary>BX-PARENT-ITEM X(40).</summary>
    public string ParentItem { get; set; } = "";
    /// <summary>BX-PARENT-REV X(4): parent item revision used by bill selection.</summary>
    public string ParentRev { get; set; } = "";
    /// <summary>BX-PURPOSE X(10): blank -> STANDARD.</summary>
    public string Purpose { get; set; } = "";
    /// <summary>BX-JOB-REF X(20).</summary>
    public string JobRef { get; set; } = "";
    /// <summary>BX-VARIANT X(20).</summary>
    public string Variant { get; set; } = "";
    /// <summary>BX-REQ-QTY S9(15)V9(6): required good output of the parent.</summary>
    public decimal ReqQty { get; set; }
    /// <summary>BX-EFF-DATE 9(8): 0 -> CA-BUSINESS-DATE.</summary>
    public int EffDate { get; set; }
    /// <summary>BX-MAX-DEPTH S9(4): outside 1..25 -> 25.</summary>
    public int MaxDepth { get; set; }
    /// <summary>BX-CONSOLIDATE X: Y requests approved run consolidation (the root bill can also request it).</summary>
    public string Consolidate { get; set; } = "";
    /// <summary>BX-EXPAND-PHANTOM X: blank -> Y.</summary>
    public string ExpandPhantom { get; set; } = "";
    /// <summary>BX-INCLUDE-NOTES X: notes are recorded only when Y.</summary>
    public string IncludeNotes { get; set; } = "";
    /// <summary>BX-INCLUDE-BYPROD X: by-products are recorded only when Y.</summary>
    public string IncludeByprod { get; set; } = "";
    /// <summary>BX-COST-SET X(20) (carried, not read by BOMEXPL).</summary>
    public string CostSet { get; set; } = "";
    /// <summary>BX-CURRENCY X(3) (carried, not read by BOMEXPL).</summary>
    public string Currency { get; set; } = "";
    /// <summary>BX-VIEW X(8) for GETLINE: LEAF, INTERMED, LEVEL1; any other value = every occurrence.</summary>
    public string View { get; set; } = "";
    /// <summary>BX-SPLIT-CNT S9(4): explicit run splits of the root (used when &gt; 1).</summary>
    public int SplitCount { get; set; }
    /// <summary>BX-SPLIT-QTY(1..10) S9(15)V9(6).</summary>
    public decimal[] SplitQty { get; } = new decimal[10];
    /// <summary>BX-OPT-CNT S9(4): option choices (used when 1..10).</summary>
    public int OptionCount { get; set; }
    /// <summary>BX-OPTIONS(1..10).</summary>
    public BomOptionChoice[] Options { get; } = Enumerable.Range(0, 10).Select(_ => new BomOptionChoice("", "")).ToArray();
    /// <summary>BX-STOCK-SOURCE X(8): blank -> LIVE (net mode supply source).</summary>
    public string StockSource { get; set; } = "";
    /// <summary>BX-USE-INBOUND X: Y lets net mode take in-transit stock after free stock.</summary>
    public string UseInbound { get; set; } = "";

    // ---- summary (outputs) -------------------------------------------------------------------------------------------
    /// <summary>BX-BOM-ID S9(9) COMP-5: root bill selected.</summary>
    public int BomId { get; set; }
    /// <summary>BX-BOM-REF X(30).</summary>
    public string BomRef { get; set; } = "";
    /// <summary>BX-BOM-REV X(4).</summary>
    public string BomRev { get; set; } = "";
    /// <summary>BX-NOMINAL-BASIS S9(13)V9(6).</summary>
    public decimal NominalBasis { get; set; }
    /// <summary>BX-YIELD S9(5)V9(6).</summary>
    public decimal Yield { get; set; }
    /// <summary>BX-QUANTUM S9(13)V9(6).</summary>
    public decimal Quantum { get; set; }
    /// <summary>BX-BATCH-SIZE S9(13)V9(6).</summary>
    public decimal BatchSize { get; set; }
    /// <summary>BX-GROSS-STARTS S9(15)V9(6) of the root node.</summary>
    public decimal GrossStarts { get; set; }
    /// <summary>BX-RUN-COUNT S9(9) COMP-5 of the root node.</summary>
    public int RunCount { get; set; }
    /// <summary>BX-BATCH-COUNT S9(9) COMP-5 of the root node.</summary>
    public int BatchCount { get; set; }
    /// <summary>BX-EXCESS S9(15)V9(6) (not written by BOMEXPL).</summary>
    public decimal Excess { get; set; }
    /// <summary>BX-OCC-COUNT S9(9) COMP-5: occurrences in the retained result.</summary>
    public int OccCount { get; set; }
    /// <summary>BX-LEAF-COUNT S9(9) COMP-5: aggregated leaf lines.</summary>
    public int LeafCount { get; set; }
    /// <summary>BX-DEPTH-REACHED S9(4).</summary>
    public int DepthReached { get; set; }
    /// <summary>BX-SHORTAGE-CNT S9(4) (not written by BOMEXPL).</summary>
    public int ShortageCount { get; set; }
    /// <summary>BX-MAX-BUILDABLE S9(15)V9(6) (not written by BOMEXPL).</summary>
    public decimal MaxBuildable { get; set; }
    /// <summary>BX-MATERIAL-COST S9(13)V9(2) (not written by BOMEXPL).</summary>
    public decimal MaterialCost { get; set; }
    /// <summary>BX-CONVERSION-COST S9(13)V9(2) (not written by BOMEXPL).</summary>
    public decimal ConversionCost { get; set; }
    /// <summary>BX-RECOVERY-CREDIT S9(13)V9(2) (not written by BOMEXPL).</summary>
    public decimal RecoveryCredit { get; set; }
    /// <summary>BX-TOTAL-COST S9(13)V9(2) (not written by BOMEXPL).</summary>
    public decimal TotalCost { get; set; }
    /// <summary>BX-UNIT-COST S9(11)V9(6) (not written by BOMEXPL).</summary>
    public decimal UnitCost { get; set; }
    /// <summary>BX-UNIT-DENOM S9(15)V9(6) (not written by BOMEXPL).</summary>
    public decimal UnitDenom { get; set; }
    /// <summary>BX-COMPLETENESS X(12): COMPLETE, INCOMPLETE or ERROR.</summary>
    public string Completeness { get; set; } = "";
    /// <summary>BX-ERROR-PATH X(200).</summary>
    public string ErrorPath { get; set; } = "";
    /// <summary>BX-CYCLE-PATH X(200).</summary>
    public string CyclePath { get; set; } = "";
    /// <summary>BX-WARNING-CNT S9(4).</summary>
    public int WarningCount { get; set; }
    /// <summary>BX-EXPLOSION-ID S9(9) COMP-5 (STORE only).</summary>
    public int ExplosionId { get; set; }
    /// <summary>BX-KEY-ITEM X(40) for LEAFQTY.</summary>
    public string KeyItem { get; set; } = "";
    /// <summary>BX-KEY-TOTAL S9(15)V9(6) from LEAFQTY.</summary>
    public decimal KeyTotal { get; set; }

    // ---- paging window ----------------------------------------------------------------------------------------------
    /// <summary>BX-FROM-LINE S9(9) COMP-5: first occurrence to read (&lt; 1 -> 1); left on the next unread occurrence.</summary>
    public int FromLine { get; set; }
    /// <summary>BX-WIN-MAX S9(4): window size 1..25 (other -> 25).</summary>
    public int WinMax { get; set; }
    /// <summary>BX-WIN-CNT S9(4): lines returned.</summary>
    public int WinCnt { get; set; }
    /// <summary>BX-MORE-LINES X: Y when occurrences remain after the window.</summary>
    public string MoreLines { get; set; } = "";
    /// <summary>BX-LINES OCCURS 25 (entries 0..WinCnt-1 are valid).</summary>
    public BomExplosionWindowLine[] Lines { get; } = Enumerable.Range(0, 25).Select(_ => new BomExplosionWindowLine()).ToArray();

    /// <summary>The result retained by EXPLODE for GETLINE/LEAFQTY (the BOMWORK pool of this calculation).</summary>
    public BomCalculation? Calculation { get; set; }
}

/// <summary>One BX-LINES entry.</summary>
public sealed class BomExplosionWindowLine
{
    public int Seq { get; set; }
    public int Depth { get; set; }
    /// <summary>BX-L-PATH X(80).</summary>
    public string Path { get; set; } = "";
    public string Item { get; set; } = "";
    public string Rev { get; set; } = "";
    /// <summary>BX-L-DESC X(40).</summary>
    public string Desc { get; set; } = "";
    public string NodeType { get; set; } = "";
    public string Supply { get; set; } = "";
    public string Stop { get; set; } = "";
    public string Basis { get; set; } = "";
    /// <summary>BX-L-UOM: the stock unit of the item.</summary>
    public string Uom { get; set; } = "";
    public string BomRev { get; set; } = "";
    public decimal Usage { get; set; }
    public decimal Unrounded { get; set; }
    public decimal RoundInc { get; set; }
    public decimal ReqQty { get; set; }
    public decimal GoodQty { get; set; }
    public decimal Gross { get; set; }
    public int Runs { get; set; }
    public int Batches { get; set; }
    public decimal StockUsed { get; set; }
    public decimal Shortage { get; set; }
    public decimal PackQty { get; set; }
    public decimal Excess { get; set; }
    public decimal UnitCost { get; set; }
    public decimal ExtCost { get; set; }
    public decimal Branch { get; set; }
    public string Element { get; set; } = "";
    /// <summary>BX-L-WARNING X(60).</summary>
    public string Warning { get; set; } = "";
}

/// <summary>
/// The BOMCOSTCA COMMAREA (cobol/copy/BOMCOSTCA.cpy). Same conventions as <see cref="BomExplosionArea"/>.
/// </summary>
public sealed class BomCostArea
{
    // ---- STCAHDR ------------------------------------------------------------------------------------------------------
    /// <summary>CA-FUNCTION: COST, GETLINE, BRANCH.</summary>
    public string Function { get; set; } = "";
    public string Company { get; set; } = "";
    public string Site { get; set; } = "";
    public string User { get; set; } = "";
    /// <summary>CA-BUSINESS-DATE 9(8) yyyymmdd.</summary>
    public int BusinessDate { get; set; }
    public string CorrelationId { get; set; } = "";
    /// <summary>CA-PROGRAM (set to BOMCOST by the engine).</summary>
    public string Program { get; set; } = "";
    /// <summary>CA-RETURN-CODE, CA-REASON-CODE, CA-MESSAGE, CA-MSG-COUNT, CA-REQ-REF after the call.</summary>
    public LegacyResult Result { get; set; } = LegacyResult.Ok();

    // ---- request (BC-BODY) ---------------------------------------------------------------------------------------------
    public string ParentItem { get; set; } = "";
    public string ParentRev { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Variant { get; set; } = "";
    /// <summary>BC-REQ-QTY S9(15)V9(6).</summary>
    public decimal ReqQty { get; set; }
    /// <summary>BC-EFF-DATE 9(8).</summary>
    public int EffDate { get; set; }
    /// <summary>BC-MAX-DEPTH S9(4) (passed to BOMEXPL: outside 1..25 -> 25).</summary>
    public int MaxDepth { get; set; }
    public string CalcRef { get; set; } = "";
    /// <summary>BC-COST-SET X(20).</summary>
    public string CostSet { get; set; } = "";
    /// <summary>BC-CURRENCY X(3): blank -> GBP.</summary>
    public string Currency { get; set; } = "";
    /// <summary>BC-COST-DATE 9(8): accepted, never read (SME-1739).</summary>
    public int CostDate { get; set; }
    /// <summary>BC-NETTED X: Y requests a NET explosion (SME-1738).</summary>
    public string Netted { get; set; } = "";

    // ---- result -------------------------------------------------------------------------------------------------------
    public decimal GrossStarts { get; set; }
    public int RunCount { get; set; }
    public int BatchCount { get; set; }
    public int OccCount { get; set; }
    /// <summary>BC-MATERIAL-COST S9(13)V9(2).</summary>
    public decimal MaterialCost { get; set; }
    /// <summary>BC-CONVERSION-COST S9(13)V9(2) (already contains setup).</summary>
    public decimal ConversionCost { get; set; }
    /// <summary>BC-SETUP-COST S9(13)V9(2).</summary>
    public decimal SetupCost { get; set; }
    /// <summary>BC-SERVICE-COST S9(13)V9(2).</summary>
    public decimal ServiceCost { get; set; }
    /// <summary>BC-RECOVERY-CREDIT S9(13)V9(2).</summary>
    public decimal RecoveryCredit { get; set; }
    /// <summary>BC-TOTAL-COST S9(13)V9(2) = material + conversion + service - recovery.</summary>
    public decimal TotalCost { get; set; }
    /// <summary>BC-UNIT-COST S9(11)V9(6) = ROUNDED(total / quantity) when quantity &gt; 0.</summary>
    public decimal UnitCost { get; set; }
    public decimal UnitDenom { get; set; }
    public int MissingRates { get; set; }
    public string Completeness { get; set; } = "";
    public string ErrorPath { get; set; } = "";
    /// <summary>BC-BRANCH-SEQ S9(9) COMP-5 for BRANCH.</summary>
    public int BranchSeq { get; set; }
    public string BranchItem { get; set; } = "";
    public decimal BranchQty { get; set; }
    public decimal BranchCost { get; set; }
    public decimal BranchUnit { get; set; }

    // ---- paging window ----------------------------------------------------------------------------------------------
    public int FromLine { get; set; }
    public int WinMax { get; set; }
    public int WinCnt { get; set; }
    public string MoreLines { get; set; } = "";
    /// <summary>BC-LINES OCCURS 25 (entries 0..WinCnt-1 are valid).</summary>
    public BomCostWindowLine[] Lines { get; } = Enumerable.Range(0, 25).Select(_ => new BomCostWindowLine()).ToArray();

    /// <summary>The valued result retained by COST for GETLINE/BRANCH.</summary>
    public BomCalculation? Calculation { get; set; }
}

/// <summary>One BC-LINES entry.</summary>
public sealed class BomCostWindowLine
{
    public int Seq { get; set; }
    public int Depth { get; set; }
    /// <summary>BC-L-PATH X(80).</summary>
    public string Path { get; set; } = "";
    public string Item { get; set; } = "";
    public string Rev { get; set; } = "";
    /// <summary>BC-L-DESC X(40).</summary>
    public string Desc { get; set; } = "";
    public string Uom { get; set; } = "";
    public string NodeType { get; set; } = "";
    public string Element { get; set; } = "";
    public string Source { get; set; } = "";
    public decimal ReqQty { get; set; }
    public decimal UnitCost { get; set; }
    public decimal ExtCost { get; set; }
    /// <summary>BC-L-BRANCH: always 0 from GETLINE (BOMCOST.cob:369, SME-1743).</summary>
    public decimal Branch { get; set; }
    public string Fixed { get; set; } = "";
    /// <summary>BC-L-WARNING X(60).</summary>
    public string Warning { get; set; } = "";
}
