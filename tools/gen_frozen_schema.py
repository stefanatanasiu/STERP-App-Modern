"""Generate contracts/frozen-schema.json: the frozen data-contract snapshot of contracts 6.11.

Reads the 384 objects of contracts/sql-object-mapping.csv (Agent 06 binding data contract) and their catalog shape from
the isolated deploy of the repository definitions (build-db.ps1 at commit 00631916, Agent 07 freshc backup restored on the
Agent 08 isolated LocalDB instance). Never run against the shared server.

usage: gen_frozen_schema.py <server> <database> <sql-object-mapping.csv> <out.json>
"""
import csv
import json
import os
import shutil
import subprocess
import sys

server, database, mapping, out = sys.argv[1:5]
low = server.lower().strip()
if low in (".", "(local)", "localhost", "127.0.0.1") or not low.startswith("(localdb)"):
    raise SystemExit(f"refusing server {server}: only an isolated LocalDB instance may be used")


def find_sqlcmd():
    """STERP_SQLCMD_DIR, else the ODBC 18 sqlcmd in its standard location, else sqlcmd on PATH (as scripts/isolated-paths.ps1)."""
    candidates = []
    if os.environ.get("STERP_SQLCMD_DIR"):
        candidates.append(os.path.join(os.environ["STERP_SQLCMD_DIR"], "SQLCMD.EXE"))
    program_files = os.environ.get("ProgramW6432") or os.environ.get("ProgramFiles", "")
    candidates.append(os.path.join(program_files, "Microsoft SQL Server", "Client SDK", "ODBC", "180", "Tools", "Binn", "SQLCMD.EXE"))
    for candidate in candidates:
        if os.path.isfile(candidate):
            return candidate
    on_path = shutil.which("sqlcmd")
    if on_path:
        return on_path
    raise SystemExit("sqlcmd not found: install the ODBC 18 sqlcmd utility, set STERP_SQLCMD_DIR or put sqlcmd on PATH")


SQLCMD = find_sqlcmd()

QUERY = r"""
SET NOCOUNT ON;
SELECT (
 SELECT s.name AS [schema], o.name AS [name], o.type AS [type],
   (SELECT c.column_id AS id, c.name, TYPE_NAME(c.user_type_id) AS type, c.max_length AS maxLength, c.precision, c.scale,
           c.is_nullable AS nullable, c.is_identity AS [identity], c.is_computed AS computed
      FROM sys.columns c WHERE c.object_id = o.object_id ORDER BY c.column_id FOR JSON PATH) AS columns,
   (SELECT p.parameter_id AS id, p.name, TYPE_NAME(p.user_type_id) AS type, p.max_length AS maxLength, p.precision, p.scale,
           p.is_output AS [output]
      FROM sys.parameters p WHERE p.object_id = o.object_id ORDER BY p.parameter_id FOR JSON PATH) AS parameters,
   (SELECT i.name FROM sys.indexes i WHERE i.object_id = o.object_id AND i.name IS NOT NULL ORDER BY i.name FOR JSON PATH) AS indexes
 FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
 WHERE o.is_ms_shipped = 0 AND o.type IN ('U','V','P','FN','IF','TF','TR')
 ORDER BY s.name, o.name FOR JSON PATH
) AS j;
"""

import os
import tempfile

with tempfile.NamedTemporaryFile("w", suffix=".sql", delete=False, encoding="utf-8") as qf:
    qf.write(QUERY)
    qpath = qf.name
try:
    res = subprocess.run([SQLCMD, "-S", server, "-E", "-C", "-d", database, "-y", "0", "-b", "-i", qpath],
                         capture_output=True, text=True, encoding="utf-8")
finally:
    os.unlink(qpath)
if res.returncode != 0:
    raise SystemExit(f"sqlcmd failed: {res.stdout}\n{res.stderr}")
catalog = json.loads("".join(line.strip() for line in res.stdout.splitlines()))
by_name = {f"{o['schema']}.{o['name']}": o for o in catalog}

objects = []
missing = []
with open(mapping, encoding="utf-8") as fh:
    for row in csv.DictReader(fh):
        name, kind, slice_ = row["object"], row["object_kind"], row["owner_slice"]
        entry = {"object": name, "kind": kind, "slice": slice_, "driftCheck": row["drift_check"] == "yes"}
        if kind == "DATABASE":
            entry["database"] = name
        elif kind == "INDEX":
            table, index = name.split("#", 1)
            t = by_name.get(table)
            if t is None or not any(i["name"] == index for i in t.get("indexes", [])):
                missing.append(name)
            entry["table"], entry["index"] = table, index
        else:
            o = by_name.get(name)
            if o is None:
                missing.append(name)
            else:
                entry["type"] = o["type"].strip()
                if o.get("columns"):
                    entry["columns"] = [{k: c[k] for k in ("name", "type", "maxLength", "precision", "scale", "nullable")}
                                        for c in o["columns"]]
                if o.get("parameters"):
                    entry["parameters"] = [{k: p[k] for k in ("name", "type", "maxLength", "precision", "scale", "output")}
                                           for p in o["parameters"]]
        objects.append(entry)

if missing:
    raise SystemExit(f"objects of the contract missing from the isolated deploy: {missing}")

doc = {
    "schema": "sterp-frozen-schema/1",
    "source": "repository definitions at STERP-App commit 00631916e85a4711b3db4d3e969a4b4d41d0e541 deployed by build-db.ps1 "
              "(Agent 07 freshc backup) on the Agent 08 isolated instance; object list = contracts/sql-object-mapping.csv",
    "database": database,
    "objects": objects,
}
with open(out, "w", encoding="utf-8", newline="\n") as fh:
    json.dump(doc, fh, indent=1)
kinds = {}
for o in objects:
    kinds[o["kind"]] = kinds.get(o["kind"], 0) + 1
cols = sum(len(o.get("columns", [])) for o in objects if o["kind"] == "TABLE")
print(json.dumps({"objects": len(objects), "kinds": kinds, "tableColumns": cols}))
