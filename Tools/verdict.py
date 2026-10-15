"""Vergleicht die URTEILE zweier Messstaende, nicht die Zahlen.

Jede Probe bekommt eine eigene Regel, was an einem Fall das Urteil ist
(oben/nicht oben, gestiegen/nicht, -> ok, soll erfuellt ...). Gibt jeden
Fall aus, dessen Urteil sich zwischen Referenzmenge und neuem Stand
unterscheidet, mit beiden vollstaendigen Zeilen.
"""
import io, os, re, sys, collections

def num(s):
    return float(s.rstrip(",").replace(",", "."))

def verdicts(path):
    probe = os.path.basename(path)
    out = {}
    for line in io.open(path, encoding="utf-8"):
        line = line.rstrip("\n")
        if " ~ " in line:
            continue
        m = re.match(r"^\[(\w+)\] == ([^:]+):(.*)$", line)
        m2 = re.match(r"^\[(\w+)\]\s+(.+?)\s+->\s+(\S+)\s*$", line)
        if m:
            name, rest = m.group(2).strip(), m.group(3)
            v = None
            if probe.startswith("Crest"):
                v = "oben" if "oben bei" in rest else "NIE OBEN"
            elif probe.startswith("SlopeJump"):
                v = "ohne Boden " + re.search(r"ohne Boden (\d+)", rest).group(1)
            elif probe.startswith("Step") and name[:1] in "+-":
                mx = num(re.search(r"max ([\d,]+)", rest).group(1))
                v = "steigt" if mx > 0.1 else "steigt nicht"
            elif probe.startswith("Step") and name[:1] == "=":
                end = num(re.search(r"end (-?[\d,]+)", rest).group(1))
                v = "bleibt oben" if end > -0.5 else "faellt"
            elif probe.startswith("Step"):
                mx = num(re.search(r"max ([+-]?[\d,]+)", rest).group(1))
                rut = int(re.search(r"Rutsch-Ticks (\d+)", rest).group(1))
                v = ("oben" if mx > 4.5 else "unten") + (", rutscht" if rut else ", rutscht nicht")
            elif probe.startswith("Impact"):
                vert = num(re.search(r"senkrecht (-?[\d,]+)", rest).group(1))
                v = "obendrauf" if vert > 1.0 else "daneben"
            elif probe.startswith("Melee"):
                pairs = re.findall(r"([\d,]+)\s*%?\s*\(soll (?:je )?([\d,]+)\)", rest)
                if pairs:
                    v = "soll erfuellt" if all(num(a) == num(b) for a, b in pairs) else "soll VERFEHLT"
            if v is not None:
                out[probe + " " + name] = v
        elif m2:
            key = re.split(r"\s+nach\s+|;|:", m2.group(2))[0].strip()[:40]
            out[probe + " " + key] = "-> " + m2.group(3)
        elif re.search(r"ALLE F|FAELLE WIE|WIE ERWARTET", line):
            out[probe + " GESAMT"] = line.split("] ", 1)[-1]
    return out

def lines(path):
    d = {}
    for line in io.open(path, encoding="utf-8"):
        d.setdefault(line.split(":")[0], line.rstrip())
    return d

refs = sys.argv[1].split(",")
new = sys.argv[2]
ref = collections.defaultdict(set)
for r in refs:
    for fn in os.listdir(r):
        if fn.endswith(".txt"):
            for k, v in verdicts(os.path.join(r, fn)).items():
                ref[k].add(v)
neu = {}
for fn in os.listdir(new):
    if fn.endswith(".txt"):
        neu.update(verdicts(os.path.join(new, fn)))

same = diff = 0
for k in sorted(set(ref) | set(neu)):
    a, b = ref.get(k), neu.get(k)
    if a is not None and b in a:
        same += 1
        continue
    diff += 1
    print("URTEIL   %s\n    gestern: %s\n    heute  : %s" % (k, " | ".join(sorted(a)) if a else "-", b or "-"))
print("--- %d Urteile, %d gleich, %d anders" % (same + diff, same, diff))
