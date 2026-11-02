"""Records the floor flask lane's filling verdicts on small synthetic floor flask fillings into $PARITY_OUT, reading the lane from $FLOORFLASK.

Each filling is the station's sand less a cast box, the cuts of its runner and the runner box's seat, with the lane's seven runner box elements."""
import contextlib, io, json, os, re, shutil, subprocess, sys, tempfile
from pathlib import Path

import numpy as np

FF = Path(os.environ["FLOORFLASK"])
OUT = Path(os.environ["PARITY_OUT"])
sys.path.insert(0, str(FF))
os.chdir(FF)
import gate
sys.argv = [sys.argv[0]]
impcheck = type(sys)("impcheck")
impcheck.__file__ = str(FF / "impcheck.py")
exec(compile("".join((FF / "impcheck.py").read_text().splitlines(True)[:1828]), impcheck.__file__, "exec"), impcheck.__dict__)
import shaper

FACES = ("north", "east", "south", "west", "up", "down")
STATION = ((2, 2, -14), (30, 14, 14))
SEAT = ((5, 13, 10), (19, 16, 16))
BOX7 = [
    ("BoxFloor", (5, 13, 10), (19, 14, 16)),
    ("BoxWallS1", (5, 14, 15), (7, 16, 16)),
    ("BoxWallS2", (9, 14, 15), (19, 16, 16)),
    ("BoxWallN1", (5, 14, 10), (15, 16, 11)),
    ("BoxWallN2", (17, 14, 10), (19, 16, 11)),
    ("BoxWallW", (5, 14, 11), (6, 16, 15)),
    ("BoxWallE", (18, 14, 11), (19, 16, 15)),
]
CAST = ((0, 0, 0), (6, 4, 6))
DROP = ((15, 13.5, 9), (17, 14, 10))
RUN_X = (15, 17)


def face_map(tex):
    return {f: {"texture": "#" + tex} for f in FACES}


def box(name, lo, hi, tex):
    return {"name": name, "from": list(lo), "to": list(hi), "faces": face_map(tex)}


def decompose(region, holes):
    """Maximal-ish boxes covering `region` less the union of `holes` (lo, hi pairs)."""
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


def placed(at):
    return (tuple(c + a for c, a in zip(CAST[0], at)), tuple(c + a for c, a in zip(CAST[1], at)))


def filling(pieces, cuts, extra_sand=(), runner=BOX7, extra_seat_sand=()):
    holes = [placed(at) for at in pieces] + list(cuts) + [SEAT]
    els = [box("Sand%d" % n, lo, hi, "andesite") for n, (lo, hi) in enumerate(decompose(STATION, holes), 1)]
    els += [box("Sand%dX" % n, lo, hi, "andesite") for n, (lo, hi) in enumerate(extra_sand, 1)]
    els += [box("Seat%d" % n, lo, hi, "andesite") for n, (lo, hi) in enumerate(extra_seat_sand, 1)]
    els += [box(n, lo, hi, "burned") for n, lo, hi in runner]
    return {"textures": {"andesite": "block/stone/sand/andesite", "burned": "block/clay/vessel/sides/burned"}, "elements": els}


ONE = [(13, 10, -3)]
RUN1 = (((15, 13.5, 2.5), (17, 14, 9)),)
DROPCUT = (DROP,)
TWO = [(13, 10, -3), (13, 10, -13)]
GATE2 = ((15, 13.5, -7.5), (17, 14, -2.5))
DAM = ((15, 13.5, 5), (17, 14, 6))
GATE_DAM = ((15, 13.5, -6), (17, 14, -5))
WITHOUT = lambda name: [b for b in BOX7 if b[0] != name]
LOWERED = [(n, lo, (hi[0], 13.5, hi[2])) if n == "BoxFloor" else (n, lo, hi) for n, lo, hi in BOX7]

IMPRESSION = {
    "fill-ok": filling(ONE, RUN1 + DROPCUT),
    "fill-in": filling(ONE, RUN1 + DROPCUT, extra_sand=[((18, 10, -1), (19, 14, 1))]),
    "fill-open": filling([], [placed(ONE[0]), ((19, 10, -3), (20, 14, 3))] + list(RUN1 + DROPCUT)),
}
FLOOD = {
    "fill-ok": IMPRESSION["fill-ok"],
    "fill-dam": filling(ONE, RUN1 + DROPCUT, extra_sand=[DAM]),
    "fill-seed": filling(ONE, RUN1 + DROPCUT, extra_sand=[DROP]),
    "fill-chain": filling(TWO, RUN1 + DROPCUT + (GATE2,)),
    "fill-chain-dam": filling(TWO, RUN1 + DROPCUT + (GATE2,), extra_sand=[GATE_DAM]),
}
RUNNER = {
    "fill-ok": IMPRESSION["fill-ok"],
    "fill-nowall": filling(ONE, RUN1 + DROPCUT, runner=WITHOUT("BoxWallW")),
    "fill-seat": filling(ONE, RUN1 + DROPCUT, extra_seat_sand=[((6, 13, 12), (8, 14, 13))]),
    "fill-lowered": filling(ONE, RUN1 + DROPCUT, runner=LOWERED),
}


def write(path, raw):
    path.write_text(json.dumps(raw, indent=1) + "\n")


def cast_item():
    return {
        "textures": {"cast-iron1": "block/metal/cast-iron1"},
        "elements": [{"name": "Bed", "from": [0, 0, 0], "to": [0, 0, 0], "children": [box("Body", CAST[0], CAST[1], "cast-iron1")]}],
    }


