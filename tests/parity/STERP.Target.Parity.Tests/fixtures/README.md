# Legacy-run fixtures (Agent 07)

Files copied by scenario `file` steps (tests/README.md). Each one is the byte-for-byte output of an attributable run of
the isolated legacy runtime built from `STERP-App` commit `00631916`; scenarios pin its sha256.

| Fixture | sha256 | Legacy run | Used by |
|---|---|---|---|
| `legacy-run/spool/RECONCTL-STC-20261021230858.lst` | `77ee30382ecf6b1e306a4bc15388531e509afe24540d85c1ac84a782b665db78` | `RECONCTL.exe CO=STC ASAT=20261031 MODE=PREVIEW` on the demo dataset (`democ.bak` sha256 `df65d1f27b10470ac09824fff571c0176fbfebaef85b376b11dd7fcb93503687`), LocalDB `STERPA08_platform`, 2026-10-21 23:08:58, exit 0, core.JobRun row `RECONCTL STC 'CO=STC ASAT=20261031 MODE=PREVIEW' FINCTRL OK 0 rows 8` | FR-STERP-PLATFORM-045-AC4, FR-STERP-JOBS-011-AC1 |

Evidence (console output, listing copy): Agent 07 amendment `agent-07-20261021T211031Z-a544e35d`, `evidence/p1/`.
