# How to call the STERP APIs

This page reproduces sections 4 (HTTP API conventions) and 5 (security model) of the shared platform contracts
(`01-shared-platform-contracts.md`, Agent 06 attempt `a06-20261020T112437Z-034eff00`), so that return codes, reason
codes and headers can be understood without opening the design package.

## Calling an operation from the command line

Every operation requires Windows authentication (section 5.1). The API Console shows three ready-to-run command lines
for each request prepared or executed with "Try it out", including the context headers, the `Idempotency-Key` and the
JSON body:

| Shell | Command line |
|---|---|
| bash / Git Bash | `curl --negotiate -u : -X 'GET' 'https://localhost:7101/api/platform/v1/session' -H 'accept: application/json' -H 'X-STERP-Company: STC'` |
| Windows (Command Prompt or PowerShell) | `curl.exe --negotiate -u : -X GET "https://localhost:7101/api/platform/v1/session" -H "accept: application/json" -H "X-STERP-Company: STC"` |
| PowerShell | `Invoke-RestMethod -UseDefaultCredentials -Method Get -Uri 'https://localhost:7101/api/platform/v1/session' -Headers @{ 'accept' = 'application/json'; 'X-STERP-Company' = 'STC' }` |

The hosts use the ASP.NET Core development certificate locally (section 5.8); trust it once with
`dotnet dev-certs https --trust`. An operation of a slice that is not yet delivered answers `501` with reason code
`API0501`; a Windows login without an STERP user answers `403` with `API0010`.

## 4. HTTP API conventions

### 4.1 Addressing and versioning

- Online operations: `https://<api-host>/api/{segment}/v1/...`; batch/job operations:
  `https://<jobs-host>/api/{segment}/v1/jobs/...` and the generic job resources
  `https://<jobs-host>/api/jobs/v1/runs/...` (section 8). Each OpenAPI document declares both servers and
  uses path-level `servers` for job paths.
- Resources are plural kebab-case nouns keyed by the **legacy business key** (for example
  `/sales-orders/{orderNumber}`), never by a target-invented surrogate. Legacy verbs that are not CRUD
  (`ACCEPT`, `POST`, `RESERVE`, `RELEASE`, `PREVIEW`, ...) are `POST` sub-resources named after the verb
  (`/sales-orders/{orderNumber}/accept`).
- Version `v1` changes only additively (new optional fields, new operations). Removing or retyping is a
  binding change.

### 4.2 Representation

- JSON (`application/json; charset=utf-8`), camelCase names derived from the legacy field or column name
  (`SA-ORDER-NUMBER` -> `orderNumber`, `CustomerCode` -> `customerCode`); errors use
  `application/problem+json`; spool and SYSOUT content uses `text/plain; charset=windows-1252` as stored.
- Code-valued fields are strings carrying the **legacy code values unchanged** (`'ACCEPTED'`, `'Y'`/`'N'`,
  reason codes), declared as `enum` only where the legacy domain is closed and cited.
- Absent and `null` mean "not supplied". Responses omit nothing that the legacy screen or result set
  shows; columns are emitted even when null.

### 4.3 Data types

| Legacy definition | JSON / OpenAPI | Rule |
|---|---|---|
| `PIC X(n)`, `varchar(n)`, `char(n)` | `string`, `x-sterp-length: n` | output right-trimmed of trailing spaces; leading spaces preserved; input normalized per 4.4 |
| `PIC 9(n)`/`S9(n)` without scale, `int`, `bigint` | `integer` (`int32`/`int64`) | |
| `PIC S9(p)V9(s)`, `COMP-3`, `decimal(p,s)`, `money` | `number`, `format: decimal`, `x-sterp-digits`, `x-sterp-scale` | serialized with exactly `s` decimal places (System.Text.Json writes the `decimal` scale); never `double` in .NET code |
| `PIC 9(8)` dates `yyyyMMdd`, `date` | `string`, `format: date` (`yyyy-MM-dd`) | `00000000`/spaces -> `null` |
| `datetime2`, `datetime` | `string`, `format: x-sterp-local-date-time` (`yyyy-MM-ddTHH:mm:ss.fffffff`, no offset) | values are SQL Server local time as stored; no time-zone conversion |
| `rowversion` | `string`, `format: byte`, name `rowVersion` | opaque base64 |
| `bit` | `boolean` | |
| OCCURS n with a used-count field | `array`, `maxItems: n` | the count field is implied by array length |

