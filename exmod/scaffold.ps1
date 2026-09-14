# Scaffolding one thing into an existing mod: a block, a megablock, a multiblock machine or a
# network node, from the templates exlib ships as ExpandedLib.Templates.
#
#   exmod scaffold <kind> <Name> [-Mod <id>]

$ScaffoldKinds = @('block', 'item', 'recipe', 'megablock', 'multiblock', 'node', 'blockbehavior', 'entitybehavior', 'config', 'migration', 'command')

# The lang keys each kind's generated code reads, with the text a fresh mod ships. Merged into the
# mod's en.json so LangParityTests is green the moment the files land.
function Get-ScaffoldLangKeys([string]$Kind, [string]$Domain, [string]$Slug) {
  switch ($Kind) {
    'block'      { @{ "block-$Slug-*" = $Slug; "${Domain}:$Slug-ticks" = 'Ticks: {0}' } }
    'node'       { @{ "block-$Slug-*" = $Slug } }
    'megablock'  { @{ "block-$Slug-*" = $Slug; "${Domain}:$Slug-cells" = 'Cells: {0}'; "${Domain}:$Slug-clicked" = "The $Slug clicks." } }
    'multiblock' { @{ "block-$Slug-*" = $Slug; "${Domain}:multiblock-$Slug-incomplete" = "The $Slug is missing {0} block(s)."; "${Domain}:multiblock-$Slug-complete" = "The $Slug is complete."; "${Domain}:$Slug-ticks" = 'Ticks: {0}' } }
    'item'       { @{ "item-$Slug" = $Slug } }
    'recipe'     { @{} }
    'blockbehavior'  { @{ "${Domain}:$Slug-info" = "${Slug}: {0}" } }
    'entitybehavior' { @{ "${Domain}:$Slug-count" = "$Slug count: {0}" } }
    'config'     { @{} }
    'migration'  { @{} }
    'command'    { @{ "${Domain}:command-$Slug-desc" = "Prints the $Slug line."; "${Domain}:command-$Slug-result" = "${Slug}: {0}" } }
  }
}

function Merge-LangKeys([string]$Path, [hashtable]$Keys) {
  $lang = if (Test-Path $Path) { Get-Content $Path -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
  foreach ($k in $Keys.Keys) { if (-not $lang.ContainsKey($k)) { $lang[$k] = $Keys[$k] } }
  New-Item -ItemType Directory -Force -Path (Split-Path $Path -Parent) | Out-Null
  ($lang | ConvertTo-Json -Depth 3) | Set-Content $Path
}

# The template source: the sibling exlib checkout's own folder when the workspace has one (source
# mode: the templates are proven against that checkout's API), else the package at the version
# Directory.Packages.props pins.
function Install-ScaffoldTemplate([string]$Kind) {
  $sibling = Join-Path $RepoRoot '../exlib/templates/content'
  if (Test-Path (Join-Path $sibling "exlib-$Kind")) {
    dotnet new install (Join-Path $sibling "exlib-$Kind") --force | Out-Null
    return
  }
  $props = Join-Path $RepoRoot 'Directory.Packages.props'
  if (-not (Test-Path $props)) { throw "No Directory.Packages.props under $RepoRoot to read the pinned exlib version from." }
  $m = [regex]::Match((Get-Content $props -Raw), 'PackageVersion Include="ExpandedLib" Version="([^"]+)"')
  if (-not $m.Success) { throw "Directory.Packages.props names no ExpandedLib PackageVersion." }
  dotnet new install "ExpandedLib.Templates::$($m.Groups[1].Value)" --force | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "dotnet new install ExpandedLib.Templates::$($m.Groups[1].Value) failed." }
}

