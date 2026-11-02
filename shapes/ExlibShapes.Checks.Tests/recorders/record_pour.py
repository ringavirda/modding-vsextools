"""Records the floor flask lane's verdicts on the stage gates' fixtures into $PARITY_OUT (parity/pieces, feeding, pit, parting and fit.json) and writes the fixtures' shapes beside them, reading the lane from $FLOORFLASK.

Run record_st11b.py into the same $FIXTURES_OUT first: the closed pours are its closed-*.json drags and copes.
The lane's gates are called where they are functions: groupcheck.check, closedcheck head, seal and parting, flystd.check_parting, check_lie and check_margins, lathecheck.clear, and pitcolcheck's orient, seat and order, each with the module's tables
pointed at the fixture. Each case holds the pour it is run as ("case") beside the verdict. `enumerated` is the count of subjects the lane reads in the case: the Piece groups, the closed pour's rules (the head, the flood's mouths and its leak, the cast's group pairs), the pit's bands, cores or gates, the parting's pieces and pattern leaves, the fit's pieces and the feeds that do not enter one."""
import ast, contextlib, io, json, os, re, sys
from pathlib import Path

import numpy as np

FF = Path(os.environ["FLOORFLASK"])
OUT = Path(os.environ["PARITY_OUT"])
sys.path.insert(0, str(FF))
os.chdir(FF)
sys.argv = [sys.argv[0]]
import bevel
import closedcheck as cc
import closedhelve as ch
import flystd
import groupcheck
import lathecheck

FACES = ("north", "east", "south", "west", "up", "down")
PARTING = 8.0
FLOOR_PLAN = 12544
(OUT / "parity").mkdir(parents=True, exist_ok=True)


def box(name, lo, hi, tex="andesite"):
    return {"name": name, "from": list(lo), "to": list(hi), "faces": {f: {"texture": "#" + tex} for f in FACES}}


def group(name, *children):
    return {"name": name, "from": [0, 0, 0], "to": [0, 0, 0], "children": list(children)}


def sand(els):
    return {"textures": {"andesite": "block/stone/sand/andesite"}, "elements": els}


def write(path, raw):
    path.write_text(json.dumps(raw, indent=1) + "\n")


def decompose(region, holes):
    """Boxes covering `region` less the union of `holes` (lo, hi pairs)."""
    cuts = [sorted({region[0][a], region[1][a]} | {v for h in holes for v in (h[0][a], h[1][a]) if region[0][a] < v < region[1][a]}) for a in range(3)]
    shape = [len(c) - 1 for c in cuts]
    solid = np.zeros(shape, bool)
    for i in range(shape[0]):
        for j in range(shape[1]):
            for k in range(shape[2]):
                c = [(cuts[0][i] + cuts[0][i + 1]) / 2, (cuts[1][j] + cuts[1][j + 1]) / 2, (cuts[2][k] + cuts[2][k + 1]) / 2]
                solid[i, j, k] = not any(all(h[0][a] < c[a] < h[1][a] for a in range(3)) for h in holes)
    done = np.zeros(shape, bool)
    out = []
    for i in range(shape[0]):
        for j in range(shape[1]):
            for k in range(shape[2]):
                if not solid[i, j, k] or done[i, j, k]:
                    continue
                i1 = i
                while i1 + 1 < shape[0] and solid[i1 + 1, j, k] and not done[i1 + 1, j, k]:
                    i1 += 1
                j1 = j
                while j1 + 1 < shape[1] and solid[i:i1 + 1, j1 + 1, k].all() and not done[i:i1 + 1, j1 + 1, k].any():
                    j1 += 1
                k1 = k
                while k1 + 1 < shape[2] and solid[i:i1 + 1, j:j1 + 1, k1 + 1].all() and not done[i:i1 + 1, j:j1 + 1, k1 + 1].any():
                    k1 += 1
                done[i:i1 + 1, j:j1 + 1, k:k1 + 1] = True
                out.append(((cuts[0][i], cuts[1][j], cuts[2][k]), (cuts[0][i1 + 1], cuts[1][j1 + 1], cuts[2][k1 + 1])))
    return out


def cast_elements(groups):
    """The cast of `groups` {name: (lo, hi)} (one box each) under a zero-sized root; a single group's box stands directly under it."""
    leaves = [{"name": n + "Body", "from": list(lo), "to": list(hi), "faces": {"up": {"texture": "#cast-iron1"}}} for n, (lo, hi) in groups.items()]
    kids = [group(n, lf) for n, lf in zip(groups, leaves)] if len(groups) > 1 else leaves
    return [group("Bed", *kids)]


