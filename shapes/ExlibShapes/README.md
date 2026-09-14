# exlib-shapes

Renders a Vintage Story shape file to textured views or animation frames, and a multiblock or
megablock blocktype file to a build schematic - a plan-grid SVG per Y layer, an isometric textured
composite, and a manifest a wiki page reads - with no game running. It depends on nothing in this
repository beyond the game install it is pointed at.

```sh
dotnet tool install --global ExpandedLib.Shapes
exlib-shapes render path/to/shape.json --out out/ --game path/to/vintagestory
exlib-shapes schematic path/to/blocktype.json --out out/ --game path/to/vintagestory
```

`--game` is any Vintage Story install carrying `assets/survival/textures` - a client install, not
the dedicated-server archive, which ships almost none of them and renders every vanilla-textured
surface as the magenta missing-texture placeholder. The client archive is still freely downloadable
without a game licence.

## render

```
exlib-shapes render FILE --out DIR [--views a,b,...] [--ppu N] [--anim CLIP --frames N]
  [--only PATH...] [--highlight PATH...] [--no-grid] [--no-edges] [--game PATH] [--repo PATH]
```

Renders `FILE`'s named views, one PNG each, to `--out`. `--views` is a comma-separated list of
`south`, `north`, `east`, `west`, `up`, `down`, `iso` (default: `iso`); `--ppu` is pixels per shape
unit (default 24). `--anim CLIP --frames N` (comma-separated frame numbers, which may be
fractional) renders that animation clip's poses instead of the shape's rest pose. `--only`
restricts the render to elements whose path starts with the given prefix, repeatable; `--highlight`
outlines an element regardless of depth, repeatable. `--no-grid`/`--no-edges` drop the floor grid
or the face outlines. `--repo` is the mod repository a `domain:path` or bare texture reference
resolves against (default: the shape file's own ancestry - the nearest ancestor holding
`workbench/`, `mods/` or `.game/`).

Prints every PNG's path, then the texture keys that could not be resolved (drawn as a magenta
placeholder).

## schematic

```
exlib-shapes schematic FILE --out DIR [--views plan,iso] [--angle N] [--layer N|all] [--ppu N]
  [--roots PATH...] [--game PATH]
```

Reads `FILE`'s `attributes.multiblockStructure` (the structure table, its oriented selectors,
filler cells, connector faces and named roles) or, for a filler-only megablock
(`ExpandedLib.Structures.IFillerHost`, the family's flywheel among them), its `attributes.fillerOffsets`
or per-variant `attributesByType` entry. `--angle` turns the whole layout before rendering (0, 90,
180 or 270 - north 0, west 90, south 180, east 270). `--views` is `plan`, `iso`, or both (default:
both): `plan` writes one `<stem>-plan-y<N>.svg` per Y layer; `iso` writes `<stem>-iso.png`, plus
one `<stem>-iso-y<N>.png` per `--layer` (a single Y, or `all` for one per layer) cut at that
height. `--ppu` is pixels per shape unit for the iso render (default 8). `--roots` names extra mod
repository roots a selector resolves against, repeatable, in addition to the golden's own
repository and its sibling `exlib` checkout when present.

A `<stem>.json` manifest is always written alongside the pictures: `files` (every path written),
`legend` (one row per declared number - its selector, resolved representative code, palette
colour, and whether it is optional, drawn as air), and `warnings` (a selector that resolved to
neither a block nor `air`; a selector whose match spanned more than one source blocktype file,
named in the warning together, since only one of them is drawn; or a blocktype or worldproperties
file that failed to parse, named by path - also printed to stderr as the run happens).

## tree / measure

`exlib-shapes tree FILE [--group PREFIX]` prints a shape's element tree (name, extent, rotation,
textures) and its animation clips. `exlib-shapes measure FILE [--group PREFIX] [--cells x,y,z;...]`
prints a shape's leaf-cube count, world extents in shape units and in 16-unit cells, and, with
`--cells`, what fraction of each named cell's volume the shape's own element boxes cover - useful
for checking a megablock's authored geometry actually fills the footprint its `fillerOffsets`
declare.

Exit codes: `0` success, `1` a resolved run-time error (a bad shape, no such clip, no elements),
`2` a usage error.

Part of [ExpandedLib](https://github.com/ringavirda/modding-vsexmods). MIT licensed.
