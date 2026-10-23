# STERP.Modules.Itm - Stock Control slice (STERP-ITM, wave W1)

Specification: Agent 06 `STERP-ITM.md` (kept with the evidence package, not in this repository); binding OpenAPI
`contracts/openapi/STERP-ITM.openapi.yaml`.

## Public in-process contracts (contracts section 3; STERP-ITM.md section 7.2)

Other slices (SO, PO, KIT, CST, XMD) call these services **inside their own unit of work**: resolve the interface from the
request/job DI scope (`ctx.Get<IStockMovementService>()` in a handler, `ctx.Get<...>()` in a `JobRunContext`). They run on
the scoped `IUnitOfWork`/`ISqlRunner` of that scope - the same engine connection and transaction as the caller, exactly as
the legacy static `CALL`s shared the one SQLIO connection. They never open a connection of their own and never commit a
caller's transaction (STKMOVE commits only the transaction it opened itself when the caller was in autocommit).

Every contract mirrors its legacy COMMAREA:

```csharp
Task<LegacyResult> ExecuteAsync(string function, XxxCommarea area, ItmCall call, CancellationToken ct = default);
```

- `function` = `CA-FUNCTION` (constants in `StkmoveFunctions`, `StkresvFunctions`, `AvailqFunctions`, `UomconvFunctions`,
  `LotserFunctions`, `SubstchkFunctions`). STKMOVE upper-cases it; STKRESV, UOMCONV, SUBSTCHK compare it exactly as received.
- `area` = the COMMAREA body (`SM-`, `SR-`, `AV-`, `UC-`, `LS-`, `SB-` fields; OpenAPI components `Itm*Commarea`). Inputs are
  read from it and outputs written back into it in place, as the COBOL program did with the LINKAGE area (including fields
  the legacy program did not reset, e.g. `SR-QTY-CONSUMED`). Text fields have PIC X(n) semantics (Windows-1252, right
  truncation; outputs right-trimmed); dates are `DateOnly?` (`null` = `00000000`); OCCURS tables are lists.
- `call` = the STCAHDR inputs (`ItmCall`: company, site, user, business date, correlation id). Build it from your own
  context exactly as your COBOL program filled the callee's header (`ItmCall.From(session)` = the CobolGateway values).
- result = the STCAHDR outcome (`LegacyResult`: return code, reason, message, message count, `DocumentRef` = `CA-REQ-REF`).
  Test the return code exactly as your COBOL program did (for example OPENSTK/CONVLOAD treat RC < 08 as posted, SME-037).

| Contract | Interface | Functions | Legacy program / copybook | Unit of work |
|---|---|---|---|---|
| IC-ITM-STKMOVE | `IStockMovementService` | RECEIPT, ISSUE, RETURN, ADJUST, COMPLETE, OPENING, SCRAP, TRANSFER, CONFIRM, HOLD, RELEASE | `STKMOVE` / `STKMOVECA` | autocommit caller: STKMOVE owns one transaction (BEGN..COMT/ROLB); caller in a transaction: `SAVE TRANSACTION STKMOVE`, rollback to it on error when `XACT_STATE() = 1`, never commits. `inv.usp_PostStockMovement` uses `XACT_ABORT ON`: a procedure refusal can doom the caller's transaction (reported as RC 12 `SQL0001` with the driver text, SME-011) |
| IC-ITM-STKRESV | `IStockReservationService` | RESERVE, RELEASE, CONSUME, REVALID, ENQUIRE | `STKRESV` / `STKRESVCA` | caller's; `inv.usp_ReserveStock` opens its own (nested) transaction and rolls back the whole open transaction on error. A procedure refusal reaches STKRESV with the driver prefix, so it is RC 12 `SQL0001` with the driver text, not RC 08 `RSV0003` (SME-011, D-ITM-10) |
| IC-ITM-AVAILQ | `IAvailabilityService` | AVAIL, ENQUIRE, BYLOC, LOTS | `AVAILQ` / `AVAILQCA` | caller's (reads only) |
| IC-ITM-UOMCONV | `IUnitConversionService` | TOSTOCK, FACTOR | `UOMCONV` / `UOMCONVCA` | caller's (reads only) |
| IC-ITM-LOTSER | `ILotSerialService` | EXPCHK, TRACE (public); CHKMOVE, NEWLOT, NEWSER, SETSTAT (STKMOVE) | `LOTSER` / `LOTSERCA` | caller's (writes join it) |
| IC-ITM-SUBSTCHK | `ISubstitutionCheckService` | CHECK | `SUBSTCHK` / `SUBSTCHKCA` | caller's (reads only) |

Deferred test-harness-only function codes (D-ITM-04: UOMCONV FROMSTK/CONVERT/CHECKQTY/UNITCOST/ITEMUOM, SUBSTCHK
LIST/REVCHECK/APPLY) throw `NotSupportedException`; unknown codes return the legacy "unsupported function" RC 08.

Example (a caller in its own transaction, as SO `DESPATCH` issuing stock):

```csharp
var stk = ctx.Get<IStockMovementService>();
var area = new StkmoveCommarea
{
    ItemCode = item, Site = site, Warehouse = whs, Location = loc, LotCode = lot,
    Quantity = qty, UomCode = uom, SourceModule = "SO", SourceDocType = "DESPATCH", SourceDocRef = despatchRef,
};
var r = await stk.ExecuteAsync(StkmoveFunctions.Issue, area, ItmCall.From(ctx.Session, correlationId), ct);
if (r.ReturnCode > 4) { /* the legacy caller's error path */ }
```

## Online operations and jobs

The operation/job map (operationId -> FR, legacy chain, unit of work) is in `contracts/openapi/STERP-ITM.openapi.yaml`
(`x-sterp-requirements`, `x-sterp-legacy`, `x-sterp-uow`).
