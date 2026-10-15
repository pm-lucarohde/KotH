"""Vergleicht die ZAHLEN eines Probenlaufs mit Toleranz gegen eine Referenzmenge.

Aufruf: regress.py ref1,ref2,ref3 neu [--alle]

Jede Messzeile wird ueber ihren Fallnamen zugeordnet, Tick-Spuren (~) ueber
Fall und Tick. Der Text ohne Zahlen
muss gleich sein (sonst STRUKTUR), jede Zahl darf um 5 mm (0,005) vom
naechsten Referenzwert abweichen oder muss zwischen den Werten der
Referenzlaeufe liegen (Ramp45/55 streuen schon in Unity). Was nur die 1 %
einhaelt, steht als NUR 1 % da; eine Einheit der letzten Anzeigestelle
zaehlt als Anzeigerundung (Mono rundete den 7-stelligen Text). Was nur 1 %
einhaelt, steht als NUR 1 % da: fuer Tempo und Prozent gilt es, fuer
Koordinaten nicht. Alles wird mit beiden Zeilen gemeldet und von Hand
begruendet.
"""
import io, os, re, sys, collections

NUM = re.compile(r"(?<![\w])[+-]?\d+(?:[.,]\d+)?(?:E[+-]?\d+)?")
ABS, REL = 0.005, 0.01


def key_of(line):
    m = re.match(r"^\[(\w+)\] == ([^:]+):(.*)$", line)
    if m:
        return "== " + m.group(2).strip()
    m = re.match(r"^\[(\w+)\] (.+)$", line)
    if m and m.group(2).startswith("~"):
        # Tick traces: case name and tick, e.g. "~ Ramp45 stehen t003".
        return "~~ " + re.split(r" y |:|\|", m.group(2))[0].strip()
    if m:
        return "-- " + re.sub(NUM, "#", m.group(2).split(":")[0])[:40]
    return None


def cases(path):
    out = collections.OrderedDict()
    seen = collections.Counter()
    for line in io.open(path, encoding="utf-8"):
        line = line.rstrip("\n")
        if "entering play mode" in line:
            continue
        k = key_of(line)
        if k is None:
            continue
        seen[k] += 1
        out["%s #%d" % (k, seen[k]) if seen[k] > 1 else k] = line
    return out


def numbers(text):
    return [float(n.replace(",", ".")) for n in NUM.findall(text)]


def last_place(text):
    """Size of one unit in the last printed digit of every number."""
    out = []
    for n in NUM.findall(text):
        m = re.search(r"[.,](\d+)", n)
        out.append(10.0 ** -len(m.group(1)) if m and "E" not in n else 0.0)
    return out


def skeleton(text):
    return NUM.sub("#", text)


def within_abs(a, b):
    return abs(a - b) <= ABS


def within_rel(a, b):
    return abs(a - b) <= REL * max(abs(a), abs(b))


def main():
    refdirs = sys.argv[1].split(",")
    newdir = sys.argv[2]
    show_all = "--alle" in sys.argv
    totals = collections.Counter()

    for fn in sorted(os.listdir(newdir)):
        if not fn.endswith(".txt"):
            continue
        refs = [cases(os.path.join(d, fn)) for d in refdirs if os.path.exists(os.path.join(d, fn))]
        if not refs:
            print("KEINE REFERENZ  %s" % fn)
            continue
        new = cases(os.path.join(newdir, fn))
        report = []
        stats = collections.Counter()

        for k, line in new.items():
            stats["faelle"] += 1
            known = [r[k] for r in refs if k in r]
            if not known:
                stats["neu"] += 1
                report.append("NEU       %s\n    neu : %s" % (k, line))
                continue
            same_shape = [r for r in known if skeleton(r) == skeleton(line)]
            if not same_shape:
                stats["struktur"] += 1
                report.append("STRUKTUR  %s\n    neu : %s\n    ref : %s" % (k, line, "\n    ref : ".join(known)))
                continue
            vals = numbers(line)
            places = last_place(line)
            refvals = [numbers(r) for r in same_shape]
            bad, rel, shown = [], [], []
            for i, v in enumerate(vals):
                rv = [r[i] for r in refvals]
                if min(rv) <= v <= max(rv) or any(within_abs(v, x) for x in rv):
                    continue
                # One unit in the last printed digit of a half-way value:
                # Mono rounded the float's 7-digit text, .NET the exact value.
                if places[i] > 0 and any(abs(abs(v - x) - places[i]) < places[i] * 1e-3 for x in rv) \
                        and places[i] <= 0.01:
                    shown.append("#%d: %g statt %s" % (i + 1, v, "/".join("%g" % x for x in sorted(set(rv)))))
                    continue
                nearest = min(rv, key=lambda x: abs(x - v))
                text = "#%d: %g statt %s (Abstand %.4g)" % (i + 1, v, "/".join("%g" % x for x in sorted(set(rv))), abs(v - nearest))
                # 1 % only holds for speeds, ticks and percent, never for a
                # coordinate; which one a number is, a human decides.
                (rel if any(within_rel(v, x) for x in rv) else bad).append(text)
            if shown and not (bad or rel):
                stats["anzeige"] += 1
                if show_all:
                    report.append("ANZEIGE   %s\n    %s" % (k, "; ".join(shown)))
            if bad or rel:
                stats["ueber" if bad else "relativ"] += 1
                report.append("%s  %s\n    %s\n    neu : %s\n    ref : %s" % (
                    "TOLERANZ" if bad else "NUR 1 % ", k, "; ".join(bad + rel), line, "\n    ref : ".join(same_shape)))
            elif not shown:
                stats["ok"] += 1
                if show_all and line not in known:
                    report.append("ok        %s\n    neu : %s\n    ref : %s" % (k, line, known[0]))

        missing = [k for k in set().union(*refs) if k not in new]
        for k in sorted(missing):
            stats["fehlt"] += 1
            report.append("FEHLT     %s" % k)

        print("=== %s: %d Faelle, %d in Toleranz, %d Anzeigerundung, %d nur in 1 %%, %d darueber, %d Struktur, %d neu, %d fehlen" % (
            fn, stats["faelle"], stats["ok"], stats["anzeige"], stats["relativ"], stats["ueber"], stats["struktur"], stats["neu"], stats["fehlt"]))
        for r in report:
            print(r)
        totals.update(stats)

    print("--- gesamt: %d Faelle, %d in Toleranz, %d Anzeigerundung, %d nur in 1 %%, %d darueber, %d Struktur, %d neu, %d fehlen" % (
        totals["faelle"], totals["ok"], totals["anzeige"], totals["relativ"], totals["ueber"], totals["struktur"], totals["neu"], totals["fehlt"]))


main()
