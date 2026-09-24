#!/usr/bin/env pwsh
# exmod - one entry point for every stage of this repo's life: provisioning a fresh clone, building,
# testing, running the game, and packaging a release. Runs on Windows, Linux and macOS under
# PowerShell 7; the platform differences live in $OnWindows branches rather than in a second copy of
# each script that has to be kept in step. exmod.sh is a launcher for POSIX shells, not a second
# implementation - it finds pwsh (bootstrapping it, when absent, into the .dotnet/tools of the
# workspace root when an exmod.workspace.json is above the checkout, else of the checkout) and
# forwards here.
#
# This file is the dispatcher: the argument helpers and resolvers every command shares, and the
# registry they register themselves in. The commands live one file per stage under scripts/exmod/ -
# provision, src, run, dist, windows - and each of those opens with the list of commands it owns.
#
#   exmod                   the command list, grouped
#   exmod help <command>    one command in detail
#   exmod -RepoRoot <path> <command>   act on a checkout other than the one containing this script

[CmdletBinding()]
param(
  [Parameter()][string]$RepoRoot,
  [Parameter(Position = 0)][string]$Command,
  [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$Arguments = @()
)

$ErrorActionPreference = 'Stop'

# The nearest directory above $PWD that holds exmod.json, so exmod works run from any subdirectory
# of a checkout - not only its root - the way git finds .git. Falls back to the wrapper's own parent
# when nothing above $PWD has one (e.g. this script run against a bare template). -RepoRoot skips
# the search and overrides both.
function Get-ExmodRepoRoot([string]$Override) {
  if ($Override) {
    if (-not (Test-Path $Override)) { throw "-RepoRoot path not found: $Override" }
    return [System.IO.Path]::GetFullPath((Resolve-Path $Override).ProviderPath)
  }
  $dir = (Get-Location).ProviderPath
  while ($true) {
    if (Test-Path (Join-Path $dir 'exmod.json')) { return $dir }
    $parent = Split-Path $dir -Parent
    if (-not $parent -or $parent -eq $dir) { break }
    $dir = $parent
  }
  return [System.IO.Path]::GetFullPath((Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath)
}

$RepoRoot = Get-ExmodRepoRoot $RepoRoot
# The tools checkout itself: the stage files, the packaging build, the verify tool and the helper
# scripts live beside this dispatcher, whatever repository it is driving.
$ToolsRoot = $PSScriptRoot
$OnWindows = [System.OperatingSystem]::IsWindows()
$ExeSuffix = if ($OnWindows) { '.exe' } else { '' }
# The slot suffix of this platform's client and the native library only a package built for it
# carries. A client built for another OS has Vintagestory.dll but not that library, so it cannot
# run here.
$PlatformSlot = if ($OnWindows) { 'windows' } elseif ($IsMacOS) { 'macos' } else { 'linux' }

# The nearest directory strictly above $RepoRoot holding exmod.workspace.json, the marker of a
# workspace whose sibling repositories share one .game and one .dotnet. $null for
# a standalone clone; a marker in $RepoRoot itself does not count.
function Get-ExmodWorkspaceRoot {
  $dir = Split-Path $RepoRoot -Parent
  while ($dir) {
    if (Test-Path -LiteralPath (Join-Path $dir 'exmod.workspace.json') -PathType Leaf) { return $dir }
    $parent = Split-Path $dir -Parent
    if ($parent -eq $dir) { break }
    $dir = $parent
  }
  return $null
}

# The full path <dir>/<Relative> for the nearest <dir> from $RepoRoot upward, $RepoRoot first, where
# that file or folder exists; $null when no directory up to the filesystem root holds it.
function Find-ExmodAbove([string]$Relative) {
  $dir = $RepoRoot
  while ($dir) {
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $dir $Relative))
    if (Test-Path -LiteralPath $candidate) { return $candidate }
    $parent = Split-Path $dir -Parent
    if ($parent -eq $dir) { break }
    $dir = $parent
  }
  return $null
}

# Where a fresh .game or .dotnet is provisioned: the workspace root inside a workspace, else
# $RepoRoot.
function Get-ExmodProvisionRoot {
  $workspace = Get-ExmodWorkspaceRoot
  if ($workspace) { return $workspace }
  return $RepoRoot
}

# The per-user folder holding the runnable client and the game data, outside every checkout: a
# native library does not load from a network share, SQLite cannot lock a save on one, and a
# checkout under \\wsl.localhost is one. %LOCALAPPDATA%\exmod on Windows,
# ~/Library/Application Support/exmod on macOS, ~/.local/share/exmod on Linux. XDG_DATA_HOME is not
# read; the generated launch configurations name this same folder.
function Get-ExmodUserStore {
  if ($OnWindows) { return Join-Path $env:LOCALAPPDATA 'exmod' }
  if ($IsMacOS) { return Join-Path $HOME 'Library/Application Support/exmod' }
  return Join-Path $HOME '.local/share/exmod'
}

# The data profile's name: 'default' for every checkout, in a workspace or not, so `exmod client`
# and every generated launch configuration share one set of settings, saves and mod configs.
function Get-ExmodProfile {
  return 'default'
}

# This OS's runnable client for series $Slug: <user store>/game/<slug>. The folder may not exist.
function Get-ClientSlot([string]$Slug) {
  return Join-Path (Get-ExmodUserStore) "game/$Slug"
}

# The client's data folder (settings, saves, mod configs): <user store>/data/<profile>. The folder
# may not exist.
function Get-ClientDataPath {
  return Join-Path (Get-ExmodUserStore) "data/$(Get-ExmodProfile)"
}

# The client's log folder under $DataPath, one per repository so the runs of two repositories
# sharing a profile keep separate logs: <DataPath>/Logs/<repository folder>.
function Get-ClientLogPath([string]$DataPath) {
  return Join-Path $DataPath "Logs/$(Split-Path $RepoRoot -Leaf)"
}

