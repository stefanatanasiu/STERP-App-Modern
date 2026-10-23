# STERP modernized application - developer guide

Modern .NET 10 replacement of the legacy STERP application ([STERP-App](https://github.com/stefanatanasiu/STERP-App),
commit `00631916`, cloned next to this repository as `..\STERP-App`), built by Agent 08 under lifecycle run
`lr-20261017T115103Z-ee5732ce` (scope `estate`). The binding design is the Agent 06 package (shared contracts
`01-shared-platform-contracts.md`, one specification per slice, 13 OpenAPI documents); its OpenAPI documents and data
contract are copied into `contracts/`, and the specifications stay with the evidence package outside this repository.
The safety net is the Agent 07 package in `tests/`.

## 1. Non-negotiable rules

1. **Never touch the shared database** (server `.`, database `STERP`). Run hosts and tests only with `scripts/run-host.ps1`
   and `scripts/run-parity.ps1`, which accept only Agent 08 isolated LocalDB instances (`STERPA08_*`). Never run `sqlcmd`,
   SSMS or any tool against `.`/`localhost`/the machine name.
2. **Never edit `tests/`** (Agent 07 cases, contracts, fixtures, harness). If a scenario looks wrong, record it as a finding
   (section 9); do not change it and do not special-case its data in product code.
3. **No DDL, no migrations, no seeding** against any database (schema policy `frozen`). The product never creates, alters
   or drops objects. Use only the objects in `contracts/sql-object-mapping.csv`.
4. **Legacy behaviour is the specification** (owner decision HD-11): same procedures and SQL, same predicates, ordering,
   lock hints, row-count checks, return codes, reason codes, message texts (character for character), data effects and
   unit-of-work boundaries - including defects the spec says are "reproduced as observed". Read the cited COBOL/VB/SQL.
5. **Binding vs advisory.** Binding: routes, operationIds, request/response shapes, status codes, reason codes, data
   contract, transactions, permission/screen gates, stack major versions. Advisory: class/method names, internal layout.
   You may refactor internals freely; never change a binding element. A binding conflict is a finding, not a decision.
6. **Money/quantity/rate = `decimal`** with the legacy scale; never `double`/`float`. COBOL `ROUNDED` =
   `MidpointRounding.AwayFromZero`; no `ROUNDED` = truncation at every receiving field (`CobolNumeric`).
7. **No secrets**, no connection strings in code; parameterized SQL only; never build SQL text from request input.

## 2. Solution layout

| Path | Content |
|---|---|
| `src/Platform/STERP.Platform.Kernel` | outcome model (`LegacyResult`), exceptions, data access (`IUnitOfWork`, `ISqlRunner`, `IProcedureRunner`), COBOL semantics (`CobolText`, `CobolNumeric`, `CobolDate`), `CommandLineArgs` (CMDARGS), shared kernel services (`Services/`) |
| `src/Platform/STERP.Platform.Hosting` | contract catalog (binding OpenAPI embedded), request pipeline, `OperationContext`, problem mapping, OpenAPI publication, health + schema drift, job framework (`Jobs/`) |
| `src/Modules/STERP.Modules.<Slice>` | one library per slice: handlers for the slice's binding operations, job definitions, public in-process contracts for other slices |
| `src/Modules/STERP.Modules.Composition` | the module list composed into both API hosts |
| `src/Hosts/STERP.Api`, `src/Hosts/STERP.Jobs` | online and batch web services |
| `src/Hosts/STERP.Web`, `STERP.Web.Client` | Blazor WebAssembly GUI (legacy MDI structure): host and shell |
| `src/Web/STERP.Web.Framework` | GUI page framework (BaseForm/ListForm/GridHelper equivalents, API client, dialogs, busy signal) |
| `src/Web/STERP.Web.Pages.<Slice>` | one Razor class library of pages per slice (section 10); `STERP.Web.Pages.Template` is the empty example |
| `src/Hosts/STERP.ApiPortal` | API Console (STERP-PORTAL): server-rendered shell and catalogue (`Portal/ConsolePages.cs`), host status and contract-drift probes, and the browser app (`wwwroot/console/console.js`) that renders operation pages and sends "Try it out" calls with the user's Windows credentials |
| `contracts/` | byte-identical copies of the binding OpenAPI documents, `sql-object-mapping.csv`, `gui-routes.json`, `frozen-schema.json` (drift snapshot) |
| `tests/` | Agent 07 safety net (read only) |
| `scripts/` | isolated environment, host and test launchers |

## 3. How an operation is served

Every operation of the 13 binding documents is routed automatically from the embedded YAML (`ContractRouter`). The
pipeline per call: Negotiate authentication -> STERP user from `core.usp_GetUserContext` (403 `API0010`) -> SYSADM run-as
(`X-STERP-Run-As`, 403 `API0003`) -> context headers (400 `API0001`) -> company/site access list (403 `API0002`) -> handler
registered? else 501 `API0501` -> screen gate `x-sterp-screen-gate` through `core.usp_UiCheckAccess` (403 `PRM0006`) ->
handler -> outcome/problem mapping -> structured log. A handler never re-implements these steps.

Register handlers in the slice module:

```csharp
public sealed class ItmModule : ISterpModule
{
    public string Slice => "STERP-ITM";
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IStockMovementService, StockMovementService>();   // public contract for SO/PO/KIT/CST/XMD
    }
    public void MapOperations(OperationRegistry ops)
    {
        ops.Handle("itm_getItem", ItemEndpoints.GetItemAsync);
    }
    public void MapJobs(JobCatalog jobs) => jobs.Add(StkstatJob.Definition);
}
```

`OperationContext` (`ctx`) gives: `ctx.Session` (`SterpContext`: `UserCode`, `RealUserCode`, `CompanyCode`, `SiteCode`,
`BusinessDate`, `CorrelationId`), `ctx.Route("orderNumber")`, `ctx.Query(...)`/`QueryInt`/`QueryDate`/`QueryBool`,
`await ctx.BodyObjectAsync()` (JsonObject) or `await ctx.BodyAsAsync<T>()`, `ctx.Get<T>()` for services. Results:

| Operation kind (`x-sterp-outcome`) | Return |
|---|---|
| `direct` single object | `ctx.Direct(obj)` |
| `direct` list (contracts 4.10) | `ctx.List(jsonArray)` -> `{ "items": [...], "count": n }` |
| `envelope` (COBOL-backed) | `ctx.Envelope(LegacyResult, data)` -> RC 0/4 = 200, 6 = 202, 8 = 422, 12 = 500, 16 = 404, other = 500; failures carry `data` |
| not found / business refusal on a direct op | `ctx.NotFound(reason, text)` / `ctx.BusinessRule(reason, text)` |

Exceptions are mapped too: `LegacyOutcomeException(result, data)`, `BusinessRuleException` (procedure THROW
`STERP-E-xxxx: text` -> 422 `E-xxxx`), `SqlioException` (RC 12 `SQL0001` + driver text), `ApiProblemException`, any other
-> 500 `SYS0001` with the exception text.

JSON conventions (contracts 4.2-4.3): camelCase names derived from the legacy field/column (`OrderNumber` ->
`orderNumber`), strings right-trimmed, `decimal` keeps its scale (`CobolNumeric.WithScale(v, 2)` for COBOL fields),
dates `yyyy-MM-dd`, date-times `yyyy-MM-ddTHH:mm:ss.fffffff`, rowversion base64, nulls emitted. `SqlJson.ReadRowsAsync`
and the `QueryJsonAsync` runners apply these rules to result sets automatically. Always check the response schema and
example of the operation in the YAML for exact property names.

## 4. Data access - the two legacy session families (contracts 6.4)

| Legacy path | Target | Semantics |
|---|---|---|
| COBOL program -> `SQLIO` (one engine connection per process) | `ISqlRunner` on the request's `IUnitOfWork` | autocommit unless `BeginAsync()` (BEGN); `CommitAsync()`/`RollbackAsync()` (COMT/ROLB) end the one unit - no nesting, a callee's commit commits the caller's work; string params are right-trimmed and cut at 256; failures throw `SqlioException` (driver text prefixed `[Microsoft][ODBC Driver 18 for SQL Server][SQL Server]`) |
| VB facade -> `SqlHelper` (one short connection per call) | `IProcedureRunner` | own connection, autocommit; `""`/null -> NULL; name without space = procedure, text with a space = ad-hoc SQL; 120 s timeout; THROW `STERP-x-code` -> `BusinessRuleException` |

Use the family the legacy path used for that operation (`x-sterp-uow`). Cross-slice calls run on the caller's
`IUnitOfWork` (the same scoped instance). The scoped unit of work is rolled back if a request ends with it still open.
Dapper is available (`QueryAsync<T>`, anonymous parameter objects). Reproduce lock hints (`WITH (UPDLOCK, HOLDLOCK)`),
`sp_getapplock`, rowversion predicates and `@@ROWCOUNT` checks exactly as the legacy statement.

## 5. COBOL semantics helpers

- `CobolText.Fit(value, n)` - PIC X(n) as stored (Windows-1252, `?` replacement, right truncation, space padding);
  `CobolText.Trimmed(value, n)` - same, right-trimmed for output. Inputs of COMMAREA-backed operations are normalized
  this way before business logic (contracts 4.4, FR-STERP-API-011).
- `CobolNumeric.Normalize(value, digits, scale, signed)` - input normalization (round half away from zero, high-order
  truncation, unsigned absolute value). `Store(value, intDigits, scale, signed, rounded)` - COBOL MOVE/COMPUTE into a
  receiving field. `Rounded`, `Truncated`, `WithScale`, `SizeError`.
- `CobolDate` - yyyymmdd conversions, `IntegerOfDate`/`DateOfInteger`, `TestDate`, leap/days-in-month/end-of-month/
  weekday (STFUNCS) helpers.
- `CobolNumeric.NumVal` (FUNCTION NUMVAL exactly as the deployed GnuCOBOL 3.2 runtime evaluates it: no validation, digits
  collected, first '.' starts the fraction, any '-' negates, CR/DB negates and ends the scan, everything else skipped -
  characterized in every legacy dialect, SME-039; do not write a module-local NUMVAL), `RoundAway`/`SignOf` (STFUNCS),
  exact `Multiply`/`Divide`/
  `MultiplyDivide` into a receiving field; `CobolEdit.Field/Format(value, picture)` - numeric-edited pictures.
- `Sqlio` (`ISqlRunner` extensions `SingleAsync`/`FetchAllAsync`/`ExecAsync` with `SqlioParm`/`SqlioRow`) - a COBOL
  program's own SQL with SQLIO binding (varchar text, `?` markers) and ODBC character rendering of result columns.
- `CommandLineArgs.Parse(text)` - CMDARGS semantics for batch programs; `CommandLineArgs.Build(pairs)` - ReportService.BuildArguments.

## 6. Shared kernel services (contracts 7)

`src/Platform/STERP.Platform.Kernel/Services` reproduces the common COBOL services (PERMCHK, PERIODCK, NUMGEN, IDEMPOT,
AUDITLOG, EXCPRAIS, PARMGET, DATECALC, TERMCALC, FXCONV, RNDALLOC, STRUTIL, JOBLOG, PING), the GL posting gateway
(`fin.JournalStage` + `fin.usp_PostJournal`) and the batch conventions (FINBATPR/XMDBATPR housekeeping, STRPTHDR
`Report132`); STFUNCS is `CobolNumeric`/`CobolDate`, RUNPATH is `OutputPaths` (jobs framework).
Each service mirrors its legacy COMMAREA: same function codes, inputs, outputs and STCAHDR result (`LegacyResult`), same
SQL on the caller's unit of work. The API of each service is documented in
`src/Platform/STERP.Platform.Kernel/Services/README.md`. Kernel tests: `verification/STERP.Kernel.Tests` (run
`verification\STERP.Kernel.Tests\bin\Release\net10.0\STERP.Kernel.Tests.exe`; the database tests use
`STERP_KERNEL_TEST_SERVER`, default `(localdb)\STERPA08_kernel`, and refuse any server that is not a LocalDB
`STERPA08_*` instance). With the .NET 10 SDK `dotnet test` refuses xUnit v3 (Microsoft.Testing.Platform) projects
unless global.json opts in to the new test experience - run the test executables directly.

## 7. Jobs (contracts 8; STERP-JOBS; FR-STERP-PLATFORM-057)

A batch program is a `JobDefinition` added in `MapJobs`:

```csharp
jobs.Add(new JobDefinition
{
    JobCode = "STKSTAT", Slice = "STERP-ITM", SubmitOperationId = "itm_submitStkstatRun",
    ParameterOrder = ["CO", "SITE", "ITEM", "ASAT", "MODE", "USER"],      // schema order of the parameter object
    PreCheck = ctx => ...,                // checks the program made BEFORE its own JOBLOG START (refusal = 422, nothing registered)
    SelfRegistration = ctx => new JobRegistration("STKSTAT", co, user, site, args),   // program's own JOBLOG START, or null
    Execute = StkstatProgram.RunAsync,    // returns the exit code 0/4/8/12/16
});
```

The framework (`JobService`) builds the command line (ReportService.BuildArguments, USER defaults to the caller), checks
delivery (501), runs `PreCheck`, registers (`core.usp_JobRunStart`), writes the run manifest, returns 202
`JobRunAccepted`, runs `Execute` on its own worker with a fresh DI scope and the submitter's `SterpContext`, then ends the
registration (`core.usp_JobRunEnd`) with the program's END values (`EndValues`, `ctx.EndOverride`, or the default JOBLOG
mapping 0 OK / 4 WARNING / other FAILED). In `Execute`, use `ctx.Args` (parsed CMDARGS), `ctx.Display(line)` (console =
SYSOUT log), `ctx.OpenSpool(reportId, company)` + `ctx.AnnounceSpool(path)` (listing file in Windows-1252, opened without a
share lock like GnuCOBOL OPEN OUTPUT), `ctx.Result = json` (GET .../runs/{id}/result), `ctx.RowsProcessed`, `ctx.Message`,
`ctx.Get<ISqlRunner>()`.

