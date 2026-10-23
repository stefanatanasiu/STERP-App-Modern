# STERP application parity safety net (Agent 07)

Lifecycle run `lr-20261017T115103Z-ee5732ce`, attempt `a07-20261020T154730Z-b88d28f6`, scope `estate`.
Evidence package: the Agent 07 evidence package (oracle register, coverage, results, manifest), kept outside this
repository.

## Two tiers

| Tier | Project | Runs against | Expected state now |
|---|---|---|---|
| Legacy characterization | `legacy/STERP.Legacy.Characterization.Tests` | the isolated legacy runtime built from commit `00631916` (`..\run\STERP-App-isolated-tests\a07-b88d28f6\legacy`, next to this repository) and its separate LocalDB instance | pass |
| Target parity | `parity/STERP.Target.Parity.Tests` (API, jobs, contracts, data, operations) and `parity/STERP.Target.Gui.Parity.Tests` (GUI journeys, Playwright on the installed Edge) | the modern hosts (`STERP_TARGET_*_URL`, defaults `https://localhost:7100-7103`) and the isolated database | red until Agent 08 delivers each slice (`RED-UNTIL-IMPLEMENTED: ... not reachable`) |

Target parity scenarios are data: `cases/<APP>.cases*.json` (DSL `sterp-parity-cases/1`, one scenario per Agent 06
acceptance criterion plus edge/coexistence scenarios). They bind only to the binding contracts copied in `contracts/`
(hash-checked against `contracts/binding-manifest.json` at every run). Do not edit cases or contracts to make a test
pass; a binding change needs a new Agent 06 attempt and an Agent 07 rerun.

## Run

```powershell
dotnet build tests/STERP.Parity.Tests.slnx -c Release
# legacy tier (needs the isolated legacy runtime and backups of this attempt)
tests/legacy/STERP.Legacy.Characterization.Tests/bin/Release/net10.0/STERP.Legacy.Characterization.Tests.exe -result-xml legacy.xml
# target tier (API/jobs/contract/operations, then GUI)
tests/parity/STERP.Target.Parity.Tests/bin/Release/net10.0/STERP.Target.Parity.Tests.exe -result-xml target.xml
tests/parity/STERP.Target.Gui.Parity.Tests/bin/Release/net10.0/STERP.Target.Gui.Parity.Tests.exe -result-xml gui.xml
```

`dotnet test` also works (VSTest adapter). Filter one slice with `-trait "application=STERP-ITM"`.

## Isolation

Every database statement goes to `STERP_PARITY_SQLSERVER` (default `(localdb)\STERPA07_b88d28f6`). The runner refuses
server `.`/`(local)`/`localhost`/the machine name and any default instance, so the shared `STERP` database (profile
`target.shared_database`) is never contacted. Each scenario restores its dataset (`freshc` = `build-db.ps1` deploy,
`democ` = plus `build/demo-data.ps1`) from `STERP_PARITY_BACKUPS` before it runs.

| Variable | Default |
|---|---|
| `STERP_TARGET_API_URL`, `STERP_TARGET_JOBS_URL`, `STERP_TARGET_WEB_URL`, `STERP_TARGET_PORTAL_URL` | `https://localhost:7101`, `:7102`, `:7100`, `:7103` |
| `STERP_PARITY_SQLSERVER`, `STERP_PARITY_DATABASE`, `STERP_PARITY_BACKUPS` | isolated instance of this attempt, `STERP`, `..\run\STERP-App-isolated-tests\a07-b88d28f6\backups` |
| `STERP_LEGACY_ROOT`, `STERP_GNUCOBOL`, `STERP_SQLCMD_DIR` | isolated legacy export (`..\run\STERP-App-isolated-tests\a07-b88d28f6\legacy`), `..\GC32-BDB-SP1`, ODBC 18 sqlcmd (`%ProgramFiles%\Microsoft SQL Server\Client SDK\ODBC\180\Tools\Binn`) |
| `STERP_CHARACTERIZATION_CAPTURE=1` | re-pins legacy fixtures (oracle capture run only; never during verification) |
| `STERP_PARITY_JOB_TIMEOUT_SECONDS` | `600`: bound of a `job` step's poll and of the suite-isolation wait at the end of each scenario |
| `STERP_TARGET_OUTPUT_ROOT` | none: the hosts' `Sterp:Jobs:OutputRoot`; needed by `file` steps with root `target-output` and by `${outputRoot}` |
| `STERP_TARGET_LEGACY_RUN_ROOT` | none: the hosts' `Sterp:Jobs:LegacyRunRoot`, the isolated stand-in for the legacy run root `..\STERP-App\run`; needed by `file` steps with root `legacy-run` and by `${legacyRunRoot}`; anything below the legacy application folder `..\STERP-App\` is refused |
| `STERP_TARGET_RESTART_COMMAND` | none: PowerShell command that kills one host and starts it again (`{host}` = `Api`, `Jobs`, `Web`, `Portal`), exiting 0 once it is ready; needed by the `fault` steps `jobs-host-restart` / `restartHost` |