# The .dotnet folder to use and provision into: $RepoRoot/.dotnet when it holds a dotnet muxer, else
# the workspace root's when it holds one, else <provision root>/.dotnet, which may not exist yet.
# No folder above the workspace root, or above $RepoRoot outside a workspace, is searched: the
# home folder's .dotnet is the user's own dotnet or the CLI's global-tools folder.
function Get-ExmodDotnetDir {
  $roots = @($RepoRoot, (Get-ExmodWorkspaceRoot)) | Where-Object { $_ }
  foreach ($root in $roots) {
    $dir = Join-Path $root '.dotnet'
    if (Test-Path -LiteralPath (Join-Path $dir "dotnet$ExeSuffix") -PathType Leaf) { return $dir }
  }
  return Join-Path (Get-ExmodProvisionRoot) '.dotnet'
}

# Whether a project's last build was another platform's: its MSBuild file list names paths in
# the other platform's form.
function Test-ForeignBuildState([string]$ProjectDir) {
  $lists = @(Get-ChildItem -Path (Join-Path $ProjectDir 'obj') -Recurse -Filter '*.FileListAbsolute.txt' -ErrorAction SilentlyContinue)
  foreach ($list in $lists) {
    $first = Get-Content $list.FullName -TotalCount 1 -ErrorAction SilentlyContinue
    if (-not $first) { continue }
    if ($OnWindows) { if ($first.StartsWith('/')) { return $true } }
    elseif ($first -match '^([A-Za-z]:\\|\\\\)') { return $true }
  }
  return $false
}

function Get-NativeMarker([string]$InstallDir) {
  $lib = if ($OnWindows) { 'Lib/e_sqlite3.dll' } elseif ($IsMacOS) { 'Lib/libe_sqlite3.dylib' } else { 'Lib/libe_sqlite3.so' }
  return Join-Path $InstallDir $lib
}

# MSBuild worker nodes are not kept alive after a build: on this install idle nodes never exit and
# a day of building left 74 of them holding 11 GB. Directory.Build.rsp says the same for builds
# started outside this script.
$env:MSBUILDDISABLENODEREUSE = '1'
# glibc 2.41 and later refuse to load a shared object that needs an executable stack; MonoMod's
# native helper for the .NET 7 lane (game 1.20) is one, so every Harmony patch there fails without
# this tunable. Harmless on older glibc and on the other lanes.
if (-not $OnWindows) { $env:GLIBC_TUNABLES = 'glibc.rtld.execstack=2' }

#region Argument helpers

# -Name <value>; returns $Default when absent.
function Get-Opt([string[]]$Argv, [string]$Name, $Default = $null) {
  for ($i = 0; $i -lt $Argv.Count; $i++) {
    if ($Argv[$i] -ieq $Name) {
      if ($i + 1 -ge $Argv.Count) { throw "$Name needs a value." }
      return $Argv[$i + 1]
    }
  }
  return $Default
}

# -Name used as a switch.
function Get-Flag([string[]]$Argv, [string]$Name) {
  foreach ($a in $Argv) { if ($a -ieq $Name) { return $true } }
  return $false
}

# Arguments that are neither an option name nor an option value.
function Get-Positional([string[]]$Argv, [string[]]$ValueOpts, [string[]]$FlagOpts) {
  $out = @()
  for ($i = 0; $i -lt $Argv.Count; $i++) {
    $a = $Argv[$i]
    if ($ValueOpts -contains $a) { $i++; continue }
    if ($FlagOpts -contains $a) { continue }
    $out += $a
  }
  # Returned bare. Every call site wraps the result in @(), which is what keeps a none- or one-element
  # result an array; returning `, $out` on top of that nests it, so the caller's [0] is the whole inner
  # array and interpolates as one space-joined string. That reads as a single malformed positional -
  # `exmod test 1.21 -Filter X` reported `Unknown version '1.21 -Filter X'` - and hides until a command
  # is given two positionals, which is why it survived in `test` and `stage` alike.
  return $out
}

function Assert-Windows([string]$What) {
  if (-not $OnWindows) { throw "$What is Windows-only." }
}

# One section header, so a command built out of several steps reads as those steps on the terminal.
function Write-Step([string]$Text) {
  Write-Host ''
  Write-Host "== $Text" -ForegroundColor Cyan
}

#endregion

#region Shared resolvers

# The three supported game series and what each one builds against - vendor knowledge, true in
# every repo that builds against this engine, so it stays here rather than in the manifest. Every
# version-taking command resolves through here, so a new series is added in one place.
$GameTfms = [ordered]@{ '1.22' = 'net10.0'; '1.21' = 'net8.0'; '1.20' = 'net7.0' }
$GameRuntimeMajors = @{ '1.22' = '10'; '1.21' = '8'; '1.20' = '7' }

#region Manifest

# Resolves a manifest-relative path to an absolute one. Fails naming the field and the path it
# named, rather than the missing-file error a downstream Get-ChildItem would give - the manifest
# naming a path that isn't there is a manifest bug, not a build that hasn't happened yet.
function Resolve-ManifestPath([string]$Field, [string]$RelPath) {
  if (-not $RelPath) { throw "exmod.json: '$Field' is required." }
  $full = if ([System.IO.Path]::IsPathRooted($RelPath)) { $RelPath } else { Join-Path $RepoRoot $RelPath }
  if (-not (Test-Path $full)) { throw "exmod.json: '$Field' names a path that does not exist: $RelPath" }
  return (Resolve-Path $full).ProviderPath
}

# The single .csproj directly under $Dir, or a naming error when there is none or more than one -
# the convention every mod, sample, test and package entry in the manifest relies on.
function Find-SingleCsproj([string]$Dir, [string]$Field) {
  $found = @(Get-ChildItem $Dir -Filter '*.csproj' -File)
  if ($found.Count -ne 1) {
    throw "exmod.json: '$Field' names $Dir, which holds $($found.Count) .csproj file(s) (want exactly one)."
  }
  return $found[0].FullName
}

# The directory a mod's project resolves under: <path>/src when that folder holds exactly one
# .csproj (the layout every real mod, sample and generated starter uses), otherwise <path> itself -
# a mod with no src/ split at all.
function Get-ModProjectDir([string]$Path) {
  $srcDir = Join-Path $Path 'src'
  if ((Test-Path $srcDir) -and @(Get-ChildItem $srcDir -Filter '*.csproj' -File).Count -eq 1) { return $srcDir }
  return $Path
}

