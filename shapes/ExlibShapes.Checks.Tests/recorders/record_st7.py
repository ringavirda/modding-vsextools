"""Records vsshape's verdicts on the parity fixtures into $PARITY_OUT, reading vsshape from $VSSHAPE."""
import os, json, sys, io, contextlib
from pathlib import Path
sys.path.insert(0, os.environ["VSSHAPE"])
import numpy as np
from vsshape.shape import Shape
from vsshape.checks import facecheck, sizes, uvwrapcheck
from vsshape.lints import sizes as lint_sizes

VS = Path(os.environ["VSSHAPE"])
OUT = Path(os.environ["PARITY_OUT"])
(OUT / "parity").mkdir(parents=True, exist_ok=True)
FACES = ("north", "east", "south", "west", "up", "down")


def explicit_origins(els):
    for e in els:
        if any(e.get(k) for k in ("rotationX", "rotationY", "rotationZ")) and "rotationOrigin" not in e:
            e["rotationOrigin"] = list(e["from"])
        explicit_origins(e.get("children", []))


def write(name, raw):
    explicit_origins(raw["elements"])
    (OUT / name).write_text(json.dumps(raw, indent=1) + "\n")
    return OUT / name


def box(name, frm, to, tex, uv=(0, 0)):
    d = [b - a for a, b in zip(frm, to)]
    rect = {"north": (d[0], d[1]), "south": (d[0], d[1]), "east": (d[2], d[1]), "west": (d[2], d[1]),
            "up": (d[0], d[2]), "down": (d[0], d[2])}
    return {"name": name, "from": frm, "to": to,
            "faces": {f: {"texture": "#" + tex, "uv": [uv[0], uv[1], uv[0] + rect[f][0], uv[1] + rect[f][1]]}
                      for f in FACES}}


ring = json.loads((VS / "tests/fixtures/checks/uvwrap-ring.json").read_text())
head = {"textureWidth": 16, "textureHeight": 16,
        "textures": {"iron": "block/metal/cast-iron1", "plate": "block/metal/plate/iron"},
        "elements": [box("Head", [0, 0, 0], [12, 6, 16], "iron", (0, 0)),
                     {"name": "Boss", "from": [0, 0, 0], "to": [0, 0, 0], "children": ring["elements"]}]}
for b in head["elements"][1]["children"]:
    for fn, f in b["faces"].items():
        f["texture"] = "#plate"
        if fn != "up":
            f["uv"] = {"north": [0, 0, 6, 1], "south": [0, 0, 6, 1], "east": [0, 0, 2, 1], "west": [0, 0, 2, 1], "down": [0, 0, 6, 2]}[fn]
base = write("head-small.json", head)
planted = json.loads(json.dumps(head))
planted["elements"].append(box("Lug", [0, 6, 0], [2, 7, 2], "iron", (3, 3)))
planted["elements"][-1]["faces"]["up"]["uv"] = [0, 0, 2, 2]
write("head-small-origin.json", planted)
bad = json.loads(json.dumps(head))
bad["elements"].append(box("Slab", [0, 8, 0], [17, 9, 1], "iron", (0, 0)))
bad["elements"][-1]["faces"]["east"]["texture"] = "#none"
bad["elements"][-1]["faces"]["west"]["uv"] = [0, 0, 3, 1]
write("head-small-lint.json", bad)
table = {"uv": {"iron": "random", "plate": "random"}}
(OUT / "table.json").write_text(json.dumps(table, indent=1) + "\n")
spec = {"sizes": [{"element": "Head", "axis": "x", "voxels": 12}, {"element": "Head", "axis": "y", "voxels": 5},
                  {"element": "Boss", "axis": "x", "voxels": 6}, {"element": "Nope", "axis": "z", "voxels": 1}]}
(OUT / "sizes-spec.json").write_text(json.dumps(spec, indent=1) + "\n")

mp = json.loads((VS / "tests/fixtures/mppump-sketch-defective.json").read_text())
write("mppump-sketch-defective.json", mp)
flush = json.loads((VS / "tests/fixtures/checks/flush.json").read_text())
write("flush.json", flush)


def facecheck_run(name, shipped=None):
    sh = Shape.load(str(OUT / name))
    sp = Shape.load(str(OUT / shipped)) if shipped else None
    bad, found, wrong, n, count, allowed = facecheck.check(sh, sp, facecheck.read_table(str(OUT / "table.json")) if sp else None, [])
    faulted = sorted([f"{e.path} {f}" for e, f, r in bad] + [f"{e.path} {f}" for e, f, r in wrong])
    return {"exit": 1 if bad or wrong else 0, "enumerated": count, "faulted": faulted}


par = {
    "flush.json": facecheck_run("flush.json"),
    "mppump-sketch-defective.json": facecheck_run("mppump-sketch-defective.json"),
    "head-small.json": facecheck_run("head-small.json"),
    "head-small-origin.json": facecheck_run("head-small-origin.json", "head-small.json"),
}
(OUT / "parity/facecheck.json").write_text(json.dumps(par, indent=1) + "\n")

out = io.StringIO()
with contextlib.redirect_stdout(out):
    code = sizes.cli([str(OUT / "head-small.json"), "--spec", str(OUT / "sizes-spec.json")])
print(out.getvalue(), code)
lines = out.getvalue().splitlines()
faulted = sorted({l.split(":")[0] for l in lines if ": " in l and not l.startswith("sizes:")})
(OUT / "parity/sizes.json").write_text(json.dumps({"head-small.json": {"exit": code, "enumerated": len(spec["sizes"]), "faulted": faulted}}, indent=1) + "\n")

lint = {}
for n in ("head-small.json", "head-small-lint.json", "mppump-sketch-defective.json", "flush.json"):
    sh = Shape.load(str(OUT / n))
    fs = lint_sizes.check(sh, None)
    lint[n] = {"exit": 1 if any(f.level == "error" for f in fs) else 0, "enumerated": len(list(sh.leaves())),
               "faulted": sorted(f"{f.kind} {f.path}" for f in fs)}
(OUT / "parity/sizeslint.json").write_text(json.dumps(lint, indent=1) + "\n")
print(json.dumps(par, indent=1)); print(json.dumps(lint, indent=1))
