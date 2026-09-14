# exlib-shapes: renders a shape file to textured views or animation frames, or a multiblock/
# megablock blocktype file to a build schematic - the same commands the wiki's own figures call.
#
#   exmod render      shape file -> textured view or animation-frame PNGs
#   exmod schematic   multiblock/megablock blocktype file -> plan SVGs, an iso PNG, a manifest

# Runs shapes/ExlibShapes's own dotnet project for one subcommand, the way Invoke-Verify runs
# ExlibVerify: -p:GamePath resolves the tool project's own VintagestoryAPI reference, and the same
# install is passed as the tool's own --game unless the caller already named one (a modder pointing
# at a different install than this repository's own provisioned one). This must be a client install,
# never the default server one: the dedicated-server archive ships almost no assets/survival/textures,
# so a render or schematic against it comes out entirely in the magenta missing-texture placeholder.
function Invoke-ExlibShapes([string]$Subcommand, [string[]]$Argv) {
  $game = Get-Opt $Argv '--game' $null
  if (-not $game) { $game = Resolve-GameInstall $CurrentGameVersion 'client' }

  $toolArgs = @($Subcommand) + $Argv
  if (-not (Get-Opt $Argv '--game' $null)) { $toolArgs += @('--game', $game) }

  $proj = Join-Path $ToolsRoot 'shapes/ExlibShapes/ExlibShapes.csproj'
  dotnet run --project $proj -p:GamePath=$game -- @toolArgs
  if ($LASTEXITCODE -ne 0) { throw "exlib-shapes $Subcommand failed (exit $LASTEXITCODE)." }
}

function Invoke-Render([string[]]$Argv) { Invoke-ExlibShapes 'render' $Argv }
function Invoke-Schematic([string[]]$Argv) { Invoke-ExlibShapes 'schematic' $Argv }

Add-ExmodCommand -Group source -Name render -Summary 'render a shape file to textured views or animation frames' -Action {
  param([string[]]$Argv) Invoke-Render $Argv
} -Detail @'
exmod render FILE --out=DIR [--views a,b,...] [--ppu N] [--anim CLIP --frames N] [--only PATH...]
  [--highlight PATH...] [--no-grid] [--no-edges] [--game PATH] [--repo PATH]

Renders a shape file's named views (or one animation clip's frames) to PNG - see
shapes/ExlibShapes/README.md for the full option list and the standalone `exlib-shapes` tool.
`--out` needs the `=` form here: PowerShell's own parameter binder treats a bare `--out` token as
an ambiguous prefix of its common `-OutVariable`/`-OutBuffer` parameters and refuses it outright.

  FILE            the shape JSON to render
  --out=DIR       where the PNGs are written
  --views a,b     named views to render (south, north, east, west, up, down, iso); default: iso
  --ppu N         pixels per shape unit (default: 24)
  --anim CLIP --frames N   render one animation clip's frame(s) instead of a static view
  --only PATH     restrict to elements whose path starts with PATH (repeatable)
  --highlight PATH   outline this element regardless of depth (repeatable)
  --no-grid       no floor grid
  --no-edges      no face outlines
  --game PATH     a specific game install (default: this repository's own provisioned one)
  --repo PATH     a specific mod repository root (default: the shape file's own ancestry)
'@

Add-ExmodCommand -Group source -Name schematic -Summary 'render a multiblock/megablock layout to a build schematic' -Action {
  param([string[]]$Argv) Invoke-Schematic $Argv
} -Detail @'
exmod schematic FILE --out=DIR [--views plan,iso] [--angle N] [--layer N|all] [--ppu N]
  [--roots PATH...] [--game PATH]

Renders a blocktype file's multiblockStructure or megablock footprint to a plan-grid SVG per Y
layer, an isometric textured composite, and a manifest.json the wiki's directive reads - see
shapes/ExlibShapes/README.md for the full option list and the standalone `exlib-shapes` tool.
`--out` needs the `=` form here: PowerShell's own parameter binder treats a bare `--out` token as
an ambiguous prefix of its common `-OutVariable`/`-OutBuffer` parameters and refuses it outright.

  FILE            the blocktype JSON to render
  --out=DIR       where the SVGs, PNGs and manifest.json are written
  --views a,b     plan, iso, or both (default: plan,iso)
  --angle N       turn the structure before rendering (0, 90, 180 or 270)
  --layer N|all   an iso render cut at Y layer N, or one per layer with "all"
  --ppu N         pixels per shape unit for the iso render (default: 8)
  --roots PATH    extra mod repository roots to resolve selectors against (repeatable)
  --game PATH     a specific game install (default: this repository's own provisioned one)
'@
