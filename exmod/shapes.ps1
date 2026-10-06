# exlib-shapes: renders a shape file to textured views or animation frames, a multiblock/megablock
# blocktype file to a build schematic, one blocktype variant to the views a wiki page shows, or one
# itemtype variant to its own picture - the same commands the wiki's own figures call.
#
#   exmod render      shape file -> textured view or animation-frame PNGs
#   exmod schematic   multiblock/megablock blocktype file -> plan SVGs, an iso PNG, a manifest
#   exmod block       blocktype file -> a PNG per view, a footprint SVG, a manifest
#   exmod item        itemtype file -> an iso PNG or an enlarged icon, a manifest

# Runs shapes/ExlibShapes's built tool for one subcommand, building it first only when its output is
# missing or older than its inputs (set EXMOD_SHAPES_NO_BUILD to refuse instead, for a caller that
# holds the build lock shared: it builds with `exmod shapes-build` under the exclusive lock, then
# renders under the shared one). The way Invoke-Verify runs ExlibVerify: -p:GamePath resolves the tool project's own VintagestoryAPI reference, and the same
# install is passed as the tool's own --game unless the caller already named one (a modder pointing
# at a different install than this repository's own provisioned one). This must be a client install,
# never the default server one: the dedicated-server archive ships almost no assets/survival/textures,
# so a render or schematic against it comes out entirely in the magenta missing-texture placeholder.
function Invoke-ExlibShapes([string]$Subcommand, [string[]]$Argv) {
  $game = Get-Opt $Argv '--game' $null
  if (-not $game) { $game = Resolve-GameInstall $CurrentGameVersion 'client' }

  $toolArgs = @($Subcommand) + $Argv
  if (-not (Get-Opt $Argv '--game' $null)) { $toolArgs += @('--game', $game) }

  $dll = Get-ExlibShapesDll
  if (Test-ExlibShapesStale $dll $game) {
    if ($env:EXMOD_SHAPES_NO_BUILD) {
      throw "exlib-shapes is not built or is older than its sources, and EXMOD_SHAPES_NO_BUILD forbids building under a shared lock. Build it first, under the exclusive lock: exmod shapes-build"
    }
    Build-ExlibShapes $game
  }
  dotnet $dll @toolArgs
  if ($LASTEXITCODE -ne 0) { throw "exlib-shapes $Subcommand failed (exit $LASTEXITCODE)." }
}

# The built tool: Debug output of shapes/ExlibShapes.
function Get-ExlibShapesDll {
  Join-Path $ToolsRoot 'shapes/ExlibShapes/bin/Debug/net10.0/exlib-shapes.dll'
}

# True when $Dll is missing or older than any input of the build: the sources and project files of
# ExlibShapes and the ExlibAssets project it references, the props, packages and SDK pin they
# import, exmod.json (the package version), and the game's VintagestoryAPI.dll. bin/ and obj/ are
# not inputs.
function Test-ExlibShapesStale([string]$Dll, [string]$Game) {
  if (-not (Test-Path -LiteralPath $Dll)) { return $true }
  $built = (Get-Item -LiteralPath $Dll).LastWriteTimeUtc
  $inputs = @()
  foreach ($dir in 'shapes/ExlibShapes', 'assets/ExlibAssets') {
    $inputs += Get-ChildItem (Join-Path $ToolsRoot $dir) -Recurse -File |
      Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Name -ne 'README.md' }
  }
  foreach ($name in 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Build.rsp', 'Directory.Packages.props', 'global.json', 'exmod.json') {
    $inputs += Get-Item -LiteralPath (Join-Path $ToolsRoot $name)
  }
  $api = Join-Path $Game 'VintagestoryAPI.dll'
  if (Test-Path -LiteralPath $api) { $inputs += Get-Item -LiteralPath $api }
  [bool]($inputs | Where-Object { $_.LastWriteTimeUtc -gt $built } | Select-Object -First 1)
}

# Builds the tool. A build rewrites bin/ and obj/, so a caller running renders side by side holds
# the workspace build lock exclusive around this and shared around renders.
function Build-ExlibShapes([string]$Game) {
  $proj = Join-Path $ToolsRoot 'shapes/ExlibShapes/ExlibShapes.csproj'
  dotnet build $proj -c Debug -p:GamePath=$Game -clp:ErrorsOnly
  if ($LASTEXITCODE -ne 0) { throw "exlib-shapes build failed (exit $LASTEXITCODE)." }
  (Get-Item -LiteralPath (Get-ExlibShapesDll)).LastWriteTimeUtc = [datetime]::UtcNow
}