# Reads exmod.json once and applies its defaults - this is the only place they're written down.
# ConvertFrom-Json keeps a PSCustomObject's property order as the file's order, so `mods` and
# `samples` are read through .PSObject.Properties everywhere, never Keys/Values, to keep that order.
function Get-ExmodManifest {
  if ($Script:ExmodManifestCache) { return $Script:ExmodManifestCache }

  $path = Join-Path $RepoRoot 'exmod.json'
  if (-not (Test-Path $path)) { throw "No exmod.json under $RepoRoot." }
  $m = Get-Content $path -Raw | ConvertFrom-Json

  # 'solution' is resolved lazily, by Get-ExmodSolution, so a repo with neither a 'solution' field
  # nor a .sln at its root still loads and runs every command that names no solution.
  if (-not $m.PSObject.Properties['series'] -or -not $m.series) {
    # No series named at all: the first entry of the vendor table above is "the current series".
    $m | Add-Member -NotePropertyName series -NotePropertyValue @(@($GameTfms.Keys)[0]) -Force
  }
  foreach ($p in @('mods', 'samples')) {
    if (-not $m.PSObject.Properties[$p]) { $m | Add-Member -NotePropertyName $p -NotePropertyValue ([pscustomobject]@{}) }
  }
  foreach ($p in @('tests', 'packages')) {
    if (-not $m.PSObject.Properties[$p]) { $m | Add-Member -NotePropertyName $p -NotePropertyValue @() }
  }

  $Script:ExmodManifestCache = $m
  return $m
}

# Every mod this repo builds as its own, in manifest order (the build order: exlib before iiex
# before siex is a real ProjectReference chain, not a discovery accident), id -> @{ Path; Project;
# Tests; Overlays } (all absolute except Overlays). A mod's project is the single .csproj under
# <path>/src when that folder holds one, or under <path> itself otherwise - every real mod, sample
# and generated starter mod carries the split; its test project is the single .csproj under
# <path>/tests when that folder exists.
function Get-ExmodMods {
  $manifest = Get-ExmodManifest
  $out = [ordered]@{}
  foreach ($prop in $manifest.mods.PSObject.Properties) {
    $id = $prop.Name
    $entry = $prop.Value
    $path = Resolve-ManifestPath "mods.$id.path" $entry.path
    $project = Find-SingleCsproj (Get-ModProjectDir $path) "mods.$id"
    $testsDir = Join-Path $path 'tests'
    $tests = if (Test-Path $testsDir) { Find-SingleCsproj $testsDir "mods.$id.tests" } else { $null }
    $out[$id] = [pscustomobject]@{ Path = $path; Project = $project; Tests = $tests; Overlays = $entry.overlays }
  }
  return $out
}

# Every sample this repo builds as its own mod, in manifest order, id -> @{ Path; Project; Tests }.
# A sample's project is the single .csproj under <path>/src when that folder holds one, or under
# <path> itself otherwise, the same rule Get-ExmodMods resolves a mod's project by; its test
# project, when the manifest names one, is the single .csproj under that named folder.
function Get-ExmodSamples {
  $manifest = Get-ExmodManifest
  $out = [ordered]@{}
  foreach ($prop in $manifest.samples.PSObject.Properties) {
    $id = $prop.Name
    $entry = $prop.Value
    $path = Resolve-ManifestPath "samples.$id.path" $entry.path
    $project = Find-SingleCsproj (Get-ModProjectDir $path) "samples.$id"
    $tests = if ($entry.PSObject.Properties['tests'] -and $entry.tests) {
      Find-SingleCsproj (Resolve-ManifestPath "samples.$id.tests" $entry.tests) "samples.$id.tests"
    } else { $null }
    $out[$id] = [pscustomobject]@{ Path = $path; Project = $project; Tests = $tests }
  }
  return $out
}

# Every mod/sample this repo builds as its own mod, in manifest order (mods, then samples), as
# folder-name key -> its project path. Shared by build, run and verify so none of them can drift on
# what "every mod" means.
function Get-ExmodBuildTargets {
  $out = [ordered]@{}
  foreach ($mod in (Get-ExmodMods).GetEnumerator()) { $out[$mod.Key] = $mod.Value.Project }
  foreach ($sample in (Get-ExmodSamples).GetEnumerator()) { $out[$sample.Key] = $sample.Value.Project }
  return $out
}

# Every test project this repo runs: each mod's, in mod order, then each sample's, then
# $Manifest.tests, each carrying the series it runs for - every series in the manifest for a mod
# and for a $Manifest.tests entry (a mod-level test project that simply lives outside its mod's own
# folder, e.g. exlib's own ExpandedLib.Tests), the current series only for a sample (a legacy lane
# buys it nothing). Shared by `test` and `build -Tests` so the two commands cannot drift on what
# "every test project" means.
function Get-ExmodTestProjects {
  $manifest = Get-ExmodManifest
  $series = @($manifest.series)
  $current = @($series[0])

  $out = [ordered]@{}
  foreach ($mod in (Get-ExmodMods).GetEnumerator()) {
    if (-not $mod.Value.Tests) { continue }
    $out[$mod.Key] = [pscustomobject]@{
      Project = [IO.Path]::GetFileNameWithoutExtension($mod.Value.Tests)
      Proj    = $mod.Value.Tests
      Series  = $series
    }
  }
  foreach ($sample in (Get-ExmodSamples).GetEnumerator()) {
    if (-not $sample.Value.Tests) { continue }
    $out[$sample.Key] = [pscustomobject]@{
      Project = [IO.Path]::GetFileNameWithoutExtension($sample.Value.Tests)
      Proj    = $sample.Value.Tests
      Series  = $current
    }
  }
  $i = 0
  foreach ($rel in @($manifest.tests)) {
    $proj = Find-SingleCsproj (Resolve-ManifestPath "tests[$i]" $rel) "tests[$i]"
    $name = [IO.Path]::GetFileNameWithoutExtension($proj)
    $id = ($name -replace '\.Tests$', '').ToLowerInvariant()
    $out[$id] = [pscustomobject]@{ Project = $name; Proj = $proj; Series = $series }
    $i++
  }
  return $out
}

