# Kernel services - `Sterp.Platform.Kernel.Services`

One service per legacy common COBOL program (`cobol/online/common/*.cob`), reproducing FR-STERP-PLATFORM-010..030
(STERP-PLATFORM.md section 4.14 and 7.4, contracts 01 sections 6.4-6.9 and 7). Each service mirrors its COMMAREA
(`<PROG>CA.cpy`): the same function codes, inputs and outputs, the STCAHDR result, the same validations in the same
order, the same reason codes and message texts (character for character) and the same SQL on the same session family.
Slices call these services wherever their legacy program issued `CALL "<PROG>"`.

| Legacy program | C# type | FR | Lifetime |
|---|---|---|---|
| PERMCHK | `IPermissionService` | 012 | scoped |
| PERIODCK | `IPeriodService` | 013 | scoped |
| NUMGEN | `IDocumentNumberService` | 014 | scoped |
| IDEMPOT | `IIdempotencyService` | 015 | scoped |
| AUDITLOG | `IAuditWriter` | 016 | scoped |
| EXCPRAIS | `IExceptionRaiser` | 017 | scoped |
| PARMGET | `IParameterService` | 018 | scoped |
| DATECALC | `IBusinessCalendar` (+ `ICalendarCache`, singleton) | 019 | scoped |
| TERMCALC | `IPaymentTermsCalculator` | 020 | scoped |
| FXCONV | `IFxConverter` | 021 | scoped |
| RNDALLOC | `IRoundingAllocator` | 022 | singleton |
| STRUTIL | `CobolString` (static) | 023 | - |
| STFUNCS | `CobolNumeric` / `CobolDate` (static, `Sterp.Platform.Kernel.Cobol`) | 024 | - |
| JOBLOG | `IJobRunRegistry` | 027 | scoped |
| FINBATPR / XMDBATPR / STRPTHDR | `BatchSuite`, `Report132`, `Report132Writer` (static / plain classes) | 028 | - |
| `fin.JournalStage` + `fin.usp_PostJournal` | `IGlPostingGateway` | 029 | scoped |
| PING | `IDatabaseProbe` | 030 | singleton |
| SQLIO statement semantics | `Sqlio` extensions on `ISqlRunner`, `SqlioParm`, `SqlioRow`, `OdbcText` (`Sterp.Platform.Kernel.Data`) | 010/011 | - |

CMDARGS (`CommandLineArgs`, FR-025) and RUNPATH (`OutputPaths` in `STERP.Platform.Hosting/Jobs/JobModel.cs`, FR-026) are
not in this folder.

## 1. Registration and configuration

`SterpHostBuilder` calls `KernelServices.AddSterpKernel(services)`; every host gets the registrations above. Scoped
services take the request's (or job run's) `ISqlRunner`, i.e. they run on the caller's engine connection and unit of
work. Configuration (`Sterp:Kernel`, `SterpKernelOptions`):

| Key | Default | Meaning |
|---|---|---|
| `Sterp:Kernel:CalendarCacheTtl` | `00:05:00` | lifetime of a cached DATECALC calendar year (section 3.8) |
| `Sterp:Kernel:LegacyDriverMessagePrefix` | not set (the database option applies: `[Microsoft][ODBC Driver 18 for SQL Server][SQL Server]`) | overrides `Sterp:Database:LegacyDriverMessagePrefix`. With the prefix present every SQL failure text starts with `[Microsoft]`, so the "STERP-E-/STERP-W-" business branch of the services is never taken (RC 12 SQL0001, as in the legacy deployment); set it to `""` to reproduce FR-010 AC4/AC5 (business reason codes) |

## 2. Calling convention

```csharp
using Sterp.Platform.Kernel;            // LegacyResult, LegacyOutcomeException, SterpContext
using Sterp.Platform.Kernel.Services;

var caller = KernelCaller.From(ctx);    // company, site, effective user, business date, correlation id (STCAHDR inputs)
var perm = await permissions.CheckAsync(caller, "AR.RECEIPT.POST", ct: ct);
if (perm.Header.ReturnCode != 0)
    throw new LegacyOutcomeException(perm.Result);     // only if your legacy program passed PERMCHK's result through
```

