"""Print compact views of Agent 06 slice specification requirements (skips unit/citation lists).

usage: specview.py <spec.md> [FR-id-prefix ...] [--sections]
"""
import re
import sys

path = sys.argv[1]
wanted = [a for a in sys.argv[2:] if not a.startswith("--")]
show_sections = "--sections" in sys.argv

text = open(path, encoding="utf-8").read()
lines = text.splitlines()

if show_sections:
    for i, l in enumerate(lines, 1):
        if l.startswith("#"):
            print(i, l[:160])
    sys.exit(0)

blocks = re.split(r"(?m)^#### ", text)
for b in blocks[1:]:
    title = b.splitlines()[0]
    if wanted and not any(title.startswith(w) for w in wanted):
        continue
    print("#### " + title)
    for line in b.splitlines()[1:]:
        if line.startswith("**Legacy units implemented") or line.startswith("**Citations"):
            continue
        if line.startswith("<!--"):
            continue
        if line.startswith("| Field") or line.startswith("|---"):
            continue
        if line.startswith("### ") or line.startswith("## "):
            break
        print(line[:1500])
    print()
