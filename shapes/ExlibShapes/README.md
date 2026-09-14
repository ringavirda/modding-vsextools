# exlib-shapes

Renders a Vintage Story shape file to textured views or animation frames, a blocktype variant to
the views a wiki page shows, an itemtype variant to its own picture, and a multiblock or megablock
blocktype file to a build schematic - a plan-grid SVG per Y layer, an isometric textured composite,
and a manifest a wiki page reads - with no game running. It depends on nothing in this repository beyond the game install it is pointed at.

```sh
dotnet tool install --global ExpandedLib.Shapes
exlib-shapes render path/to/shape.json --out out/ --game path/to/vintagestory
exlib-shapes block path/to/blocktype.json --out out/ --game path/to/vintagestory
exlib-shapes item path/to/itemtype.json --out out/ --game path/to/vintagestory
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
both): `plan` writes one `<stem>-plan-y<N>.svg` per Y layer, each captioned with the layer it draws,
every drawn cell carrying its legend's display number and the edges named for the machine (`front`
under the near edge, `back` over the far one, north marked when it is neither); `iso` writes
`<stem>-iso.png` with a vertical scale down its left edge, one tick per layer carried across the
picture as a faint guide at the height that layer reads at on the corner column nearest the camera,
plus one `<stem>-iso-y<N>.png` per `--layer` (a single Y, or `all` for one per layer) cut at that
height. `--ppu` is pixels per shape unit for the iso render (default 8). `--roots` names extra mod
repository roots a selector resolves against, repeatable, in addition to the golden's own
repository and its sibling `exlib` checkout when present.

The declared offsets are authored in the block's own frame and the game turns them by its
`StructureAngle`, a number that lives in C# and in no blocktype file; the drawing therefore takes
the model's own `rotateY` as given and turns the declared cells onto it, which is that angle
wherever the footprint tells the quarter turns apart. A megablock's own body is drawn over a thin
outline of the cells it reserves rather than under grey boxes; a structure's filler cells, which
the player leaves clear, keep theirs.

Both `schematic` and `block` show the front of a machine, the side a player stands at. A machine is
placed facing away from the player, so an oriented family is drawn at the facing whose front turns
toward the isometric camera, which stands to the south-east and above; and, facing data or none, the
layout is then turned so its starter block stands on the camera-facing edge of the footprint - the
only thing an old structure, which records no facing at all, says about where a player works from.
Every block of the structure turns with it. The manifest's `front` names the side the front then
looks toward; `--variant` and `--angle` override the choice.

A `<stem>.json` manifest is always written alongside the pictures: `files` (every path written),
`plans` (one `{file, layer}` row per plan SVG), `front` (the world side the drawn machine's front
looks toward, turned with `--angle`, `null` for a structure that faces no way), `legend` (one row
per declared number - the mod's own `number`, the `display` number the plans draw, its selector,
resolved representative code, palette colour, and whether it is optional, drawn as air in the iso
and hatched in the plans), `unpaintedFaces` (one line per face key a drawn block assigns nothing,
painted with the magenta placeholder wherever the iso shows it), and `warnings` (a selector that
resolved to neither a block nor `air`; a texture value naming a file that is not there; a selector
whose match spanned more than one source blocktype file, named in the warning together, since only
one of them is drawn; or a blocktype or worldproperties file that failed to parse, named by path -
also printed to stderr as the run happens).

## block

```
exlib-shapes block FILE --out DIR [--variant CODE] [--views iso,north,east,south,west,up]
  [--angle N] [--full] [--ppu N] [--roots PATH...] [--game PATH]
```

Renders one variant of `FILE` the way the game draws it in the world: its own shape file under the
turn its `shape`/`shapeByType` entry carries for that variant, painted with the blocktype's texture
map (the `all` entry standing in for every key it does not name), or a unit cube when it ships no
shape at all. `--variant` names the variant by full code or bare path; with none, the family is
drawn at its presentation facing, or as its first variant when the family has no facing.
`--views` defaults to `iso,north,east,south,west,up`, one `<stem>-<view>.png` each; `--ppu` is
pixels per shape unit (default 24). A family declaring a footprint (`fillerOffsets`) also gets
`<stem>-footprint.svg`, the plan of the principal and the cells it reserves, the principal marked,
drawn in the same frame as the pictures.

The machine is turned so its front meets the camera: the quarter turn that stands its own cell on
the camera-facing edge of the footprint it reserves, or, for a family with no facing variant to
choose between (the blast furnace door, whose facing lives in its C#), the turn that brings the most
of its detail - every face painted with a texture other than the model's most-used one - toward the
camera. `--angle` overrides the choice. A part an animation of the model's own shape parks outside
the block's own cells - the puddling door's rabble, the chimney cap's control rod - is left out
unless `--full` says otherwise; static art is drawn however far it reaches.

The `<stem>.json` manifest carries `files` (every path written), `variant` (the code drawn),
`angle` (the quarter turn it is drawn at), `front` (the world side that variant's front then looks
toward, `null` for a block that faces no way), `clipped` and `hidden` (whether parts were left out
and which), `missingTextures` (one line per value naming a file that is not there),
`unpaintedFaces` (one line per face key the blocktype assigns nothing, drawn as the magenta
placeholder wherever a picture shows it) and `warnings` (a block with no shape of its own, a
selector whose match spanned more than one source file, a blocktype or worldproperties file that
failed to parse).

## item

```
exlib-shapes item FILE --out DIR [--variant CODE] [--ppu N] [--roots PATH...] [--game PATH]
```

Renders one variant of an itemtype file. An item that ships a shape (`shape`/`shapeByType`, its own
textures map overridden by the itemtype's) gets `<stem>-iso.png`, the isometric render of that
model; an item that ships a flat inventory texture (`texture`/`textureByType`) gets
`<stem>-icon.png`, that texture at four pixels to the texel on the renders' own paper. An item
declaring both gets both, and one declaring neither is a warning and no picture. `--variant` names
the variant by full code or bare path, defaulting to the family's first; `--ppu` is pixels per shape
unit for the isometric render (default 24).

The `<stem>.json` manifest carries `files`, `variant`, `missingTextures`, `unpaintedFaces` and
`warnings`.

## tree / measure

`exlib-shapes tree FILE [--group PREFIX]` prints a shape's element tree (name, extent, rotation,
textures) and its animation clips. `exlib-shapes measure FILE [--group PREFIX] [--cells x,y,z;...]`
prints a shape's leaf-cube count, world extents in shape units and in 16-unit cells, and, with
`--cells`, what fraction of each named cell's volume the shape's own element boxes cover - useful
for checking a megablock's authored geometry actually fills the footprint its `fillerOffsets`
declare.

Exit codes: `0` success, `1` a resolved run-time error (a bad shape, no such clip, no elements),
`2` a usage error.

Part of [ExpandedLib](https://github.com/ringavirda/modding-vsextools). MIT licensed.