# The packable project paths named by $Manifest.packages, each a folder holding exactly one
# .csproj.
function Get-ExmodPackages {
  $manifest = Get-ExmodManifest
  $i = 0
  $out = @()
  foreach ($rel in @($manifest.packages)) {
    $out += Find-SingleCsproj (Resolve-ManifestPath "packages[$i]" $rel) "packages[$i]"
    $i++
  }
  return $out
}

# The coverage floors file: $Manifest.coverageFloors when the manifest names one (a path that does
# not exist is a manifest error), else the first of tests/coverage-floors.json and
# infra/test/coverage-floors.json that exists, else $null - a repository with no floors runs no gate.
function Get-ExmodCoverageFloors {
  $manifest = Get-ExmodManifest
  if ($manifest.PSObject.Properties['coverageFloors'] -and $manifest.coverageFloors) {
    return Resolve-ManifestPath 'coverageFloors' $manifest.coverageFloors
  }
  foreach ($candidate in @('tests/coverage-floors.json', 'infra/test/coverage-floors.json')) {
    $full = Join-Path $RepoRoot $candidate
    if (Test-Path $full) { return (Resolve-Path $full).ProviderPath }
  }
  return $null
}

# The solution: $Manifest.solution, or the single .sln at the repo root when the manifest names none.
function Get-ExmodSolution {
  $manifest = Get-ExmodManifest
  if ($manifest.PSObject.Properties['solution'] -and $manifest.solution) {
    return Resolve-ManifestPath 'solution' $manifest.solution
  }
  $sln = @(Get-ChildItem $RepoRoot -Filter '*.sln' -File)
  if ($sln.Count -ne 1) { throw "exmod.json has no 'solution' and $RepoRoot does not hold exactly one .sln." }
  return $sln[0].FullName
}

#endregion

$Manifest = Get-ExmodManifest
$CurrentGameVersion = @($Manifest.series)[0]

# 'latest', 'all' or one series, as the list of series to act on.
function Resolve-GameVersions([string]$Spec) {
  switch ($Spec) {
    'latest' { return @($CurrentGameVersion) }
    'all' { return @($Manifest.series) }
    default {
      if (-not $GameTfms.Contains($Spec)) {
        throw "Unknown version '$Spec'. Use latest, all, or one of: $($GameTfms.Keys -join ', ')."
      }
      return @($Spec)
    }
  }
}

# The .NET runtime majors $Versions need that the system dotnet muxer does not have, as an array;
# empty when it has them all. A machine without a dotnet on PATH lacks every one.
function Get-MissingRuntimeMajors([string[]]$Versions) {
  $needed = @($Versions | ForEach-Object { $GameRuntimeMajors[$_] } | Select-Object -Unique)
  $sysRuntimes = try { (& dotnet --list-runtimes 2>$null) -join "`n" } catch { '' }
  return @($needed | Where-Object { $sysRuntimes -notmatch "Microsoft\.NETCore\.App $([regex]::Escape($_))\." })
}

# The dotnet muxer to drive for $Versions: the system one when it already has every runtime major
# they need, otherwise the one in Get-ExmodDotnetDir, provisioned first. The global muxer ignores
# DOTNET_ROOT, so runtimes provisioned into .dotnet are only visible through .dotnet/dotnet - which
# is what lets a machine with only .NET 10 installed still run the 1.21 and 1.20 lanes.
# -NoProvision returns that local muxer's path without provisioning; it may not exist.
function Resolve-DotnetHost([string[]]$Versions, [switch]$NoProvision) {
  $missing = @(Get-MissingRuntimeMajors $Versions)
  if ($missing.Count -eq 0) { return 'dotnet' }
  if ($NoProvision) { return (Join-Path (Get-ExmodDotnetDir) "dotnet$ExeSuffix") }
  Write-Host "Missing .NET runtime major(s) system-wide: $($missing -join ', ') - provisioning a local .dotnet..."
  Invoke-ProvisionDotnet @('-Version', ($Versions.Count -eq 1 ? $Versions[0] : 'all'))
  return (Join-Path (Get-ExmodDotnetDir) "dotnet$ExeSuffix")
}

# The install the series' override variable names (VINTAGE_STORY for 1.22, VINTAGE_STORY_121,
# VINTAGE_STORY_120), the one the builds take before any lookup; $null when it is unset or empty.
# The folder may not exist.
function Get-GameInstallOverride([string]$Slug) {
  $name = if ($Slug -eq '1.22') { 'VINTAGE_STORY' } else { "VINTAGE_STORY_$($Slug.Replace('.', ''))" }
  $value = [Environment]::GetEnvironmentVariable($name)
  if ($value) { return $value }
  return $null
}

# Every folder that may hold a $Kind install of series $Slug, in the order they are tried: the
# series' override variable (Get-GameInstallOverride) when set, this OS's client slot in the user
# store, then the nearest .game/<slug>-<kind>, .game/<slug>-<platform> and .game/<slug> holding the
# kind's entry assembly, each searched from $RepoRoot upward. A name found nowhere is left out; the
# client slot is always listed, whether or not it exists.
function Get-GameInstallCandidates([string]$Slug, [string]$Kind) {
  $entry = if ($Kind -eq 'server') { 'VintagestoryServer.dll' } else { 'Vintagestory.dll' }
  $out = @(Get-ClientSlot $Slug)
  $override = Get-GameInstallOverride $Slug
  if ($override) { $out = @($override) + $out }
  foreach ($name in @("$Slug-$Kind", "$Slug-$PlatformSlot", $Slug)) {
    $hit = Find-ExmodAbove ".game/$name/$entry"
    if ($hit) { $out += Split-Path $hit -Parent }
  }
  return $out
}