### 4.4 Legacy input normalization (parity)

Operations whose legacy implementation passed input through a COMMAREA apply, at the API boundary and
before any business logic, exactly the legacy marshalling of `CobolCodec`
(`dotnet/STERP.CobolInterop/CobolCodec.cs:15-100`) [observed]:

| Field type | Legacy behaviour (parity, binding) | Citation |
|---|---|---|
| alphanumeric | encode to Windows-1252 (unrepresentable characters become `?`), right-truncate to the field length, space-pad | `CobolCodec.cs:15,37-43` |
| numeric with scale | round half away from zero to the field scale | `CobolCodec.cs:24-29` |
| numeric too large | keep the low-order digits (high-order truncation) | `CobolCodec.cs:76-79` (zoned), packed equivalent |
| unsigned numeric | store the absolute value | `CobolCodec.cs:71-86` |
| dates | `yyyyMMdd` | `CommArea.cs:72-84` (Agent 05 `03-dotnet...` section 2.2) |

Operations whose legacy implementation passed input to a stored procedure through `SqlHelper` apply the
legacy parameter rules: an empty string becomes SQL `NULL` (`dotnet/STERP.Data/SqlHelper.cs:33`), a name
without a space is a procedure and text containing a space is ad-hoc SQL
(`SqlHelper.cs:51`), command timeout 120 s (`SqlHelper.cs:18`) [observed]; the target calls the same
procedures with the same parameter names so SQL Server applies the same implicit conversions.
Stricter validation (reject instead of normalize) is proposed as behaviour changes BC-001..BC-004
(section 13) and applies only when approved.

### 4.5 Context headers

| Header | Legacy source | Rule |
|---|---|---|
| `X-STERP-Company` | `SterpSession.CompanyCode` -> `CA-COMPANY`, `@CompanyCode` (`STERP.Common/SterpSession.cs`; `CobolGateway.cs:69-83`) | required on every company-scoped operation; must be in the caller's access list (5.3) |
| `X-STERP-Site` | `SterpSession.SiteCode` -> `CA-SITE` | required on site-scoped operations; must pair with the company in the access list |
| `X-STERP-Business-Date` | `SterpSession.BusinessDate` -> `CA-BUSINESS-DATE` (`CobolGateway.cs:77`) | optional ISO date; default = server local date (legacy `DateTime.Today`) |
| `X-STERP-Run-As` | SYSADM "Continue as" (`LoginService.vb:23-60`; `SterpSession.RealUserCode`) | optional STERP user code; honoured only when the authenticated user resolves to `SYSADM`; otherwise 403 `API0003` |
| `Idempotency-Key` | `CA-CORRELATION-ID X(36)` (`CobolGateway.cs:78`; `SterpSession.NewCorrelationId`, COM-015) | optional, at most 36 characters; default a new GUID per request; the GUI sends one stable key per user instruction wherever the legacy form/facade reused a correlation id |
| `traceparent` | none | W3C trace context; observability only |

### 4.6 Outcome model and HTTP status mapping

Operations backed by legacy COBOL logic (a COMMAREA header exists) return the **outcome envelope**;
operations backed by legacy direct SQL/procedure reads return their payload directly. Each OpenAPI
operation declares which via `x-sterp-outcome: envelope|direct`.

```json
{
  "outcome": {
    "returnCode": 0,
    "severity": "OK",
    "reasonCode": "",
    "message": "Completed.",
    "messageCount": 0,
    "documentRef": "",
    "correlationId": "3f1c…"
  },
  "data": { }
}
```

| Legacy result | Meaning (legacy) | HTTP | Body |
|---|---|---|---|
| `CA-RETURN-CODE` 00 | OK (`CobolResult.cs:6-26`) | 200 | envelope, severity `OK` |
| 04 | warning; request accepted | 200 | envelope, severity `WARNING` |
| 06 | hold: needs an authority; document placed on hold, approval requested (`BaseForm.vb:189-195`); not `CA-FAILED` (`STCAHDR.cpy:27`, SME-038) | 202 | envelope, severity `HOLD` |
| 08 | error; no business effect | 422 | problem `business-rule` |
| 12 | severe; predominantly `SQL0001` with the driver message (for example `cobol/online/ap/APALLOC.cob:734-736`) | 500 | problem `severe`; `detail` = legacy message (BC-005 proposes redaction) |
| 16 | not found (`NOT FOUND`); bridge miss maps to `PGM0001` (`CobolGateway.cs:132-137`) | 404 | problem `not-found` |
| any other code | `RCnn` | 500 | problem `severe`, `returnCode` = nn |
| procedure `THROW 5xxxx 'STERP-<s>-<code>: text'` | `BusinessRuleException`, Code = `<s>-<code>` (`STERP.Data/BusinessRuleException.cs:24-40`) | 422 | problem `business-rule`, `reasonCode` = `<s>-<code>`, `severity` from `<s>` |
| other database or runtime exception | `Guard` shows the exception text with `SYS0001` (`BaseForm.vb:221-238`) | 500 | problem `severe`, `reasonCode` `SYS0001` |

