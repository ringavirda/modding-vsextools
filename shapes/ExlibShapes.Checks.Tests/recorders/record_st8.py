"""Records vsshape's verdicts on the parity fixtures into $PARITY_OUT, reading vsshape from $VSSHAPE."""
import os, json, sys, io, re, contextlib
from pathlib import Path
sys.path.insert(0, os.environ["VSSHAPE"])
import numpy as np
from vsshape.shape import Shape
from vsshape import geom, poses
from vsshape.checks import flushcheck, footsweep
from vsshape.lints import zfight, footprint, Context

VS = Path(os.environ["VSSHAPE"])
OUT = Path(os.environ["PARITY_OUT"])
FACES = ("north", "east", "south", "west", "up", "down")


def explicit_origins(els):
    for e in els:
        if any(e.get(k) for k in ("rotationX", "rotationY", "rotationZ")) and "rotationOrigin" not in e:
            e["rotationOrigin"] = list(e["from"])
        explicit_origins(e.get("children", []))


def write(name, raw, indent=None):
    explicit_origins(raw["elements"])
    (OUT / name).write_text(json.dumps(raw, indent=indent, separators=None if indent else (",", ":")) + "\n")


def box(name, frm, to, tex="iron"):
    return {"name": name, "from": frm, "to": to, "faces": {f: {"texture": "#" + tex} for f in FACES}}


for src, name in (("watt-condensate-elbow.json", "watt-condensate-elbow.json"),
                  ("enginehorizontal-exhaust.json", "enginehorizontal-exhaust.json"),
                  ("checks/foot.json", "foot.json")):
    write(name, json.loads((VS / "tests/fixtures" / src).read_text()), 1 if name == "foot.json" else None)
(OUT / "golden.json").write_text((VS / "tests/fixtures/checks/golden.json").read_text())

write("filling-small.json", {"textures": {"iron": "block/metal/cast-iron1"}, "elements": [
    box("Flask", [0, 0, 0], [16, 8, 16]),
    {"name": "Piece1", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
        box("Bedded", [4, 2, 4], [8, 6, 8]), box("Level", [4, 7, 4], [8, 8, 8])]}]}, 1)

clip = {"textures": {"iron": "block/metal/cast-iron1"}, "elements": [box("Body", [0, 0, 0], [16, 16, 16])],
        "animations": [{"name": "reach", "code": "reach", "quantityframes": 4, "onAnimationEnd": "Repeat",
                        "keyframes": [{"frame": 0, "elements": {"Body": {"offsetX": 0.0, "offsetY": 0.0, "offsetZ": 0.0}}},
                                      {"frame": 3, "elements": {"Body": {"offsetX": 0.0, "offsetY": 0.0, "offsetZ": 6.0}}}]}]}
write("foot-clip.json", clip, 1)

(OUT / "parity").mkdir(exist_ok=True)


def load(n):
    return Shape.load(str(OUT / n))


def pairs_out(found):
    rows = sorted((f"{p1} {f1} | {p2} {f2}", round(a, 4)) for p1, f1, p2, f2, a in found)
    return [r[0] for r in rows], [r[1] for r in rows]


flush = {}
for n, hide in (("flush.json", []), ("mppump-sketch-defective.json", []), ("watt-condensate-elbow.json", []),
                ("enginehorizontal-exhaust.json", []), ("filling-small.json", ["Piece1/Level"])):
    faces, found = flushcheck.flush_pairs(load(n), None, hide)
    f, a = pairs_out(found)
    flush[n] = {"exit": 1 if found else 0, "enumerated": faces, "faulted": f, "areas": a}
nolevel = flushcheck.flush_pairs(load("filling-small.json"), None, [])
f, a = pairs_out(nolevel[1])
flush["filling-small.json (no hide)"] = {"exit": 1 if nolevel[1] else 0, "enumerated": nolevel[0], "faulted": f, "areas": a}
(OUT / "parity/flushcheck.json").write_text(json.dumps(flush, indent=1) + "\n")

zf = {}
for n in ("flush.json", "mppump-sketch-defective.json", "watt-condensate-elbow.json", "enginehorizontal-exhaust.json",
          "filling-small.json"):
    sh = load(n)
    fs = zfight.check(sh, Context())
    mats = geom.world_matrices(sh)
    faces = sum(1 for el in sh.leaves() for q in geom.face_quads(el, mats[el.path]) if zfight._axis_aligned(q.normal))
    rows = sorted((f"{f.path} | {f.data['other']}" if f.path < f.data["other"] else f"{f.data['other']} | {f.path}",
                   round(f.data["area"], 4)) for f in fs)
    zf[n] = {"exit": 1 if fs else 0, "enumerated": faces, "faulted": [r[0] for r in rows], "areas": [r[1] for r in rows]}
(OUT / "parity/zfight.json").write_text(json.dumps(zf, indent=1) + "\n")

fp = {}
cells = [(x, y, z) for x in (0, 1) for y in (0, 1) for z in (0, 1)]
sh = load("mppump-sketch-defective.json")
fs = footprint.check(sh, Context(footprint=cells, pipe_prefixes=("Suction", "Delivery")))
fp["mppump-sketch-defective.json"] = {"exit": 1 if fs else 0, "enumerated": len(cells) + len(sh.leaves()),
                                      "faulted": sorted(f.message for f in fs)}
sh = load("foot.json")
fs = footprint.check(sh, Context(footprint=[(0, 0, 0)]))
fp["foot.json"] = {"exit": 1 if fs else 0, "enumerated": 1 + len(sh.leaves()), "faulted": sorted(f.message for f in fs)}
(OUT / "parity/footprint.json").write_text(json.dumps(fp, indent=1) + "\n")

GROUP = re.compile(r"^cell .*? reach\s+(\S+) \((.+?)\) (\S+)$")


def sweep(name, *args):
    sh = load(name)
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        code = footsweep.cli([str(OUT / name), *args])
    faulted = sorted(m.group(3) for l in out.getvalue().splitlines() if (m := GROUP.match(l)) and float(m.group(1)) > 4 + 1e-6)
    n = 1 + sum(len(poses.clip_frames(sh, c, 1.0)) for c in poses.all_clips(sh))
    print(name, args, code); print(out.getvalue())
    return {"exit": code, "enumerated": n * len(sh.leaves()), "faulted": faulted}


fs = {"foot.json": sweep("foot.json"),
      "foot.json+golden": sweep("foot.json", "--golden", str(OUT / "golden.json")),
      "foot.json+extra": sweep("foot.json", "--extra", "0,0,1"),
      "foot-clip.json": sweep("foot-clip.json")}
(OUT / "parity/footsweep.json").write_text(json.dumps(fs, indent=1) + "\n")
for k, v in (("flush", flush), ("zfight", zf), ("footprint", fp), ("footsweep", fs)):
    print(k, json.dumps(v)[:1500])
