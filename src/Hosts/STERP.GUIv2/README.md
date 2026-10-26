# STERP.GUIv2 - bills of material in 3D

GUIv2 is an additional, modern front end for STERP. It runs next to the classic GUI (`STERP.Web`), which is unchanged.
This first pass covers **bills of material**: a clickable 3D explosion tree, a 3D product studio that renders the cake
and every ingredient that goes into it, a bike atelier that lays out every line of a bicycle's explosion and builds the
bike from it on a timeline you can scrub, where-used, revision comparison, engineering-change preview and the BOM
reports.

GUIv2 **only uses the existing STERP APIs**. It has no business logic and no database connection, and it calculates
no business figures. Every quantity, cost, total, validation result, preview and listing it shows is the answer of an
existing `STERP.Api` or `STERP.Jobs` operation, shown with that operation's own messages and reason codes.

## Run it

`run-sterp.bat` in the repository root builds GUIv2 with the other services and starts it at <https://localhost:7144>,
then opens it next to the classic GUI (<https://localhost:7140>). By hand, with Api and Jobs already running on the same
port base:

```powershell
./scripts/run-host.ps1 -Name GUIv2 -Instance STERPA08_demo -PortBase 7140    # https://localhost:7144
```

The front end is built with **Node.js LTS** (`winget install OpenJS.NodeJS.LTS`). The .NET build runs it for you:
`npm ci` when `package-lock.json` changes, then `npm run build` when a client source changes. If Node.js is missing,
the build still succeeds. It warns with `GUIV2001` (no bundle yet) or `GUIV2002` (an older bundle is served), and GUIv2
shows a page that explains the fix. If the npm registry cannot be reached, the build warns with `GUIV2003`. The other
services are never held back. `/health/ready` reports `Degraded` until the bundle exists.

## What it offers

| Page | Route | What you see | Operations used |
|---|---|---|---|
| Overview | `/` | live 3D hero of a finished good, catalogue figures, entry points | lookups `BOM.HEADER`, `ITM.ITEM` |
| Bill catalogue | `/bills` | every bill as cards or a table, filtered by status | lookup `BOM.HEADER` |
| Bill detail | `/bills/:bomRef` | header, components, by-products, revision timeline, validate and release | `bom_getBillHeader`, `bom_listBillComponents`, `bom_listBillByProducts`, `bom_listBillRevisions`, `bom_validateBill`, `bom_releaseBillRevision` |
| Explosion 3D | `/explode/:item` | the multi-level explosion as an interactive 3D tree (cone, orbit or tier layout). Click a node to fly to it and inspect it; overlays show quantities, costs, net requirements or materials, with a table drawer | `bom_explodeBill` (INDENT and LEAF views), `bom_costBill`, `bom_netBillRequirement`, `cst_listCostSets`, `itm_getItem` |
| Product studio | `/studio/:item` | a 3D render of the product: the cake assembled, cut away or exploded into its parts, every ingredient a 3D prop with its required quantity and cost, flowing into the part it goes into. A bicycle opens the **bike atelier** instead (below). Batch size, recipe or build sheet and work instruction come from the APIs | `bom_explodeBill` (INDENT and LEAF), `bom_costBill`, `bom_listBillRevisions`, `bom_listBillComponents`, `itm_getItem`, `cst_listCostSets` |
| Where used | `/where-used/:item` | every parent that uses a component, as a 3D tree and a list | `bom_getWhereUsed` |
| Compare revisions | `/compare/:bomRef` | the differences between two revisions of a bill | `bom_listBillRevisions`, `bom_compareBillRevisions` |
| Change preview | `/changes` | which bills a component replacement would change or fail, before anything is applied | `bom_previewBulkChange` |
| Reports | `/reports` | BOMRP010 and BOMRP030 run on STERP.Jobs and are followed live; the spool is shown as a 132-column listing | `bom_submitBomrp010Run`, `bom_submitBomrp030Run`, `jobs_getRun`, `jobs_getRunSpool`, `jobs_getSpoolFile`, `jobs_getRunSysout` |

The shell signs on like the classic GUI: `platform_getSession`, plus `platform_listRunAsUsers` for "Continue as".
Company, site, business date and run-as user are sent as the usual `X-STERP-*` context headers. `Ctrl+K` opens a
command palette that searches bills and items through the master-file lookups.

The item-master category picks the render: `BAKERY` is drawn as the cake (layers, roses and candles read from the bill's
own lines) and `BICYCLE` opens the bike atelier. Any other product appears in the studio as an abstract core surrounded
by its materials.

The bike atelier is a 2.5D drawing (SVG, no WebGL), so it stays light on any machine:

- **Every line is drawn as the part it is.** All occurrences of the live explosion are staged: tube stock, filler wire,
  argon, swarf, paint and primer, decals, headset and bottom-bracket bearings, dropouts, rims, hubs, every spoke and
  nipple, rim tape, rotors, cassette, tyres and tubes, cockpit, crankset, chain, derailleur, calipers, hoses and
  housing, saddle, seatpost, pedals, accessories, the hardware kit, grease, the job tickets of the services, the work
  instruction and the packaging. Counts come from the line's usage per parent (24 spokes per wheel, 6 inserts per
  frame), lengths of stock from its usage in metres, and sizes, paint colour and brand from the descriptions. A line it
  does not recognise is still shown, as a tagged tile.
