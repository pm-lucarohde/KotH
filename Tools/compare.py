"""Vergleicht einen Probenlauf gegen eine MENGE von Referenzlaeufen.

Ein Fall gilt als unveraendert, wenn sein Ergebnis in der Referenzmenge
vorkommt. Noetig, weil Ramp45 und Ramp55 stehen von sich aus zwischen zwei
Ergebnissen springen - ein Vergleich gegen einen einzelnen Lauf gaebe dort
bei jeder Etappe Fehlalarm.
"""
import io, os, re, sys, collections

def cases(path):
    out = {}
    for line in io.open(path, encoding="utf-8"):
        line = line.rstrip("\n")
        if " ~ " in line:            # Tick-Spuren sind Diagnostik
            continue
        m = re.match(r"^\[(\w+)\] == ([^:]+):(.*)$", line)
        if m:
            out["== " + m.group(2)] = m.group(3).strip()
        else:
            m2 = re.match(r"^\[(\w+)\] (.+)$", line)
            if m2:
                out["-- " + m2.group(2).split(":")[0][:40]] = m2.group(2)
    return out

refdirs = sys.argv[1].split(",")
newdir = sys.argv[2]
total = same = spread = changed = 0
report = []

for fn in sorted(os.listdir(newdir)):
    if not fn.endswith(".txt"):
        continue
    refsets = collections.defaultdict(set)
    for d in refdirs:
        p = os.path.join(d, fn)
        if os.path.exists(p):
            for k, v in cases(p).items():
                refsets[k].add(v)
    for k, v in cases(os.path.join(newdir, fn)).items():
        total += 1
        known = refsets.get(k)
        if known is None:
            changed += 1
            report.append("NEU      %s %s" % (fn, k))
        elif v in known:
            same += 1
            if len(known) > 1:
                spread += 1
        else:
            changed += 1
            report.append("ANDERS   %s %s\n           neu : %s\n           ref : %s"
                          % (fn, k, v, "\n           ref : ".join(sorted(known))))

print("\n".join(report) if report else "keine Abweichung")
print("--- %d Faelle: %d wie Referenz (%d davon streuen in der Referenz selbst),"
      " %d abweichend" % (total, same, spread, changed))