function Invoke-Render([string[]]$Argv) { Invoke-ExlibShapes 'render' $Argv }
function Invoke-Schematic([string[]]$Argv) { Invoke-ExlibShapes 'schematic' $Argv }
function Invoke-Block([string[]]$Argv) { Invoke-ExlibShapes 'block' $Argv }
function Invoke-ShapesBuild([string[]]$Argv) {
  $game = Get-Opt $Argv '--game' $null
  if (-not $game) { $game = Resolve-GameInstall $CurrentGameVersion 'client' }
  if (Test-ExlibShapesStale (Get-ExlibShapesDll) $game) { Build-ExlibShapes $game } else { 'exlib-shapes is current.' }
}

function Invoke-Item([string[]]$Argv) { Invoke-ExlibShapes 'item' $Argv }

Add-ExmodCommand -Group source -Name render -Summary 'render a shape file to textured views or animation frames' -Action {
  param([string[]]$Argv) Invoke-Render $Argv
} -Detail @'
exmod render FILE --out=DIR [--views a,b,...] [--ppu N] [--anim CLIP --frames N] [--only PATH...]
  [--highlight PATH...] [--selective PATTERN,...] [--no-grid] [--no-edges] [--game PATH] [--repo PATH]

Renders a shape file's named views (or one animation clip's frames) to PNG - see
shapes/ExlibShapes/README.md for the full option list and the standalone `exlib-shapes` tool.
`--out` needs the `=` form here: PowerShell's own parameter binder treats a bare `--out` token as
an ambiguous prefix of its common `-OutVariable`/`-OutBuffer` parameters and refuses it outright.

The tool is built only when its output is missing or older than its sources, so renders can run
side by side: a render started under a shared lock (`flock -s -w 240 LOCK exmod ...` with
EXMOD_SHAPES_NO_BUILD=1) never builds, and refuses with the build command when the tool is stale.
Build it under the exclusive lock first: `flock -w 240 -o LOCK exmod shapes-build`.

  FILE            the shape JSON to render
  --out=DIR       where the PNGs are written
  --views a,b     named views to render (south, north, east, west, up, down, iso); default: iso
  --ppu N         pixels per shape unit (default: 24)
  --anim CLIP --frames N   render one animation clip's frame(s) instead of a static view
  --only PATH     restrict to elements whose path starts with PATH (repeatable)
  --highlight PATH   outline this element regardless of depth (repeatable)
  --selective PATTERN,...   keep only the selectiveElements that match, as a shape entry's own list does
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
layer, an isometric textured composite, and a `<stem>.json` manifest the wiki's directive reads - see
shapes/ExlibShapes/README.md for the full option list and the standalone `exlib-shapes` tool.
`--out` needs the `=` form here: PowerShell's own parameter binder treats a bare `--out` token as
an ambiguous prefix of its common `-OutVariable`/`-OutBuffer` parameters and refuses it outright.