def cast_file(name, groups):
    write(OUT / name, {"textures": {"cast-iron1": "block/metal/cast-iron1"}, "elements": cast_elements(groups)})
    return name


def run(fn, *args):
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        ret = fn(*args)
    return ret, buf.getvalue()


def entry(exit_, enumerated, faulted, case):
    return {"exit": int(exit_), "enumerated": enumerated, "faulted": sorted(set(faulted)), "case": case}


def at_matrix(x, y, z):
    return [x, y, z]


def world(groups, at):
    """The cast's boxes `groups` {name: (lo, hi)} translated by `at`."""
    return [(tuple(a + b for a, b in zip(lo, at)), tuple(a + b for a, b in zip(hi, at))) for lo, hi in groups.values()]


CASTBOX = {"Body": ((0, 0, 0), (6, 4, 6))}
SPRUE_BOX = [14, 9, 0, 16, 14, 2]
RISER_BOX = [17, 9, -2, 18, 14, -1]
SPRUE = {"name": "Sprue", "kind": "sprue", "boxes": [SPRUE_BOX]}
RISER = {"name": "Riser", "kind": "riser", "boxes": [RISER_BOX]}
RUNNER1 = {"name": "Runner1", "kind": "runner", "boxes": [[15, 13.5, 2.5, 17, 14, 9]]}


# ---- pieces: groupcheck.check on the floor station's fillings

def slab(z_to=14):
    return box("Slab", (2, 2, -14), (30, 14, z_to))


def piece(k, x0, x1, level_top=14, level_x1=None, z0=-3, z1=3):
    return group("Piece%d" % k, group("Bedded", box("Cavity", (x0, 2, z0), (x1, 10, z1))),
                 group("Level", box("Cover", (x0, 10, z0), (level_x1 if level_x1 is not None else x1, level_top, z1))))


def spare(k, x0, x1):
    p = piece(k, x0, x1)
    p["children"].append(group("Spare"))
    return p


PIECES = {
    "pieces-ok": ([slab(), piece(1, 6, 12), piece(2, 20, 26)], 2, "alt"),
    "pieces-flush": ([slab(), piece(1, 6, 12, 13), piece(2, 20, 26)], 2, "alt"),
    "pieces-count": ([slab(), piece(1, 6, 12)], 2, "alt"),
    "pieces-names": ([slab(), piece(1, 6, 12), piece(3, 20, 26)], 2, None),
    "pieces-holds": ([slab(), piece(1, 6, 12), spare(2, 20, 26)], 2, "alt"),
    "pieces-foot": ([slab(), piece(1, 6, 12, level_x1=10), piece(2, 20, 26)], 2, "alt"),
    "pieces-apart": ([slab(), piece(1, 6, 12), piece(2, 10, 16)], 2, None),
    "pieces-cover": ([slab(10), piece(1, 6, 12), piece(2, 20, 26)], 2, "alt"),
    "pieces-order": ([slab(), piece(1, 20, 26), piece(2, 6, 12)], 2, "alt"),
}
pieces_out = {}
for name, (els, count, order) in PIECES.items():
    write(OUT / (name + ".json"), sand(els))
    checks = groupcheck.check(els, count, FLOOR_PLAN, order or "alt")
    faulted = [c for c, ok, _ in checks if not ok and (order or c != "order")]
    found = len([g for n in range(1, 60) for g in groupcheck.find(els, "Piece%d" % n)])
    case = {"shape": name + ".json", "station": "floor", "at": [at_matrix(13 + 8 * i, 10, -3) for i in range(count)], "feeds": [RUNNER1], "order": order}
    pieces_out[name] = entry(bool(faulted), found, faulted, case)
write(OUT / "parity" / "pieces.json", pieces_out)


# ---- the closed pour: closedcheck head, seal and parting, with lathecheck.clear's apart lines for the layout