# A provisioned game install for $Version that can actually run here, preferring $Kind, from
# Get-GameInstallCandidates. A client package is a superset of a server one, so the client slot is
# searched even for a server. Provisions one when none answers, to provision game's default -Dest
# for $Kind.
function Resolve-GameInstall([string]$Version = $CurrentGameVersion, [string]$Kind = 'server') {
  if ($Kind -notin @('server', 'client')) { throw "Kind must be 'server' or 'client'." }
  $slug = ($Version -split '\.')[0..1] -join '.'
  $entry = if ($Kind -eq 'server') { 'VintagestoryServer.dll' } else { 'Vintagestory.dll' }

  # The entry assembly is in the archive for every platform; the native libraries beside it are not.
  # A package left over from another OS has the dll and none of them, and starts only far enough to
  # fail, so it does not count as an install here.
  $find = {
    foreach ($c in (Get-GameInstallCandidates $slug $Kind)) {
      if ((Test-Path (Join-Path $c $entry)) -and (Test-Path (Get-NativeMarker $c))) { return $c }
    }
    return $null
  }

  $hit = & $find
  if ($hit) { return $hit }

  Write-Host "No usable $Kind install for $Version - provisioning one..."
  $provisionArgs = @('-Version', $Version, '-Kind', $Kind)
  Invoke-ProvisionGame $provisionArgs

  $hit = & $find
  if (-not $hit) {
    throw "Provisioning completed but no usable $Kind install was found in $(Get-ClientSlot $slug) or a .game/$slug above $RepoRoot."
  }
  return $hit
}

# Every mod/sample the manifest names, as built output directories (the folder holding modinfo.json
# and the dll). A mod that has not been built yet is built first, so a fresh clone still works.
function Get-BuiltModDirs([string]$Configuration = 'Debug') {
  $out = @()
  foreach ($target in (Get-ExmodBuildTargets).GetEnumerator()) {
    $srcDir = Split-Path $target.Value -Parent
    $built = Join-Path $srcDir "bin/$Configuration/Mods/mod"
    if (-not (Test-Path (Join-Path $built 'modinfo.json'))) {
      Write-Host "Building $($target.Key) (not yet built) ..."
      dotnet build $target.Value -c $Configuration -clp:ErrorsOnly | Out-Host
      if ($LASTEXITCODE -ne 0) { throw "Build of $($target.Value) failed." }
    }
    $out += $built
  }
  return $out
}

# Resolves each caller-supplied path to one or more mod folders, so both "path/to/one/mod" and
# "path/to/several/mods" work wherever a -Mods list is accepted.
function Resolve-ModDirs([string[]]$Dirs) {
  $out = @()
  foreach ($d in $Dirs) {
    $full = if ([System.IO.Path]::IsPathRooted($d)) { $d } else { Join-Path $RepoRoot $d }
    if (-not (Test-Path $full)) { throw "Mod path not found: $full" }
    if (Test-Path (Join-Path $full 'modinfo.json')) {
      $out += $full
    }
    else {
      $subs = @(Get-ChildItem $full -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'modinfo.json') })
      if (-not $subs) { throw "'$full' is neither a mod folder (no modinfo.json) nor a folder of mod folders." }
      $out += @($subs.FullName)
    }
  }
  return $out
}

#region Dependency mods

# A version string with any prerelease suffix (everything from the first '-' on) stripped, cast to
# [version] so two versions compare numerically rather than lexically ("0.7.10" > "0.7.9").
function Get-VersionCore([string]$Version) {
  return [version](($Version -split '-', 2)[0])
}

# The workspace-sibling build output for a dependency id, or $null when no directory beside
# $RepoRoot names it. A sibling is a directory next to this checkout holding its own exmod.json
# whose `mods` names $Id; its manifest is read raw rather than through Get-ExmodManifest, which is
# fixed to this checkout's $RepoRoot.
function Get-ExmodDependencySiblingProject([string]$Id) {
  $parent = Split-Path $RepoRoot -Parent
  if (-not $parent -or -not (Test-Path $parent)) { return $null }
  foreach ($dir in Get-ChildItem $parent -Directory -ErrorAction SilentlyContinue) {
    if ($dir.FullName -eq $RepoRoot) { continue }
    $manifestPath = Join-Path $dir.FullName 'exmod.json'
    if (-not (Test-Path $manifestPath -ErrorAction SilentlyContinue)) { continue }
    $sib = $null
    try { $sib = Get-Content $manifestPath -Raw -ErrorAction Stop | ConvertFrom-Json } catch { continue }
    if (-not $sib.PSObject.Properties['mods']) { continue }
    $entry = $sib.mods.PSObject.Properties[$Id]
    if (-not $entry) { continue }
    $modPath = Join-Path $dir.FullName $entry.Value.path
    if (-not (Test-Path $modPath)) { continue }
    return Find-SingleCsproj (Get-ModProjectDir $modPath) "sibling mods.$Id"
  }
  return $null
}

# The ModDB release to use for $Id at floor $Floor: the release whose modversion equals the floor
# exactly, else the lowest modversion above it. A prerelease floor ModDB never carries (it only ever
# publishes stable releases) fails naming the two branches that do carry one.
function Resolve-ModDbRelease([string]$Id, [string]$Floor) {
  $record = Invoke-RestMethod -Uri "https://mods.vintagestory.at/api/mod/$Id" -TimeoutSec 15
  if (-not $record.mod.releases) { throw "ModDB has no releases for '$Id'." }
  $exact = $record.mod.releases | Where-Object { $_.modversion -eq $Floor } | Select-Object -First 1
  if ($exact) { return $exact }
  $floorCore = Get-VersionCore $Floor
  $above = @($record.mod.releases | Where-Object { (Get-VersionCore $_.modversion) -gt $floorCore }) |
    Sort-Object { Get-VersionCore $_.modversion }
  if ($above) { return $above[0] }
  if ($Floor -match '-') {
    throw "No ModDB release of '$Id' at or above prerelease floor $Floor - ModDB does not carry prereleases. Name 'url' or 'github' for it in exmod.json's depends.$Id."
  }
  throw "No ModDB release of '$Id' at or above $Floor."
}

# Extracts $Zip into $Dest (replaced if present), unwrapping one top-level folder when the archive
# wraps its content that way (GitHub's "download zip" habit) - the same shape ExlibVerify's
# ModSource.Load applies to a mod zip.
function Expand-DependencyZip([string]$Zip, [string]$Dest) {
  if (Test-Path $Dest) { Remove-Item -Recurse -Force $Dest }
  New-Item -ItemType Directory -Force -Path $Dest | Out-Null
  Expand-Archive -Path $Zip -DestinationPath $Dest -Force
  if (Test-Path (Join-Path $Dest 'modinfo.json')) { return }
  $inner = Get-ChildItem $Dest -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'modinfo.json') } |
    Select-Object -First 1
  if (-not $inner) { throw "$Zip has no modinfo.json at its root or one level down." }
  Get-ChildItem $inner.FullName -Force | Move-Item -Destination $Dest -Force
  Remove-Item -Recurse -Force $inner.FullName
}

