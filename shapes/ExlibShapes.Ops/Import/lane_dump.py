"""Prints a floor flask lane module's tables as JSON: lane_dump.py DIR MODULE.

MODULE is lathe, shaper or smallflask (DIR/propose.py). The output has `tables` (every upper-case module constant that is
plain data), `kinds` (the module's `kinds(stdfill)` entries, when it has them) and `press` (press.py's cross slide entries,
for the lathe), each value as lists, objects, numbers, strings and null."""
import importlib
import json
import math
import sys

FILES = {"lathe": "lathe", "shaper": "shaper", "smallflask": "propose"}


def dump(v):
    if v is None or isinstance(v, (bool, int, str)):
        return v
    if isinstance(v, float):
        return v if math.isfinite(v) else None
    if hasattr(v, "tolist"):
        return dump(v.tolist())
    if isinstance(v, dict):
        return {str(k): dump(x) for k, x in v.items()}
    if isinstance(v, (set, frozenset)):
        return sorted((dump(x) for x in v), key=json.dumps)
    if isinstance(v, (list, tuple)):
        return [dump(x) for x in v]
    raise TypeError(type(v).__name__)


def dump_each(d):
    out = {}
    for k, v in d.items():
        try:
            out[k] = dump(v)
        except (TypeError, ValueError):
            pass
    return out


def main():
    lane, module = sys.argv[1], sys.argv[2]
    sys.path.insert(0, lane)
    real, sys.stdout = sys.stdout, sys.stderr
    try:
        mod = importlib.import_module(FILES[module])
        result = {"module": module, "tables": dump_each({n: v for n, v in vars(mod).items() if n.isupper()})}
        if hasattr(mod, "kinds"):
            try:
                result["kinds"] = {k: dump_each(v) for k, v in mod.kinds(importlib.import_module("stdfill")).items()}
            except Exception as e:
                result["kindsError"] = "%s: %s" % (type(e).__name__, e)
        if module == "lathe":
            press = importlib.import_module("press")
            result["press"] = {k: dump_each(press.KINDS[k]) for k in press.CROSSSLIDE}
    finally:
        sys.stdout = real
    json.dump(result, sys.stdout, sort_keys=True)


main()
