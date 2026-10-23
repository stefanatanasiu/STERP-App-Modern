# STERP modernized - deployment, coexistence and rollback

This runbook covers how the four modernized deployables are built, configured, started, verified, run beside the
legacy application and rolled back. It implements Agent 06 contracts sections 1, 5, 6.10, 8 and 10
(`01-shared-platform-contracts.md`, kept with the evidence package outside this repository).

> **Authorization.** Building, publishing and running against an isolated LocalDB instance are verification
> activities. Starting the hosts against the shared `STERP` database (server `.`), merging, cutover and legacy
> retirement are **not** authorized by the implementation package: each needs an Agent 09 decision and explicit
> human approval. Legacy coexistence is `legacy-remains-active`; no cutover or retirement is planned in this release.

## 1. Deployables

| Target application | Project | URL (binding default) | Role |
|---|---|---|---|
| STERP-WEB | `src/Hosts/STERP.Web` (+ `STERP.Web.Client`, WebAssembly) | `https://localhost:7100` | GUI shell and slice pages; calls Api/Jobs from the browser with the user's Windows credentials |
| STERP-API | `src/Hosts/STERP.Api` | `https://localhost:7101` | every online module API (11 slices composed in one process) |
| STERP-JOBS | `src/Hosts/STERP.Jobs` | `https://localhost:7102` | job submission, monitoring, restart, SYSOUT/spool; runs batch programs and streams in-process |
| STERP-PORTAL | `src/Hosts/STERP.ApiPortal` | `https://localhost:7103` | API Console: catalogue, operation pages, "Try it out" with Windows authentication, contract-drift check, guide |

All four run on this machine (HD-03) under the Windows account that starts them (HD-14/D4: each user runs the services
under their own account, so audit attribution matches legacy; RR-01 accepted for that local use).

Prerequisites: .NET 10 (ASP.NET Core) runtime 10.0.x; a trusted HTTPS certificate for `localhost`
(`dotnet dev-certs https --trust` locally); Windows authentication (Kerberos/NTLM) for the users; network access to
SQL Server `.` with Integrated Security for those users. No ODBC driver, COBOL runtime or GnuCOBOL is needed.

## 2. Build and publish

```powershell
dotnet build STERP.Modernized.slnx -c Release      # locked restore (packages.lock.json), warnings as errors
./scripts/publish.ps1                              # -> artifacts\publish\<deployable>\ + release-manifest.json
```

`release-manifest.json` records the commit, whether the working tree was clean, the SDK, and the SHA-256 of every
published file. It is the release evidence for an approved deployment: deploy only a manifest built from a clean tree.

## 3. Configuration

Each host reads `appsettings.json` and environment variables (`Section__Key`). There are no secrets. A relative folder
setting is resolved against the host's content root: the project folder `src/Hosts/<host>` when it runs from source, the
deployable's folder when it is published (`artifacts/publish/<host>` by default; both are three levels below the
repository root).

