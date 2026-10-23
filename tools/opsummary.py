"""Summarize binding OpenAPI operations from the Agent 06 JSON renders (stdlib only).

usage: opsummary.py <json-dir> [document-substring] [--csv out.csv] [--detail operationId]
"""
import csv
import glob
import json
import os
import sys

args = [a for a in sys.argv[1:]]
d = args[0]
flt = args[1] if len(args) > 1 and not args[1].startswith("--") else None
out_csv = args[args.index("--csv") + 1] if "--csv" in args else None
detail = args[args.index("--detail") + 1] if "--detail" in args else None

rows = []
for f in sorted(glob.glob(os.path.join(d, "*.openapi.json"))):
    name = os.path.basename(f).replace(".openapi.json", "")
    if flt and flt not in name:
        continue
    doc = json.load(open(f, encoding="utf-8"))
    comps = doc.get("components", {})
    default_server = (doc.get("servers") or [{}])[0].get("url", "")
    for p, item in doc["paths"].items():
        pserver = (item.get("servers") or [{}])[0].get("url") if item.get("servers") else None
        for m in ("get", "post", "put", "patch", "delete"):
            if m not in item:
                continue
            op = item[m]
            server = (op.get("servers") or [{}])[0].get("url") if op.get("servers") else (pserver or default_server)
            params = []
            for src in (item.get("parameters", []), op.get("parameters", [])):
                for prm in src:
                    if "$ref" in prm:
                        prm = comps["parameters"][prm["$ref"].split("/")[-1]]
                    params.append(f"{prm['in']}:{prm['name']}{'!' if prm.get('required') else ''}")
            body = op.get("requestBody", {}).get("content", {}).get("application/json", {}).get("schema", {})
            body_ref = body.get("$ref", "").split("/")[-1] if body else ""
            gate = op.get("x-sterp-screen-gate", {})
            codes = list(op.get("responses", {}).keys())
            rows.append({
                "doc": name, "operationId": op["operationId"], "method": m.upper(), "path": p,
                "host": "jobs" if ":7102" in (server or "") else "api",
                "outcome": op.get("x-sterp-outcome", ""), "engine": op.get("x-sterp-engine", ""),
                "gate": "|".join(gate.get("anyOf", []) if isinstance(gate, dict) else []),
                "params": " ".join(params), "body": body_ref, "codes": ",".join(codes),
                "wave": op.get("x-sterp-wave", ""),
            })
            if detail and op["operationId"] == detail:
                print(json.dumps(op, indent=1)[:12000])
if out_csv:
    with open(out_csv, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)
if not detail:
    for r in rows:
        print(f"{r['operationId']:<40} {r['method']:<5} {r['host']:<4} {r['outcome']:<8} {r['path']:<70} gate={r['gate']} body={r['body']}")
    print(len(rows), "operations")
