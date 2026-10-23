# STERP.Modules.Ar - Debtors (STERP-AR, wave W2)

Binding specification: Agent 06 `STERP-AR.md` (kept with the evidence package, not in this repository); binding contract
`contracts/openapi/STERP-AR.openapi.yaml`.

## Public in-process contracts (consumed by STERP-SO)

Each contract mirrors a legacy `CALL "<program>" USING <COMMAREA>` (STERP-AR.md section 7.2). The caller:

1. creates a new area (`new CredchkArea()` = COBOL `INITIALIZE`: text empty, numbers zero, tables cleared);
2. fills the header context exactly as its COBOL did - the kernel `CommareaHeader`: `area.Header.Prepare(new
   KernelCaller(company, site, user, businessDate, correlationId), function)`, or `area.Header.PrepareLink(session, program,
   function)` from a request `SterpContext` (`BusinessDate` is `yyyymmdd` as an `int`) - and the body inputs;
3. calls `CallAsync(area)` with `area.Header.Function` set, or the per-function helper (which sets the function);
4. reads the outputs from the area and the result from `area.Header` (`ReturnCode`, `ReasonCode`, `Message`,
   `MessageCount`, `RequestRef`) - the returned `LegacyResult` is the same header result (`area.Header.Outcome()`).

The call runs on the caller's unit of work: the engines use the scoped `ISqlRunner`/`IUnitOfWork` of the same DI scope
(request or job run), as the legacy static CALL shared the one engine connection. `BEGN`/`COMT`/`ROLB` positions are
part of the contract: a callee `COMT` commits the caller's open work too (SME-013).

| Interface (DI, scoped) | Legacy | Area (copybook) | Functions | Unit of work |
|---|---|---|---|---|
| `ICreditCheckService` | `CREDCHK` | `CredchkArea` (`CREDCHKCA`) | `EXPOSURE`, `CHECK`, `RELEASE`, `OVERRIDE`, `APPROVE` (`CreditCheckFunctions`) | no BEGN for EXPOSURE/CHECK/RELEASE (exposure UPDATE and EXCPRAIS hold in the caller's unit or autocommit); OVERRIDE/APPROVE own a unit when the caller is in autocommit, savepoint `CREDCHK` otherwise |
| `IArDepositService` | `ARDEPOS` | `ArdeposArea` (`ARDEPOSCA`) | `DEPOSIT`, `ENQUIRE`, `APPLY`, `REFUND` (`DepositFunctions`) | DEPOSIT: PERMCHK/PERIODCK/NUMGEN/ARVATWRT LOOKUP, then BEGN..COMT/ROLB; others none |
| `IArInvoicePostingService` | `ARINVPST` | `ArinvpstArea` (`ARINVPSTCA`) | `VALIDATE`, `PROFORMA`, `POST` (`InvoicePostingFunctions`) | VALIDATE reads; PROFORMA own BEGN/COMT; POST: PERMCHK/PERIODCK/IDEMPOT/NUMGEN, then BEGN..COMT, IDEMPOT end after |
| `IArCreditNoteService` | `ARCRNOTE` | `ArcrnoteArea` (`ARCRNOTECA`) | `CHECK`, `POST` (`CreditNoteFunctions`) | CHECK reads; POST: PERMCHK/PERIODCK/IDEMPOT/NUMGEN `SCRN`, then BEGN..COMT |

Field names are the COBOL names without the program prefix in PascalCase (`CK-ORDER-VALUE` -> `OrderValue`,
`AI-L-NET` -> `Lines[i].LNet`); every field carries its `[Pic]` so `CommareaCodec.Normalize(area)` applies the legacy
marshalling (contracts 4.4) and `area.ToJson()` gives the OpenAPI `Ar*Commarea` form. Tables are fixed arrays of the
OCCURS size (`Lines`, `Vats`, `Missing`) with their count field (`LineCnt`, `VatCnt`, `MissingCnt`); index 0 is COBOL
occurrence 1.

Engines (all delivered, registered scoped by `ArModule.ConfigureServices`): `CreditCheckEngine` (CREDCHK),
`DepositEngine` (ARDEPOS), `InvoicePostingEngine` (ARINVPST), `CreditNoteEngine` (ARCRNOTE); they share
`VatWriterEngine` (ARVATWRT) and `ArGlPostingEngine` (ARPOSTGL). The `Pending*` classes in
`Application/PendingPrograms.cs` are only used by a build without the kernel (`STERP_KERNEL` undefined): they answer
RC 16 `PGM0001` ("Program X is not in the engine program directory.", the legacy gateway outcome of a missing program).

Legacy behaviour the callers inherit (HD-11): ARCRNOTE and ARDEPOS continue after an RC 06 from the permission check
(`ARC0040`/`ARC0041`, `DEP0005`) and post, returning RC 06; ARINVPST stops on it (`ARI0050`). ARCRNOTE stops after the
idempotency claim on any RC 04 (a replay `ARC0043`, or the validation warning `ARC0004` of a goodwill credit naming an
invoice - the claim then stays open). ARDEPOS has no idempotency claim: a repeated DEPOSIT with the same correlation id
draws the same number from NUMGEN and fails on `UQ_ArOpenItem` (RC 12 `SQL0001`).