def parity(name, exit_, enumerated, faulted):
    return {"exit": exit_, "enumerated": enumerated, "faulted": faulted}


(OUT / "parity").mkdir(parents=True, exist_ok=True)
work = Path(tempfile.mkdtemp())
try:
    names = {}
    for table in (IMPRESSION, FLOOD, RUNNER):
        names.update(table)
    for name, raw in names.items():
        write(OUT / (name + ".json"), raw)
        write(work / ("floorflask-filling-%s.json" % name[5:]), raw)
    write(OUT / "fill-cast.json", cast_item())
    island = cast_item()
    island["elements"][0]["children"].append(box("Island", (8, 0, 0), (10, 4, 2), "cast-iron1"))
    write(OUT / "fill-island-cast.json", island)
    write(OUT / "fill-island.json", filling([], [placed(ONE[0]), ((21, 10, -3), (23, 14, -1))] + list(RUN1 + DROPCUT)))
    write(work / "castbox.json", cast_item())

    def cut_inside(x, y, z, tag):
        return (x >= 15) & (x <= 17) & (z >= 2.5) & (z <= 10) & (y >= 13.5)

    gate.inside = cut_inside
    impcheck.PIECES["synth"] = ("castbox", 1, 1)
    impcheck.PIECE_PATHS["synth"] = str(work / "castbox.json")
    impression = {}
    for name in IMPRESSION:
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            fail = impcheck.piece_check("synth", str(work / ("floorflask-filling-%s.json" % name[5:])))
        counts = {m.group(1): int(m.group(2)) for m in re.finditer(r"^(in|open far|open)\s+(\d+) samples", buf.getvalue(), re.M)}
        faulted = [k for k in ("in", "open") if counts[{"in": "in", "open": "open far"}[k]]]
        impression[name] = parity(name, fail, 1, sorted(faulted))
    write(OUT / "parity" / "impression.json", impression)

    flood = {}
    for name in FLOOD:
        p = subprocess.run([sys.executable, "gatecheck.py", str(work), name[5:]], capture_output=True, text=True)
        out = p.stdout
        m = re.search(r"under the runner's end\s+([\d.]+)", out)
        faulted = []
        if p.returncode:
            faulted = ["seed"] if float(m.group(1)) == 0 else ["unreached"]
        flood[name] = parity(name, p.returncode, 1, faulted)
    write(OUT / "parity" / "flood.json", flood)

    runner_dir = Path(tempfile.mkdtemp())
    try:
        for name in RUNNER:
            write(runner_dir / ("floorflask-filling-%s.json" % name[5:]), RUNNER[name])
        for shell in ("sandcastingfloorflask", "sandcastingpitflask", "sandcastinglongflask"):
            write(runner_dir / (shell + ".json"), {"textures": {}, "elements": []})
        for base in ("floorflask", "pitflask", "longflask"):
            write(runner_dir / (base + "-filling-base.json"), {"textures": {"burned": "x"}, "elements": [box(n, lo, hi, "burned") for n, lo, hi in BOX7]})
        for n in ("helve", "standard", "camring"):
            write(runner_dir / ("longflask-filling-castbellyhelve%s.json" % n), {"textures": {}, "elements": []})
        for n in ("castanvil", "castbeam", "castflywheelstandard", "castflywheellargepedestal", "castflywheellargebearer", "castflywheellargeendframe"):
            write(runner_dir / ("longflask-filling-%s.json" % n), {"textures": {}, "elements": []})
        p = subprocess.run([sys.executable, "runnercheck.py", str(runner_dir)], capture_output=True, text=True)
        rows = {}
        for line in p.stdout.splitlines():
            m = re.match(r"(\S+)\s+(\d+) clay boxes, box floor top (\[.*?\]), (\d+) sample points", line)
            if m and m.group(1).startswith("fill-") is False:
                rows[m.group(1)] = (int(m.group(2)), m.group(3), int(m.group(4)), line.endswith("FAIL"))
        runner = {}
        for name in RUNNER:
            boxes, top, held, bad = rows["floorflask-filling-%s" % name[5:]]
            faulted = (["boxes"] if boxes != 7 else []) + (["floor"] if json.loads(top) != [14] else []) + (["seat"] if held else [])
            runner[name] = parity(name, 1 if bad else 0, 7, sorted(faulted))
        write(OUT / "parity" / "runner.json", runner)
    finally:
        shutil.rmtree(runner_dir)

    capacity = {}
    for name, units, pieces, vx3, head, head_units in (
        ("ways", shaper.WAYS_CAPACITY, 2, 305.6, 0, 0),
        ("ram", shaper.RAM_CAPACITY, 1, 634.4, 0, 0),
        ("saddle", shaper.SADDLE_CAPACITY, 1, 126.3, 0, 0),
        ("table", shaper.TABLE_CAPACITY, 1, 507.6, 151.9, None),
    ):
        per = round(vx3 * 2.5 + 1e-9)
        share = round(head * 2.5 + 1e-9)
        assert per * pieces + share == units, (name, per, pieces, share, units)
        capacity[name] = {"exit": 0, "enumerated": pieces + 1, "faulted": [], "areas": [vx3, head, per, share, pieces]}
    write(OUT / "parity" / "capacity.json", capacity)
finally:
    shutil.rmtree(work)