The tool is built only when its output is missing or older than its sources, so renders can run
side by side: a render started under a shared lock (`flock -s -w 240 LOCK exmod ...` with
EXMOD_SHAPES_NO_BUILD=1) never builds, and refuses with the build command when the tool is stale.
Build it under the exclusive lock first: `flock -w 240 -o LOCK exmod shapes-build`.

  FILE            the blocktype JSON to render
  --out=DIR       where the SVGs, PNGs and <stem>.json are written
  --views a,b     plan, iso, or both (default: plan,iso)
  --angle N       turn the structure before rendering (0, 90, 180 or 270)
  --layer N|all   an iso render cut at Y layer N, or one per layer with "all"
  --ppu N         pixels per shape unit for the iso render (default: 8)
  --roots PATH    extra mod repository roots to resolve selectors against (repeatable)
  --game PATH     a specific game install (default: this repository's own provisioned one)
'@

Add-ExmodCommand -Group source -Name block -Summary 'render one blocktype variant to the views a page shows' -Action {
  param([string[]]$Argv) Invoke-Block $Argv
} -Detail @'
exmod block FILE --out=DIR [--variant CODE] [--views iso,north,east,south,west,up] [--angle N]
  [--full] [--ppu N] [--selective PATTERN,...] [--transparent] [--roots PATH...] [--game PATH]

Renders one variant of a blocktype file the way the game draws it in the world - its own shape
under its shapeByType turn, painted with its texture map, or a unit cube when it ships no shape -
one PNG per view, plus the plan of the footprint it reserves when it declares one, and a
`<stem>.json` manifest the wiki's generator reads. See shapes/ExlibShapes/README.md for the full option
list and the standalone `exlib-shapes` tool. `--out` needs the `=` form here: PowerShell's own
parameter binder treats a bare `--out` token as an ambiguous prefix of its common
`-OutVariable`/`-OutBuffer` parameters and refuses it outright.

The tool is built only when its output is missing or older than its sources, so renders can run
side by side: a render started under a shared lock (`flock -s -w 240 LOCK exmod ...` with
EXMOD_SHAPES_NO_BUILD=1) never builds, and refuses with the build command when the tool is stale.
Build it under the exclusive lock first: `flock -w 240 -o LOCK exmod shapes-build`.

  FILE            the blocktype JSON to render
  --out=DIR       where the PNGs, the footprint SVG and <stem>.json are written
  --variant CODE  the variant to draw (default: the presentation facing, else the family's first)
  --views a,b     named views (south, north, east, west, up, down, iso); default: all but down
  --angle N       turn the machine before rendering (0, 90, 180 or 270)
  --full          draw the whole model, the parts an animation parks outside the block included
  --ppu N         pixels per shape unit (default: 24)
  --selective PATTERN,...   keep only the selectiveElements that match, as a shape entry's own list does
  --transparent   leave the background transparent
  --roots PATH    extra mod repository roots to resolve textures against (repeatable)
  --game PATH     a specific game install (default: this repository's own provisioned one)
'@

Add-ExmodCommand -Group source -Name item -Summary 'render one itemtype variant to its own picture' -Action {
  param([string[]]$Argv) Invoke-Item $Argv
} -Detail @'
exmod item FILE --out=DIR [--variant CODE] [--ppu N] [--roots PATH...] [--game PATH]

Renders one variant of an itemtype file: the isometric view of its own shape, painted with the
itemtype's texture map, when it ships a model, and its flat inventory texture enlarged on the same
paper when it ships one. Writes `<stem>-iso.png`, `<stem>-icon.png` and a `<stem>.json` manifest.
See shapes/ExlibShapes/README.md for the full option list and the standalone `exlib-shapes` tool.
`--out` needs the `=` form here: PowerShell's own parameter binder treats a bare `--out` token as
an ambiguous prefix of its common `-OutVariable`/`-OutBuffer` parameters and refuses it outright.

The tool is built only when its output is missing or older than its sources, so renders can run
side by side: a render started under a shared lock (`flock -s -w 240 LOCK exmod ...` with
EXMOD_SHAPES_NO_BUILD=1) never builds, and refuses with the build command when the tool is stale.
Build it under the exclusive lock first: `flock -w 240 -o LOCK exmod shapes-build`.

  FILE            the itemtype JSON to render
  --out=DIR       where the PNGs and <stem>.json are written
  --variant CODE  the variant to draw (default: the family's first)
  --ppu N         pixels per shape unit for the isometric render (default: 24)
  --roots PATH    extra mod repository roots to resolve textures against (repeatable)
  --game PATH     a specific game install (default: this repository's own provisioned one)
'@

Add-ExmodCommand -Group source -Name shapes-build -Summary 'build the shapes tool when it is stale' -Action {
  param([string[]]$Argv) Invoke-ShapesBuild $Argv
} -Detail @'
exmod shapes-build [--game PATH]

Builds shapes/ExlibShapes when its output is missing or older than its sources, the ExlibAssets
project it references, the props it imports or the game's VintagestoryAPI.dll, and says so when it
is current. render, schematic, block and item build it themselves the same way; this command is
for a caller that runs them side by side. A build rewrites the tool's bin/ and obj/, so the build
runs under the exclusive build lock and the renders under the shared one:

  flock -w 240 -o LOCK exmod shapes-build
  EXMOD_SHAPES_NO_BUILD=1 flock -s -w 240 LOCK exmod render FILE --out=DIR

With EXMOD_SHAPES_NO_BUILD set a stale tool makes the render refuse instead of building, so two
renders started together on a stale tool never build at once.
'@