function Invoke-Scaffold([string[]]$Argv) {
  $positional = @(Get-Positional $Argv @('-Mod') @())
  if ($positional.Count -lt 2) { throw "exmod scaffold needs a kind and a name: exmod scaffold <kind> <Name> [-Mod <id>]" }
  $kind = $positional[0]; $name = $positional[1]
  if ($kind -notin $ScaffoldKinds) { throw "Unknown kind '$kind'. Kinds: $($ScaffoldKinds -join ', ')." }
  if ($name -notmatch '^[A-Z][A-Za-z0-9]*$') { throw "Name '$name' must be PascalCase: a capital letter, then letters and digits." }

  $manifest = Get-ExmodManifest
  $modIds = @($manifest.mods.PSObject.Properties.Name)
  $modId = Get-Opt $Argv '-Mod' $null
  if (-not $modId) {
    if ($modIds.Count -ne 1) { throw "This repository names $($modIds.Count) mods; pass -Mod <id> ($($modIds -join ', '))." }
    $modId = $modIds[0]
  }
  if ($modId -notin $modIds) { throw "exmod.json names no mod '$modId'." }
  $modDir = Join-Path $RepoRoot $manifest.mods.$modId.path
  $csproj = Get-ChildItem (Join-Path $modDir 'src') -Filter *.csproj | Select-Object -First 1
  if (-not $csproj) { throw "No csproj under $modDir/src." }
  $ns = [regex]::Match((Get-Content $csproj.FullName -Raw), '<RootNamespace>([^<]+)</RootNamespace>').Groups[1].Value
  if (-not $ns) { $ns = $modId.Substring(0, 1).ToUpperInvariant() + $modId.Substring(1) }

  Write-Step "Scaffolding $kind $name into mods/$modId"
  Install-ScaffoldTemplate $kind
  # The dry run lists the files the template will write, so the summary names them whatever the kind.
  $planned = dotnet new "exlib-$kind" -n $name -o $modDir --Domain $modId --Namespace $ns --force --dry-run 2>&1 | Where-Object { $_ -match '^\s+(Creating|File):?\s' -or $_ -match '\.(cs|json)$' }
  dotnet new "exlib-$kind" -n $name -o $modDir --Domain $modId --Namespace $ns --force
  if ($LASTEXITCODE -ne 0) { throw "dotnet new exlib-$kind failed." }
  $keys = Get-ScaffoldLangKeys $kind $modId $name.ToLowerInvariant()
  if ($keys.Count -gt 0) { Merge-LangKeys (Join-Path $modDir "assets/$modId/lang/en.json") $keys }
  Write-Host "Scaffolded $kind $name into mods/${modId}:"
  $planned | ForEach-Object { Write-Host "  $_" }
  if ($keys.Count -gt 0) { Write-Host "  lang keys merged: $($keys.Keys -join ', ')" }
}

Add-ExmodCommand -Group start -Name scaffold -Alias @('g') -Summary 'scaffold a block, megablock, multiblock or node into a mod' -Action {
  param([string[]]$Argv) Invoke-Scaffold $Argv
} -Detail @'
exmod scaffold <kind> <Name> [-Mod <id>]

Puts a compiling, tested <kind> named <Name> into mods/<id>: src/Blocks/Block<Name>.cs,
src/BlockEntities/BlockEntity<Name>.cs and tests/<Name>Tests.cs, from the dotnet new templates
exlib ships (ExpandedLib.Templates, at the version Directory.Packages.props pins; the sibling
exlib checkout's own templates when the workspace has one). The lang keys the generated code
reads are merged into assets/<id>/lang/en.json.

  kind     block (a block and its entity with a saved counter), item (a code-first item), recipe (a
           grid recipe), megablock (a 3x3 filler footprint with per-cell interaction), multiblock (a
           layout of vanilla blocks around a core with a production tick), node (a self-orienting
           network node and its entity), blockbehavior, entitybehavior (a behaviour with a saved
           counter), config (a ranged, live-editable value), migration (a block code rename),
           command (a /exmod sub-command)
  -Mod     the target mod; needed when exmod.json names more than one

<Name> is PascalCase. Read the wiki's First-Machine page for what each kind shows.
'@