`returnCode`, `reasonCode` and `message` values are the legacy values (trimmed). The GUI maps them back
to the legacy presentation (section 9.3).

### 4.7 Problem details

RFC 9457 `application/problem+json` with `type` = `https://sterp.local/problems/<kind>`
(`business-rule`, `severe`, `not-found`, `validation`, `forbidden`, `unauthenticated`,
`not-implemented`), `title`, `status`, `detail` (= legacy message), and extensions `returnCode`,
`reasonCode`, `severity`, `messageCount`, `correlationId`, `traceId`, optional `errors` (field -> messages,
validation only) and optional `data`: for COBOL-backed operations whose legacy screen reads the COMMAREA
after a refusal (for example `frmSalesOrderEntry.vb:162-173`), the problem carries the same `data` object
the success envelope would carry, so the GUI can show those fields exactly as legacy did. Target-only
conditions use reserved reason codes `API0001`-`API0999` (section 5.10) so they never collide with legacy
codes.

### 4.8 Idempotency

The target reproduces the legacy correlation semantics exactly: the request's `Idempotency-Key` becomes
the legacy correlation id and flows to the kernel idempotency service (`IDEMPOT` ->
`core.usp_IdempotentBegin`/`End`, `core.IdempotencyKey`) wherever the legacy program used it, including
its observed quirks (BRF-IDEMPOT-01: the FAILED branch is unreachable because `usp_IdempotentBegin`
resets FAILED to NEW; BEGIN commits INPROGRESS under autocommit, Agent 02 `COBOL-SharedStateRegister.md`
section 7) [observed]. The API adds no payload-hash comparison of its own.

### 4.9 Optimistic concurrency

Concurrency control is the legacy mechanism per operation: `RowVer rowversion` columns (COM-016),
approval `ContentHash` (`core.usp_RequestApproval` `@ContentHash`), stale-check queries and lock hints
inside procedures (Agent 03 `SQL-Programmability.csv` `lock_hints`, `applocks`). Read models expose the
legacy token (`rowVersion` or `contentHash`) wherever the legacy screen keeps it between read and update;
conflicts surface through the legacy return/reason codes and the 4.6 mapping. The API adds no ETag or
`If-Match` rule that legacy does not have.

### 4.10 Collections

List operations return the **complete** legacy result set in legacy order (parity with the grids bound
by `GridHelper.Bind`), wrapped as `{ "items": [...], "count": n }`. No implicit paging or truncation.
Optional paging parameters may be added later as an additive change.

### 4.11 OpenAPI document conventions

- OpenAPI 3.1, one document per slice, `info.version` = `1.0.0+a06-20261020T112437Z-034eff00` (the build
  metadata names the Agent 06 attempt that issued the document).
- `servers`: `https://localhost:7101` (online) and, at path level for job operations,
  `https://localhost:7102`.
- `tags`: one per legacy screen or capability, named after the legacy caption.
- `operationId`: `<segment>_<verbNoun>` (for example `so_acceptSalesOrder`), unique across all documents.
- Vendor extensions on every operation: `x-sterp-requirements` (target FR IDs), `x-sterp-legacy`
  (forms, facade members, programs/functions, procedures, reports, jobs with citations),
  `x-sterp-permission` (legacy permission codes checked on the path), `x-sterp-screen-gate` (gate codes,
  section 5.4; added by the consolidation step), `x-sterp-engine`
  (`online`|`batch`), `x-sterp-outcome` (`envelope`|`direct`), `x-sterp-uow` (unit-of-work statement,
  section 6.4), `x-sterp-wave`.