work = Path(os.environ.get("TMPDIR", "/tmp")) / "record_pour"
work.mkdir(parents=True, exist_ok=True)
ch.PARTING = PARTING
cc.DRAG, cc.COPE = "drag", "cope"
cc.FED = cc.MOUTHS = ((14, 16, 0, 2), (17, 18, -2, -1))
cc.SOLES = ()
clay_floor = box("BoxFloor", (5, 13, 10), (19, 14, 16), "burned")
write(work / "longflask-cope.json", {"textures": {}, "elements": [box("Bar1", (3, 14, -40), (28, 15, -39))]})


def closed_pair(drag, cope):
    raw = json.loads((OUT / cope).read_text())
    raw["textures"]["burned"] = "block/clay/vessel/sides/burned"
    raw["elements"].append(clay_floor)
    write(work / "cope.json", raw)
    write(work / "drag.json", json.loads((OUT / drag).read_text()))


def point_at(groups, at):
    cc.CAST = lambda: [b for lo, hi in world(groups, at) for b in bevel.boxed([box("c", lo, hi)])]


def clear_lines(groups, at, feeds):
    """The output of lathecheck.clear on a cast of `groups` placed by `at` with the lane's `feeds` {name: (x0, x1, z0, z1, y0, y1)}."""
    lathecheck.CAST["synth"] = "synth"
    els = cast_elements(groups)
    lie = np.eye(4)
    lie[:3, 3] = at
    flystd.KINDS["synth"] = {"lie": lie, "copies": ((0.0, 0.0, 0.0),), "feeds": feeds, "drag": [n for n in feeds if n != "Sprue"], "cope": ["Sprue"],
                             "parting": (1, PARTING - at[1])}
    lathecheck.read = lambda d, name: els
    _, out = run(lathecheck.clear, "synth", "")
    return out, els


def categories_from_clear(out):
    cats = []
    for line in out.splitlines():
        if not line.endswith("FAIL"):
            continue
        m = re.search(r"sand under ([-\d.]+), over ([-\d.]+)", line)
        if m:
            under, over = float(m.group(1)), float(m.group(2))
            cats.append("floor" if under < 1 - 1e-4 else "roof" if over < 1 - 1e-4 else "wall")
        elif re.match(r"\S+ to Runner\d+ ", line):
            cats.append("runner")
        else:
            cats.append("layout")
    return cats


def feeds_for(at, x0=None):
    x = at[0] + 2 if x0 is None else x0
    y = at[1] + 0.5
    return {"Sprue": (x, x + 2, 8, 10, 8.0, 14.0), "Runner1": (x, x + 2, 6, 8, y, y + 1), "Gate1": (x, x + 2, 3, 6, y, y + 1)}


def feed_json(f):
    kinds = {"Sprue": "sprue", "Runner1": "runner", "Gate1": "gate"}
    return [{"name": n, "kind": kinds[n], "boxes": [[v[0], v[4], v[2], v[1], v[5], v[3]]]} for n, v in f.items()]


def run_closed(drag, cope, groups, at):
    closed_pair(drag, cope)
    point_at(groups, at)
    faulted = []
    head_ok, _ = run(cc.head, str(work))
    seal_ok, _ = run(cc.seal, str(work))
    if not head_ok:
        faulted.append("head")
    if not seal_ok:
        faulted.append("seal")
    out, _ = clear_lines(groups, at, feeds_for(at))
    pairs = len(groups) * (len(groups) - 1) // 2
    faulted += ["layout" for c in categories_from_clear(out) if c == "layout"]
    return faulted, 1 + len(cc.MOUTHS) + 1 + pairs