Legacy listings come from the GnuCOBOL 3.2 Report Writer (REPORT SECTION in 55 batch programs). Lay them out with
`Sterp.Platform.Kernel.Reporting.GnuReportWriter` (port of libcob reportio.c: `Initiate`/`Generate`/`Terminate`,
`RwLine` LINE/LINE PLUS, `RwControl` control headings/footings that present the previous control value, `RwText`
COLUMN/PIC lines that may be wider than 132 and VALUE literals printed at full length). The legacy listings have **no
form feeds**: pages are padded with blank lines (PAGE LIMIT - 1 on an overflow at a GENERATE, PAGE LIMIT inside a group and
at TERMINATE), the first body group of a page starts on FIRST DETAIL and every line is tested against LAST DETAIL. Do not
lay out a Report Writer listing with `Report132`/`SpoolWriter.PageBreak` and do not write a module-local report writer;
verify a listing line for line against the legacy executable's output on an isolated database (the AR, BOM and FIN streams
did this; their module-local writers predate the shared one).

### 7.1 Platform job framework extension points (entry points, streams, lookups, maintenance)

All of this lives in `src/Platform/STERP.Platform.Hosting/Jobs` (owned by the PLATFORM work stream); slices only add
definitions in `MapJobs` and never call the engine internals.

