"""Records the floor flask lane's corecheck verdicts on small synthetic lathe bed cores and half boxes into $PARITY_OUT, reading the lane from $FLOORFLASK.

Each case draws the bed's plank core and two-half box from nominal figures (core 24 long, 2 high, 3 deep) and moves one by the case's areas: [core end short, gap before plank 2, core rise, box wall extra, boxes shallower in total, half 2 floor longer]."""
import contextlib, io, json, os, shutil, sys, tempfile
from pathlib import Path

FF = Path(os.environ["FLOORFLASK"])
OUT = Path(os.environ["PARITY_OUT"])
sys.path.insert(0, str(FF))
os.chdir(FF)
import lathe
import corecheck

lathe.BED_CORE = ((0.0, 0.0, 0.0), (24.0, 2.0, 3.0))
RULES = ("length", "section", "fit", "extent")
NAMES = {"length": "length", "section": "section", "fit": "pocket", "extent": "extent"}


def leaf(name, lo, hi):
    return {"name": name, "from": list(lo), "to": list(hi)}


def write(directory, name, children):
    (directory / (name + ".json")).write_text(json.dumps({"elements": [{"name": "Root", "from": [0, 0, 0], "children": children}]}))


def draw(directory, short, gap, rise, wall, shallow, floor):
    top = 2.0 + rise
    write(directory, "drysandcore-castlathebed", [
        leaf("Plank1", (0, 0, 0), (8, top, 3)),
        leaf("Plank2", (8 + gap, 0, 0), (16, top, 3)),
        leaf("Plank3", (16, 0, 0), (24 - short, top, 3)),
    ])
    each = 2.5 - shallow / 2
    write(directory, "corebox-castlathebed", [
        leaf("Half1Floor", (0, 0, 0), (26, 1, each)),
        leaf("Half2Floor", (0, 0, 10), (26 + floor, 1, 10 + each)),
        leaf("Half1Wall", (0, 1, 0), (26, 3 + wall, each)),
        leaf("Half2Wall", (0, 1, 10), (26, 3 + wall, 10 + each)),
        leaf("Half1EndW", (0, 1, 0), (1, 3, each)),
        leaf("Half1EndE", (25, 1, 0), (26, 3, each)),
    ])


CASES = {
    "bed-ok": (0, 0, 0, 0, 0, 0),
    "bed-short": (0.125, 0, 0, 0, 0, 0),
    "bed-gap": (0, 0.125, 0, 0, 0, 0),
    "bed-section": (0, 0, 0.125, 0, 0, 0),
    "bed-narrow": (0, 0, 0, 0.125, 0, 0),
    "bed-shallow": (0, 0, 0, 0, 0.125, 0),
    "bed-floor": (0, 0, 0, 0, 0, 0.125),
}

out = {}
for case, areas in CASES.items():
    faulted = []
    with tempfile.TemporaryDirectory() as tmp:
        directory = Path(tmp)
        draw(directory, *areas)
        for rule in RULES:
            with contextlib.redirect_stdout(io.StringIO()):
                code = corecheck.main("castlathebed", rule, str(directory))
            if code != 0:
                faulted.append(NAMES[rule])
    out[case] = {"exit": 1 if faulted else 0, "enumerated": len(RULES), "faulted": sorted(faulted), "areas": list(areas)}
(OUT / "parity").mkdir(parents=True, exist_ok=True)
(OUT / "parity" / "corefit.json").write_text(json.dumps(out, indent=1) + "\n")