tall_cope = decompose(((2, PARTING, -46), (30, 14, 14)), [((13, PARTING, -3), (19, 13.5, 3)), tuple(map(tuple, (SPRUE_BOX[:3], SPRUE_BOX[3:]))), tuple(map(tuple, (RISER_BOX[:3], RISER_BOX[3:])))])
write(OUT / "closed-cope-tall.json", sand([box("Sand%d" % n, lo, hi) for n, (lo, hi) in enumerate(tall_cope, 1)]))
TWO = lambda right: {"Left": ((0, 0, 0), (2, 4, 6)), "Right": ((right, 0, 0), (6, 4, 6))}
CLOSED = {
    "closed-ok": ("closed-drag.json", "closed-cope.json", CASTBOX, (13, 5, -3), None),
    "closed-head": ("closed-drag.json", "closed-cope-tall.json", {"Body": ((0, 0, 0), (6, 8.5, 6))}, (13, 5, -3), "pcast-tall.json"),
    "closed-leak": ("closed-drag-leak.json", "closed-cope.json", CASTBOX, (13, 5, -3), None),
    "closed-blocked": ("closed-drag.json", "closed-cope-blocked.json", CASTBOX, (13, 5, -3), None),
    "closed-vent": ("closed-drag.json", "closed-cope-vent.json", CASTBOX, (13, 5, -3), None),
    "closed-groups": ("closed-drag.json", "closed-cope.json", TWO(3), (13, 5, -3), "pcast-groups.json"),
    "closed-groups-near": ("closed-drag.json", "closed-cope.json", TWO(2.5), (13, 5, -3), "pcast-groups-near.json"),
}
feeding_out = {}
for name, (drag, cope, groups, at, cast) in CLOSED.items():
    if cast:
        cast_file(cast, groups)
    faulted, count = run_closed(drag, cope, groups, at)
    case = {"shape": drag, "station": "long", "mould": "closed", "pair": cope, "cast": cast or "fill-cast.json", "at": [list(at)], "feeds": [SPRUE, RISER]}
    if name == "closed-vent":
        case["portOmits"] = ["head"]
    feeding_out[name] = entry(bool(faulted), count, faulted, case)
write(OUT / "parity" / "feeding.json", feeding_out)


def parting_text(out):
    high = float(re.search(r"drag's sand ([\d.]+)", out).group(1))
    low = float(re.search(r"cope's ([\d.]+)", out).group(1))
    both = int(re.search(r"both halves (\d+)", out).group(1))
    return (["over"] if high > PARTING + cc.PARTING_TOL else []) + (["under"] if low < PARTING - cc.PARTING_TOL else []) + (["shared"] if both else [])


def pattern_file(name, drag_hi, cope_lo):
    leaf = lambda n, lo, hi: {"name": n, "from": list(lo), "to": list(hi), "faces": {"up": {"texture": "#cast-iron1"}}}
    raw = {"textures": {"cast-iron1": "block/metal/cast-iron1"},
           "elements": [group("Drag", leaf("DragBody", (0, 0, 0), (6, drag_hi, 6))), group("Cope", leaf("CopeBody", (0, cope_lo, 0), (6, 4, 6)))]}
    write(OUT / name, raw)
    return raw


def spans(at, pattern):
    """flystd.check_parting on the cast placed by `at` and its pattern roots: the categories it faults."""
    flystd.KINDS["synth"] = {"lie": np.eye(4), "copies": ((0.0, 0.0, 0.0),), "parting": (1, PARTING - at[1])}
    els = json.loads((OUT / "fill-cast.json").read_text())["elements"]
    _, out = run(flystd.check_parting, "synth", els, pattern["elements"] if pattern else [group("Drag"), group("Cope")])
    cats = []
    if int(re.search(r"(\d+) do not span", out).group(1)):
        cats.append("span")
    if re.search(r"Drag past the parting \[[^\]]", out):
        cats.append("drag")
    if re.search(r"Cope past the parting \[[^\]]", out):
        cats.append("cope")
    return cats


pattern_file("pparting-ok.json", 3, 3)
pattern_file("pparting-drag.json", 3.5, 3)
pattern_file("pparting-cope.json", 3, 2.5)
PARTINGS = {
    "parting-ok": ("closed-drag.json", "closed-cope.json", (13, 5, -3), "pparting-ok.json"),
    "parting-over": ("closed-drag-over.json", "closed-cope.json", (13, 5, -3), None),
    "parting-under": ("closed-drag.json", "closed-cope-under.json", (13, 5, -3), None),
    "parting-shared": ("closed-drag-over.json", "closed-cope-shared.json", (13, 5, -3), None),
    "parting-above": ("closed-drag.json", "closed-cope.json", (13, 9, -3), None),
    "parting-below": ("closed-drag.json", "closed-cope.json", (13, 1, -3), None),
    "parting-pattern-drag": ("closed-drag.json", "closed-cope.json", (13, 5, -3), "pparting-drag.json"),
    "parting-pattern-cope": ("closed-drag.json", "closed-cope.json", (13, 5, -3), "pparting-cope.json"),
}
parting_out = {}
for name, (drag, cope, at, pattern) in PARTINGS.items():
    closed_pair(drag, cope)
    _, out = run(cc.parting, str(work))
    faulted = parting_text(out) + spans(at, json.loads((OUT / pattern).read_text()) if pattern else None)
    leaves = 2 if pattern else 0
    case = {"shape": drag, "station": "long", "mould": "closed", "pair": cope, "cast": "fill-cast.json", "at": [list(at)], "feeds": [SPRUE, RISER]}
    if pattern:
        case["pattern"] = pattern
    parting_out[name] = entry(bool(faulted), 1 + 1 + 1 + leaves, faulted, case)