**Entry points.** Every submission goes through `JobService` (IJobSubmission). A slice never registers rows itself; it only
declares how its program registered in legacy (`SelfRegistration`) and which checks preceded that (`PreCheck`).

| Entry point (manifest `entryPoint`) | Operation | Command line | Rows registered before 202 (`core.usp_JobRunStart`) | 202 `jobRunId` |
|---|---|---|---|---|
| `SLICE` | the slice's `POST /api/<seg>/v1/jobs/<CODE>/runs` (automatic from `SubmitOperationId`) | typed body in `ParameterOrder`, `ReportService.BuildArguments`, USER = caller unless given | program's own row (`SelfRegistration`), else one row named after the job code (D-PLAT-09); a stream: its SETENV row | own row |
| `CATALOGUE` | `platform_launchCatalogueReport` -> `JobService.LaunchFromCatalogueAsync` | non-empty panel values, keys upper-cased, USER replaced in place or appended, BuildArguments | launcher row (`@JobName` = ProgramName, session company, user) **plus** the program's/stream's own row (SME-010); no D-PLAT-09 row | launcher row |
| `CONSOLE` | `platform_submitConsoleJob` -> `JobService.SubmitFromConsoleAsync` | STERPOPS 3350: ' KEY=VALUE' (key 14, value 24), unquoted, cut to 200 | nothing of its own: the program's own row / D-PLAT-09 row, or the stream's SETENV row | own row |
| `RESTART` | `jobs_restartRun` -> `JobService.RestartAsync` | original command line + ` RESTART=<label>` | as the original entry point (a catalogue restart registers a new launcher row again) | as the original |
| `STREAM-STEP` | a `RUNSTEP` program step inside a stream | the step PARM | the program's own row only (no D-PLAT-09 row) | - |