# One dependency, resolved: a workspace sibling's build output (built first if missing), else a
# cached extraction reused when its version already matches the floor, else a fresh download - the
# manifest's depends.<Id>.url (with {version}/{id} substituted), else depends.<Id>.github
# (owner/repo, resolved against its v<version> release tag), else the ModDB API. Downloads land
# under $RepoRoot/.exmod/cache and extract under $RepoRoot/.exmod/mods/<Id>.
function Resolve-OneDependency([string]$Id, [string]$Floor, [string]$Configuration) {
  $siblingProject = Get-ExmodDependencySiblingProject $Id
  if ($siblingProject) {
    $srcDir = Split-Path $siblingProject -Parent
    $built = Join-Path $srcDir "bin/$Configuration/Mods/mod"
    # A sibling another platform builds (a WSL checkout seen from Windows) is that platform's to
    # build, but its built mod is managed code and assets and runs anywhere: it is staged as it
    # stands, and only a sibling with no build falls back to the release the floor names.
    $foreign = Test-ForeignBuildState $srcDir
    if (-not (Test-Path (Join-Path $built 'modinfo.json'))) {
      if ($foreign) {
        Write-Host "$Id : workspace sibling built on another platform and not staged - using the release instead"
        $siblingProject = $null
      }
      else {
        Write-Host "Building $Id (workspace sibling, not yet built) ..."
        dotnet build $siblingProject -c $Configuration -clp:ErrorsOnly | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Build of $siblingProject failed." }
      }
    }
    if ($siblingProject) {
      $stamp = (Get-Item (Join-Path $built 'modinfo.json')).LastWriteTime.ToString('yyyy-MM-dd HH:mm')
      $note = if ($foreign) { " (built on the other platform, $stamp; rebuild it there after a change)" } else { "" }
      Write-Host "$Id : workspace sibling, built output at $built$note"
      return [pscustomobject]@{ Id = $Id; Version = $Floor; Path = $built; Source = 'workspace sibling' }
    }
  }

  $cacheDir = Join-Path $RepoRoot ".exmod/mods/$Id"
  $cacheModinfo = Join-Path $cacheDir 'modinfo.json'
  if (Test-Path $cacheModinfo) {
    $cached = Get-Content $cacheModinfo -Raw | ConvertFrom-Json
    if ($cached.version -eq $Floor) {
      Write-Host "$Id : cached release $Floor at $cacheDir"
      return [pscustomobject]@{ Id = $Id; Version = $Floor; Path = $cacheDir; Source = 'cache' }
    }
  }

  $manifest = Get-ExmodManifest
  $dependsEntry = $null
  if ($manifest.PSObject.Properties['depends']) {
    $prop = $manifest.depends.PSObject.Properties[$Id]
    if ($prop) { $dependsEntry = $prop.Value }
  }

  $version = $Floor
  if ($dependsEntry -and $dependsEntry.PSObject.Properties['url'] -and $dependsEntry.url) {
    $url = $dependsEntry.url.Replace('{version}', $Floor).Replace('{id}', $Id)
    $sourceLabel = "download: $url"
  }
  elseif ($dependsEntry -and $dependsEntry.PSObject.Properties['github'] -and $dependsEntry.github) {
    $url = "https://github.com/$($dependsEntry.github)/releases/download/v$Floor/${Id}_$Floor.zip"
    $sourceLabel = "GitHub release: $($dependsEntry.github) v$Floor"
  }
  else {
    $release = Resolve-ModDbRelease $Id $Floor
    $version = $release.modversion
    $url = $release.mainfile
    $sourceLabel = "ModDB release $version"
  }

  $cacheZipDir = Join-Path $RepoRoot '.exmod/cache'
  New-Item -ItemType Directory -Force -Path $cacheZipDir | Out-Null
  $zip = Join-Path $cacheZipDir "${Id}_$version.zip"
  if (-not (Test-Path $zip)) {
    Write-Host "Downloading $Id $version from $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
  }
  Expand-DependencyZip $zip $cacheDir
  Write-Host "$Id : $sourceLabel, extracted to $cacheDir"
  return [pscustomobject]@{ Id = $Id; Version = $version; Path = $cacheDir; Source = $sourceLabel }
}

# Every runtime dependency this repo does not build itself: the `dependencies` of every mod and
# sample the manifest names, minus 'game' and every id this repo's own mods/samples build, each
# resolved at the highest floor version any of them names for it. Returns an ordered list of
# @{ Id; Version; Path; Source }, in the order the ids were first seen.
function Resolve-DependencyMods([string]$Configuration = 'Debug') {
  $builtIds = @(@((Get-ExmodMods).Keys) + @((Get-ExmodSamples).Keys))

  $floors = [ordered]@{}
  foreach ($m in (@((Get-ExmodMods).Values) + @((Get-ExmodSamples).Values))) {
    $modinfoPath = Join-Path (Split-Path $m.Project -Parent) 'modinfo.json'
    if (-not (Test-Path $modinfoPath)) { continue }
    $modinfo = Get-Content $modinfoPath -Raw | ConvertFrom-Json
    if (-not $modinfo.dependencies) { continue }
    foreach ($dep in $modinfo.dependencies.PSObject.Properties) {
      if ($dep.Name -eq 'game') { continue }
      if ($builtIds -contains $dep.Name) { continue }
      if (-not $floors.Contains($dep.Name) -or (Get-VersionCore $dep.Value) -gt (Get-VersionCore $floors[$dep.Name])) {
        $floors[$dep.Name] = $dep.Value
      }
    }
  }

  $out = @()
  foreach ($id in $floors.Keys) {
    $out += Resolve-OneDependency $id $floors[$id] $Configuration
  }
  return $out
}

#endregion

#region Command registry