`scripts/run-parity.ps1` (target repository) exports the last three from the isolated instance it runs against (they must
name the configuration the running hosts were started with; pass `-OutputRoot`/`-LegacyRunRoot` when the hosts were
started with overrides). A scenario that needs an unset variable fails with a message naming it.

## Scenario DSL additions (amendment `agent-07-20261021T211031Z-a544e35d`)

The DSL (`sterp-parity-cases/1`, Agent 07 `work/authoring/SCENARIO-DSL.md`) keeps every existing key and meaning. Added:

- **`file` step** (arrange, not an action): writes one file below a configured target root before the actions and
  removes it after the scenario.
  `{"file": {"root": "target-output" | "legacy-run", "name": "<relative path>", "lines": [...] | "content": "..." |
  "fixture": "<path below parity/STERP.Target.Parity.Tests/fixtures>", "sha256": "<hex, fixture only>",
  "encoding": "windows-1252" | "ascii" | "utf-8", "newline": "crlf" | "lf"}}` - `lines` are each written followed by the
  newline, `content` as given with its line breaks normalised, a fixture byte for byte after its sha256 is checked.
  Defaults: root `target-output`, encoding `windows-1252` (contracts 4.2), newline `crlf`. A name that leaves the root
  is refused. Saved value `{path, root, sizeBytes, sha256}`.
- **`fault` step** (arrange, closed vocabulary, isolated instance only - after the isolation check - and always undone
  at the end of the scenario): `"database-offline"` / `"database-online"` (ALTER DATABASE SET OFFLINE WITH ROLLBACK
  IMMEDIATE / SET ONLINE; after it is ONLINE again the runner waits until the API and job hosts answer `/health/ready`
  200), `{"renameObject": "schema.name"}` and `{"renameColumn": "schema.table.column"}` (sp_rename to `<name>_A07FAULT`,
  renamed back afterwards, or the dataset restored if that fails), `{"lockTable": "schema.table"}` (an exclusive table
  lock held in an open transaction on a separate connection until `"release-locks"` or the end of the scenario, so
  writers of that table wait; ordinary reads are not blocked under READ_COMMITTED_SNAPSHOT), `"jobs-host-restart"` /
  `{"restartHost": "Api" | "Jobs" | "Web" | "Portal"}` (runs `STERP_TARGET_RESTART_COMMAND`). `sql` steps still may
  not contain DDL; these faults are the only schema or availability changes a scenario can arrange.
- **Placeholders** `${outputRoot}` and `${legacyRunRoot}` expand to the two roots in every string of a step or an
  assertion (single quotes doubled inside `sql`, regex-escaped inside `matches`); any other `${name}` is refused.
- **Saved-value references** `${<save>:<json path>}` (for example `"query": {"path": "${files:$.items[0].path}"}`)
  insert one value of a result saved by an *earlier* step into a later step or an assertion. The path is applied as a
  `json` assertion applies it (to the parsed body of an HTTP result, otherwise to the saved value itself) and must select
  exactly one non-null value; its text is inserted with the same encoding rules as the placeholders. A reference to a
  name no earlier step saves is refused before the scenario runs. Typical use: read a listing through
  `jobs_getSpoolFile` at the path `jobs_listSpoolFiles` just returned.
- **`bodyContains`** compares unescaped text: the response text, any string value of the parsed body or the body
  serialized without HTML-safe escaping; for job results, sql rows and GUI results any string value inside them (for
  example the SYSOUT) or their JSON text (escaped as before, or relaxed). `<`, `>`, `&` and `'` match as written.