- `KernelCaller` is what the legacy caller moved into the callee's STCAHDR before the CALL. Most programs moved their own
  header (`KernelCaller.From(ctx)`); some INITIALIZEd the area and set selected fields (`KernelCaller.Empty with
  { Company = co, User = user }`). Pass exactly what your program passed (blank site, business date 0, no correlation id
  are all observable differences).
- Every service offers typed convenience methods (`CheckAsync`, `NextAsync`, `CallAsync(caller, "FUNCTION", fill)`) that
  return the filled area, and `ExecuteAsync(area)` - the literal `CALL "<PROG>" USING area` - for callers that build the
  area themselves (unsupported function codes, odd inputs, re-used areas).
- Outputs are on the area (body fields plus `area.Header`): `ReturnCode` (00 OK, 04 WARNING, 06 HOLD, 08 ERROR,
  12 SEVERE, 16 NOT FOUND), `ReasonCode`, `Message` (CA-MESSAGE X(200)), `MessageCount`, `RequestRef`;
  `area.Result` is the `LegacyResult`. `Header.Failed` is CA-FAILED (08..16) - **06 HOLD is not failed** (SME-038).
  Text outputs are right-trimmed; numeric outputs carry the PIC precision of the COMMAREA field.
- The services do not throw for legacy outcomes. A SQL failure (`SqlioException`, including a failed connection open)
  becomes the program's 9300-SQL-FAILED result: CA-MESSAGE = first 200 bytes of the driver text and RC 12 SQL0001, or the
  program's business reason RC 08 when the text starts `STERP-E-`/`STERP-W-` (see section 1). Only cancellation and
  programming errors (for example a non-whitelisted GL stage column) escape as exceptions.
- Map the callee's result exactly as your legacy program did: some programs pass the callee's reason and message through,
  many substitute their own reason codes or texts, some ignore the return code entirely (for example JOBLOG RCs in the
  batch suites). Code is authoritative over comments.

### Unit of work

The scoped services execute on `ISqlRunner`, which is the request's `IUnitOfWork` (SQLIO semantics, DEVELOPER-GUIDE
section 4). No kernel service issues BEGN/COMT/ROLB itself:

- caller has **not** begun a unit (autocommit): every statement commits on its own - a NUMGEN draw, an IDEMPOT claim,
  an AUDITLOG row or an EXCPRAIS item survives a later failure of the caller;
- caller **has** begun a unit (`uow.BeginAsync()`): the service's statements join it and commit or roll back with the
  caller's `CommitAsync()` / `RollbackAsync()`. Procedures that own a transaction (`core.usp_NextDocumentNumber`,
  `core.usp_IdempotentBegin/End`, `fin.usp_PostJournal`, ...) only own one when none is open;
- a procedure that THROWs under `XACT_ABORT` inside the caller's unit rolls the **whole** unit back server-side (as the
  legacy ODBC session did); `EngineUnitOfWork` then transparently starts a new transaction for the following statements
  (ODBC manual-commit semantics), so the caller's later COMT commits only what ran after the failure - exactly the legacy
  behaviour. Reproduce your program's BEGN/COMT order, not a "cleaner" one: where the legacy program drew a number or
  claimed an instruction before its BEGN, do the same.