- Schema properties carry `x-sterp-legacy-field` (copybook field and PIC or column and type) plus the
  4.3 extensions. Every operation has a `description`, request/response examples and the standard
  problem responses (`400`, `401`, `403`, `404`, `422`, `500`; `202` where HOLD can occur).
- `securitySchemes`: `windows` (`type: http`, `scheme: negotiate`); global `security: [{windows: []}]`.

## 5. Security model

### 5.1 Authentication

All endpoints of `STERP-API`, `STERP-JOBS` and the API-backed pages of `STERP-WEB` require Windows
authentication (`Negotiate`; Kerberos or NTLM) (HD-06). Anonymous access is limited to
`/health/live`, `/health/ready` and the OpenAPI documents (no business data). The browser negotiates
with each API using the signed-on Windows user (WebAssembly client; `credentials: 'include'`), so the
API always sees the real end user. Legacy equivalent: the desktop process ran as the interactive user
and passed `DOMAIN\user` to `core.usp_GetUserContext` (`LoginService.vb:11-33`) [observed].

### 5.2 STERP user resolution and "Continue as"

- The authenticated name (`DOMAIN\user`) is resolved per request with `core.usp_GetUserContext
  @WindowsLogin` (result set 1 = user, result set 2 = company/site access list) exactly as legacy
  sign-on (`LoginService.vb:23-60`); a short in-memory cache (<= 60 s, keyed by login) is allowed.
- No mapped user: 403 problem `API0010` with the legacy text `No STERP user is mapped to <login>.`
  (`LoginService.vb:43-45`).
- `X-STERP-Run-As` is honoured only when the resolved user is `SYSADM` (`SterpSession.IsSysAdm`), resolving
  the target user with `core.usp_GetUserContext @UserCode`; all legacy fields that received `CA-USER` or
  `@UserCode` receive the run-as user, as in legacy (`SterpSession.UserCode`), and the real user is
  logged (`SterpSession.RealUserCode`).
- The legacy degraded sign-on path (`LoginService.Fallback`, `LoginService.vb:81-...`, used only when the
  security procedures are not deployed) is not reproduced: the shared database has them deployed
  [inferred from Agent 03 catalogue]; disposition `excluded`, confirmed by the repository owner on 2026-10-20: no user
  relies on the fallback (decision D4, HD-14; Q-SEC-02, register row SME-2280 `ANSWERED`).

### 5.3 Company and site authorization

`X-STERP-Company`/`X-STERP-Site` must match a row of the caller's access list (result set 2), the same
list the legacy sign-on dialog offered (`frmLogin`; DEMO-WORKFLOWS step "RESTRICT" refused CAL/LIV).
Mismatch: 403 `API0002`. The API never widens access beyond the legacy list.

### 5.4 Permission checks

The target performs exactly the legacy permission checks, at the same points of the flow and with the
same permission codes: COBOL `PERMCHK` -> `core.usp_CheckPermission`; .NET-side checks
(for example `dotnet/STERP.Business/Finance/VatService.vb:66`); catalogue gating through
`core.usp_UiCheckAccess` inside `core.usp_UiMenuLoad`, `core.usp_UiReportList`, `core.usp_UiMaint*`
(Agent 03 `SQL-Programmability.csv`) [observed]. Refusals inside the legacy logic keep the legacy
return/reason codes and mapping (a `PERMCHK` refusal is RC 06 `PRM0006` with the text
`<user> does not hold <permission>`, `cobol/online/common/PERMCHK.cob:162-176`; others such as `ARI0050`,
`APV0007`, `BOM0403`).

