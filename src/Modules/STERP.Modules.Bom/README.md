# STERP.Modules.Bom - STERP-BOM slice (wave W8)

Bills of Material: explosion, costing, netting, where-used, revision comparison, structure reads, validation check,
release and bulk engineering change preview, plus the batch listings `BOMRP010`/`BOMRP030`. Binding design: Agent 06
`STERP-BOM.md` (kept with the evidence package, not in this repository); binding contract: `contracts/openapi/STERP-BOM.openapi.yaml`.

## Public in-process contracts (contracts section 3)

Other slices call the BOM engines in process, on **their own unit of work** (the scoped `ISqlRunner`/`IUnitOfWork` of the
current request or job step - inject the contract into a service of the same DI scope). Both contracts are registered by
`BomModule.ConfigureServices` (scoped) and live in `Sterp.Modules.Bom.Contracts` (`Contracts/BomContracts.cs`).

They mirror the legacy `CALL ... USING <COMMAREA>`: a mutable *area* object carries the STCAHDR header, the request fields,
the outputs and the 25-line paging window exactly as the copybook does. Fill it, set `Function`, call `LinkAsync`, then
read `area.Result` (a `LegacyResult`: `ReturnCode` 0/4/8/12/16, `ReasonCode`, `Message`, `MessageCount`, `DocumentRef` =
CA-REQ-REF) and the output fields. Field names are the copybook names without prefix (`BX-REQ-QTY` -> `ReqQty`,
`BX-L-REQ-QTY(i)` -> `Lines[i-1].ReqQty`). Conventions: text right-trimmed (cut to the PIC X(n) length by the engine),
dates PIC 9(8) as `int` yyyymmdd (0 = not given), quantities `decimal` with the legacy scale, flags `"Y"`/`"N"`.

| Contract | Legacy | Functions | Callers |
|---|---|---|---|
| `IBomExplosionEngine` (IC-BOM-BOMEXPL, FR-STERP-BOM-040) | `BOMEXPL` / `BOMEXPLCA.cpy` | `EXPLODE`, `GETLINE`, `LEAFQTY`; other -> RC 8 `BOM0501` (`NODE` internal; `STORE` deferred, SME-015) | KIT `KITCREAT`, `KITTRIAL`, `MULTITRL`; this slice |
| `IBomCostEngine` (IC-BOM-BOMCOST, FR-STERP-BOM-041) | `BOMCOST` / `BOMCOSTCA.cpy` | `COST`, `GETLINE`, `BRANCH`; other -> RC 8 `BOM0801` | CST `COSTROLL`; this slice |

### Retained result (paging)

`EXPLODE`/`COST` store the calculation in `area.Calculation` (the legacy BOMWORK pool); `GETLINE` pages it from
`FromLine` (< 1 = 1) in windows of `WinMax` (1..25, else 25), filtered by `View` for BOMEXPL (`LEAF`, `INTERMED`,
`LEVEL1`, anything else = every occurrence), and leaves `FromLine` on the next unread occurrence with `MoreLines` = `"Y"`
when more remain. Reuse the **same area** for the `GETLINE` calls (as the COBOL caller reused its WORKING-STORAGE area).
The result belongs to that area - there is no process-wide pool (BC-BOM-01, approved). `GETLINE` sets RC 0 and a blank
reason but, like the COBOL program, leaves `Message`/`MessageCount` as they were.

### Behaviour (binding, spec FR-STERP-BOM-040/041, sections 4.3-4.6)

- `EXPLODE` writes its defaults back: `ParentItem` upper-cased, `EffDate` 0 -> `BusinessDate`, `Purpose` blank ->
  `STANDARD`, `CalcType` blank -> `GROSS` (`NET` = net mode, any other value such as KIT's `EXECUTION` = gross),
  `MaxDepth` outside 1..25 -> 25, `ExpandPhantom` blank -> `Y`, `StockSource` blank -> `LIVE`. Notes and by-products are
  recorded only when `IncludeNotes`/`IncludeByprod` = `Y`.
- Outcome: `COMPLETE` RC 0 (RC 4 with warnings) "Explosion complete - n occurrence(s)"; `INCOMPLETE` RC 4 with the last
  reason (`BOM0520` depth limit, `BOM0512` no child bill, `BOM0524` capacity, ...); `ERROR` RC 8 (`BOM0504` unknown item,
  `BOM0503` negative quantity, `BOM0513` cycle with `CyclePath`, BOMSELCT reasons); `SQL0001` RC 12.
- Summary outputs: `BomId`/`BomRef`/`BomRev`, basis, yield, quantum, batch size, root `GrossStarts`/`RunCount`/
  `BatchCount`, `OccCount`, `LeafCount`, `DepthReached`, `Completeness`, `ErrorPath`, `CyclePath`, `WarningCount`.
- Every requirement is in the component's stock unit (line usage x conversion factor, material occurrences rounded up
  once to the item stock quantum). Capacities: 2,000 occurrences, 500 aggregated leaves, 500 lines per bill, depth 25.
- `COST`: currency blank -> `GBP`; `Netted` = `Y` requests a NET explosion; `CostDate` is accepted and never read
  (SME-1739). Totals: `TotalCost` = material + conversion + service - recovery; `UnitCost` = ROUNDED(total / quantity);
  any missing rate -> `INCOMPLETE`, RC 4 `BOM0810`; an `ERROR` explosion (RC 8) is still valued and published (SME-1740);
  RC 12 returns before valuation. `ConversionCost` already contains setup (`SetupCost` is reported separately).
  `Lines[i].Branch` is always 0 (SME-1743).

### Example (KIT `KITTRIAL` shape)

```csharp
var area = new BomExplosionArea
{
    Function = "EXPLODE", Company = co, Site = site, User = user, BusinessDate = bizDate,
    CalcType = "EXECUTION", ParentItem = item, Purpose = "STANDARD", ReqQty = qty, EffDate = eff,
    MaxDepth = 25, ExpandPhantom = "Y", IncludeNotes = "N", IncludeByprod = "N",
};
var r = await bomExplosion.LinkAsync(area, ct);          // IBomExplosionEngine from DI (same scope = same unit of work)
if (r.ReturnCode > 4) { /* refuse with r.ReasonCode / r.Message */ }
area.Function = "GETLINE"; area.View = "LEAF"; area.FromLine = 1; area.WinMax = 25;
do
{
    await bomExplosion.LinkAsync(area, ct);
    for (var i = 0; i < area.WinCnt; i++) { var line = area.Lines[i]; /* line.Item, line.ReqQty, line.Uom ... */ }
} while (area.MoreLines == "Y");
```

## Operations (STERP.Api) and jobs (STERP.Jobs)

The operation -> handler table, legacy chains and units of work are in `contracts/openapi/STERP-BOM.openapi.yaml`
(`x-sterp-legacy`, `x-sterp-uow`) and the handlers under `Endpoints/` and `Jobs/`.
