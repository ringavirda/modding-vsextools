"""Records vsshape's verdicts on the parity fixtures into $PARITY_OUT, reading vsshape from $VSSHAPE."""
import os, contextlib, copy, importlib.util, io, json, re, shutil, sys
from pathlib import Path
sys.path.insert(0, os.environ["VSSHAPE"])
from vsshape.shape import Shape
from vsshape.checks import octcheck, uvwrapcheck

VS = Path(os.environ["VSSHAPE"])
OUT = Path(os.environ["PARITY_OUT"])
spec = importlib.util.spec_from_file_location("vstests", VS / "tests/test_checks.py")
sys.path.insert(0, str(VS / "tests"))
mod = importlib.util.module_from_spec(spec); spec.loader.exec_module(mod)


def write(name, raw, indent=1):
    (OUT / name).write_text(json.dumps(raw, indent=indent) + "\n")


def tex(raw):
    raw["textures"] = {"iron": "block/metal/plate/iron"}
    return raw


for src, name in (("checks/uvwrap-octagon.json", "uvwrap-octagon.json"), ("checks/uvwrap-ring.json", "uvwrap-ring.json"),
                  ("checks/uvwrap-nested.json", "uvwrap-nested.json"), ("gearbevel-mitre-proposed.json", "gearbevel-mitre-proposed.json")):
    shutil.copy(VS / "tests/fixtures" / src, OUT / name)

write("octagon-small.json", tex({"elements": [mod.octagon()]}))
write("octagon-small-missing-chamfer.json", tex({"elements": [mod.octagon(drop=2)]}))
write("rect-bar-small.json", tex({"elements": [mod.rect_bar()]}))
write("rect-bar-small-unequal-inset.json", tex({"elements": [mod.rect_bar(wide_x=0.8)]}))
write("shaft-small.json", tex({"elements": [mod.octagon(arms=4 * 0.4142)]}))

import copy as _c
ringfix = lambda **kw: tex({"elements": [mod.plate_ring("y", **kw)]})
write("ring-small.json", ringfix())
write("ring-small-dropped-plate.json", ringfix(drop=3))
off = ringfix(); off["elements"][0]["children"][1]["rotationY"] = 75.0
write("ring-small-off-step.json", off)
write("ring-regular-small.json", ringfix(regular=True))
ring = json.loads((VS / "tests/fixtures/checks/uvwrap-ring.json").read_text())
anvil = copy.deepcopy(ring)
for k, e in enumerate(anvil["elements"]):
    e["from"][0], e["to"][0] = 2, 5
    e["rotationOrigin"][0] = 2
    v = (12.0 - 2 * k) % 16
    e["faces"]["up"]["uv"] = [2.0, v, 5.0, v + 2]
write("anvil-small.json", anvil)

rand = copy.deepcopy(ring)
for k, v in enumerate([5.0, 11.0, 0.5, 8.0, 13.0, 2.5, 9.5, 6.0]):
    rand["elements"][k]["faces"]["up"]["uv"] = [2.0, v, 8.0, v + 2.0]
write("uvwrap-ring-random-rects.json", rand)


def run(mod_, name, *args):
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        code = mod_.cli([str(OUT / name), *args])
    return code, out.getvalue()


def faulted_oct(text):
    return sorted(m.group(1) for l in text.splitlines() if (m := re.match(r"^  FAULT (\S+): ", l)))


rec = {}
for name, args in (("octagon-small.json", []), ("octagon-small-missing-chamfer.json", []), ("shaft-small.json", ["--regular", "*"]),
                   ("rect-bar-small.json", []), ("rect-bar-small-unequal-inset.json", []),
                   ("shaft-small.json (no regular)", []), ("uvwrap-octagon.json", []), ("ring-small.json", []),
                   ("ring-small-dropped-plate.json", []), ("ring-small-off-step.json", []), ("ring-regular-small.json", []),
                   ("gearbevel-mitre-proposed.json", [])):
    real = name.split(" (")[0]
    code, text = run(octcheck, real, *args)
    print(name, args, code); print(text)
    sh = Shape.load(str(OUT / real))
    res = octcheck.check(sh, set(), set(args[1:]))
    rec[name] = {"exit": code, "enumerated": len(res), "faulted": faulted_oct(text)}
(OUT / "parity/octcheck.json").write_text(json.dumps(rec, indent=1) + "\n")

uv = {}
for name in ("uvwrap-octagon.json", "uvwrap-ring.json", "uvwrap-nested.json", "anvil-small.json", "ring-small.json",
             "uvwrap-ring-random-rects.json"):
    code, text = run(uvwrapcheck, name)
    print(name, code); print(text)
    sh = Shape.load(str(OUT / name))
    rs = uvwrapcheck.runs(sh)
    uv[name] = {"exit": code, "enumerated": len(rs), "faulted": sorted(f"{p}" for p, n, k, ok, t in rs if not ok)}
(OUT / "parity/uvwrap.json").write_text(json.dumps(uv, indent=1) + "\n")