**Screen and catalogue gate (the API never widens access).** The legacy GUI exposed each capability only
through permission-filtered menu rows (`core.MenuItem.PermissionCode`, evaluated by
`core.usp_UiCheckAccess` inside `core.usp_UiMenuLoad`, `db/procs/core/usp_UiMenuLoad.sql:46`) or report
catalogue rows (`core.ReportDef.PermissionCode` through `core.usp_UiReportList`). Because an API can be
called without opening the screen, every operation enforces the gate of the legacy entries that led to it:
before the handler runs, the host calls `core.usp_UiCheckAccess` (allowed when the code is blank,
`db/procs/core/usp_UiCheckAccess.sql:15-16`) for the operation's gate codes; any one granted code admits
the call (a user could reach the screen through any of its menu rows). Refusal: 403 problem
`reasonCode` `PRM0006`, `detail` `<user> does not hold <permission>` (legacy PERMCHK wording). The gate codes
of each operation are published in the OpenAPI extension `x-sterp-screen-gate`, computed
deterministically from the page routing (STERP-WEB section 7.2), the menu catalogue and the report
catalogue (`../work/openapi/screen-gates.csv`; `../scripts/a06_05a_screen_gates.py`); an operation reachable without a permission-coded entry has
an empty gate. The legacy toolbar is such an entry: `frmMain_Load` always builds it and it opens the approvals,
exceptions, audit, report launcher, spool viewer, job monitor, maintenance launcher and engine diagnostics pages
without a check (`frmMain.vb:31,183-193`; kept by FR-STERP-WEB-012), while the report launcher and generic
maintenance stay parameter-dependent inside their procedures. A job submission takes the gate of its own catalogue
rows: a `JOB.<stream>` row gates only the stream, never a same-named program (`ReportService.vb:80-88`), and the run
viewers (spool viewer, job monitor) never contribute to a submission's gate. Session, menu, lookup and other shell
services have no gate. `x-sterp-permission` lists the
business checks of the legacy flow (and may repeat the gate code). The API adds no other permission check.

### 5.5 Audit identity attribution

Writes populate `CreatedBy`/`ModifiedBy`/`*By` columns with the STERP user code exactly where the
legacy writer did. Audit triggers use `COALESCE(i.ModifiedBy, i.CreatedBy, d.ModifiedBy, SUSER_SNAME())`
(`db/triggers/core/tr_CoreMasters.sql:10`, and 11 more sites; Agent 03 `SQL-Triggers.csv`) [observed].
Where a legacy write leaves both columns null, the trigger falls back to the SQL login, which in the
target is the Windows account running `STERP-API`/`STERP-JOBS`. Running the services under the
interactive user's own account (the local default, HD-03) makes that fallback identical to legacy.
With several Windows users sharing one service account, fallback rows would name the service account
(residual risk RR-01). The repository owner decided on 2026-10-20 (decision D4, HD-14; Q-SEC-01, register row SME-2279
`ANSWERED`) that each user runs the services under their own Windows account, accepted RR-01 for that local use and
declined BC-006, so a shared service account is not part of this release.

### 5.6 Service identity and database connection

- Connection string (no secret): `Server=.;Database=STERP;Integrated Security=True;Encrypt=Mandatory;
  TrustServerCertificate=True;Application Name=STERP.Api` (`STERP.Jobs` for the job host). Legacy used
  `Integrated Security=SSPI` / ODBC `Trusted_Connection=yes` (Agent 05 `02-deployment-topology...`
  section 1) [observed]. `TrustServerCertificate=True` is accepted for the local self-signed certificate
  and is recorded as residual risk RR-02.
- The services run under the Windows account that starts them (local default: the interactive user).
  No SQL role or grant is declared in the repository (Agent 03 `SQL-Permissions.csv`: "(none declared)")
  [observed]; a least-privilege role for a dedicated service account is proposed as additive item
  DB-ADD-002 (section 6.12), not required for local use.

### 5.7 CORS

`STERP-API` and `STERP-JOBS` allow credentialed CORS only from the configured `STERP-WEB` and
`STERP-PORTAL` origins; pre-flight requests are answered before authentication.

### 5.8 Transport

HTTPS only (ASP.NET Core development certificate locally, trusted with `dotnet dev-certs https --trust`);
HSTS off for localhost; HTTP redirects to HTTPS.

### 5.9 Sensitive data

Bank details (`ap.SupplierBank`, `ar.DirectDebitMandate`), personal data (`core.AppUser`), and approval
comments are never written to logs or traces; request/response body logging is off by default and, when
enabled for diagnostics, redacts fields tagged `x-sterp-sensitive: true` in the OpenAPI documents.

### 5.10 Target-only reason codes

| Code | HTTP | Meaning |
|---|---|---|
| `API0001` | 400 | malformed or missing context header |
| `API0002` | 403 | company/site not in the caller's access list |
| `API0003` | 403 | run-as requested by a user who is not SYSADM |
| `API0004` | 400 | request body does not match the schema (type/format errors only) |
| `API0010` | 403 | no STERP user mapped to the Windows login (legacy text) |
| `API0401` | 401 | not authenticated |
| `API0501` | 501 | operation belongs to a slice not yet delivered in the target |
| `PRM0006` (legacy code reused) | 403 | screen/catalogue gate refused (5.4); same code and wording as the legacy PERMCHK refusal |