| Setting | Binding default | Notes |
|---|---|---|
| `Sterp:Database:ConnectionString` | `Server=.;Database=STERP;Integrated Security=True;Encrypt=Mandatory;TrustServerCertificate=True` | `Application Name` is set to `STERP.Api` / `STERP.Jobs` by the host (contracts 5.6). `TrustServerCertificate` accepts the local self-signed certificate (RR-02). Unless the string sets them, the hosts also use `Pool Blocking Period=NeverBlock` and `Connect Retry Interval=1`: while the database is unavailable requests answer within seconds (menu empty, status DEGRADED, console self-test RC 8, as legacy), and the first request after it is back succeeds. |
| `Sterp:Database:IsolatedOnly` | `false` | `true` makes the host refuse the shared server; set by every verification script. |
| `Sterp:Jobs:OutputRoot` | `../../../run` (this repository's `run` folder) | spool, logs, work, bacs, mtd, einvoice, vat (legacy `RUNPATH` layout), created on demand. Paths in responses are absolute. |
| `Sterp:Jobs:LegacyRunRoot` | `../../../../STERP-App/run` (the legacy clone next to this repository) | legacy spool/SYSOUT recorded in `core.JobRun` under this root are shown read-only. The isolated verification scripts set it to a per-instance scratch folder. |
| `Xmd:LegacyRoot` | `../../../../STERP-App` | the legacy application folder that a relative CONVLOAD `FILE=` (for example `data\legacy\CALAROPN.DAT` of the XMDCONV stream) is read from (D-XMD-08). |
| `Sterp:Jobs:MaxConcurrentRuns` | `0` | no limit, like the legacy launcher (FR-STERP-PLATFORM-057). |
| `Sterp:Jobs:RunPollWaitMilliseconds` | `1500` | `jobs_getRun` of a run still executing in the job host waits up to this long for it to end, so pollers (GUI, clients) read the final state as soon as it exists instead of one poll interval later; `0` answers at once. |
| `Sterp:Host:CorsOrigins` | Web and portal origins | credentialed CORS only from these origins (contracts 5.7). |
| `Sterp:Host:UndeliveredSlices` | empty | slices listed here answer 501 `API0501` (GUI placeholder, portal "planned"): staged delivery and per-slice rollback (section 6). |
| `Sterp:Web:ApiBaseUrl`, `Sterp:Web:JobsBaseUrl` | `https://localhost:7101`, `:7102` | browser targets of the GUI (same keys under `Sterp:Portal` for the portal). |
| `Kestrel:Endpoints:Https:Url` | per deployable | HTTPS only; HTTP redirects to HTTPS. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | when set, traces, metrics (meter `STERP`) and logs are exported over OTLP. |

## 4. Start, verify, stop

```powershell
./scripts/run-local.ps1                                   # isolated LocalDB instance (verification/demonstration)
./scripts/run-local.ps1 -SharedDatabase -ApprovalReference <Agent 09 + human approval id>   # approved deployment only
./scripts/smoke.ps1 -Authenticated                        # health, readiness, OpenAPI index, GUI, portal, Windows auth
```

For a demonstration with the Agent 07 demo dataset and your Windows login mapped to SYSADM, run `run-sterp.bat` in the
repository root (README "Quick start"): it provisions the isolated instance `STERPA08_demo` on the first start, starts
the four hosts on ports 7140-7143 in the background and checks your sign-on; `/stop`, `/restart`, `/reset` and `/status`
manage them. By hand: `./scripts/new-isolated-env.ps1 -Name demo -Dataset demo -MapLoginTo SYSADM`, then
`./scripts/run-local.ps1 -Instance STERPA08_demo -PortBase 7140`.

`run-local.ps1` prints the process ids; stop with `Stop-Process -Id <id>` (or `run-sterp.bat /stop` on ports 7140-7143).
Before stopping `STERP.Jobs`, check the job monitor for runs it started that are still `RUNNING` (`run-sterp.bat /stop`
refuses while the job host reports executing runs): a stopped host loses its in-memory run manifests (contracts 8.3,
FR-STERP-PLATFORM-057) and the `core.JobRun` row stays `RUNNING`, as when a legacy batch process is killed.

`/health/ready` (Api and Jobs) reports: the database probe (legacy `PING`: `ENGINE AND DATABASE AVAILABLE`), the schema
drift check of every delivered slice against the frozen data contract (`contracts/frozen-schema.json`; a missing or
changed object makes the host `Unhealthy`, 503), the output folders and the delivered slices.

## 5. Coexistence with the legacy application (legacy-remains-active)

- The legacy WinForms client, the COBOL engine, batch executables, the nine job streams, the operator console and both
  SQL Agent jobs keep running against the same database. **The target schedules nothing**; scheduling stays legacy
  (contracts 6.10, HD-05).
- The database is shared in place: no migration, `EnsureCreated`, seeding or DDL exists in the target; it uses the
  legacy procedures, tables, `rowVersion` columns, application locks and idempotency keys (contracts 6). Readiness
  fails closed on schema drift.
- Target job runs register in `core.JobRun` through `core.usp_JobRunStart`/`usp_JobRunEnd`, so the legacy Job Monitor
  and STERPOPS show them and the target job monitor shows legacy runs.
- **Operational rule (RR-04):** never run the same posting program or stream in both systems for the same period or
  range (MONTHEND, YEAREND, SUPPPERF, DDCOLL, CONVLOAD, XMDCLOSE COMMIT, CSTINVAL POST, ...). The legacy idempotency keys
  prevent some duplicates, not all.
- **RR-03:** a running legacy process keeps its DATECALC holiday cache; after editing a calendar through the target,
  restart the legacy engine before relying on its date arithmetic.
- Spool files of target runs are written under the target output root; legacy listings stay where legacy wrote them.

## 6. Staged delivery and rollback

- **Per-slice withdrawal.** Add the slice to `Sterp:Host:UndeliveredSlices` (Api, Jobs, Web and Portal) and restart the
  hosts. Its operations answer 501 `API0501` naming the slice and wave, its pages show the "Not yet available in the
  modernized application - use the legacy client" placeholder, and users return to the legacy client for it. Nothing
  in the database changes.
- **Full rollback.** Stop the four hosts. Because the target made no schema change and writes only through the legacy
  procedures and table formats, every row it wrote remains valid for the legacy application, which keeps running
  unaffected. Finish or let fail any target job runs first (section 4).
- **Data written by the target** is not rolled back automatically: it is business data in the legacy format, owned
  from then on by the legacy processes, exactly as if it had been entered in the legacy client.

## 7. Operations

- Logs: JSON console logs with `traceId`, user, company, site, operation id, legacy program, return/reason code and
  elapsed time; bodies are not logged and sensitive fields are never written (contracts 5.9).
- Metrics: meter `STERP` (operation count, latency, return-code distribution); traces: GUI -> API -> SQL.
- Suggested alerts: readiness `Unhealthy` (database probe or drift), a rise of RC 12 / HTTP 500 outcomes, job runs
  ending `FAILED`/`ABENDED`.
- Support diagnostics: the *Service diagnostics* page (version, health, drift result, delivered slices and waves) and
  the operator console page.
- Backup and recovery are unchanged and owned by the legacy deployment (`SIMPLE` recovery, contracts 10).

## 8. Residual risks carried

| ID | Risk | Treatment |
|---|---|---|
| RR-01 | audit triggers fall back to the SQL login when several users share one service account | services run under each user's own account (D4); no shared service account in this release |
| RR-02 | `TrustServerCertificate=True` for the local self-signed SQL Server certificate | accepted locally; use a trusted certificate before any shared deployment |
| RR-03 | legacy DATECALC caches stay stale after target calendar edits | restart the legacy engine after calendar maintenance |
| RR-04 | the same stream run in both systems for the same period | operational rule (section 5) |
