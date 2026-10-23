# STERP (modernized edition)

**UK supply-chain ERP, modernized from COBOL and WinForms to .NET 10**

This repository is the modernized edition of [STERP - Stefan's ERP](https://github.com/stefanatanasiu/STERP-App), a
complete, working UK supply-chain ERP: general ledger and VAT, debtors, creditors, sales orders, purchasing, stock,
costing, kits and bills of materials, plus cross-module controls and a printed reporting estate. The legacy edition was
built on purpose in a legacy technology mix (1990s COBOL, a C bridge, SQL Server and a .NET Framework WinForms client)
as a modernisation demonstration asset. This edition is the same application after modernisation. Four .NET 10 web
applications replace the COBOL programs, batch jobs and WinForms client and reproduce their behaviour. They run on the
legacy `STERP` database in place, without any schema change, so both editions can run side by side.

It was produced by an agent-driven modernisation pipeline. Discovery and analysis of the legacy estate fed a binding
target design (specifications and 13 OpenAPI contracts) and an application parity safety net derived from the legacy
system (in [tests/](tests/README.md)). The implementation was then built and verified against that safety net. Agent
names and attempt identifiers in the code and documents refer to that pipeline. Its evidence package is not part of
this repository.

> **All data is fictional.** Every company, person, address, bank account, sort code, IBAN, VAT and company registration
> number in the seed and demonstration data is invented for the demonstration; any resemblance to a real organisation or
> account is coincidental.

## Contents

- [Status](#status)
- [Architecture](#architecture)
- [Prerequisites](#prerequisites)
- [Quick start](#quick-start)
- [Demo workflows](#demo-workflows)
- [Other ways to run](#other-ways-to-run)
- [Running the tests](#running-the-tests)
- [Authorization](#authorization)
- [Operational notes](#operational-notes)
- [Repository layout](#repository-layout)
- [Documentation](#documentation)
- [Licence](#licence)

## Status

| Area | Summary (final verification, 22 October 2026) |
|---|---|
| Scope | All 268 API operations, all 61 batch jobs and job streams and all 46 GUI routes (the legacy forms) are implemented |
| Parity | 1,606 of the 1,623 safety-net tests pass. Each of the other 17 asserts specification text that the legacy application contradicts. The application keeps the legacy behaviour, and the 17 tests trace to 11 documented specification corrections |
| Requirements | Of 375 requirements, 359 are fully verified and 16 partially verified (only through those 17 tests); none lacks a passing test |
| Data | The legacy database is used in place, with no migration and no schema change. The 13 binding OpenAPI documents are served byte-identical |
| Use | Verified on isolated LocalDB instances only. Running against a shared `STERP` database is not yet approved (see [Authorization](#authorization)) |

This is a summary: detailed test results are kept with the evidence package, not in this repository.

## Architecture

| Deployable | Project | Default URL | Role |
|---|---|---|---|
| STERP-WEB | `src/Hosts/STERP.Web` + `STERP.Web.Client` | `https://localhost:7100` | GUI shell and the slice pages (legacy forms reproduced caption for caption) |
| STERP-API | `src/Hosts/STERP.Api` | `https://localhost:7101` | all online module operations (11 slices in one process) |
| STERP-JOBS | `src/Hosts/STERP.Jobs` | `https://localhost:7102` | job submission, monitoring, restart, SYSOUT and spool; runs batch programs and streams in-process |
| STERP-PORTAL | `src/Hosts/STERP.ApiPortal` | `https://localhost:7103` | API Console: catalogue, operation pages with legacy traceability and "Try it out" with Windows authentication |

Ports follow one rule: Web = `PortBase`, Api = `PortBase+1`, Jobs = `PortBase+2`, Portal = `PortBase+3`. The business
logic is split into eleven vertical slices, delivered in waves W0-W10 (`src/Modules/STERP.Modules.<Slice>`, with pages
in `src/Web/STERP.Web.Pages.<Slice>`). The COBOL semantics (decimal scale, rounding, truncation, padding and dates) live
in `src/Platform/STERP.Platform.Kernel`, and the host pipeline (Windows Negotiate authentication, user context, error
model, jobs and health) lives in `src/Platform/STERP.Platform.Hosting`. The binding API contracts are in
`contracts/openapi/` (13 OpenAPI documents) and the frozen data contract is `contracts/frozen-schema.json`.

Security: Windows Negotiate end to end. The database connection uses Integrated Security under the account that runs
the services, so legacy audit triggers record the real user. There are no secrets in configuration.

## Prerequisites

- Windows (x64) with your Windows account (Negotiate/NTLM) and PowerShell 7 (`pwsh`).
- .NET SDK **10.0.402** (`global.json`, roll-forward to the latest patch).
- SQL Server **LocalDB** (`sqllocaldb`) and the ODBC 18 `sqlcmd`. The scripts use `STERP_SQLCMD_DIR` when it is set,
  then the ODBC 18 install folder, then `sqlcmd` on `PATH`.
- The two dataset backups `freshc.bak` (empty deploy) and `democ.bak` (demo data), built from the legacy repository as
  described in [docs/DEMO-DATA.md](docs/DEMO-DATA.md#create-the-backups). They are binaries and are not published here.
- A trusted localhost HTTPS certificate: `dotnet dev-certs https --trust` (once per user).
- For the GUI tests only: Microsoft Edge (Playwright drives the installed Edge).

No ODBC driver, COBOL runtime or GnuCOBOL is needed to run the modernized application. They are needed only to build
the legacy datasets and to run the legacy characterization tests.

### Folder layout

Every path in the scripts and configuration is relative to this repository. Clone the two editions side by side:

```text
<workspace>\
  STERP-App\                      legacy edition (dataset build, conversion files in data\legacy, live legacy run folder)
  STERP-App-Modern\               this repository
  run\STERP-App-isolated-tests\   created by the scripts: dataset backups, isolated databases, job output
```

Set `STERP_ISOLATED_ROOT` to keep the isolated-test area somewhere else. The hosts' folder settings
(`Sterp:Jobs:OutputRoot`, `Sterp:Jobs:LegacyRunRoot`, `Xmd:LegacyRoot`) default to this layout and can be overridden
([docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) section 3).

## Quick start

Trust the localhost HTTPS certificate once (`dotnet dev-certs https --trust`). Then double-click `run-sterp.bat` in the
repository root, or run it from a terminal:

```bat
run-sterp.bat              :: build, start the four services, wait until they are ready, open the GUI
run-sterp.bat /status      :: show which services are up and ready
run-sterp.bat /stop        :: stop the services (refuses while batch jobs run; /force stops anyway)
run-sterp.bat /restart     :: stop, rebuild and start again
run-sterp.bat /reset       :: stop, restore the demo data, start again (many demo steps are one-shot)
run-sterp.bat /?           :: every option, including /nobuild and /nobrowser
```

The first start creates a private LocalDB instance, `STERPA08_demo`, from the demo dataset and maps your Windows login to
SYSADM. The four services run in the background without windows, against that instance only, on ports 7140-7143. Their
logs are written to `hosts\<service>-<port base>.log` in the instance folder under `..\run\STERP-App-isolated-tests\`.
The work is done by `scripts/run-sterp.ps1`, which also takes `-Name` and `-PortBase` to run a second, separate demo
environment.

Then open:

- GUI: <https://localhost:7140>. You are signed on as SYSADM (System Administrator), who can use "Continue as" to work as
  any other STERP user, as in the legacy client.
- API Console: <https://localhost:7143>
- Health: `https://localhost:7141/health/ready` (Api), `https://localhost:7142/health/ready` (Jobs)

The same steps by hand, from the repository root:

```powershell
dotnet build STERP.Modernized.slnx -c Release                              # warnings are errors; locked restore

# 1. a private LocalDB instance (STERPA08_demo) with the demo dataset; your Windows login becomes SYSADM
./scripts/new-isolated-env.ps1 -Name demo -Dataset demo -MapLoginTo SYSADM

# 2. start the four hosts on ports 7140-7143 (prints the process ids) and run the smoke check
./scripts/run-local.ps1 -Instance STERPA08_demo -PortBase 7140
```

`run-sterp.bat /stop` also stops the hosts that `run-local.ps1` started; otherwise close its four minimized PowerShell
windows, or stop each printed process id and its `dotnet` child with `Stop-Process -Id <id>`. Before you stop STERP-JOBS,
let its running jobs finish (`run-sterp.bat /stop` checks this for you). A stopped job host marks its interrupted runs
`LOST` on the next start, and `core.JobRun` keeps them `RUNNING`, as when a legacy batch process is killed.

Notes:

- Use your own instance name and port base. `STERPA08_aa6a44e4` and ports 7100-7303 are used by the verification
  scripts, and every test scenario restores the database it runs on.
- `-Dataset fresh` (the default) restores the empty deploy. Without `-MapLoginTo`, a Windows login that is not mapped
  to a STERP user gets 403 `API0010`.
- Every script refuses any server other than a `(localdb)\STERPA08_*` instance, and the hosts run with
  `Sterp:Database:IsolatedOnly=true`. They also use a scratch legacy run root per instance and refuse the live legacy
  folder `..\STERP-App\run`.

## Demo workflows

The six scripted demonstrations of the legacy edition run unchanged in the modernized GUI. They use the same companies,
users, menus, keys and figures: [docs/DEMO-WORKFLOWS.md](docs/DEMO-WORKFLOWS.md).

| # | Company / site | Story |
|---|---|---|
| 1 | STC / RDG | Order to cash on a legacy core |
| 2 | STC / RDG | Engineer to build: BOM, kit and cost |
| 3 | STC / BHM | Procure to pay with controls |
| 4 | CAL / GLA | Second company and data migration |
| 5 | CAL / LIV | Credit hold and site security |
| 6 | either STC site | Close the month |

The same document lists the demo users, how to switch between them and the suggested running order for each audience.
[docs/DEMO-DATA.md](docs/DEMO-DATA.md) explains where the dataset comes from and how to reset it.

## Other ways to run

```powershell
# one host in the foreground (Api | Jobs | Web | Portal), e.g. with staged delivery: slices listed here answer 501
./scripts/run-host.ps1 -Name Api -Instance STERPA08_demo -PortBase 7140 -Undelivered STERP-CST

# smoke check of running hosts (health, readiness, OpenAPI index, GUI, portal, Windows authentication)
./scripts/smoke.ps1 -PortBase 7140 -Authenticated

# publish the four deployables + release manifest (commit, clean tree, SDK, SHA-256 of every file)
./scripts/publish.ps1                                                      # -> artifacts\publish
```

Configuration keys (database, folders, CORS, undelivered slices, job poll wait, OpenTelemetry) are listed in
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) section 3.

## Running the tests

| Suite | Command |
|---|---|
| Target parity (API, jobs, contracts, operations) | `./scripts/run-parity.ps1 -Tier target -Instance STERPA08_demo -PortBase 7140 -ResultXml TestResults/target.xml -- -trait "application=STERP-ITM"` |
| GUI parity (Playwright, Edge) | `./scripts/run-parity.ps1 -Tier gui -Instance STERPA08_demo -PortBase 7140 -ResultXml TestResults/gui.xml -- -trait "application=STERP-SO"` |
| Legacy characterization | `./scripts/run-parity.ps1 -Tier legacy -Instance STERPA08_demo` (needs the isolated legacy runtime described in [tests/README.md](tests/README.md)) |
| Earlier waves and the characterization configuration | `./scripts/run-wave-transitions.ps1 -Instance STERPA08_wave -PortBase 7300 -ResultDir TestResults/waves` |
| Kernel suite (COBOL semantics, kernel services) | `./scripts/new-isolated-env.ps1 -Name kernel` once, then `dotnet test verification/STERP.Kernel.Tests -c Release` (server from `STERP_KERNEL_TEST_SERVER`, default `(localdb)\STERPA08_kernel`) |

The hosts must be running for the parity tiers, and `-NoBuild` skips the test build. Each scenario restores its
dataset, so the demo data is reset whenever you run tests on that instance. The scenarios, contracts and fixtures under
`tests/` form the parity safety net and are never edited to make a test pass ([tests/README.md](tests/README.md)).
Test results (`TestResults/`, `out/`, `*.trx`) are not committed.

**Reference paths in the frozen contracts.** The binding contracts (`contracts/`, `tests/contracts/`) and some parity
scenarios contain example and expected paths of the reference workstation (`C:\SourceCode\STERP-App-Modern\run\...`,
`C:\SourceCode\STERP-App\run\...`). They are SHA-256-pinned and served byte for byte, so they are kept as they are. Six
platform scenarios (FR-STERP-PLATFORM-026 AC1 and AC3, 028 AC3 and AC4, 044 AC3, 051 AC1) assert the job output path
itself. They pass only with this repository at `C:\SourceCode\STERP-App-Modern` and the hosts started with
`-OutputRoot` set to its `run` folder, as in the reference verification.

## Authorization

Building, publishing and running against an isolated LocalDB instance are verification activities. Starting the hosts
against a shared `STERP` database (server `.`), merging to a release line, deploying, cutting over and retiring the
legacy application each need a governance decision and explicit human approval. Once approved, the deployment
command is `./scripts/run-local.ps1 -SharedDatabase -ApprovalReference <approval id>`, which starts the published release
with its binding production configuration and refuses to start without an approval reference. The runbook is
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## Operational notes

- **Coexistence.** The legacy client, COBOL engine, batch executables, job streams, operator console and SQL Agent jobs
  keep running. The modern app schedules nothing, and target runs register in `core.JobRun` exactly like legacy runs.
- **RR-04.** Never run the same posting program or stream in both systems for the same period (MONTHEND, YEAREND,
  CSTINVAL POST, ...).
- **RR-03.** Restart the legacy engine after editing a calendar through the modern app (legacy DATECALC cache).
- **RR-02.** `TrustServerCertificate=True` is accepted only for the local SQL Server certificate.
- **RR-01.** Each user runs the services under their own account, so audit attribution matches legacy.
- **Rollback.** List a slice in `Sterp:Host:UndeliveredSlices` to withdraw it (it answers 501 and the GUI shows "use the
  legacy client"). For a full rollback, stop the four hosts. The app made no schema change and wrote only legacy-format
  rows, so the legacy application keeps working.

## Repository layout

```text
run-sterp.bat    one-step demo launcher: start, stop, restart, reset and status of the four services
contracts\       binding contracts served by the hosts: 13 OpenAPI documents, frozen data contract, GUI routes,
                 SQL object mapping
docs\            deployment runbook, developer guide, demo workflows and demo dataset
scripts\         isolated LocalDB instances, host start/stop, smoke check, parity runs, publish
src\Hosts\       STERP.Api, STERP.Jobs, STERP.Web (+ STERP.Web.Client), STERP.ApiPortal
src\Modules\     one project per slice (Ap Ar Bom Cst Fin Itm Kit Platform Po So Xmd) + Composition
src\Platform\    STERP.Platform.Kernel (COBOL semantics, kernel services), STERP.Platform.Hosting (pipeline, jobs, health)
src\Web\         STERP.Web.Framework and the Blazor page library of each slice
tests\           parity safety net: scenarios (cases\), contract copies, harness, legacy golden-master fixtures
tools\           contract and traceability helpers
verification\    kernel test suite
```

## Documentation

| Document | What is in it |
|---|---|
| [docs/DEMO-WORKFLOWS.md](docs/DEMO-WORKFLOWS.md) | the six demonstrations step by step, the demo users and which company/site to use for which story |
| [docs/DEMO-DATA.md](docs/DEMO-DATA.md) | the two datasets, how to build the backups from the legacy repository, restore and reset them |
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | deployment, configuration, coexistence, rollback and operations runbook |
| [docs/DEVELOPER-GUIDE.md](docs/DEVELOPER-GUIDE.md) | solution layout, how an operation is served, COBOL semantics helpers, jobs, GUI pages and the slice workflow |
| [tests/README.md](tests/README.md) | the parity safety net: tiers, isolation, scenario DSL and fixtures |
| [STERP-App](https://github.com/stefanatanasiu/STERP-App) | the legacy edition: architecture, tech-debt register, data model, module guides |

## Licence

STERP is released under the [MIT Licence](LICENSE). Packages restored from NuGet (for example ASP.NET Core, SqlClient,
Dapper, OpenTelemetry, YamlDotNet, xUnit and Playwright) are covered by their own licences.
The legacy edition, GnuCOBOL, SQL Server and Microsoft Edge are not part of this repository.
