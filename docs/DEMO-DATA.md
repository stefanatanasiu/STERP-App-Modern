# STERP - demonstration dataset (modernized application)

The modernized application uses the legacy `STERP` database in place, without any schema change. Its demonstration
dataset is therefore the legacy one: built by the legacy repository's `build\demo-data.ps1`
([STERP-App `docs/DEMO-DATA.md`](https://github.com/stefanatanasiu/STERP-App/blob/main/docs/DEMO-DATA.md)) and
captured as a SQL Server database backup that the scripts here restore into an isolated LocalDB instance. The
[demo workflows](DEMO-WORKFLOWS.md) run on it.

> **All data is fictional.** Every company, person, address, bank account, sort code, IBAN, VAT and company
> registration number in the seed and demonstration data is invented for the demonstration; any resemblance to a real
> organisation or account is coincidental.

## The two datasets

| Dataset | Backup | Built by the legacy repository (commit `00631916`) | Used by |
|---|---|---|---|
| `fresh` | `freshc.bak` | `build\build-db.ps1 -Full`: schema, procedures, triggers, views and seed data | the default of `new-isolated-env.ps1`; most parity scenarios |
| `demo` | `democ.bak` | the same, then `build\demo-data.ps1`: the COBOL demonstration drivers, the control-account bridge and `RECONCTL` | `new-isolated-env.ps1 -Dataset demo`; the demo workflows; the demo parity scenarios |

The backups are binaries and are not part of this repository. `scripts/new-isolated-env.ps1` reads them from
`..\run\STERP-App-isolated-tests\a08-aa6a44e4\backups` (next to this repository) unless you pass `-SourceBackups <folder>`.

## Create the backups

You need the legacy repository cloned next to this one (`..\STERP-App`) with its prerequisites (32-bit GnuCOBOL 3.2,
the 32-bit ODBC Driver 18, the .NET Framework 4.8 targeting pack: see its
[README](https://github.com/stefanatanasiu/STERP-App#prerequisites)), plus SQL Server LocalDB. Build the legacy
database straight into a LocalDB instance, so the backups restore into any LocalDB instance of the same or a later
version:

```powershell
# from this repository's root
git -C ..\STERP-App checkout 00631916             # the legacy commit this application reproduces
sqllocaldb create STERPDATA -s                     # a private LocalDB instance for the build
$env:STERP_SQLSERVER = '(localdb)\STERPDATA'       # every legacy build script and program uses it
$backups = (New-Item -ItemType Directory -Force ..\run\STERP-App-isolated-tests\a08-aa6a44e4\backups).FullName

Push-Location ..\STERP-App
build\build-all.cmd -Full                          # create the STERP database, build COBOL and .NET
sqlcmd -S $env:STERP_SQLSERVER -E -C -b -Q "BACKUP DATABASE [STERP] TO DISK = N'$backups\freshc.bak' WITH INIT, COPY_ONLY"
build\demo-data.cmd                                # demonstration drivers, control bridge, RECONCTL (exit code 0)
sqlcmd -S $env:STERP_SQLSERVER -E -C -b -Q "BACKUP DATABASE [STERP] TO DISK = N'$backups\democ.bak' WITH INIT, COPY_ONLY"
Pop-Location
```

The legacy `db\00-create-database.sql` creates the database with the `Latin1_General_CI_AS` collation. The figures in
the demo workflows depend on it, because some seeded quantities, costs and prices come from `CHECKSUM()`.

## Restore it into an isolated instance

`run-sterp.bat` in the repository root does this on its first start, and `run-sterp.bat /reset` stops the services,
restores the dataset and starts them again. By hand:

```powershell
./scripts/new-isolated-env.ps1 -Name demo -Dataset demo -MapLoginTo SYSADM   # (localdb)\STERPA08_demo, your login = SYSADM
./scripts/run-local.ps1 -Instance STERPA08_demo -PortBase 7140               # the four hosts on ports 7140-7143
```

Run `run-sterp.bat /reset` (or `new-isolated-env.ps1`) again to put the instance back to the dataset: many demo steps are
one-shot. The parity tests
restore the dataset of every scenario they run, so running them on an instance also resets it. Without `-MapLoginTo`
your Windows login is not a STERP user and the hosts answer 403 `API0010`.

## What the demo dataset contains

Two companies on an April-March fiscal year: **STC** Stellar Components Ltd (sites `RDG` Reading, `BHM` Birmingham) and
**CAL** Caledon Assemblies Ltd (`GLA` Glasgow, `LIV` Livingston). The users and the depth of data at each company and
site are described in [DEMO-WORKFLOWS.md](DEMO-WORKFLOWS.md). Source paths below are in the legacy repository.

| Area | Source | Content |
|---|---|---|
| Companies, calendar, users | `db\seed\00..07-core-*.sql` | Stellar Components (STC) and Caledon Assemblies (CAL), FY2026 periods 1-9, roles and users |
| Chart of accounts | `db\seed\08-fin-coa.sql` | ~110 accounts per company; control accounts marked with `ControlType` AR, AP, INVENTORY, WIP, GRNI, VAT, BANK, ACCRUAL, DEPOSIT, RETAINED |
| Finance activity | `db\seed\91-fin-demo.sql`, `FINDEMO` | Summarised trading journals for periods 1-9 posted through `fin.JournalStage` + `fin.usp_PostJournal` |
| Sales ledger | `db\seed\91-ar-demo.sql`, `db\seed\98-demo-ar-cal.sql` | Aged debt for both companies, receipts, a write-off, disputes, collection cases, promises to pay, Direct Debit mandates |
| Purchase ledger | `db\seed\90-ap-demo.sql`, `APDEMO` | Supplier invoices, credit notes, a payment run, three-way matched invoices |
| Stock, items, BOM, kits, costing | `db\seed\2x/3x/9x-*.sql` | Items, locations, stock movements, structures, execution kits with work in progress |
| Showcase bills of materials | `db\seed\90-bom-showcase.sql` | Two released multi-level structures with complete costs in the `BOMDEMO` cost set: the celebration cake `FG-CAKE-CELEB-8IN` and the commuter bicycle `FG-BIKE-COMMUTE-M` |
| Purchasing | `db\seed\92-po-demo.sql`, `PODEMO` | Orders, receipts (goods received not invoiced), service acceptances |
| Kits and work in progress | `db\seed\95-kit-demo.sql`, `KITDEMO` | Six seeded kits in various states, plus one kit driven right through: issues, a completion into stock with genealogy, a kit cost and a disassembly |
| Sales orders | `SODEMO` | Sales orders, allocations, despatches and invoices |
| Consistency bridge | `db\seed\98-demo-consistency.sql` | Brings the general ledger control accounts onto the subsidiary ledgers |

## Check it

*Continue as* `FINCTRL` at STC and run *Reports > Controls and reconciliation > Control account reconciliation*
(`RECONCTL`) as at `20261031` in `PREVIEW`. On a freshly restored demo dataset all eight control families
(AR, AP, INVENTORY, WIP, GRNI, VAT, BANK, ACCRUAL) are **AGREED** and the run ends with return code 0. After the demo
workflows have posted, the differences they leave are explained in the workflows themselves.