Order at every entry point: 501 `API0501` (program, or any program a stream declares, not delivered; message names the
owning slice from the contract job paths, `JobOwnership`) -> `PreCheck` (422 with the legacy code, nothing registered) ->
registration (failure = 500 `SYS0001`, nothing runs, D-PLAT-05) -> manifest `<root>\work\jobs\<id>.json` -> 202 -> execution.
Inside a stream a `PreCheck` refusal cannot be a 422: the step ends with the refusal's return code and the console lines of
`RefusalDisplay` (default: the message) go to SYSOUT.

END values: a self-registering program's row ends with `ctx.EndOverride` ?? `EndValues(ctx, exit)` ?? the JOBLOG suite
mapping (0 OK, 4 WARNING, other FAILED); the D-PLAT-09 row with the JOBEND mapping (>= 12 ABENDED, >= 8 FAILED, >= 4
WARNING); `LogFile` defaults to the run console (see below) when the END values give none; `Status = null` leaves the row
RUNNING (SME-009). The catalogue launcher row ends with `ReportService.JobStatus` (<= 0 OK, <= 4 WARNING, < 16 FAILED, else
ABENDED), `LogFile` = launcher log, `SpoolFile` = last `STERP-I-SPOOL <path>` console line.

Run console files: catalogue = launcher log `<root>\logs\<PROGRAM>-<yyyyMMddHHmmss at end>.log` (UTF-8, written at the
end); console program = `<root>\logs\OPS-<PROG>-<sessionStartDate>000000.log` (Windows-1252, overwritten); slice program =
`<root>\logs\<PROG>-<jobRunId>.log`; a stream step writes to the stream SYSOUT; a stream's own console
(STERP-I-0001/0002/0009 lines) = `<root>\work\jobs\<jobRunId>.console.log` (catalogue: the launcher log).