def lie_case(name, matrix):
    kind = {"lie": np.array(matrix, float).reshape(4, 4), "copies": ((0.0, 0.0, 0.0),), "parting": (1, 3.0)}
    flystd.KINDS["synth"] = kind
    ok, out = run(flystd.check_lie, "synth")
    case = {"shape": "fill-ok.json", "station": "floor", "at": [matrix], "feeds": [RUNNER1]}
    parting_out[name] = entry(not ok, 1, [] if ok else ["lie"], case)


lie_case("lie-ok", [1, 0, 0, 13, 0, 1, 0, 10, 0, 0, 1, -3, 0, 0, 0, 1])
lie_case("lie-turn", [0, 0, 1, 13, 0, 1, 0, 10, -1, 0, 0, -3, 0, 0, 0, 1])
lie_case("lie-mirror", [-1, 0, 0, 13, 0, 1, 0, 10, 0, 0, 1, -3, 0, 0, 0, 1])
lie_case("lie-scaled", [2, 0, 0, 13, 0, 1, 0, 10, 0, 0, 1, -3, 0, 0, 0, 1])
write(OUT / "parity" / "parting.json", parting_out)


# ---- fit: flystd.check_margins and lathecheck.clear on the long station

def gap3(a, b):
    return max(max(a[0][i] - b[1][i], b[0][i] - a[1][i]) for i in range(3))


def fit_case(name, at, size=(6, 4, 6), feeds=None, omits=()):
    groups = {"Body": ((0, 0, 0), size)}
    cast_file("pcast-" + name + ".json", groups)
    f = feeds or feeds_for(at)
    out, els = clear_lines(groups, at, f)
    _, text = run(flystd.check_margins, "synth", els)
    bad = ast.literal_eval(re.search(r"short of [\d.]+: (\[.*?\]);", text).group(1))
    walls = {"floor": "floor", "top": "roof"}
    cats = categories_from_clear(out) + [walls.get(b, "runner" if "leaves" in b else "wall") for b in bad]
    placed = world(groups, at)[0]
    entered = [n for n, v in f.items() if gap3(((v[0], v[4], v[2]), (v[1], v[5], v[3])), placed) <= 1e-9]
    case = {"shape": "closed-drag.json", "station": "long", "mould": "closed", "pair": "closed-cope.json", "cast": "pcast-" + name + ".json", "at": [list(at)],
            "feeds": feed_json(f)}
    if omits:
        case["portOmits"] = list(omits)
    return entry(bool(cats), 1 + len(f) - len(entered), cats, case)


fit_out = {}
fit_out["fit-ok"] = fit_case("fit-ok", (13, 5, -3))
fit_out["fit-floor"] = fit_case("fit-floor", (13, 2.5, -3))
fit_out["fit-roof"] = fit_case("fit-roof", (13, 9.5, -3))
fit_out["fit-west"] = fit_case("fit-west", (2.5, 5, -3))
near = feeds_for((13, 5, -3))
near["Runner1"] = (15, 17, 3.5, 8, 5.5, 6.5)
fit_out["fit-runner"] = fit_case("fit-runner", (13, 5, -3), feeds=near)
fit_out["fit-cope-wall"] = fit_case("fit-cope-wall", (4.2, 5, -3), omits=["wall"])
above = feeds_for((13, 5, -3))
above["Runner1"] = (15, 17, 3.5, 8, 11.0, 12.0)
fit_out["fit-runner-above"] = fit_case("fit-runner-above", (13, 5, -3), feeds=above, omits=["runner"])
write(OUT / "parity" / "fit.json", fit_out)


# ---- pit: pitcolcheck's orient, seat and order with the part table pointed at the fixture

def load_pitcheck():
    src = (FF / "pitcolcheck.py").read_text().splitlines(True)[:-3]
    argv = sys.argv
    sys.argv = ["pitcolcheck.py", "baseround", "orient", str(FF / "runtime")]
    ns = {"__name__": "pitcolcheck", "__file__": str(FF / "pitcolcheck.py")}
    exec(compile("".join(src), "pitcolcheck.py", "exec"), ns)
    sys.argv = argv
    return ns