`IDatabaseProbe` is the exception: it uses its own short connection (it must not disturb a request's unit) and does not
retry. The engine connection itself is opened with the legacy login retry (sterpsql.c ensure_conn: up to four attempts,
30 s login timeout, 2,000 ms x attempt between attempts, no retry when the login is refused - SQLSTATE 28000).

## 3. Services

### 3.1 PERMCHK -> `IPermissionService` (FR-012)

```csharp
Task<PermchkArea> CheckAsync(KernelCaller caller, string permission, string? checkUser = null, string? checkSite = null, CancellationToken ct = default);
```
Function `CHECK`. Check user defaults to CA-USER, site to CA-SITE (blank site = NULL = all sites); permission upper-cased.
Refusals RC 08: PRM0001 no user, PRM0002 no permission, PRM0003 no company, PRM0009 other function.
`EXEC core.usp_CheckPermission user, permission, company, site`. Allowed: RC 00 `Allowed = "Y"`, message
`<user> holds <permission> in <company>`. Not allowed: RC 06 PRM0006 (a hold) with the procedure's reason text (or
`<user> does not hold <permission>`). No row RC 12 PRM0005; SQL failure RC 12 SQL0001 (business text: RC 06 PRM0007).
Read-only.

### 3.2 PERIODCK -> `IPeriodService` (FR-013)

```csharp
Task<PeriodckArea> CheckAsync(KernelCaller caller, int postingDate, int documentDate = 0, int taxPoint = 0, string? action = null, string function = "CHECK", CancellationToken ct = default);
```
Functions `CHECK` and `DATES` (identical). Posting date 0 takes CA-BUSINESS-DATE; action defaults to POST (upper-cased).
Validation order: PER0001 company, PER0002 posting date, PER0003 document date, PER0004 tax point, then PER0009 function.
One inline `fin.Period` read with `core.fn_HasPermission(user,'FIN.PERIOD.SOFTPOST',...)` (not `fin.usp_ValidatePeriod`).
OPEN allowed RC 00; SOFT allowed with that authority (`... (SOFT) - posted as a close adjustment`), else RC 06 PER0006;
HARD RC 08 PER0003 and FUTURE RC 08 PER0004 (the legacy re-uses the date-validation codes), no period RC 08 PER0005,
unknown status RC 08 PER0007. SQL failure RC 12 SQL0001; `STERP-E-` text gives RC 08 PER0006 (status CLOSED) or, when the
text contains "soft closed", RC 06 PER0007 (status SOFT). Outputs `FiscalYear`, `PeriodNo`, `PeriodKey` (yyyypp),
`PeriodStatus`, `Allowed`, `OverridePermission` (`FIN.PERIOD.SOFTPOST`), `ReasonText`. Read-only.

### 3.3 NUMGEN -> `IDocumentNumberService` (FR-014)

```csharp
Task<NumgenArea> NextAsync(KernelCaller caller, string seriesCode, CancellationToken ct = default);
Task<NumgenArea> VoidAsync(KernelCaller caller, string seriesCode, string documentNumber, string reason, CancellationToken ct = default);
```
NEXT with a correlation id first replays the lowest-sequence number already issued for (company, series, correlation)
with its stored status - RC 04 NUM0007, nothing drawn; otherwise `core.usp_NextDocumentNumber` draws (`DocumentNumber`,
`Sequence`, `Status = ISSUED`). Replay and draw are separate statements without a shared lock (SME-060, reproduced).
VOID runs `core.usp_VoidDocumentNumber` (reason required NUM0004). Unit of work: see section 2 - drawn outside the
caller's unit exactly when the caller had not begun one (SME-434/SME-1037).

```csharp
var n = await numbers.NextAsync(KernelCaller.From(ctx), "SDES", ct);
if (n.Header.Failed) return n.Result;          // NUM0001/2/8/9, SQL0001
var despatchNo = n.DocumentNumber;             // replay (RC 04 NUM0007) returns the earlier number
```

### 3.4 IDEMPOT -> `IIdempotencyService` (FR-015)