**Job streams.** A stream is a `JobDefinition` with a `Stream` script instead of `Execute`. Write the script block by block
in the order of `jobs\<STREAM>.cmd`; the engine supplies SETENV (parameter parsing exactly as cmd.exe delivered them, the
defaults, ASAT/PERIOD/YEAR/PP/YYYYPP/PEREND, run folders, SYSOUT `<root>\logs\<JOB>-<stamp>.log` with the job card,
registration with `Arguments` 'MODE=.. ASAT=.. YEAR=.. PERIOD=..'), `if defined P_RESTART goto %P_RESTART%` (an undefined
label: console 'The system cannot find the batch label specified - <label>', exit 1, no JOBEND, row stays RUNNING),
RUNSTEP (SYSOUT/console lines, program steps run in-process with their own registration, @SQL steps with sqlcmd -W output,
LASTCC/MAXCC) and JOBEND (status by MAXCC, end lines, END with 'Job stream <JOB> ended with COND code <n>').

```csharp
jobs.Add(new JobDefinition
{
    JobCode = "JOB.MONTHEND", ProgramName = "MONTHEND", Slice = "STERP-FIN",
    SubmitOperationId = "fin_submitMonthendStreamRun",
    ParameterOrder = ["CO", "YEAR", "PERIOD", "MODE", "USER", "RESTART"],
    Stream = StreamScript.Create("MONTHEND")                                   // SETENV JOBNAME
        .SqlStep("STEP005",
            s => $"SELECT CompanyCode, FiscalYear, PeriodNo, Status FROM fin.Period WHERE CompanyCode='{s.Var("CO")}' AND FiscalYear={s.Var("YEAR")} AND PeriodNo={s.Var("PERIOD")};",   // PARM shown in SYSOUT
            "SELECT CompanyCode, FiscalYear, PeriodNo, Status FROM fin.Period WHERE CompanyCode=@co AND FiscalYear=@year AND PeriodNo=@period;",  // executed (D-PLAT-08)
            s => new { co = s.Var("CO"), year = s.Var("YEAR"), period = s.Var("PERIOD") })
        .Step("STEP010", "REVALRUN", s => $"CO={s.Var("CO")} ASAT={s.Var("ASAT")} MODE={s.Var("MODE")} USER={s.Var("USER")}")
        // ... STEP020 .. STEP080; Step/SqlStep add 'if %LASTCC% GEQ 8 goto ABEND'; falling off the last block = goto EOJ
        .Abend(s => [StreamScript.TerminatedLine(s), "//*  CORRECT THE FAULT AND RESTART AT THE FAILING STEP"])
        .Build(),
});
```