pit = load_pitcheck()
PIT = {}
pit_out = {}


def banded(name, bands):
    groups = {"B%d" % i: ((3 - r, y0, 3 - r), (3 + r, y1, 3 + r)) for i, (y0, y1, r) in enumerate(bands)}
    cast_file(name + ".json", {n: g for n, g in groups.items()})
    leaves = [{"name": n + "Body", "from": list(lo), "to": list(hi), "faces": {"up": {"texture": "#cast-iron1"}}} for n, (lo, hi) in groups.items()]
    write(OUT / (name + ".json"), {"textures": {"cast-iron1": "block/metal/cast-iron1"}, "elements": [group("Bed", *leaves)]})
    pit["SPEC"] = {"bands": [(y0, y1, r, "sq") for y0, y1, r in bands], "cells": [(16.0, 0.0)], "half": 3.0}
    pit["WHICH"] = "shaft"
    rc, _ = run(pit["check_orient"], [], None)
    case = {"shape": "fill-ok.json", "station": "pit", "cast": name + ".json", "at": [[13, 0, -3]], "feeds": [RUNNER1]}
    pit_out[name] = entry(rc, len(bands) - 1, ["band"] if rc else [], case)


banded("pit-orient-ok", [(0, 2, 2), (2, 6, 3)])
banded("pit-orient-wide", [(0, 2, 3), (2, 6, 2)])


def seated(name, soil_top, foot):
    prism = ((8.5, soil_top, -4.5), (13.5, foot + 8, 0.5))
    els = [box("Floor1", (2, -14, -14), (30, soil_top, 14))]
    els += [box("Sand%d" % n, lo, hi) for n, (lo, hi) in enumerate(decompose(((2, soil_top, -14), (30, 14, 14)), [prism]), 1)]
    els.append(group("Core1", box("Stem", (8.5, foot, -4.5), (13.5, foot + 8, 0.5))))
    write(OUT / (name + ".json"), sand(els))
    pit["SPEC"] = {"bands": [], "cells": [(11.0, -2.0)], "half": 3.0}
    pit["SQUARE"] = True
    pit["colpit"].CORE_R = 2.5
    rc, out = run(pit["check_seat"], els, None)
    faulted = []
    for line in out.splitlines():
        if line.endswith("FAIL"):
            faulted.append("floor" if line.startswith("floor sand") else "core")
    case = {"shape": name + ".json", "station": "pit", "at": [[13, 0, -3]], "feeds": [RUNNER1]}
    pit_out[name] = entry(rc, 1, faulted, case)


seated("pit-seat-ok", -5, -5)
seated("pit-seat-thin", -13.5, -13.5)
seated("pit-seat-air", -5, -4)


def ordered(name, ats, gates, order):
    cells = [(16.0, az) for az in ats]
    pit["SPEC"] = {"bands": [], "cells": cells, "half": 3.0}
    pit["colpit"].gates = lambda part, gates=gates: [(15.0, 17.0, 0.0, 10.0)] + gates
    rc, out = run(pit["check_order"], [], None)
    feeds = [RUNNER1] + [{"name": "Gate%d" % (k + 1), "kind": "gate", "into": k + 1, "boxes": [[g[0], 13, g[2], g[1], 14, g[3]]]} for k, g in enumerate(gates)]
    case = {"shape": "fill-ok.json", "station": "pit", "at": [[13, 0, az - 3] for az in ats], "feeds": feeds, "order": order}
    pit_out[name] = entry(rc, len(gates), ["order"] if rc else [], case)


ordered("pit-order-ok", [0.0, -9.0], [(14.0, 15.0, -0.5, 0.5), (17.0, 18.0, -9.5, -8.5)], "alt")
ordered("pit-order-near", [0.0, 9.0], [(14.0, 15.0, -0.5, 0.5), (17.0, 18.0, 8.5, 9.5)], "alt")
ordered("pit-order-side", [0.0, -9.0], [(14.0, 15.0, -0.5, 0.5), (14.0, 15.0, -9.5, -8.5)], "alt")
ordered("pit-order-off", [0.0, -9.0], [(14.0, 15.0, 7.5, 8.5), (17.0, 18.0, -9.5, -8.5)], "alt")
write(OUT / "parity" / "pit.json", pit_out)