# Every command registers itself here, next to its own implementation, so the help text and the
# dispatch table cannot disagree about what exists.
$Script:ExmodCommands = [ordered]@{}

# Group orders the summary and titles its sections.
$Script:ExmodGroups = [ordered]@{
  start   = 'first run'
  source  = 'source'
  run     = 'run'
  package = 'package'
  machine = 'machine'
}

# Registers one command. Summary is its line in the grouped list; Detail is what `exmod help <name>`
# prints; Action receives the arguments after the command name as a string array.
function Add-ExmodCommand {
  param(
    [Parameter(Mandatory)][string]$Group,
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$Summary,
    [Parameter(Mandatory)][string]$Detail,
    [Parameter(Mandatory)][scriptblock]$Action,
    [string[]]$Alias = @()
  )
  if (-not $Script:ExmodGroups.Contains($Group)) { throw "Unknown command group '$Group'." }
  $Script:ExmodCommands[$Name] = [pscustomobject]@{
    Group   = $Group
    Name    = $Name
    Summary = $Summary
    Detail  = $Detail.Trim()
    Action  = $Action
    Alias   = $Alias
  }
}

function Resolve-ExmodCommand([string]$Name) {
  if (-not $Name) { return $null }
  if ($Script:ExmodCommands.Contains($Name)) { return $Script:ExmodCommands[$Name] }
  foreach ($c in $Script:ExmodCommands.Values) {
    if ($c.Alias -contains $Name) { return $c }
  }
  return $null
}

function Show-ExmodHelp([string]$Name) {
  if ($Name) {
    $cmd = Resolve-ExmodCommand $Name
    if (-not $cmd) {
      Write-Host "exmod: no such command: $Name" -ForegroundColor Red
      Show-ExmodHelp
      exit 1
    }
    Write-Host ''
    Write-Host $cmd.Detail
    Write-Host ''
    return
  }

  Write-Host ''
  Write-Host 'exmod - every task in this repo, from a fresh clone to a tagged release.'
  Write-Host ''
  Write-Host '  exmod <command> [arguments]        exmod help <command> for one in detail'
  $width = ($Script:ExmodCommands.Values | ForEach-Object { $_.Name.Length } | Measure-Object -Maximum).Maximum
  foreach ($group in $Script:ExmodGroups.Keys) {
    $members = @($Script:ExmodCommands.Values | Where-Object { $_.Group -eq $group })
    if (-not $members) { continue }
    Write-Host ''
    Write-Host $Script:ExmodGroups[$group] -ForegroundColor Cyan
    foreach ($c in $members) {
      Write-Host ('  {0}  {1}' -f $c.Name.PadRight($width), $c.Summary)
    }
  }
  Write-Host ''
}

#endregion

#region WSL