- `Setup(s => ...)`: statements between `call SETENV.cmd` and the RESTART jump (XMDCONV's `P_CUTOVER` default and `CVTAIL`).
- `Block(label, async s => { ...; return StreamFlow.Next | Goto("STEP030") | Abend | Eoj; }, new StreamStepInfo(step, program))`
  for inline lookups, conditional PARMs, custom SYSOUT/console lines and jumps (PAYRUN STEP020, XMDCONV's COMMIT bypass).
  Declare every program a block may run, so the 501 check covers it.
- `StreamContext`: `Var/IsDefined/Set` (P_ variables, '' when undefined), `LastCC`, `MaxCC` (settable, PAYRUN `set MAXCC=8`),
  `Sysout(line)`, `Console(line)`, `RunStepAsync(step, program, parm)`, `RunSqlStepAsync(step, displayParm, statement, params)`,
  `QueryLastLineAsync(statement, params)` (the `for /f ... sqlcmd -h -1` idiom), `Services`, `JobRunId`, `SysoutPath`.
- A stream runs only when its slice and the slice of every declared step program are delivered and the program is
  registered (`catalog.FindProgram`); otherwise 501 naming that slice (MONTHEND with CST undelivered names STERP-CST /
  CSTINVAL; VATRET's step program has no source and stays 501).
- `jobs_restartRun` accepts the STEPnnn labels of the script, ABEND and EOJ (else 422 `API0024`); only runs with a stream
  manifest are restartable (else 422 `API0023`). Programs submitted through the catalogue or console need no extra code:
  the entry points resolve `core.ReportDef.ProgramName` through `catalog.FindProgram` / `FindStream`.

**Named lookups** (`PlatformContracts/LookupSources.cs`): implement `ILookupSource` (`LookupCode`, `Slice`, `FindAsync`) with
the legacy query and register it in the slice module (`services.AddScoped<ILookupSource, CustomerLookup>()`);
`LookupQuery.RunAsync(sql, procOrSql, request)` runs it with the legacy @CompanyCode/@SiteCode/@Search/@Argument and returns
`DynamicRows` (columns + rows keyed by the exact column names). Until a source is registered, platform_lookupNamed answers
501 naming the owner in `NamedLookups.Owners`.

**Generic maintenance** (`PlatformContracts/GenericMaintenance.cs`): `IGenericMaintenance.SaveAsync(maintCode, mode,
values)` is MaintService.Save (core.usp_UiMaintSave with the `MaintenanceXml.ToXml` @Data), for slices whose legacy forms
saved through it (ITM.ITEM, ITM.COUNTLINE).

## 8. Working on a slice

1. Read the slice specification (`application-specs/STERP-<SLICE>.md`): requirements, section 7 operations table
   (legacy chain, permission, unit of work), section 8 data contract, section 12 decisions.
2. For each operation read its YAML entry (`contracts/openapi/STERP-<SLICE>.openapi.yaml`): description, schemas,
   examples, `x-sterp-legacy` citations, `x-sterp-uow`.
3. Read the cited legacy code in the legacy clone: `..\STERP-App\cobol\online\<mod>\*.cob`, `cobol\batch\<mod>\*.cob`,
   `cobol\copy\*.cpy`, `dotnet\STERP.Business\**`, `dotnet\STERP.Desktop\Forms\**`, `db\procs\**`, `db\tables\**`.
   Upstream analysis (Agent 02 FRs per program, Agent 03 data model, Agent 04 rules) is in the evidence package outside
   this repository.
4. Read the slice's scenarios in `tests/cases/STERP-<SLICE>.cases*.json` (steps, assertions, derivation).
5. Implement, build, run the slice's scenarios, iterate until green:

```powershell
./scripts/new-isolated-env.ps1 -Name itm                      # once: STERPA08_itm
dotnet build STERP.Modernized.slnx -c Release
./scripts/run-host.ps1 -Name Api  -Instance STERPA08_itm -PortBase 7120   # separate terminals / async
./scripts/run-host.ps1 -Name Jobs -Instance STERPA08_itm -PortBase 7120
./scripts/run-parity.ps1 -Tier target -Instance STERPA08_itm -PortBase 7120 -ResultXml out/itm.xml -- -trait "application=STERP-ITM"
```

Verification hosts are isolated by default (`scripts/isolated-paths.ps1`): the instance's own output root and a scratch
legacy run root (`Sterp:Jobs:LegacyRunRoot`) instead of the live legacy folder `..\STERP-App\run`, which the
scripts refuse. `run-parity.ps1` passes the same roots to the harness (`STERP_TARGET_OUTPUT_ROOT`,
`STERP_TARGET_LEGACY_RUN_ROOT`; give it `-OutputRoot`/`-LegacyRunRoot` when the hosts were started with them) and
`STERP_TARGET_RESTART_COMMAND`, which runs `scripts/restart-host.ps1` to kill one host and start it again with the same
binaries and roots (fault injection for restart scenarios).

## 9. Findings and deviations

Record in your work-stream report (kept with the Agent 08 evidence package, not in this repository or in code comments):
advisory design deviations, scenario/spec contradictions (with the evidence), legacy defects reproduced, and every
scenario that does not pass with the exact reason.

## 10. GUI pages

The GUI (STERP-WEB) is a Blazor Web App in Interactive WebAssembly render mode without prerendering. `STERP.Web` only
serves the client (every path, so deep links work), `/_sterp/config` (product name, API base URLs), a client-error log
and health; it never touches the database (FR-STERP-WEB-001). The browser calls STERP.Api and STERP.Jobs directly with
the user's Windows credentials (`credentials: 'include'`).

| Project | Content |
|---|---|
| `src/Hosts/STERP.Web.Client` | the shell (`frmMain`): start-up/splash, automatic sign-on, menu bar, toolbar, status bar, MDI workspace, context change, shell commands, page registry composition (`PageModules.cs`) |
| `src/Web/STERP.Web.Framework` | `StPage` (BaseForm), `StListPage` + `StListLayout` (ListForm), `StGrid` + `GridData` + `UkFormat` (GridHelper), fields, dialogs, `ApiClient`, `JobRunner`, `GuiRoutes` (STERP-WEB 7.2), `sterp.js`/`sterp.css` |
| `src/Web/STERP.Web.Pages.Platform` | the twelve PLATFORM pages (STERP-PLATFORM 7.2) - the reference implementation |
| `src/Web/STERP.Web.Pages.<Slice>` | the pages of one slice (one Razor class library per slice) |
| `src/Web/STERP.Web.Pages.Template` | empty example to copy |

### 10.1 Adding a slice's pages

1. Copy `src/Web/STERP.Web.Pages.Template` to `src/Web/STERP.Web.Pages.<Slice>` and rename the project, namespace and
   module class (`<Slice>Pages : IPageModule`). `Slice` returns the target application id (`"STERP-ITM"`).
2. In `Register`, map every legacy form class of the slice (the rows of STERP-WEB section 7.2, `GuiRoutes.All`) to its
   page: `pages.Add<StockEnquiryPage>("frmStockEnquiry");`. The route, caption and window size come from `GuiRoutes`;
   do not add `@page` directives - the shell opens pages as MDI child windows (menu, toolbar, deep link).
3. Reference the project from `src/Hosts/STERP.Web.Client/STERP.Web.Client.csproj`, add `new <Slice>Pages()` to
   `PageModules.All()`, and add the project to the `/web/` folder of `STERP.Modernized.slnx`.
4. A registered page opens only while STERP.Api reports the slice delivered (`/health/ready` -> `deliveredSlices`);
   otherwise the shell shows the placeholder "Not yet available in the modernized application - use the legacy client
   (slice STERP-XX, wave Wn)". A form class that is neither registered nor routed shows `STERP-E-0004`.

### 10.2 Writing a page

- Inherit `StPage` (or `StListPage` for enquiries/worklists: implement `LoadDataAsync`, call `Populate(GridData)`, render
  with `<StListLayout>`; it provides "Refresh (F5)", "Close (F3)" and '<n> record(s)').
- Keys: override `OnHelpKeyAsync` (F1), `OnLookupAsync` (F2; default "No lookup is available for this field."),
  `OnRefreshKeyAsync` (F5), `OnConfirmAsync` (F10) and `OnKey` for page-specific keys; F3/Esc close through the close
  guard `CanCloseAsync` (ask about unsaved work with `ConfirmAsync`, exactly as the legacy form did).
- Calls: `Api.GetAsync/PostAsync(path, body, options)` (`ApiHost.Jobs` for STERP.Jobs). The session context headers are
  added automatically. Send one `Idempotency-Key` per user instruction where the legacy reused a correlation id:
  create `var instruction = Instruction.New();` when the instruction starts and pass `instruction.Options()` to each call.
- Outcomes: `ShowResultAsync(response)` maps RC 0/4/6/8+ to the legacy status cues (`OK`, `!  WARNING`, `## HOLD`,
  `*** ERROR`) and dialogs with "Reason code: <code>" (contracts 9.3); `ShowProblemAsync` for failures; `GuardAsync` /
  `CallAsync` reproduce `BaseForm.Guard` (BUSY cue, SYS0001 for unexpected errors).
- Jobs: `JobRunner.RunAsync(() => Api.PostAsync(ApiHost.Jobs, ...))` submits, polls the run until it has ended and returns
  the return code, message and spool file; open the spool viewer with `OpenForm("frmSpoolViewer", spoolFile)`.
- Grids: `GridData.FromResultSet` / `FromItems` humanise column names (`GridHelper.Humanise`) and format by legacy type
  (MONEY `#,##0.00`, QTY `#,##0.000000`, RATE, PCT, INT `#,##0`, DATE `dd/MM/yyyy`, STAMP); `Decorate` applies
  registered captions/types/widths. `GridData.FromCommArea(items, prefix, dateFields)` shows a COMMAREA OCCURS group as
  `CommArea.GetTable` did (copybook column names, numeric fields as decimals, `PIC 9(8)` dates as the number yyyyMMdd).
- COMMAREA reads: `LegacyResult.From(response)` (namespace `Sterp.Web.Pages`, visible to every page library without a
  using) is the legacy `CobolResult` - return code, reason code, message, `Display`, `Succeeded` and `Str/Int/Dec(field)`
  of the envelope data or of the problem's data - for handlers that read the COMMAREA or `r.Display` themselves. A library
  may bring its own `LegacyResult` through its `_Imports.razor` (FIN does); that type then wins without an ambiguity.
- Status texts: `UkFormat.Money` (`Fmt.Money`, `£#,##0.00`), `UkFormat.Qty` (`Fmt.Qty`, six decimals trimmed + unit),
  `UkFormat.ParseDate` (the `Fmt.ParseDate` formats, including `ddMMyyyy`).
- A form a page creates itself (`Dim f As New frmX ... f.Show()`) opens with `Shell.OpenNew(formClass, argument,
  initialParameters)` - always a new window; the report launcher applies `InitialParameters` to its parameter boxes.
  `TextBox.MaxLength` is `data-maxlen="n"` on the input (typing stops at the limit; text placed in the box by a program is
  not cut, so the page's own width check can refuse it as the legacy dialogs did). `StTabStrip` is a `TabControl`.
- Texts, captions, messages, cues, keys and formats are copied character for character from the legacy form
  (`dotnet/STERP.Desktop/Forms/**`); layout and colours are advisory.
- The browser title is the frmMain title followed by ` - [<caption of the active window>]` (the MDI form of a maximised
  child); scenario `title` assertions read it.

### 10.3 DOM rules (accessibility and the GUI parity driver)

The Agent 07 driver (`tests/parity/STERP.Target.Gui.Parity.Tests/PlaywrightGuiDriver.cs`) clicks by accessible name
(menuitem, button, link, tab, then exact text), fills and selects by label and reads `document.title`, the body text
and the focused element. Therefore:

- every input and select has a `<label for>` with the legacy caption (`StTextField`, `StSelectField`, `StCheckField`
  do this); `aria-label` is used on form fields only - never on windows, grids, toolbars or containers, because label
  look-ups match any element carrying it;
- actions are real `<button>`s whose text is the legacy caption (`StToolButton`); menu entries are `role="menuitem"`;
- bind text inputs on `oninput` (the driver's fill raises `input` only);
- legacy texts stay visible text (status line, dialogs), not tooltips.

### 10.4 Busy signal

The driver waits for "network idle" after each action. `sterp.js` keeps a hidden frame with a pending request to
`/_sterp/hold` attached while the shell is busy (start-up, any `fetch`, a click/command-key grace period), so the wait
lasts until the result is rendered. API calls are covered automatically. Wrap long work without network traffic in
`using (Browser.Busy()) { ... }`; a dialog that waits for the operator must not hold the signal. Never add delays.

### 10.5 Running the GUI scenarios

```powershell
./scripts/run-host.ps1 -Name Api  -Instance STERPA08_x -PortBase 7140   # plus Jobs and Web (async terminals)
./scripts/run-parity.ps1 -Tier gui -Instance STERPA08_x -PortBase 7140 -ResultXml TestResults/gui.xml -- -trait "application=STERP-ITM"
./scripts/run-parity.ps1 -Tier gui -Instance STERPA08_x -PortBase 7140 -- -method "STERP.Target.Gui.Parity.GuiScenarioTests.Routed_page_opens_without_error_for_SYSADM"
./scripts/run-parity.ps1 -Tier gui -Instance STERPA08_x -PortBase 7140 -WebUrl https://localhost:7143 -- -trait "application=STERP-PORTAL"
```

`run-parity.ps1` pre-enumerates the theory rows, so `-trait` filters see every scenario. `$env:STERP_GUI_HEADED=1` shows
the browser. The portal scenarios navigate the harness Web URL, hence `-WebUrl` = the portal (PortBase + 3). Keep the
machine awake during GUI runs (a suspended session stalls the browser and fails the running scenario).
