# exlib-shapes: renders a shape file to textured views or animation frames, a multiblock/megablock
# blocktype file to a build schematic, one blocktype variant to the views a wiki page shows, or one
# itemtype variant to its own picture, or a views dump to its pictures - the same commands the wiki's own figures call.
#
#   exmod render      shape file -> textured view or animation-frame PNGs
#   exmod schematic   multiblock/megablock blocktype file -> plan SVGs, an iso PNG, a manifest
#   exmod scene       setup-scene/1 dump -> plain and tinted corner PNGs, a plan SVG per layer, a manifest
#   exmod block       blocktype file -> a PNG per view, a footprint SVG, a manifest
#   exmod item        itemtype file -> an iso PNG or an enlarged icon, or its gui and ground views, a manifest
#   exmod item-views  item-views/1 dump -> a PNG per view, a manifest

# Runs shapes/ExlibShapes's tool for one subcommand. Without EXMOD_SHAPES_NO_BUILD it runs the built
# output, building it first only when that is missing or older than its inputs. With it set, it runs
# the published copy `exmod shapes-build` left under `current`, takes no lock and builds nothing, so
# a build or suite elsewhere never holds a render up; it refuses when nothing is published.
# The way Invoke-Verify runs ExlibVerify: -p:GamePath resolves the tool project's own VintagestoryAPI reference, and the same
# install is passed as the tool's own --game unless the caller already named one (a modder pointing
# at a different install than this repository's own provisioned one). This must be a client install,
# never the default server one: the dedicated-server archive ships almost no assets/survival/textures,
# so a render or schematic against it comes out entirely in the magenta missing-texture placeholder.
function Invoke-ExlibShapes([string]$Subcommand, [string[]]$Argv) {
  $game = Get-Opt $Argv '--game' $null
  if (-not $game) { $game = Resolve-GameInstall $CurrentGameVersion 'client' }

  $toolArgs = @($Subcommand) + $Argv
  if (-not (Get-Opt $Argv '--game' $null)) { $toolArgs += @('--game', $game) }

  if ($env:EXMOD_SHAPES_NO_BUILD) {
    $dir = Get-ShapesPublished (Get-ShapesPublishRoot)
    if (-not $dir) {
      throw "exlib-shapes is not published, and EXMOD_SHAPES_NO_BUILD forbids building. Publish it first: exmod shapes-build"
    }
    $dll = Join-Path $dir 'exlib-shapes.dll'
  } else {
    $dll = Get-ExlibShapesDll
    if (Test-ExlibShapesStale $dll $game) { Build-ExlibShapes $game }
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

# Builds the tool. A build rewrites bin/ and obj/, so renders side by side run from the published
# copy, never from them.
function Build-ExlibShapes([string]$Game) {
  $proj = Join-Path $ToolsRoot 'shapes/ExlibShapes/ExlibShapes.csproj'
  dotnet build $proj -c Debug -p:GamePath=$Game -clp:ErrorsOnly
  if ($LASTEXITCODE -ne 0) { throw "exlib-shapes build failed (exit $LASTEXITCODE)." }
  (Get-Item -LiteralPath (Get-ExlibShapesDll)).LastWriteTimeUtc = [datetime]::UtcNow
}

# The folder the published copies live under: one publish-* folder per publish and `current`, a
# symlink to the one renders run from.
function Get-ShapesPublishRoot { Join-Path $ToolsRoot '.exmod/shapes' }

# The publish folder `current` names, resolved so a render keeps reading it after a later publish
# moves the link; $null when nothing is published there.
function Get-ShapesPublished([string]$Root) {
  $link = Join-Path $Root 'current'
  if (-not (Test-Path -LiteralPath $link)) { return $null }
  $dir = [System.IO.Directory]::ResolveLinkTarget($link, $true)
  if (-not $dir) { $dir = Get-Item -LiteralPath $link }
  if (-not (Test-Path -LiteralPath (Join-Path $dir.FullName 'exlib-shapes.dll'))) { return $null }
  $dir.FullName
}

# Copies the build output folder $BuildDir into a new publish-* folder under $Root, then points
# `current` at it by renaming a fresh symlink over the old one, so a reader sees the old publish or
# the new one whole (Unix: renames with mv -T). Never writes into an existing publish. Returns the new folder's path; $Stamp is
# recorded in it for Test-ShapesPublished.
function Publish-ExlibShapes([string]$Root, [string]$BuildDir, [string]$Stamp) {
  New-Item -ItemType Directory -Force -Path $Root | Out-Null
  $name = 'publish-{0:yyyyMMddHHmmssfff}-{1}' -f [datetime]::UtcNow, $PID
  $dest = Join-Path $Root $name
  New-Item -ItemType Directory -Path $dest | Out-Null
  Copy-Item -Path (Join-Path $BuildDir '*') -Destination $dest -Recurse
  Set-Content -LiteralPath (Join-Path $dest 'source-stamp') -Value $Stamp -NoNewline

  $next = Join-Path $Root "current.$PID.new"
  if (Test-Path -LiteralPath $next) { Remove-Item -LiteralPath $next -Force }
  New-Item -ItemType SymbolicLink -Path $next -Target $name | Out-Null
  & mv -T -- $next (Join-Path $Root 'current')
  if ($LASTEXITCODE -ne 0) { throw "Could not switch $Root/current to $name (mv exit $LASTEXITCODE)." }
  $dest
}

# True when `current` under $Root holds a publish made from the build stamped $Stamp.
function Test-ShapesPublished([string]$Root, [string]$Stamp) {
  $dir = Get-ShapesPublished $Root
  if (-not $dir) { return $false }
  $file = Join-Path $dir 'source-stamp'
  (Test-Path -LiteralPath $file) -and ((Get-Content -LiteralPath $file -Raw) -eq $Stamp)
}

# Deletes the publish-* folders under $Root other than the one `current` names and the newest other
# one, when they were last written more than $MinAgeMinutes ago.
function Remove-OldShapesPublishes([string]$Root, [int]$MinAgeMinutes = 10) {
  $live = Get-ShapesPublished $Root
  $others = @(Get-ChildItem -LiteralPath $Root -Directory -Filter 'publish-*' |
    Where-Object { $_.FullName -ne $live } | Sort-Object Name -Descending)
  $cutoff = [datetime]::UtcNow.AddMinutes(-$MinAgeMinutes)
  foreach ($old in ($others | Select-Object -Skip 1)) {
    if ($old.LastWriteTimeUtc -lt $cutoff) { Remove-Item -LiteralPath $old.FullName -Recurse -Force }
  }
}

function Invoke-Render([string[]]$Argv) { Invoke-ExlibShapes 'render' $Argv }
function Invoke-Schematic([string[]]$Argv) { Invoke-ExlibShapes 'schematic' $Argv }
function Invoke-Scene([string[]]$Argv) { Invoke-ExlibShapes 'scene' $Argv }
function Invoke-Block([string[]]$Argv) { Invoke-ExlibShapes 'block' $Argv }
function Invoke-ShapesBuild([string[]]$Argv) {
  $game = Get-Opt $Argv '--game' $null
  if (-not $game) { $game = Resolve-GameInstall $CurrentGameVersion 'client' }
  if (Test-ExlibShapesStale (Get-ExlibShapesDll) $game) { Build-ExlibShapes $game } else { 'exlib-shapes is current.' }
  $dll = Get-Item -LiteralPath (Get-ExlibShapesDll)
  $root = Get-ShapesPublishRoot
  $stamp = [string]$dll.LastWriteTimeUtc.Ticks
  if (Test-ShapesPublished $root $stamp) { return }
  $dest = Publish-ExlibShapes $root $dll.DirectoryName $stamp
  Remove-OldShapesPublishes $root
  "exlib-shapes published to $dest."
}

function Invoke-Item([string[]]$Argv) { Invoke-ExlibShapes 'item' $Argv }
function Invoke-ItemViews([string[]]$Argv) { Invoke-ExlibShapes 'item-views' $Argv }

Add-ExmodCommand -Group source -Name render -Summary 'render a shape file to textured views or animation frames' -Action {
  param([string[]]$Argv) Invoke-Render $Argv
} -Detail @'
exmod render FILE --out=DIR [--views a,b,...] [--ppu N] [--anim CLIP --frames N] [--only PATH...]
  [--highlight PATH...] [--selective PATTERN,...] [--no-grid] [--no-edges] [--game PATH] [--repo PATH]

Renders a shape file's named views (or one animation clip's frames) to PNG - see
shapes/ExlibShapes/README.md for the full option list and the standalone `exlib-shapes` tool.
`--out` needs the `=` form here: PowerShell's own parameter binder treats a bare `--out` token as
an ambiguous prefix of its common `-OutVariable`/`-OutBuffer` parameters and refuses it outright.

The tool is built only when its output is missing or older than its sources. With
EXMOD_SHAPES_NO_BUILD=1 it runs the copy `exmod shapes-build` published, takes no lock, so renders
run side by side while a build or suite runs, and refuses with the build command when nothing is
published.

  FILE            the shape JSON to render
  --out=DIR       where the PNGs are written
  --views a,b     named views to render (south, north, east, west, up, down, iso, ne, nw, se, sw); default: iso
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

The tool is built only when its output is missing or older than its sources. With
EXMOD_SHAPES_NO_BUILD=1 it runs the copy `exmod shapes-build` published, takes no lock, so renders
run side by side while a build or suite runs, and refuses with the build command when nothing is
published.

  FILE            the blocktype JSON to render
  --out=DIR       where the SVGs, PNGs and <stem>.json are written
  --views a,b     plan, iso, or both (default: plan,iso)
  --angle N       turn the structure before rendering (0, 90, 180 or 270)
  --layer N|all   an iso render cut at Y layer N, or one per layer with "all"
  --ppu N         pixels per shape unit for the iso render (default: 8)
  --roots PATH    extra mod repository roots to resolve selectors against (repeatable)
  --game PATH     a specific game install (default: this repository's own provisioned one)
'@

Add-ExmodCommand -Group source -Name scene -Summary 'render a setup-scene/1 dump to corner views and layer plans' -Action {
  param([string[]]$Argv) Invoke-Scene $Argv
} -Detail @'
exmod scene PATH --out=DIR [--views ne,nw,se,sw,plan] [--ppu N] [--roots PATH...] [--game PATH]

Renders a setup's `<setup>.scene.json` dump to a plain and a tinted PNG per corner view, an SVG
plan per Y layer and a `<stem>.json` manifest of the files, the networks and lines, the unjoined
ports, the open faces, the codes no root resolved, the missing textures and the warnings. A folder PATH renders every `*.scene.json`
directly in it, in name order. Prints each file and the manifest path, then one `unjoined:` or
`open:` line per finding. Exits 1 for an unreadable scene or a format other
than setup-scene/1. `--out` needs the `=` form here: PowerShell's own parameter binder treats a
bare `--out` token as an ambiguous prefix of its common `-OutVariable`/`-OutBuffer` parameters and
refuses it outright.

The tool is built only when its output is missing or older than its sources. With
EXMOD_SHAPES_NO_BUILD=1 it runs the copy `exmod shapes-build` published, takes no lock, so renders
run side by side while a build or suite runs, and refuses with the build command when nothing is
published.

  PATH            a .scene.json file, or a folder of them
  --out=DIR       where the PNGs, SVGs and <stem>.json are written
  --views a,b     ne, nw, se, sw and plan (default: ne,nw,se,sw,plan)
  --ppu N         pixels per shape unit for the corner views (default: 4)
  --roots PATH    extra mod repository roots to resolve block codes against (repeatable)
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

The tool is built only when its output is missing or older than its sources. With
EXMOD_SHAPES_NO_BUILD=1 it runs the copy `exmod shapes-build` published, takes no lock, so renders
run side by side while a build or suite runs, and refuses with the build command when nothing is
published.

  FILE            the blocktype JSON to render
  --out=DIR       where the PNGs, the footprint SVG and <stem>.json are written
  --variant CODE  the variant to draw (default: the presentation facing, else the family's first)
  --views a,b     named views (south, north, east, west, up, down, iso, ne, nw, se, sw); default: all but down
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
exmod item FILE --out=DIR [--variant CODE] [--views gui,tp,fp,ground[,iso]] [--ppu N] [--roots PATH...] [--game PATH]

Renders one variant of an itemtype file: the isometric view of its own shape, painted with the
itemtype's texture map, when it ships a model, and its flat inventory texture enlarged on the same
paper when it ships one. Writes `<stem>-iso.png`, `<stem>-icon.png` and a `<stem>.json` manifest.
With --views it writes exactly the views named, each to `<stem>-<view>.png`: `gui` is the item in
the centre slot of a 3 x 3 grid of inventory slots, `tp` the player holding it, seen from the right
side and from the front right side by side, `fp` its hands holding it seen from the eye (both need the game's player shape among
the assets), `ground`
the item lying on a stone block, all from the itemtype's own transforms and held clips.
See shapes/ExlibShapes/README.md for the full option list and the standalone `exlib-shapes` tool.
`--out` needs the `=` form here: PowerShell's own parameter binder treats a bare `--out` token as
an ambiguous prefix of its common `-OutVariable`/`-OutBuffer` parameters and refuses it outright.

The tool is built only when its output is missing or older than its sources. With
EXMOD_SHAPES_NO_BUILD=1 it runs the copy `exmod shapes-build` published, takes no lock, so renders
run side by side while a build or suite runs, and refuses with the build command when nothing is
published.

  FILE            the itemtype JSON to render
  --out=DIR       where the PNGs and <stem>.json are written
  --variant CODE  the variant to draw (default: the family's first)
  --views LIST    comma-separated: iso, gui, tp, fp, ground (default: the iso render and the icon)
  --ppu N         pixels per shape unit for the isometric render (default: 24)
  --roots PATH    extra mod repository roots to resolve textures against (repeatable)
  --game PATH     a specific game install (default: this repository's own provisioned one)
'@

Add-ExmodCommand -Group source -Name item-views -Summary 'render an item-views/1 dump to its gui, ground and iso pictures' -Action {
  param([string[]]$Argv) Invoke-ItemViews $Argv
} -Detail @'
exmod item-views PATH --out=DIR [--views gui,tp,fp,ground,iso] [--sheet] [--ppu N] [--roots PATH...] [--game PATH]

Renders an item's `<domain>-<code>.views.json` dump, the transforms the game resolved for a group of
collectibles, to a `<stem>-<view>.png` per view and a `<stem>.json` manifest of the files, the
dump's view and kind, the gates that failed, the missing textures and the warnings. A folder PATH
renders every `*.views.json` directly in it, in name order. Prints each file and the manifest path.
With --sheet it also writes `sheet.png`, one row per dump, and prints its path.
Exits 1 for an unreadable dump or a format other than item-views/1. `--out` needs the `=` form
here: PowerShell's own parameter binder treats a bare `--out` token as an ambiguous prefix of its
common `-OutVariable`/`-OutBuffer` parameters and refuses it outright.

The tool is built only when its output is missing or older than its sources. With
EXMOD_SHAPES_NO_BUILD=1 it runs the copy `exmod shapes-build` published, takes no lock, so renders
run side by side while a build or suite runs, and refuses with the build command when nothing is
published.

  PATH            a .views.json file, or a folder of them
  --out=DIR       where the PNGs and <stem>.json are written
  --views LIST    comma-separated: iso, gui, tp, fp, ground (default: gui,ground,iso)
  --sheet         also write sheet.png to --out: a row per dump, a column per view, failing gates in red
  --ppu N         pixels per shape unit for the isometric render (default: 24)
  --roots PATH    extra mod repository roots to resolve the shape and textures against (repeatable)
  --game PATH     a specific game install (default: this repository's own provisioned one)
'@

Add-ExmodCommand -Group source -Name shapes-build -Summary 'build the shapes tool when it is stale and publish it' -Action {
  param([string[]]$Argv) Invoke-ShapesBuild $Argv
} -Detail @'
exmod shapes-build [--game PATH]

Builds shapes/ExlibShapes when its output is missing or older than its sources, the ExlibAssets
project it references, the props it imports or the game's VintagestoryAPI.dll, then publishes the
output to a new folder under .exmod/shapes and points the `current` symlink there by rename. The
previous publish stays; older ones are deleted once they are more than 10 minutes old. render,
schematic, scene, block, item and item-views build the tool themselves the same way, unless
EXMOD_SHAPES_NO_BUILD is set: then they run `current` with no lock, and refuse when nothing is
published. Only the build needs the exclusive build lock; the renders do not:

  flock -w 240 -o LOCK exmod shapes-build
  EXMOD_SHAPES_NO_BUILD=1 exmod render FILE --out=DIR
'@