# The distro and Linux path behind a Windows path into a WSL share, \\wsl.localhost\<distro>\... or
# \\wsl$\<distro>\... in either slash direction and any case, as @{ Distro; LinuxPath }. The share's
# root, with or without a trailing slash, is LinuxPath '/'. $null for any other path.
function Get-WslShare([string]$Path) {
  if ($Path -notmatch '^[\\/]{2}wsl(\.localhost|\$)[\\/]([^\\/]+)([\\/].*)?$') { return $null }
  $linux = "$($Matches[3])".Replace('\', '/').TrimEnd('/')
  return [pscustomobject]@{ Distro = $Matches[2]; LinuxPath = ($linux ? $linux : '/') }
}

# Whether $Command with $Argv runs on Windows for a checkout on a WSL share instead of being handed
# to the distro: client, logs, help, every machine-group command, and provision game -Kind client,
# which installs into the Windows user store. Aliases resolve to their command first.
function Test-ExmodWindowsSideCommand([string]$Command, [string[]]$Argv = @()) {
  $cmd = Resolve-ExmodCommand $Command
  $name = if ($cmd) { $cmd.Name } else { $Command }
  if ($name -in @('client', 'logs', 'help')) { return $true }
  if ($cmd -and $cmd.Group -eq 'machine') { return $true }
  if ($name -eq 'provision') {
    return (@($Argv)[0] -eq 'game') -and ((Get-Opt $Argv '-Kind' 'server') -eq 'client')
  }
  return $false
}

# Runs `bash scripts/exmod.sh <Argv>` inside WSL distro $Distro with $LinuxPath as the working
# directory, its output passed to the host, and returns its exit code. The arguments reach bash
# through wsl.exe --exec, so no shell expands or splits them. Windows only; the checkout's own
# launcher picks the tools and pwsh on the Linux side.
function Invoke-ExmodInWsl([string]$Distro, [string]$LinuxPath, [string[]]$Argv) {
  & wsl.exe -d $Distro --cd $LinuxPath --exec bash scripts/exmod.sh @Argv | Out-Host
  return $LASTEXITCODE
}

# Whether this is a WSL distro that can start Windows programs: Linux with the WSLInterop or
# WSLInterop-late binfmt handler registered. $false on Windows and macOS.
function Test-WslInterop {
  if ($OnWindows -or $IsMacOS) { return $false }
  return (Test-Path '/proc/sys/fs/binfmt_misc/WSLInterop') -or (Test-Path '/proc/sys/fs/binfmt_misc/WSLInterop-late')
}

# $Path converted by wslpath: to its Windows form with -ToWindows (a distro path becomes
# \\wsl.localhost\<distro>\...), else a Windows path to the form Linux reaches it by (/mnt/c/...).
# The path need not exist. Throws when wslpath fails. WSL only.
function Convert-WslPath([string]$Path, [switch]$ToWindows) {
  $out = & wslpath ($ToWindows ? '-w' : '-u') $Path
  if ($LASTEXITCODE -ne 0 -or -not $out) { throw "wslpath could not convert '$Path'." }
  return "$out".Trim()
}

# The first line cmd.exe prints for $Line on Windows, CR trimmed, or $null when it prints nothing,
# fails, or cmd.exe cannot be started. cmd.exe's warning about a Linux working directory goes to
# stderr and is dropped. WSL only.
function Invoke-WindowsCmd([string]$Line) {
  $out = @(try { & cmd.exe /c $Line 2>$null } catch { })
  if ($LASTEXITCODE -ne 0 -or -not $out) { return $null }
  $first = "$($out[0])".TrimEnd("`r").Trim()
  return ($first ? $first : $null)
}

# Windows' user store, %LOCALAPPDATA%\exmod as a Windows path, read through cmd.exe; $null when
# %LOCALAPPDATA% cannot be read. WSL only.
function Get-WindowsUserStore {
  $local = Invoke-WindowsCmd 'echo %LOCALAPPDATA%'
  if (-not $local -or $local -eq '%LOCALAPPDATA%') { return $null }
  return "$local\exmod"
}

# The Windows path of the first $Name on Windows' PATH (`where <name>`), or $null when there is
# none. WSL only.
function Get-WindowsProgram([string]$Name) {
  return Invoke-WindowsCmd "where $Name"
}

#endregion

#region GPU preference

# Windows renders a program on the GPU that drives the display unless this key holds a value named
# by the program's full path; 'GpuPreference=2;' asks for the high-performance GPU.
$GpuPreferencesKey = 'HKCU\Software\Microsoft\DirectX\UserGpuPreferences'
$HighPerformanceGpu = 'GpuPreference=2;'

# Gives the program at $ExePath, a full Windows path such as a client slot's Vintagestory.exe, the
# high-performance GPU under $GpuPreferencesKey. A value for that path exists when `reg query`
# exits 0 (WSL) or GetValue returns non-null (Windows). No value: writes $HighPerformanceGpu. A
# string value (REG_SZ, REG_EXPAND_SZ) with a `GpuPreference=` entry is the user's choice and is
# kept; one without gets $HighPerformanceGpu appended, its other entries kept and a `;` put before
# it when the value lacks a trailing one, and is written back as REG_SZ. A value of another type is
# kept with one warning. Reads and writes through the HKCU: drive on Windows and through reg.exe
# from WSL with interop. Prints one line when it writes; a registry that cannot be read or written
# prints a warning and returns, since the game still runs without a preference. -DryRun writes
# nothing and prints `gpu: <value>` when the value holds a preference, `gpu: <value>, would append
# GpuPreference=2;` when it does not, and `gpu: none, would register GpuPreference=2;` when there
# is none.
function Register-ClientGpuPreference([string]$ExePath, [switch]$DryRun) {
  $drivePath = "HKCU:\$($GpuPreferencesKey.Substring(5))"
  $exists = $false
  $current = $null
  try {
    if ($OnWindows) {
      $key = Get-Item -LiteralPath $drivePath -ErrorAction SilentlyContinue
      if ($key) { $current = $key.GetValue($ExePath) }
      $exists = $null -ne $current
    }
    else {
      $out = @(& reg.exe query $GpuPreferencesKey /v $ExePath 2>$null)
      $exists = $LASTEXITCODE -eq 0
      if ($exists) {
        $line = $out | Where-Object { "$_" -match '\s{4}REG_[A-Z_]+' } | Select-Object -First 1
        if ("$line" -match '\s{4}(REG_(?:EXPAND_)?SZ)(?:\s{4}(.*))?$') { $current = "$($Matches[2])" }
      }
    }
  }
  catch {
    Write-Warning "Could not read the GPU preference for $ExePath : $($_.Exception.Message)"
    return
  }

  if ($exists -and $current -isnot [string]) {
    Write-Warning "The GPU preference value for $ExePath is not a string; left as it is."
    return
  }
  if ($exists -and $current -match '(^|;)\s*GpuPreference=') {
    if ($DryRun) { Write-Host "gpu: $current" }
    return
  }
  if ($DryRun) {
    Write-Host ($exists ? "gpu: $current, would append $HighPerformanceGpu" : "gpu: none, would register $HighPerformanceGpu")
    return
  }
  $value = if ($exists -and $current -and -not $current.TrimEnd().EndsWith(';')) { "$current;$HighPerformanceGpu" } else { "$current$HighPerformanceGpu" }
  try {
    if ($OnWindows) {
      if (-not (Test-Path -LiteralPath $drivePath)) { New-Item -Path $drivePath -Force | Out-Null }
      New-ItemProperty -LiteralPath $drivePath -Name $ExePath -Value $value -PropertyType String -Force | Out-Null
    }
    else {
      & reg.exe add $GpuPreferencesKey /v $ExePath /t REG_SZ /d $value /f *> $null
      if ($LASTEXITCODE -ne 0) { throw "reg.exe add exited with $LASTEXITCODE." }
    }
  }
  catch {
    Write-Warning "Could not register the high-performance GPU for $ExePath : $($_.Exception.Message)"
    return
  }
  Write-Host "Registered $HighPerformanceGpu (the high-performance GPU) for $ExePath"
}

#endregion

# The commands themselves, one file per stage. Dot-sourced, so everything above is in scope for them
# and their Add-ExmodCommand calls run before dispatch. A file that is not there is skipped rather
# than fatal: another repo copies this dispatcher with only the stages it wants (see
# templates/ci/tests.yml), and here a missing one shows up as a missing command in `exmod`.
foreach ($module in @('provision', 'new', 'scaffold', 'src', 'run', 'shapes', 'dist', 'windows')) {
  $path = Join-Path $PSScriptRoot "exmod/$module.ps1"
  if (Test-Path $path) { . $path }
}

if ($Command -in @('', $null, 'help', '-h', '--help', 'commands')) {
  Show-ExmodHelp ($Arguments | Select-Object -First 1)
  return
}

$resolved = Resolve-ExmodCommand $Command
if (-not $resolved) {
  # A mistyped command is a usage error, not a crash: the list is more use here than a stack trace.
  Write-Host "exmod: no such command: $Command" -ForegroundColor Red
  Show-ExmodHelp
  exit 1
}
$wslShare = if ($OnWindows) { Get-WslShare $RepoRoot } else { $null }
if ($wslShare -and -not (Test-ExmodWindowsSideCommand $resolved.Name $Arguments)) {
  Write-Host "Running '$Command' inside WSL ($($wslShare.Distro)): $($wslShare.LinuxPath)"
  exit (Invoke-ExmodInWsl $wslShare.Distro $wslShare.LinuxPath (@($Command) + $Arguments))
}
& $resolved.Action $Arguments
