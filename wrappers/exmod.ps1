# exmod launcher for a repository that consumes extools (PowerShell 7, any platform). Finds the
# tools checkout (EXTOOLS_HOME, the workspace sibling ../extools, or a clone of the tag pinned in
# exmod.json under .extools/) and forwards every argument to the dispatcher with this repository
# as the root. exmod.sh is the same launcher for POSIX shells; both are launchers only.
[CmdletBinding(PositionalBinding = $false)]
param(
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]]$Arguments
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Get-ToolsPin {
  $manifest = Join-Path $repoRoot 'exmod.json'
  if (-not (Test-Path $manifest)) { return $null }
  $m = [regex]::Match((Get-Content $manifest -Raw), '"tools"\s*:\s*"([^"]+)"')
  if ($m.Success) { return $m.Groups[1].Value }
  return $null
}

# Fetches $Tag into the checkout at $Dest, shallow, and leaves it checked out detached.
function Sync-PinnedTag {
  param([string]$Dest, [string]$Tag)
  & git -C $Dest fetch --quiet --depth 1 origin "refs/tags/${Tag}:refs/tags/${Tag}"
  if ($LASTEXITCODE -ne 0) { throw "git fetch of $Tag failed" }
  & git -c advice.detachedHead=false -C $Dest checkout --quiet $Tag
  if ($LASTEXITCODE -ne 0) { throw "git checkout of $Tag failed" }
}

function Resolve-Extools {
  if ($env:EXTOOLS_HOME -and (Test-Path (Join-Path $env:EXTOOLS_HOME 'exmod.ps1'))) {
    return (Resolve-Path $env:EXTOOLS_HOME).ProviderPath
  }
  $sibling = Join-Path $repoRoot '../extools'
  if (Test-Path (Join-Path $sibling 'exmod.ps1')) { return (Resolve-Path $sibling).ProviderPath }

  $version = Get-ToolsPin
  if (-not $version) {
    throw 'no extools checkout found (EXTOOLS_HOME, ../extools) and exmod.json pins no "tools" version'
  }
  $dest = Join-Path $repoRoot '.extools'
  $url = if ($env:EXTOOLS_URL) { $env:EXTOOLS_URL } else { 'https://github.com/ringavirda/modding-vsextools.git' }
  $tag = "v$version"
  if (Test-Path (Join-Path $dest 'exmod.ps1')) {
    $have = (& git -C $dest describe --tags --exact-match 2>$null)
    if ($have -ne $tag) {
      Write-Host "exmod: moving .extools from $(if ($have) { $have } else { 'an untagged commit' }) to $tag"
      Sync-PinnedTag $dest $tag
    }
  } else {
    Write-Host "exmod: cloning extools $tag into .extools/"
    # The clone takes the default branch and leaves the tree empty: --branch naming an annotated
    # tag makes git warn that the ref is not a commit, and the tag is checked out next anyway.
    & git clone --quiet --depth 1 --no-checkout $url $dest
    if ($LASTEXITCODE -ne 0) { throw "git clone of $url failed" }
    Sync-PinnedTag $dest $tag
  }
  return $dest
}

$tools = Resolve-Extools
& (Join-Path $tools 'exmod.ps1') -RepoRoot $repoRoot @Arguments
exit $LASTEXITCODE
