"""Writes the closed-pour fixtures of the feeding and parting tests into $FIXTURES_OUT: a drag and a cope of the long station around a cast box, the sprue and riser columns cut through the cope, and the variants each test breaks one rule with."""
import json, os
from pathlib import Path

import numpy as np

OUT = Path(os.environ["FIXTURES_OUT"])
FACES = ("north", "east", "south", "west", "up", "down")
STATION = ((2, 2, -46), (30, 14, 14))
PARTING = 8
CAST = ((13, 5, -3), (19, 9, 3))
SPRUE = ((14, 9, 0), (16, 14, 2))
RISER = ((17, 9, -2), (18, 14, -1))


def box(name, lo, hi):
    return {"name": name, "from": list(lo), "to": list(hi), "faces": {f: {"texture": "#andesite"} for f in FACES}}


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


def half(lo_y, hi_y, holes, extra=()):
    region = ((STATION[0][0], lo_y, STATION[0][2]), (STATION[1][0], hi_y, STATION[1][2]))
    els = [box("Sand%d" % n, lo, hi) for n, (lo, hi) in enumerate(decompose(region, holes), 1)]
    els += [box("Extra%d" % n, lo, hi) for n, (lo, hi) in enumerate(extra, 1)]
    return {"textures": {"andesite": "block/stone/sand/andesite"}, "elements": els}


def write(name, raw):
    (OUT / name).write_text(json.dumps(raw, indent=1) + "\n")


cast_in_drag = ((CAST[0][0], CAST[0][1], CAST[0][2]), (CAST[1][0], PARTING, CAST[1][2]))
cast_in_cope = ((CAST[0][0], PARTING, CAST[0][2]), (CAST[1][0], CAST[1][1], CAST[1][2]))
to_floor = ((CAST[0][0], STATION[0][1], CAST[0][2]), (CAST[1][0], PARTING, CAST[1][2]))
write("closed-drag.json", half(STATION[0][1], PARTING, [cast_in_drag]))
write("closed-cope.json", half(PARTING, STATION[1][1], [cast_in_cope, SPRUE, RISER]))
write("closed-drag-leak.json", half(STATION[0][1], PARTING, [to_floor]))
write("closed-cope-blocked.json", half(PARTING, STATION[1][1], [cast_in_cope, RISER]))
write("closed-drag-over.json", half(STATION[0][1], PARTING, [cast_in_drag], [((3, 8, 5), (5, 9, 7))]))
write("closed-cope-under.json", half(PARTING, STATION[1][1], [cast_in_cope, SPRUE, RISER], [((3, 7, 5), (5, 8, 7))]))
write("closed-cope-vent.json", half(PARTING, STATION[1][1], [cast_in_cope, SPRUE, RISER, ((18, 9, 0), (19, 14, 1))]))
write("closed-cope-shared.json", half(PARTING, STATION[1][1], [cast_in_cope, SPRUE, RISER], [((3, 8, 5), (5, 9, 7))]))
