"""Records the floor flask lane's lathecheck verdicts on small synthetic castings and patterns into $PARITY_OUT, reading the lane from $FLOORFLASK."""
import ast, contextlib, io, json, os, re, shutil, sys, tempfile
from pathlib import Path

FF = Path(os.environ["FLOORFLASK"])
OUT = Path(os.environ["PARITY_OUT"])
sys.path.insert(0, str(FF))
os.chdir(FF)
import closedcheck as cc
import flystdsand
import lathecheck as lc

FACES = ("north", "east", "south", "west", "up", "down")
KIND = "lathebed"


def box(name, frm, to, tex="cast-iron1"):
    return {"name": name, "from": frm, "to": to, "faces": {f: {"texture": "#" + tex} for f in FACES}}


def shape(*groups):
    return {
        "textures": {"cast-iron1": "block/metal/cast-iron1", "iron5": "block/metal/sheet-plain/iron5", "wood": "block/wood/planks/{wood}1"},
        "elements": [{"name": n, "from": [0, 0, 0], "to": [0, 0, 0], "children": kids} for n, kids in groups],
    }


def way(top):
    return box("Way", [4, 4, 0], [12, top, 8])


SEAT, LUG = box("Seat", [4, 2, 0], [12, 4, 8]), box("Lug", [12, 2, 2], [14, 4, 4])
IRON_LUG = {**LUG, "faces": {f: {"texture": "#iron5"} for f in FACES}}
BOLT = box("Bolt", [6, 5.125, 3], [7, 6.125, 4], "iron5")
CAST = {
    "cast-source.json": [SEAT, way(5), LUG],
    "cast-ok.json": [SEAT, way(5.125), LUG],
    "cast-allow-short.json": [SEAT, way(5), LUG],
    "cast-allow-extra.json": [{**SEAT, "to": [12, 4.125, 8]}, way(5.125), LUG],
    "cast-bolt.json": [SEAT, way(5.125), LUG, BOLT],
    "cast-iron-face.json": [SEAT, way(5.125), IRON_LUG],
}
PATTERNS = {
    "pattern-ok.json": ([box("DragBody", [6, 3, 4], [10, 8, 8], "wood"), box("DragBoss", [7, 5, 5], [9, 8, 7], "wood")], [box("CopeBody", [6, 8, 4], [10, 11, 8], "wood")]),
    "pattern-step.json": ([box("DragA", [6, 3, 4], [10, 8, 6], "wood"), box("DragB", [6, 5, 6], [10, 8, 8], "wood")], [box("CopeBody", [6, 8, 4], [10, 11, 8], "wood")]),
    "pattern-gap.json": ([box("DragLow", [6, 3, 4], [10, 5, 8], "wood"), box("DragHigh", [6, 5.5, 4], [10, 8, 8], "wood")], [box("CopeBody", [6, 8, 4], [10, 11, 8], "wood")]),
    "pattern-gap-cope.json": ([box("DragBody", [6, 3, 4], [10, 8, 8], "wood")], [box("CopeLow", [6, 8, 4], [10, 9, 8], "wood"), box("CopeHigh", [6, 9.5, 4], [10, 11, 8], "wood")]),
}


def write(path, raw):
    path.write_text(json.dumps(raw, indent=1) + "\n")


(OUT / "parity").mkdir(parents=True, exist_ok=True)
work = Path(tempfile.mkdtemp())
try:
    for name, leaves in CAST.items():
        write(OUT / name, shape(("Bed", leaves)))
    for name, (drag, cope) in PATTERNS.items():
        write(OUT / name, shape(("Drag", drag), ("Cope", cope)))
    for f in (*CAST, *PATTERNS):
        shutil.copy(OUT / f, work / f)

    def run(fn, *args):
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            ok = fn(*args)
        return ok, buf.getvalue().splitlines()

    lc.ALLOW[KIND] = {"Way": ((1, 1),)}
    lc.drawn = lambda kind: {"Bed": {e["name"]: e for e in CAST["cast-source.json"]}}
    lc.WORKED[KIND] = "cast-source"
    allow, castonly, role, draw = {}, {}, {}, {}
    for name in ("cast-ok.json", "cast-allow-short.json", "cast-allow-extra.json", "cast-iron-face.json"):
        lc.CAST[KIND] = name[:-5]
        ok, out = run(lc.allow, KIND, str(work))
        m = re.match(r"cast\s+Bed\s+\d+ leaves; missing (\[.*?\]), extra (\[.*?\]), off the drawing plus the allowance (\[.*?\])", out[0])
        bad = [n for part in m.groups() for n in ast.literal_eval(part)]
        allow[name] = {"exit": 0 if ok else 1, "enumerated": len(CAST["cast-source.json"]), "faulted": sorted(bad)}
    lc.drawn = lambda kind: {"Bed": {n: {} for n in ("Seat", "Way", "Lug")}}
    for name in ("cast-ok.json", "cast-bolt.json", "cast-iron-face.json"):
        lc.CAST[KIND], lc.WORKED[KIND], lc.PATTERNS[KIND] = name[:-5], name[:-5], name[:-5]
        ok, out = run(lc.bolt, KIND, str(work))
        m = re.match(r"cast\s+(\d+) leaves; outside the part's cast leaves (\[.*?\]); not cast-iron1 (\[.*?\])", out[0])
        leaves, stray, iron = int(m.group(1)), ast.literal_eval(m.group(2)), ast.literal_eval(m.group(3))
        castonly[name] = {"exit": 1 if stray else 0, "enumerated": leaves, "faulted": sorted(stray)}
        role[name] = {"exit": 1 if iron else 0, "enumerated": leaves * 6, "faulted": sorted(iron)}
    del role["cast-bolt.json"]

    def occupancy(kind, d, prints=True):
        els = {e["name"]: e for e in cc.load(d, current)}
        return [cc.occupancy_of(__import__("bevel").boxed([els[n]])) for n in ("Drag", "Cope")]

    flystdsand.pattern_occupancy = occupancy
    for name in PATTERNS:
        current = name[:-5]
        ok, out = run(lc.draw, KIND, str(work))
        halves = [re.match(r"(Drag|Cope): (\d+) columns occupied, (\d+) not unbroken", l).groups() for l in out]
        draw[name] = {"exit": 0 if ok else 1, "enumerated": sum(int(h[1]) for h in halves), "faulted": sorted(h[0] for h in halves if int(h[2]))}
    for name, rec in (("allowance", allow), ("castonly", castonly), ("role", role), ("draw", draw)):
        write(OUT / "parity" / (name + ".json"), rec)
finally:
    shutil.rmtree(work)