- A **`job` step** also stops polling when the run's execution state is `LOST` (job host restarted during the run).

### GUI steps (`runner: "gui"`)

`gui` opens the route in a new browser session (Playwright, installed Edge) and performs `actions` in order.

- *Acting user.* The browser carries the Windows identity, so the acting user of a GUI step is the STERP user that
  identity is mapped to for the step: the step's `as`, else `context.actor`, else `context.login`. After every GUI step
  the runner maps the identity back to `context.login`, so later `call`/`job` steps act as authored (`X-STERP-Run-As`
  only from a SYSADM login). Legacy *Continue as User* (`frmLogin.vb:46`, SYSADM only) is available in a step whose
  acting user is SYSADM (`"as": "SYSADM"`).
- *Business date.* With `context.businessDate` the browser clock starts at that date (local noon) before the route
  opens and keeps flowing (Playwright clock install), as the legacy sign-on takes `Date.Today` as the business date
  (`frmLogin.vb:31`). A frozen clock stalls pages that poll or time out.
- *Labels.* `fill`/`select` match the accessible label exactly (case-sensitive, whole string, trimmed); only when no
  exact match exists does a case-insensitive substring match apply, and it must be unique. Several exact matches (the
  same caption in two open windows) resolve to the last visible one (the window opened last).
- *Keys.* `press` takes F1-F12, Enter, Tab, Home, End, Backspace, `Esc` (Escape), `Del`, `Ins`, `PgUp`, `PgDn`,
  `Return`, `Up`/`Down`/`Left`/`Right` (arrows) and modifiers `Ctrl`, `Shift`, `Alt` joined with `+` (`Ctrl+K`,
  `Ctrl+Shift+D`, `Alt+N`); other Playwright key names pass through.
- *Stubbed response.* `{"respond": {"operation": "<operationId>", "status": <int>, "body": <json>, "contentType": "<optional>"}}`
  answers every browser call of that binding operation (method and path template of the OpenAPI document) with the
  given status and body for the whole step (registered before the route opens; CORS preflights and other methods reach
  the host; content type `application/problem+json` for status >= 400 unless given). Use only binding examples or
  schema-conformant bodies.
- *Saved page.* `{url, title, text, focused, shown}`. `text` is the visible page text followed by the displayed value
  of every visible input, select (selected option text) and text area. `shown` lists, in order, every text the page
  wrote while the step ran (texts of changed or replaced elements, including replaced values). A message that a later
  list reload overwrites (legacy `ListForm.Populate` '<n> record(s)') is asserted with
  `{"on": "<save>", "json": "$.shown[*]", "contains": "<text>"}`; `text`/`notText` assert the final page.

## Suite isolation

Every scenario restores its dataset, so a job run a scenario left executing could write into the next scenario's
data. At the end of each scenario (after its assertions, after faults are undone and before staged files are removed)
the runner collects the runs the scenario started - `job` steps, `call` responses carrying a `jobRunId` or a
`/api/jobs/v1/runs/{id}` Location, and every `core.JobRun` row above the restored dataset's highest id (runs started
indirectly or through the GUI) - and waits, through `jobs_getRun`, while a run's execution state is `ACCEPTED` or
`RUNNING`, bounded by `STERP_PARITY_JOB_TIMEOUT_SECONDS`. A run still executing after that bound fails the scenario with
a message naming it. Rows without a target execution block (dataset rows, arranged rows) and `ENDED`/`LOST` runs are
not waited for; nothing is cancelled (the contracts have no cancellation). Pass/fail of the assertions is unchanged.

## Fixtures

`parity/STERP.Target.Parity.Tests/fixtures/` holds files captured from attributable legacy runs (for example
`legacy-run/spool/RECONCTL-STC-20261021230858.lst`, the listing of `RECONCTL.exe CO=STC ASAT=20261031 MODE=PREVIEW` built
from `00631916` on the demo dataset). A scenario copies one with a `file` step that pins its sha256; the origin of each
fixture is recorded in the Agent 07 amendment register.

## Who changes scenarios

Expected values come from the legacy oracle (pinned legacy source, attributable isolated legacy runs, the restored
datasets) and the binding Agent 06 contracts, never from target output. Scenario and fixture corrections are made only
by Agent 07 against legacy evidence and recorded with the evidence in its register; a target run is used only to check
that a corrected scenario executes.