- **Flat lay.** The mat is a knolling layout of the explosion: one cell per made subassembly with its build area (a
  dashed ghost of what will be built there), its loose lines beside it and its own subassemblies' cells below; phantom
  kits lie in trays inside their parent's cell; the bike is assembled in the bay in the middle.
- **Build.** The timeline follows the bill, bottom up: each subassembly is built in its cell (tubes cut from the stock,
  welds laid by the filler wire, the frame heat-treated, primed, painted and decaled, wheels laced spoke by spoke and
  spun), then flies into its parent, and the bike's own lines are fitted one by one up to the packed carton. Play,
  pause, change speed, or scrub from everything laid flat to the finished bike; the camera follows the step being
  built (turn Follow off to look around). Each step shows its explosion line (item code, required quantity).
- **Assembled** and **Exploded** (with leaders and balloons carrying the bill's find numbers), plus **Blueprint**,
  **Night ride** and **Packaging** toggles. Hover any part for its line; click it for usage per parent, requirement,
  line cost, where else it is used, the engine's LEAF total and the item master. The build sheet beside the drawing
  is the explosion as a tree, each line ticked off as the build passes it.

## How it works

```text
 browser: GUIv2 single-page app            STERP.GUIv2 host (7144)                       existing services
 ---------------------------------  same   -------------------------------------  user's  -------------------------
 /bills, /explode/..., /studio/...  origin  serves wwwroot (built from client\)   Windows  STERP.Api   (7141) /api/...
 fetch /bff/api/...      ---------------->  /bff/api/{path}  -> Api  /api/{path}  ------>  STERP.Jobs  (7142) /api/...
 fetch /bff/jobs/...     ---------------->  /bff/jobs/{path} -> Jobs /api/{path}  identity
```

- **Forwarding, not re-implementation.** `Bff/ApiForwarder.cs` passes the method, path, query, body, `Accept`,
  `Accept-Language`, the `X-STERP-Company`/`-Site`/`-Business-Date`/`-Run-As` headers and `Idempotency-Key` through
  unchanged. The status, body and problem details come back unchanged too, along with `Location`, `Retry-After` and
  `traceparent`.
- **The user's own identity.** The host authenticates the browser with Windows Negotiate, then makes the API call
  impersonated as that user (`WindowsIdentity.RunImpersonatedAsync`, default credentials). STERP.Api therefore sees the
  same Windows login as it does for the classic GUI: same STERP user, same rights, same audit. There is no service
  account and no secret. The browser never calls the APIs directly, so their CORS configuration is unchanged.
- **Guards.** Only GUIv2's own pages can use the forwarder: an `X-STERP-GUIv2` header is required, and
  `Sec-Fetch-Site` and `Origin` must be same-origin. It forwards only an allow-list of path prefixes (BOM, session,
  lookups, item master, cost sets, BOM jobs, job runs and spool files) and rejects dot segments. Request bodies are
  limited to 1 MB and calls time out after 130 s. `WWW-Authenticate` is never passed back. The host's own refusals are
  `application/problem+json` with reason codes `GUIV2-401`, `-403`, `-404`, `-413`, `-501`, `-502` and `-504`.
- **Browser hardening.** A strict Content-Security-Policy (`default-src 'self'`, no external origin: fonts and 3D
  assets are bundled; `blob:` scripts are allowed for the 3D-text workers), plus `X-Frame-Options: DENY`, `nosniff`,
  COOP, `Referrer-Policy` and `Permissions-Policy`.
- **No business calculations in the client.** The explosion tree is rebuilt from the engine's own line order and
  paths (`client/src/domain/tree.ts`). Costs, net requirements and material totals (the LEAF view) are the API's figures.

## Configuration

Section `Sterp:GuiV2` (`appsettings.json`). `run-host.ps1` sets the URLs from the port base.

| Key | Default | Meaning |
|---|---|---|
| `ApiBaseUrl` | `https://localhost:7101` | STERP.Api (https, or http on loopback only) |
| `JobsBaseUrl` | `https://localhost:7102` | STERP.Jobs |
| `ClassicGuiUrl` | `https://localhost:7100` | link to the classic GUI |
| `ApiConsoleUrl` | `https://localhost:7103` | link to the API Console |
| `ApiForwardPrefixes` | BOM, session, lookups, item, cost sets | API paths GUIv2 may forward |
| `JobsForwardPrefixes` | BOM jobs, job runs, spool files | Jobs paths GUIv2 may forward |

Health: `/health/live`, and `/health/ready`, which checks the bundle and whether STERP.Api and STERP.Jobs answer.

## Develop

```powershell
cd src\Hosts\STERP.GUIv2\client
npm ci
npm run watch       # rebuilds wwwroot on every change; the running host serves it on the next page load
npm test            # vitest: tree reconstruction, studio model and bike atelier, on captured API answers (src/test/fixtures)
npm run typecheck
```

`dotnet build -p:GuiV2SkipClient=true` skips the front-end steps. Client layout: `src/api` (typed calls with their
operation IDs, React Query hooks), `src/domain` (tree reconstruction and formatting), `src/three/tree` (3D explosion and
where-used), `src/three/studio` (the 3D studio scene), `src/three/cake` (procedural cake, ingredients and textures),
`src/atelier` (the bike atelier: the 2.5D projection, bike geometry, the catalogue that turns bill lines into parts,
layout, timeline, artwork and the scene), `src/pages`, `src/components`.

Stack: React 19, TypeScript, Vite, Tailwind CSS 4, three.js with react-three-fiber, drei and postprocessing, TanStack
Query, React Router, zustand, motion and lucide icons. All versions are pinned in `client/package.json` and
`client/package-lock.json`.

## Known behaviour

- **Validate** on the bill page shows the API's answer as it is. For the demo bills that is `404 BOM0303`, a
  reproduced legacy defect (SME-629).
- **Reports** read the listing recorded by the job host (`execution.spoolFiles`) through `jobs_getSpoolFile` when the
  run has no `core.JobRun` spool file of its own.
- **Identity** was verified with the demo model, in which each user runs the services under their own account
  (README, RR-01). Serving other users from a shared GUIv2 host also needs Kerberos delegation to the API host. That
  setup was not verified.
- Other modules (sales, purchasing, finance and so on) remain in the classic GUI.

## Third-party notices

Shipped in the bundle, all used unmodified:

| Package | Licence |
|---|---|
| react, react-dom, react-router, @tanstack/react-query, zustand, motion, clsx | MIT |
| three, @react-three/fiber, @react-three/drei, @react-three/postprocessing, troika-three-text, camera-controls, three-stdlib | MIT |
| postprocessing | Zlib |
| lucide-react | ISC |
| Inter, JetBrains Mono, Fraunces, Pacifico (via @fontsource) | SIL Open Font License 1.1 |

Build tools, not shipped: Vite, @vitejs/plugin-react, Tailwind CSS and Vitest (MIT), TypeScript (Apache-2.0), and
lightningcss (MPL-2.0, used by the CSS build). Each package's licence text is in its folder under
`client/node_modules` after `npm ci`.