```csharp
Task<IdempotArea> BeginAsync(KernelCaller caller, string operation, CancellationToken ct = default);
Task<IdempotArea> EndAsync(KernelCaller caller, string status, string? resultRef = null, string? resultMessage = null, CancellationToken ct = default);
```
The correlation id (the request's Idempotency-Key) is the key. BEGIN: STARTED RC 00 `Instruction claimed - carry on
with <operation>`; COMPLETED RC 04 IDM0006 with the original `ResultRef`; INPROGRESS RC 06 IDM0007. The FAILED (IDM0008)
and unknown-outcome (IDM0009) branches stay unreachable (BRF-IDEMPOT-01). END: status COMPLETED or FAILED (IDM0003),
returns `Status`, `ResultRef`, `StartedAt`, `EndedAt`. Under autocommit the claim commits at once - an abandoned claim
keeps answering INPROGRESS (SME-061).

### 3.5 AUDITLOG -> `IAuditWriter` (FR-016)

```csharp
Task<AuditlogArea> WriteAsync(KernelCaller caller, AuditlogArea body, CancellationToken ct = default);
```
Function `WRITE`. Fill `EntityType`, `EntityKey`, `Action`, `FieldName`, `OldValue`, `NewValue`, `Reason`,
`AuthorityRef`, `Source` (blank -> COBOL), `Sensitive` (Y/N). Refusals: AUD0001 no user, AUD0002 entity type/key
spaces (LOW-VALUES pass, BRF-AUDITLOG-02), AUD0003 no action, AUD0009 function. `core.usp_WriteAudit`; returns `AuditId`,
message `Audited <action> of <type> <key>`.

### 3.6 EXCPRAIS -> `IExceptionRaiser` (FR-017)

```csharp
Task<ExcpraisArea> RaiseAsync(KernelCaller caller, ExcpraisArea body, CancellationToken ct = default);
```
Function `RAISE`. Fill `Module`, `Severity` (HOLD/WARNING/ERROR), `Category`, `MessageText`, `DocumentType`,
`DocumentRef`, `OwnerUser` and/or `OwnerRole`, `Priority` (outside 1..9 -> 5), `DueAt` (yyyymmdd or 0), `AmountGbp`,
`Quantity`. `core.usp_RaiseException` keeps one open item per company/module/category/document (update instead of
insert). After success the RC mirrors the severity: **HOLD 06, WARNING 04, ERROR 00** (SME-059) with
`<SEVERITY> raised on worklist: <text>`; outputs `ExceptionId`, `Status`, `RaisedAt`.

### 3.7 PARMGET -> `IParameterService` (FR-018)

```csharp
Task<ParmgetArea> GetAsync(KernelCaller caller, string paramCode, int asOf = 0, string? defaultText = null, bool numeric = false, CancellationToken ct = default);
```
`GET` / `GETNUM` (`numeric: true`). As-of 0 takes CA-BUSINESS-DATE, then today (`TimeProvider`). PARMGET's own SELECT
on `core.Parameter` (company row wins over `*`, latest EffectiveFrom on or before the date; not `core.usp_GetParameter`).
RC 00 found, RC 04 PRM0004 default used (`defaultText` given = PM-USE-DEFAULT "Y"), RC 16 PRM0003 not held, GETNUM
non-numeric RC 08. Outputs `ValueText`, `ValueNum`, `IsNumeric`, `Found`, `DataType`, `Unit`, `EffectiveFrom`, ...

### 3.8 DATECALC -> `IBusinessCalendar` (FR-019)

```csharp
Task<DatecalcArea> AddDaysAsync(int date, int days);          Task<DatecalcArea> AddMonthsAsync(int date, int months);
Task<DatecalcArea> EndOfMonthAsync(int date, int months = 0); Task<DatecalcArea> DayOfWeekAsync(int date);
Task<DatecalcArea> DaysBetweenAsync(int date1, int date2);    Task<DatecalcArea> FormatAsync(int date, string format);
Task<DatecalcArea> ParseAsync(string text, string? format = null);
Task<DatecalcArea> IsBusinessDayAsync(int date, string? calendar = null);
Task<DatecalcArea> AddBusinessDaysAsync(int date, int days, string? calendar = null);
Task<DatecalcArea> AdjustAsync(int date, string? direction = null, string? calendar = null);
Task<DatecalcArea> CallAsync(string function, Action<DatecalcArea> fill);   // VALIDATE, CACHECLR, any function
```
All 12 functions with DAT0001..DAT0010. Results: `ResultDate`, `ResultNum`, `TextOut`, `DayName`, `MonthName`,
`IsBusiness`, `IsHoliday`, `IsWeekend`, `Adjusted`, `HolidayName`, `CalSource` (SQL / BUILTIN). Quirks kept: ADDBUS
refuses beyond 3650 steps even when the count would complete, ADDBUS 0 on a non-business day continues (DAT0006 message
overwritten), PARSE of 6 digits is DDMMYY with pivot 50 (RC 04 DAT0007), calendar codes match the built-in table on
their first three characters, BACS loads the ENG holidays, an unknown calendar is weekends-only.

Calendar source and cache (contracts 6.9): the calendar year is read from `core.CalendarDay` (or `core.Holiday` when
only that table exists) and falls back to the built-in table (DATECALC's 78 entries for 2026-2028) when the read fails
or returns no rows. Loaded
years are kept in the singleton `ICalendarCache` keyed by **database identity** (`BusinessCalendar.DatabaseIdentityStatement`:
`@@SERVERNAME`, `DB_NAME()`, `sys.databases.create_date` and `sys.database_recovery_status.recovery_fork_guid`) plus
calendar and year, with a TTL (`Sterp:Kernel:CalendarCacheTtl`, default 5 minutes). `create_date` survives
`RESTORE ... WITH REPLACE` (verified on LocalDB); the recovery fork changes on every RESTORE WITH RECOVERY, so a restored
database (the parity harness restores before every scenario) never answers from the previous copy's calendar. When the
login cannot see `sys.database_recovery_status` (no VIEW ANY DATABASE, not the owner) the shared cache is not used and
each request loads its own calendar-years. Holiday maintenance is visible within the TTL; target maintenance of
`core.Calendar` / `core.CalendarDay` / `core.Holiday` must call `ICalendarCache.Clear()` (contracts 6.9). A read failure
is not cached (that request uses the built-in table only). `CACHECLR` also empties the shared cache. The legacy process counters
(`CacheHits`/`CacheLoads`) and the sticky "built-in forever" state are not reproduced (BC-PLATFORM-06).

```csharp
var due = await calendar.AddBusinessDaysAsync(20261020, 3, "BACS", ct);
if (due.Header.ReturnCode < 8) payDate = due.ResultDate;
```

### 3.9 TERMCALC -> `IPaymentTermsCalculator` (FR-020)

```csharp
Task<TermcalcArea> CallAsync(KernelCaller caller, string function, Action<TermcalcArea> fill, CancellationToken ct = default);
```
`DUEDATE`, `GETTERMS`, `DISCOUNT`. Terms from `core.PaymentTerms` (global - the table has no company column, no IsActive
filter), dates through `IBusinessCalendar`, amounts through `IRoundingAllocator`. Quirks kept: later warnings overwrite
earlier ones, the GETTERMS message `Due  - <desc>` (two spaces), discount basis and Bacs flag compared case-sensitively.
A terms read failure applies to that call only (statutory default RC 04 TRM0002 for DUEDATE, RC 16 for GETTERMS).

```csharp
var t = await terms.CallAsync(KernelCaller.From(ctx), "DUEDATE", a =>
{
    a.TermsCode = "D2N30"; a.InvoiceDate = 20261020; a.NetAmount = 1000m; a.VatAmount = 200m;
}, ct);
// t.DueDate, t.DueDateRaw, t.DiscountDate, t.DiscountAmount, t.TermsDescription, t.DueDateUk, t.Header.Result
```

### 3.10 FXCONV -> `IFxConverter` (FR-021)

```csharp
Task<FxconvArea> CallAsync(KernelCaller caller, string function, Action<FxconvArea> fill, CancellationToken ct = default);
```
`GETRATE`, `TOGBP`, `TOFCY`, `REALISED`. Rates are foreign units per GBP 1 (`core.FxRate.UnitsPerGBP`, latest on or
before the date, RC 04 FX0008 when from an earlier date). Conversions are held to 12 decimals and rounded once half away
from zero through RNDALLOC (`Unrounded`, `Residual`). FX0005 keeps the legacy text `A currency code is required -
parity is neverassumed` (STRING of a trimmed message). A rate read failure answers RC 16 FX0001 for that call only.
`core.usp_CheckFxStaleness` and its FX.MAX.AGE.DAYS / FX.MAXAGE.DAYS mismatch (SME-1207) are not part of FXCONV: the
procedure and its callers (NIGHTLY STEP060, SQL Agent job) are carried unchanged.

```csharp
var fx = await fxconv.CallAsync(caller, "TOGBP", a => { a.Currency = "EUR"; a.RateDate = 20261020; a.ForeignAmount = 1150m; }, ct);
// fx.GbpAmount, fx.Rate, fx.RateSource, fx.RateFoundDate, fx.RateText
```

### 3.11 RNDALLOC -> `IRoundingAllocator` (FR-022)

```csharp
RndallocArea Round(decimal value, int places);              RndallocArea Truncate(decimal value, int places);
RndallocArea UnitPrice(decimal value, decimal divisor, int places);
RndallocArea Extend(decimal value, decimal value2, int places);
RndallocArea Allocate(decimal amount, int places, string mode, IReadOnlyList<decimal> weights);
LegacyResult Execute(RndallocArea area);
```
Pure computation (singleton). ALLOC is largest-remainder: `Lines[i].Part`, `Allocated`, `Residual`, `Bumped`; mode
`EQUAL` or `WEIGHT` is not case-folded (`equal` is refused), EQUAL overwrites the weights, ties go to the earlier line.

### 3.12 STRUTIL -> `CobolString` (FR-023)

`CobolString.Upper`, `Code(text, maxLength)`, `DocRef(text, zeroPolicy)`, `Money(amount, currency, symbol)`,
`Quantity(amount, zeroPolicy)`, `Squeeze(text)`, `Execute(StrutilArea)`. Results on the area (`TextOut`, `LengthOut`,
`Valid`, `Changed`, `BadChar`, `BadPosition`). UPPER folds ASCII only (GnuCOBOL C locale). Examples:
`Money(1234.567m, "GBP", "Y").TextOut == "£1,234.56"` (0xA3 in Windows-1252), `DocRef("SI-000123", "STRIP").TextOut ==
"SI123"`, `Quantity(12.5m, "STRIP").TextOut == "12.5"`.

### 3.13 STFUNCS -> `CobolNumeric` / `CobolDate` (FR-024)

`CobolNumeric.RoundAway(value, places)` (places clamped 0..8), `SignOf`, `NumVal` (GnuCOBOL 3.2: invalid text -> 0),
exact `Multiply` / `Divide` / `MultiplyDivide` with receiving-field truncation or rounding, `Store`;
`CobolDate.IsLeap` / `IsLeapFunction` (false outside 1601..9999), `DaysInMonth` (0 outside the domain),
`EndOfMonth(int)`, `WeekdayOf(int)` (1 = Monday, 0 for an invalid date), `TestDate`, `IntegerOfDate(int)`,
`DateOfIntegerYyyymmdd`. `CobolEdit.Field/Format(value, picture)` renders numeric-edited pictures (`-(14)9.99`, `ZZZ9`,
`Z,ZZZ,ZZ9.99-` ...).

### 3.14 JOBLOG -> `IJobRunRegistry` (FR-027)

```csharp
Task<JoblogArea> StartAsync(KernelCaller caller, string jobName, string? arguments, CancellationToken ct = default);
Task<JoblogArea> EndAsync(KernelCaller caller, string jobName, long jobRunId, string status, int returnCode, int rowsProcessed = 0,
    string? logFile = null, string? spoolFile = null, string? message = null, CancellationToken ct = default);
```
For programs that register **themselves** (their own `CALL "JOBLOG"`); runs submitted through the job framework are
registered by `JobService` (FR-057) - do not register twice. END maps COMPLETE -> OK and CANCELLED -> ABENDED and back;
a run id of 0 is refused JOB0002 and the run stays RUNNING (SME-009, reproduce per program).

### 3.15 GL posting gateway -> `IGlPostingGateway` (FR-029)

```csharp
Task<GlStageResult> ClearStageAsync(string correlationId, CancellationToken ct = default);
Task<GlStageResult> StageLineAsync(JournalStageLine line, CancellationToken ct = default);
Task<GlStageResult> StageStatementAsync(string statement, IReadOnlyList<SqlioParm> parms, CancellationToken ct = default);
Task<GlPostResult> PostAsync(GlPostRequest request, IReadOnlyList<int>? widths = null, CancellationToken ct = default);
```
The gateway adds no validation, retry or reason codes: SQLCODE 0 / 100 / -1 and the legacy driver text come back for the
caller to map (for example POPOST POP0005 RC 08 on failure, POP0006 RC 12 on no row). Stage rows with the caller's column
order and conversions (`Text` = blank sent as '', `NullIfBlank` = `NULLIF(?,'')`, `Amount` = SQLIO-NUM-EDIT text,
`Null`); `IfNotExists = true` gives the re-runnable INSERT ... WHERE NOT EXISTS form (BANKREC, FXREVAL, GLADJUST,
YEAREND). `GlPostRequest.ArgumentCount` reproduces how many positional arguments the caller passed (DESPATCH 12, most 14,
KITCOMPL 15 with `SuppressResult = true`, then its own read-back). `GlPostResult` exposes `JournalId`, `JournalNumber`,
`FiscalYear`, `PeriodNo`, totals, `Status`, `WasDuplicate` (a repeated correlation id returns the original journal).

```csharp
var corr = ctx.CorrelationId;
await gl.ClearStageAsync(corr, ct);
await gl.StageLineAsync(new JournalStageLine(corr, 1)
    .Text("CompanyCode", co).Text("AccountCode", "5000").Amount("DebitAmount", 120.50m).Amount("CreditAmount", 0m)
    .Text("CurrencyCode", "GBP").NullIfBlank("DimCostCentre", cc).Text("Narrative", text), ct);
// ... more lines ...
var post = await gl.PostAsync(new GlPostRequest
{
    CompanyCode = co, CorrelationId = corr, UserCode = user, Description = "Goods received " + grn,
    PostingDate = postingDate, JournalType = "GL", SourceModule = "PO", SourceRef = grn, EventCode = "GRN",
}, ct: ct);
if (post.Failed) { /* map post.ErrorText to the program's reason code */ }
else if (post.NoRow) { /* the program's "no journal returned" branch */ }
```

### 3.16 PING -> `IDatabaseProbe` (FR-030)

`ProbeAsync()` runs `SELECT DB_NAME(), CONVERT(varchar(30), SYSDATETIME(), 121), LEFT(@@VERSION, 80)` on its own
short connection: RC 00 `ENGINE AND DATABASE AVAILABLE` with `DatabaseName`, `ServerTime` (27 characters) and `Version`;
when the database is unreachable a warning RC 04 SQL0001 with the first 200 bytes of the driver text (BR-PING-002) -
never an exception. `Available` is RC 00.

## 4. SQLIO helper for slice code (`Sterp.Platform.Kernel.Data.Sqlio`)

Slices that reproduce a COBOL program's own SQL use the same helper the kernel uses:

```csharp
var row = await sql.SingleAsync(
    "SELECT Name, ISNULL(RegisteredNumber,'') FROM core.Company WHERE CompanyCode = ?",
    [SqlioParm.Text(co)], widths: [40, 10], ct);       // SNGL: row.Found / SqlCode 100, slots fitted to the widths
var rows = await sql.FetchAllAsync(statement, parms, widths, ct);   // OPEN + FTCH loop
var sets = await sql.FetchSetsAsync(statement, parms, [[10, 8], [10, 20, 60]], ct);   // FTCH, MORE, FTCH ... (PERCLOSE)
var exec = await sql.ExecAsync("UPDATE ... WHERE Id = ?", [SqlioParm.Integer(id)], ct);  // EXEC: SqlCode, RowCount
```
`?` markers are positional (`@p1..`), parameters are varchar text exactly as SQLIO bound them (`Text` right-trimmed and
cut at 256, blank = '' not NULL; `NullIfSpaces`; `Number` = `-(12)9.9(6)` text; `Integer` = 17-digit text; `Digits`),
and result columns are rendered as the ODBC driver rendered them as character data (`OdbcText.Render`: decimals without a
leading zero `.50`, bit `1`/`0`, datetime2(n) with n fraction digits, NULL as spaces). `row.Text(i)`, `row.Raw(i)` and
`row.NumVal(i)` read the slots. Failures throw `SqlioException` (legacy driver text).

## 5. Batch conventions (FR-028)

- `BatchSuite.StandardArgs(BatchSuiteKind.Fin | Xmd, ctx.Args, now)` - 9500-STD-ARGS with the suite defaults (FIN: STC,
  GLCLERK, as-at today; XMD: STC, FINCTRL, PREVIEW, as-at today, SITE/FROM/TO). `args.IsCommit` (XMD writes only in COMMIT).
- `BatchSuite.JobStartAsync` / `JobEndAsync` - the suites' own JOBLOG START/END (job name = report id, arguments = command
  line in X(200), END status 0 COMPLETE / 4 WARNING / other FAILED); `EndStatus`, `StoredEndStatus`, `JobArguments`.
- `BatchSuite.FillHeadingsAsync(sql, args, heading, now)` - company name and registered number from `core.Company` (XMD also
  the jurisdiction as place of registration); the company code is printed when the row cannot be read.
- `BatchSuite.ResolvePeriodAsync` (9600), `FormatDate` (9450), `IsoToNum` (9470), `GetParmNumAsync` (XMD 9650),
  `RaiseExceptionAsync` (XMD 9700: counted only outside COMMIT; RC above 06 gives the `STERP-W-9701` console line).
- Console texts: `SpoolPathFailure` (`STERP-E-9201 ...`), `SpoolOpenFailure(path)`, `SpoolAnnouncement(path)`.
- Spool path: RUNPATH BUILD SPOOL is the job framework's `ctx.OpenSpool(reportId, company)` + `ctx.AnnounceSpool(path)`.
- `Report132.HeadingLines(heading, page)` - the STRPTHDR six-line heading (company 1, report id 50, `Page ` 112, ZZZ9 117;
  registered number line; title + `Run date ` 104 + dd/mm/yyyy 113; subtitle + as-at text 70 + user 104 + time 125;
  blank; 132 dashes). `Report132Writer(writeLine, pageBreak, heading, firstDetail: 7, lastDetail: 58)` pages detail
  groups with a form feed between pages; `Report132.Line().Put(column, value, width)` builds detail lines by column.

```csharp
// inside a JobDefinition.Execute handler (JobRunContext ctx)
var sql = ctx.Get<ISqlRunner>();
var args = BatchSuite.StandardArgs(BatchSuiteKind.Fin, ctx.Args, DateTime.Now);
var heading = await BatchSuite.FillHeadingsAsync(sql, args, new ReportHeading { ReportId = "TBRPT", Title = "Trial balance" }, DateTime.Now, ct);
using var spool = ctx.OpenSpool("TBRPT", args.Co);
var page = new Report132Writer(spool.WriteLine, spool.PageBreak, heading, lastDetail: 60);
page.Detail(new Report132.Line().Put(1, account, 10).Put(14, name, 40).Put(60, CobolEdit.Field(balance, "Z,ZZZ,ZZ9.99-"), 14).Text);
// ... at the end (9350-CLOSE-SPOOL)
ctx.AnnounceSpool(spool.Path);
```
Use the page depth (`lastDetail`) and detail layout of your program's Report Writer RD; the writer only needs "write one
line" and "page break".
