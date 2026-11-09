# The source tree: everything that reads, rewrites or compiles it without running the game.
#
#   exmod build     compile
#   exmod test      the suites, one lane per game version
#   exmod format    CSharpier, then dotnet format
#   exmod verify    the shipped assets, checked the way the game loads them
#   exmod codes     regenerate a mod's block-code table
#   exmod check     all of the above in one pass - the gate
#   exmod clean     delete build output

#region build

# Get-ExmodTestProjects and Get-ExmodBuildTargets live in exmod.ps1: they read the manifest, so
# every stage that needs "every test project" or "every mod/sample" shares the one resolver.

# The directories holding the manifest's mods, samples and standalone test projects - what format,
# clean and check treat as this repo's own source, as opposed to infra/ and templates/, which stay
# literal: they are tool conventions, true of every repo this CLI runs in, not something exmod.json
# names. A mod or sample's own parent (mods/, samples/) is walked whole rather than just its own
# path, so a test project sitting beside it (mods/<mod>/tests, samples/<Sample>.Tests) is covered
# too. $Manifest.tests entries (e.g. tests/ExpandedLib.Tests) contribute their own parent (tests/)
# the same way, so a repository whose tests live outside any mod folder is still covered.
function Get-ExmodSourceRoots {
  $manifest = Get-ExmodManifest
  $paths = @((Get-ExmodMods).Values.Path) + @((Get-ExmodSamples).Values.Path) + @($manifest.tests | ForEach-Object { Join-Path $RepoRoot $_ })
  return @($paths | Where-Object { $_ } | ForEach-Object { Split-Path $_ -Parent } | Select-Object -Unique)
}

# Compiles the mod projects (and, with -Tests, the test projects) for one or more game series.
# Mods build serially, across mods and across series: the projects share intermediate
# assemblies (exlib -> iiex -> siex), so building two of them in separate calls at once races on the
# same obj/ files and fails with CS2012. A series' test projects build in one MSBuild call instead. Warnings are never suppressed -
# a warning count is the point of this command - so nothing here passes -clp:ErrorsOnly or -v q.
# A project last built on another platform starts from clean. MSBuild's incremental clean reads
# the previous build's file list, whose paths from the other platform resolve here to the files
# this build just copied (modinfo.json, modicon.png), and deletes them; bin and obj go instead.
function Reset-ForeignBuildState([string]$ProjectDir) {
  if (-not (Test-ForeignBuildState $ProjectDir)) { return }
  Write-Host "$(Split-Path (Split-Path $ProjectDir -Parent) -Leaf): last built on another platform - starting from clean"
  Remove-Item -Recurse -Force (Join-Path $ProjectDir 'bin'), (Join-Path $ProjectDir 'obj') -ErrorAction SilentlyContinue
}

# Builds one series' test projects in a single MSBuild call, restored one after another (parallel restores
# of a shared project write its project.assets.json at once) and built across cores, so a project the
# test projects share (the mod, exlib) builds once instead of once per test project. $Items carry
# Proj, Tfm and Legacy, all of one series. Returns the Proj of every item whose project failed: when the
# call fails, each project is built again on its own (an up-to-date one costs seconds) and the ones
# that still fail are named. $QuietArgs is passed to the dotnet build lines as given; $Discard drops
# their output.
function Invoke-ExmodParallelBuild([string]$Dotnet, [object[]]$Items, [string]$Configuration, [string[]]$QuietArgs, [bool]$Discard = $false) {
  $first = $Items[0]
  # Restore leaves TargetFramework out, as `dotnet build -f` does: restored under the test projects' framework, a
  # project they reference on another one (the generators, netstandard2.0) has no assets for its own.
  $restoreProps = "Configuration=$Configuration"
  if ($first.Legacy) { $restoreProps += ';Legacy=true' }
  $props = "$restoreProps;TargetFramework=$($first.Tfm)"
  $list = ($Items | ForEach-Object { [Security.SecurityElement]::Escape([IO.Path]::GetFullPath($_.Proj, $PWD.ProviderPath)) }) -join ';'
  $dir = Join-Path $RepoRoot '.exmod'
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $traversal = Join-Path $dir "parallel-build-$($first.Version).proj"
  Set-Content -LiteralPath $traversal -Value @"
<Project DefaultTargets="Build">
  <ItemGroup><TestProject Include="$list" /></ItemGroup>
  <Target Name="Restore">
    <MSBuild Projects="@(TestProject)" Targets="Restore" Properties="$restoreProps" BuildInParallel="false" />
  </Target>
  <Target Name="Build">
    <MSBuild Projects="@(TestProject)" Targets="Build" Properties="$props" BuildInParallel="true" StopOnFirstFailure="false" />
  </Target>
</Project>
"@
  if ($Discard) { & $Dotnet build $traversal -m @QuietArgs | Out-Null } else { & $Dotnet build $traversal -m @QuietArgs | Out-Host }
  if ($LASTEXITCODE -eq 0) { return @() }
  Write-Host "parallel build failed: building each test project alone ($($first.Version))"
  $failed = foreach ($item in $Items) {
    $buildArgs = @('build', $item.Proj, '-f', $item.Tfm, '-c', $Configuration) + $QuietArgs
    if ($item.Legacy) { $buildArgs += '-p:Legacy=true' }
    if ($Discard) { & $Dotnet @buildArgs | Out-Null } else { & $Dotnet @buildArgs | Out-Host }
    if ($LASTEXITCODE -ne 0) { $item.Proj }
  }
  return @($failed)
}

function Invoke-Build([string[]]$Argv) {
  $positional = @(Get-Positional $Argv @('-Mod', '-Configuration') @('-Tests'))
  $version = if ($positional.Count -gt 0) { $positional[0] } else { 'latest' }
  $modFilter = Get-Opt $Argv '-Mod' $null
  $configuration = Get-Opt $Argv '-Configuration' 'Debug'
  $withTests = Get-Flag $Argv '-Tests'

  $wanted = Resolve-GameVersions $version
  $dotnet = Resolve-DotnetHost $wanted
  Write-Host "Using dotnet host: $dotnet"

  $targets = Get-ExmodBuildTargets
  $testTargets = Get-ExmodTestProjects
  $sampleIds = @((Get-ExmodSamples).Keys)
  if ($modFilter) {
    $key = $modFilter.ToLowerInvariant()
    if (-not $targets.Contains($key)) { throw "Unknown mod '$modFilter'. Known: $($targets.Keys -join ', ')." }
    $targets = [ordered]@{ $key = $targets[$key] }
    $testTargets = if ($testTargets.Contains($key)) { [ordered]@{ $key = $testTargets[$key] } } else { [ordered]@{} }
  }

  foreach ($proj in @($targets.Values) + @($testTargets.Values | ForEach-Object { $_.Proj })) {
    Reset-ForeignBuildState (Split-Path $proj -Parent)
  }
  foreach ($v in $wanted) {
    $tfm = $GameTfms[$v]
    Write-Step "Building $v ($tfm, $configuration)"
    foreach ($mod in $targets.Keys) {
      # A sample targets $(CurrentGameTfm) only (see samples/*/*.csproj) - the legacy series buys
      # it nothing, so it is skipped the same way `exmod test` skips it.
      if ($mod -in $sampleIds -and $v -ne $CurrentGameVersion) {
        Write-Host "-- $mod ($v) skipped, targets $($GameTfms[$CurrentGameVersion]) only --"
        continue
      }
      $buildArgs = @('build', $targets[$mod], '-f', $tfm, '-c', $configuration)
      if ($tfm -ne 'net10.0') { $buildArgs += '-p:Legacy=true' }
      Write-Host "-- $mod ($v/$tfm) --"
      & $dotnet @buildArgs
      if ($LASTEXITCODE -ne 0) { throw "Build failed: $mod ($v/$tfm)." }
    }

    if ($withTests) {
      $seriesItems = foreach ($mod in $testTargets.Keys) {
        if ($v -notin $testTargets[$mod].Series) {
          Write-Host "-- $($testTargets[$mod].Project) ($v) skipped, targets $($GameTfms[$CurrentGameVersion]) only --"
          continue
        }
        [pscustomobject]@{ Version = $v; Tfm = $tfm; Legacy = ($tfm -ne 'net10.0'); Proj = $testTargets[$mod].Proj; Project = $testTargets[$mod].Project }
      }
      if ($seriesItems) {
        Write-Host "-- test projects ($v/$tfm) --"
        $failed = @(Invoke-ExmodParallelBuild $dotnet @($seriesItems) $configuration @())
        if ($failed.Count -gt 0) {
          $names = ($seriesItems | Where-Object { $_.Proj -in $failed } | ForEach-Object { $_.Project }) -join ', '
          throw "Build failed: $names ($v/$tfm)."
        }
      }
    }
  }

  Write-Host "`nBuild succeeded."
}


Add-ExmodCommand -Group source -Name build -Summary 'compile the mods for one or more game series' -Action {
  param([string[]]$Argv) Invoke-Build $Argv
} -Detail @'
exmod build [latest|all|1.22|1.21|1.20] [-Mod <name>] [-Configuration Debug|Release] [-Tests]

Compiles the mod projects, in their dependency order (exlib -> iiex -> siex, then the
HelloExpanded sample), for the requested series. Mods build one after another, on purpose: the
projects share intermediate assemblies, and separate builds at once race on the same obj/ files
(CS2012). Warnings are never suppressed - a warning count is exactly what this command is for.

  latest          1.22 only (the default)
  all             every supported series
  -Mod <name>     build just one mod or sample (see exmod.json's mods and samples)
  -Configuration  Debug (the default) or Release
  -Tests          also build the test projects `exmod test` would build, for the same series(es), each
                  series in one parallel MSBuild call
'@

#endregion

#region test

# The census file: the last green test count per series and test assembly on this branch, a flat
# JSON object keyed "<series>/<assembly>" in .exmod/census/<branch>.json. exmods-legacy's run-tests
# scripts write the same format.
function Get-ExmodCensusPath {
  $branch = & git -C $RepoRoot rev-parse --abbrev-ref HEAD 2>$null
  if (-not $branch) { $branch = 'detached' }
  return Join-Path $RepoRoot ".exmod/census/$($branch -replace '/', '-').json"
}

# Compares each result carrying a Total against the census, prints one census line per result, and
# returns the names whose count fell below the recorded one; with $AcceptDrop a fall is printed as
# accepted and not returned. Reads the file only.
function Compare-ExmodCensus($Results, [string]$Census, [bool]$AcceptDrop) {
  $recorded = Read-ExmodCensus $Census
  $drops = @()
  foreach ($r in $Results | Where-Object { $null -ne $_.Total } | Sort-Object Name) {
    $was = $recorded[$r.Name]
    $note = if ($null -eq $was) { 'new' }
    elseif ($r.Total -ge $was) { "was $was" }
    elseif ($AcceptDrop) { "was $was, drop accepted" }
    else { $drops += "$($r.Name) ($($r.Total), was $was)"; "was $was, a drop" }
    if ($null -ne $was -and $r.Total -lt $was -and $r.Log) { $note += "; log: $($r.Log)" }
    Write-Host ("census  {0,-40} {1} ({2})" -f $r.Name, $r.Total, $note)
  }
  return $drops
}

function Read-ExmodCensus([string]$Census) {
  $recorded = [ordered]@{}
  if (Test-Path $Census) {
    (Get-Content $Census -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $recorded[$_.Name] = [int]$_.Value }
  }
  return $recorded
}

# Records each result's Total over the census, keeping the entries this run did not count (other
# series, other assemblies).
function Write-ExmodCensus($Results, [string]$Census) {
  $recorded = Read-ExmodCensus $Census
  foreach ($r in $Results) { $recorded[$r.Name] = $r.Total }
  $sorted = [ordered]@{}
  foreach ($k in $recorded.Keys | Sort-Object) { $sorted[$k] = $recorded[$k] }
  New-Item -ItemType Directory -Force -Path (Split-Path $Census) | Out-Null
  $sorted | ConvertTo-Json | Set-Content -Path $Census
}

# Throws naming the failed runs and the census drops, when there are any. $Drops is what
# Compare-ExmodCensus returned.
function Assert-ExmodTestRun($Results, $Drops, [string]$Census) {
  $failed = @($Results | Where-Object { -not $_.Ok })
  if (-not $failed -and -not $Drops) { return }
  $reasons = @()
  if ($failed) { $reasons += "$($failed.Count) test run(s) failed: $($failed.Name -join ', ')" }
  if ($Drops) { $reasons += "$(@($Drops).Count) test count(s) fell below ${Census}: $($Drops -join ', '); -AcceptDrop records them" }
  throw ($reasons -join '; ')
}

# Runs the suite per game version, each version's projects in parallel. The mods stay single-target;
# legacy versions are tested by building the test projects with -p:Legacy=true against that version's
# TFM. Each build auto-provisions its game version on demand (Directory.Build.props), so a clean
# checkout just works.
#
# Every unfiltered run, -Coverage included, checks each assembly's test count against the census and
# fails on a count below the recorded one; only an unfiltered run with every assembly green writes
# the census. A -Filter run neither reads nor writes it: its counts are a subset.
# The failures a dotnet test console log names, as { Name, Message }, pulled from the console
# logger's own failure block: "  Failed <display name> [duration]", where the name is one token
# or a theory row's token plus its argument list (Bar(x: 1, name: "a b")), followed a line or two
# later by "  Error Message:" and the message itself on the next line. A test's own output line
# such as "Failed to load asset [game:x]" is not a failure. Empty when nothing failed.
function Get-ExmodTestFailures([string[]]$Lines) {
  $failed = '^\s*Failed\s+(\S+?(\(.*\))?)\s+\[[^\[\]]*\]\s*$'
  $failures = @()
  for ($i = 0; $i -lt $Lines.Count; $i++) {
    if ($Lines[$i] -match $failed) {
      $name = $Matches[1]
      $message = ''
      for ($j = $i + 1; $j -lt $Lines.Count; $j++) {
        if ($Lines[$j] -match $failed) { break }
        if ($Lines[$j] -match '^\s*Error Message:\s*$') {
          if ($j + 1 -lt $Lines.Count) { $message = $Lines[$j + 1].Trim() }
          break
        }
      }
      $failures += [pscustomobject]@{ Name = $name; Message = $message }
    }
  }
  return $failures
}

# The wave a lane of <Version> runs in: 0, or 1 for an older series while EXLIB_WRITE_GOLDENS is set.
# A golden write on an older series compares against the shared goldens the current series' write
# rewrites, so it starts only once every current-series lane has finished.
function Get-ExmodTestWave([string]$Version) {
  if ($env:EXLIB_WRITE_GOLDENS -and $Version -ne $CurrentGameVersion) { 1 } else { 0 }
}

# Runs $Body, the parallel run of a test phase, between `test phase:` clock lines. With the build
# lock in use ($env:EXMOD_BUILD_LOCK set) it holds this checkout's .exmod/test-phase.lock for the
# run, so two test phases in one checkout take turns: they share .exmod/test-results, the test logs
# and the census. Test phases of different checkouts overlap.
function Invoke-WithTestPhase([scriptblock]$Body) {
  $phase = {
    Write-Host ("test phase: {0:HH:mm:ss} start" -f (Get-Date))
    try { & $Body } finally { Write-Host ("test phase: {0:HH:mm:ss} end" -f (Get-Date)) }
  }
  if (-not $env:EXMOD_BUILD_LOCK) { & $phase; return }
  $wait = if ($env:EXMOD_BUILD_LOCK_WAIT) { [int]$env:EXMOD_BUILD_LOCK_WAIT } else { 3000 }
  Invoke-WithFlock (Join-Path $RepoRoot '.exmod/test-phase.lock') $wait -Label 'test phase lock' -Run $phase
}

# The partitions the manifest declares for the test assembly $Project, or $null. The manifest's
# `partitions` is keyed by assembly name; an entry is { parts: [{ name, filter, heapGb? }, ..., { name }] }.
# Every part but the last carries a `dotnet test` filter made only of `|`-joined terms such as
# `FullyQualifiedName~.Walk.`; the last carries none and runs the complement of the others, so a class
# added later always lands in a partition. Returns { Parts = @({ Name; Filter; HeapGb }) } with the last
# part's Filter built by Get-ExmodComplementFilter and HeapGb the part's own `heapGb` in GiB or $null; throws on a
# malformed entry, a `heapGb` that is not a whole number above 0 included.
function Get-ExmodPartitions([string]$Project) {
  $m = Get-ExmodManifest
  if (-not $m.PSObject.Properties['partitions'] -or -not $m.partitions.PSObject.Properties[$Project]) { return $null }
  $entry = $m.partitions.$Project
  $parts = @($entry.parts)
  if ($parts.Count -lt 2) { throw "exmod.json: partitions.$Project needs at least two parts." }
  $declared = @()
  for ($i = 0; $i -lt $parts.Count; $i++) {
    $p = $parts[$i]
    if (-not $p.name) { throw "exmod.json: partitions.$Project.parts[$i] has no name." }
    $last = ($i -eq $parts.Count - 1)
    $hasFilter = [bool]($p.PSObject.Properties['filter'] -and $p.filter)
    if ($last -and $hasFilter) { throw "exmod.json: the last part of partitions.$Project is the complement and takes no filter." }
    if (-not $last -and -not $hasFilter) { throw "exmod.json: partitions.$Project.parts[$i] ($($p.name)) has no filter." }
    if (-not $last) { $declared += $p.filter }
  }
  $heap = for ($i = 0; $i -lt $parts.Count; $i++) {
    $p = $parts[$i]
    if ($p.PSObject.Properties['heapGb']) {
      if (-not (Test-ExmodWholeGb $p.heapGb)) { throw "exmod.json: partitions.$Project.parts[$i].heapGb must be a whole number of GiB above 0" }
      [int]$p.heapGb
    }
    else { $null }
  }
  $heap = @($heap)
  $out = @()
  for ($i = 0; $i -lt $parts.Count - 1; $i++) { $out += [pscustomobject]@{ Name = $parts[$i].name; Filter = $parts[$i].filter; HeapGb = $heap[$i] } }
  $out += [pscustomobject]@{ Name = $parts[-1].name; Filter = Get-ExmodComplementFilter $declared; HeapGb = $heap[-1] }
  return [pscustomobject]@{ Parts = $out }
}

# True when $Value is a JSON integer of at least 1.
function Test-ExmodWholeGb($Value) {
  return ($Value -is [int] -or $Value -is [long]) -and $Value -ge 1
}

# The manifest's `testMemory` entry for the test assembly $Project: { HeapGb; FilterHeapGb; FilterStartGb } in GiB,
# FilterHeapGb and FilterStartGb $null when the entry has none, or $null when the manifest has no entry for the assembly.
# `heapGb` is the GC heap limit the assembly's test host runs under unfiltered; `filterHeapGb` the lower one it runs under
# with -Filter; `filterStartGb` the still lower one a -Filter run tries first, rerun under filterHeapGb on an
# OutOfMemoryException. Throws `exmod.json: testMemory.<assembly>.heapGb must be a whole number of GiB above 0` (the same
# for filterHeapGb and filterStartGb) when a value is missing, fractional, below 1 or not a number.
function Get-ExmodTestMemory([string]$Project) {
  $m = Get-ExmodManifest
  if (-not $m.PSObject.Properties['testMemory'] -or -not $m.testMemory.PSObject.Properties[$Project]) { return $null }
  $entry = $m.testMemory.$Project
  $heap = if ($entry.PSObject.Properties['heapGb']) { $entry.heapGb } else { $null }
  if (-not (Test-ExmodWholeGb $heap)) { throw "exmod.json: testMemory.$Project.heapGb must be a whole number of GiB above 0" }
  $filterHeap = $null
  if ($entry.PSObject.Properties['filterHeapGb']) {
    if (-not (Test-ExmodWholeGb $entry.filterHeapGb)) { throw "exmod.json: testMemory.$Project.filterHeapGb must be a whole number of GiB above 0" }
    $filterHeap = [int]$entry.filterHeapGb
  }
  $filterStart = $null
  if ($entry.PSObject.Properties['filterStartGb']) {
    if (-not (Test-ExmodWholeGb $entry.filterStartGb)) { throw "exmod.json: testMemory.$Project.filterStartGb must be a whole number of GiB above 0" }
    $filterStart = [int]$entry.filterStartGb
  }
  return [pscustomobject]@{ HeapGb = [int]$heap; FilterHeapGb = $filterHeap; FilterStartGb = $filterStart }
}

# GiB added to a test host's GC heap limit to make its share of the memory budget: the host's memory outside the
# heap and its `dotnet test`, vstest.console and blame collector processes.
$ExmodHostOverheadGb = 1

# GiB a test unit with no `testMemory` figure takes, run under no limit.
$ExmodUnlimitedTestShareGb = 4

# The most GiB of heap a `-Filter` run can need under EXMOD_HEAVY=1: the largest `heapGb` among the partition parts of
# $Project whose classes $Filter can reach, a part with no `heapGb` counting as the assembly's. $Filter is `|`-joined
# `FullyQualifiedName~<value>` or `FullyQualifiedName=<value>` terms. A term reaches only the first declared part that
# one of its own terms is contained in (`~` part term: the value contains the part's value; `=` part term: the values are
# equal), as every name it matches then also matches that part; any other term reaches every part, the complement
# included. Returns $null, meaning the assembly's whole heap, when $Project has no partitions or testMemory figure, or
# when $Filter has a term of another property, a negation, `&` or parentheses, or is empty.
function Get-ExmodFilterReachGb([string]$Project, [string]$Filter) {
  $mem = Get-ExmodTestMemory $Project
  if (-not $mem -or -not $Filter) { return $null }
  try { $partitions = Get-ExmodPartitions $Project } catch { return $null }
  if (-not $partitions) { return $null }
  $declared = @($partitions.Parts | Select-Object -SkipLast 1)
  $terms = @()
  foreach ($t in ($Filter -split '\|')) {
    if ($t -notmatch '^\s*FullyQualifiedName\s*([~=])\s*([^()&|!~=]+?)\s*$') { return $null }
    $terms += [pscustomobject]@{ Op = $Matches[1]; Value = $Matches[2] }
  }
  $reached = [System.Collections.Generic.HashSet[int]]::new()
  foreach ($term in $terms) {
    $owner = -1
    for ($i = 0; $i -lt $declared.Count -and $owner -lt 0; $i++) {
      foreach ($pt in ($declared[$i].Filter -split '\|')) {
        if ($pt -notmatch '^\s*FullyQualifiedName\s*([~=])\s*(.+?)\s*$') { continue }
        $claims = if ($Matches[1] -eq '~') { $term.Value.Contains($Matches[2]) } else { $term.Op -eq '=' -and $term.Value -ceq $Matches[2] }
        if ($claims) { $owner = $i; break }
      }
    }
    if ($owner -ge 0) { [void]$reached.Add($owner) }
    else { return $null }
  }
  $most = 0
  foreach ($i in $reached) {
    $h = if ($null -ne $declared[$i].HeapGb) { [int]$declared[$i].HeapGb } else { $mem.HeapGb }
    if ($h -gt $most) { $most = $h }
  }
  return $most
}

# The memory a test unit takes: { ShareGb; LimitGb; FilterLimitGb; HeavyGb; RetryLimitGb; RetryShareGb }. ShareGb is the whole GiB taken
# from the budget; LimitGb the GC heap limit the host runs under, or $null for none; FilterLimitGb and HeavyGb are set only
# when the run is under the filter limit, as that limit and as the limit EXMOD_HEAVY=1 would run it under. RetryLimitGb
# and RetryShareGb are set only when the run starts under `filterStartGb`: the filter limit it reruns under after an
# OutOfMemoryException and that rerun's share. Under EXMOD_HEAVY=1 a filtered run takes the largest `heapGb` of the
# partition parts its filter can reach (Get-ExmodFilterReachGb), or the filter share when that is no more than the
# start limit; a filter that cannot be shown to miss a heavier part takes the assembly's whole heap.
# $Project: the test assembly. $PartHeapGb: the partition's own `heapGb`, or $null. $Filtered: the run has -Filter.
# $Filter: the run's -Filter expression, or $null.
function Get-ExmodTestShare([string]$Project, $PartHeapGb, [bool]$Filtered, [string]$Filter = $null) {
  $mem = Get-ExmodTestMemory $Project
  $heap = if ($null -ne $PartHeapGb) { [int]$PartHeapGb } elseif ($mem) { $mem.HeapGb } else { $null }
  $useFilterLimit = $Filtered -and $mem -and $null -ne $mem.FilterHeapGb
  if ($useFilterLimit -and $env:EXMOD_HEAVY -eq '1') {
    $start = if ($null -ne $mem.FilterStartGb) { $mem.FilterStartGb } else { $mem.FilterHeapGb }
    $reach = Get-ExmodFilterReachGb $Project $Filter
    if ($null -ne $reach -and $reach -le $start) { $useFilterLimit = $true }
    else {
      $useFilterLimit = $false
      if ($null -ne $reach) { $heap = $reach }
    }
  }
  if ($useFilterLimit) {
    $start = if ($null -ne $mem.FilterStartGb) { $mem.FilterStartGb } else { $mem.FilterHeapGb }
    $retry = if ($start -ne $mem.FilterHeapGb) { $mem.FilterHeapGb } else { $null }
    return [pscustomobject]@{ ShareGb = $start + $ExmodHostOverheadGb; LimitGb = $start; FilterLimitGb = $start; HeavyGb = $heap; RetryLimitGb = $retry; RetryShareGb = $(if ($retry) { $retry + $ExmodHostOverheadGb }) }
  }
  if ($null -eq $heap) {
    return [pscustomobject]@{ ShareGb = $ExmodUnlimitedTestShareGb; LimitGb = $null; FilterLimitGb = $null; HeavyGb = $null; RetryLimitGb = $null; RetryShareGb = $null }
  }
  return [pscustomobject]@{ ShareGb = $heap + $ExmodHostOverheadGb; LimitGb = $heap; FilterLimitGb = $null; HeavyGb = $null; RetryLimitGb = $null; RetryShareGb = $null }
}

# Starts sampling the resident memory of a test host's process tree every EXMOD_MEMORY_SAMPLE_MS (default 3000) ms.
# The host is the child of this process whose command line holds $Needle (its unique results directory), with every
# descendant of it, read from $env:EXMOD_PROC_DIR (default /proc). The PIDs of the host are written to the `pids` of
# $RecordPath, the reservation's booked-<id>.json, whenever they change. Returns the sampler for Stop-ExmodMemorySampler,
# or $null where the process table does not exist (Windows).
function Start-ExmodMemorySampler([string]$Needle, [string]$RecordPath) {
  $procDir = if ($env:EXMOD_PROC_DIR) { $env:EXMOD_PROC_DIR } else { '/proc' }
  if ([System.OperatingSystem]::IsWindows() -or -not (Test-Path -LiteralPath $procDir -PathType Container)) { return $null }
  $state = [hashtable]::Synchronized(@{ Stop = $false; PeakBytes = [int64]0 })
  $interval = if ($env:EXMOD_MEMORY_SAMPLE_MS) { [int]$env:EXMOD_MEMORY_SAMPLE_MS } else { 3000 }
  $ps = [powershell]::Create()
  [void]$ps.AddScript({
      param($Needle, $Parent, $Interval, $State, $ProcDir, $RecordPath)
      $written = ''
      while (-not $State.Stop) {
        $parentOf = @{}
        foreach ($dir in [System.IO.Directory]::GetDirectories($ProcDir)) {
          $id = [System.IO.Path]::GetFileName($dir)
          if ($id -notmatch '^\d+$') { continue }
          try { $stat = [System.IO.File]::ReadAllText("$dir/stat") } catch { continue }
          $parentOf[[int]$id] = [int]($stat.Substring($stat.LastIndexOf(')') + 2).Split(' ')[1])
        }
        $tree = [System.Collections.Generic.HashSet[int]]::new()
        foreach ($id in $parentOf.Keys) {
          if ($parentOf[$id] -ne $Parent) { continue }
          try { $cmd = [System.IO.File]::ReadAllText("$ProcDir/$id/cmdline") } catch { continue }
          if ($cmd.Contains($Needle, [System.StringComparison]::Ordinal)) { [void]$tree.Add($id) }
        }
        $roots = (@($tree) | Sort-Object) -join ','
        if ($RecordPath -and $roots -and $roots -ne $written) {
          try {
            $rec = [System.IO.File]::ReadAllText($RecordPath) | ConvertFrom-Json
            $rec.pids = @($tree | Sort-Object)
            [System.IO.File]::WriteAllText("$RecordPath.tmp", (ConvertTo-Json $rec -Compress))
            [System.IO.File]::Move("$RecordPath.tmp", $RecordPath, $true)
            $written = $roots
          }
          catch { }
        }
        do {
          $grew = $false
          foreach ($id in $parentOf.Keys) {
            if (-not $tree.Contains($id) -and $tree.Contains($parentOf[$id])) { [void]$tree.Add($id); $grew = $true }
          }
        } while ($grew)
        $bytes = [int64]0
        foreach ($id in $tree) {
          try { $bytes += [int64]([System.IO.File]::ReadAllText("$ProcDir/$id/statm").Split(' ')[1]) * 4096 } catch { }
        }
        if ($bytes -gt $State.PeakBytes) { $State.PeakBytes = $bytes }
        for ($waited = 0; $waited -lt $Interval -and -not $State.Stop; $waited += 100) { Start-Sleep -Milliseconds 100 }
      }
    }).AddArgument($Needle).AddArgument([Environment]::ProcessId).AddArgument($interval).AddArgument($state).AddArgument($procDir).AddArgument($RecordPath)
  return [pscustomobject]@{ Shell = $ps; Handle = $ps.BeginInvoke(); State = $state }
}

# Stops a sampler Start-ExmodMemorySampler returned and gives the largest resident size it saw in GiB, or $null for a
# $null sampler or when it never saw the host.
function Stop-ExmodMemorySampler($Sampler) {
  if (-not $Sampler) { return $null }
  $Sampler.State.Stop = $true
  try { $Sampler.Shell.EndInvoke($Sampler.Handle) } catch { }
  $Sampler.Shell.Dispose()
  if ($Sampler.State.PeakBytes -le 0) { return $null }
  return $Sampler.State.PeakBytes / 1GB
}

# Appends one JSON line for a finished test host to $EXMOD_MEMORY_LOG (default ~/.cache/exmod/memory-peaks.jsonl):
# label, filtered, filter, shareGb, limitGb (null for none), peakGb (null when not sampled), seconds the share was
# held, exit code and UTC time. $Reservation is the one Lock-ExmodMemory returned. Failing to write is ignored.
function Add-ExmodMemoryPeakRecord([string]$Label, [bool]$Filtered, [string]$Filter, $Reservation, $LimitGb, $PeakGb, [int]$ExitCode) {
  try {
    $path = if ($env:EXMOD_MEMORY_LOG) { $env:EXMOD_MEMORY_LOG } else { Join-Path $HOME '.cache/exmod/memory-peaks.jsonl' }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    $row = [ordered]@{
      label = $Label; filtered = $Filtered; filter = $Filter; shareGb = $Reservation.Gb; limitGb = $LimitGb
      peakGb = if ($null -ne $PeakGb) { [Math]::Round($PeakGb, 2) } else { $null }
      seconds = [Math]::Round(((Get-Date) - $Reservation.Taken).TotalSeconds); exit = $ExitCode
      time = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture)
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json $row -Compress) + "`n")
    $log = [System.IO.File]::Open($path, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
    try { $log.Write($bytes, 0, $bytes.Length) }
    finally { $log.Dispose() }
  }
  catch { }
}

# The id of this exmod process's test run: $EXMOD_RUN_ID when set, else one id made on first use and kept, so every
# host of the process records the same one.
function Get-ExmodRunId {
  if ($env:EXMOD_RUN_ID) { return $env:EXMOD_RUN_ID }
  if (-not $script:ExmodRunId) { $script:ExmodRunId = [guid]::NewGuid().ToString('N').Substring(0, 12) }
  return $script:ExmodRunId
}

# HEAD of the checkout at $Root and whether its tracked files differ from it, as { Commit; Dirty }; both $null when git
# cannot say. Leaves $LASTEXITCODE as it found it.
function Get-ExmodRunTree([string]$Root) {
  $saved = $global:LASTEXITCODE
  $tree = [pscustomobject]@{ Commit = $null; Dirty = $null }
  try {
    $head = @(git -C $Root rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $head.Count -eq 1 -and $head[0] -match '^[0-9a-f]{40,64}$') {
      $changes = @(git -C $Root status --porcelain --untracked-files=no 2>$null)
      if ($LASTEXITCODE -eq 0) { $tree = [pscustomobject]@{ Commit = $head[0]; Dirty = ($changes.Count -gt 0) } }
      else { $tree = [pscustomobject]@{ Commit = $head[0]; Dirty = $null } }
    }
  }
  catch { }
  $global:LASTEXITCODE = $saved
  return $tree
}

# Seconds per test class in the trx file $Path as an ordered map of the $Top largest, read with a streaming XmlReader
# (a result's duration by test id, then the class of each test definition). $null when the file is missing or unreadable.
function Read-ExmodTrxClassSeconds([string]$Path, [int]$Top = 40) {
  $reader = $null
  try {
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $reader = [System.Xml.XmlReader]::Create($Path, $settings)
    $byTest = @{}
    $byClass = @{}
    while ($reader.Read()) {
      if ($reader.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
      if ($reader.LocalName -eq 'UnitTestResult') {
        $id = $reader.GetAttribute('testId')
        $duration = $reader.GetAttribute('duration')
        if ($id -and $duration) {
          $byTest[$id] = [double]$byTest[$id] + [TimeSpan]::Parse($duration, [System.Globalization.CultureInfo]::InvariantCulture).TotalSeconds
        }
      }
      elseif ($reader.LocalName -eq 'UnitTest') {
        $id = $reader.GetAttribute('id')
        if ($id -and $reader.ReadToDescendant('TestMethod')) {
          $class = $reader.GetAttribute('className')
          if ($class -and $byTest.ContainsKey($id)) { $byClass[$class] = [double]$byClass[$class] + $byTest[$id] }
        }
      }
    }
    $classes = [ordered]@{}
    foreach ($entry in ($byClass.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First $Top)) {
      $classes[$entry.Key] = [Math]::Round($entry.Value, 3)
    }
    return $classes
  }
  catch { return $null }
  finally { if ($reader) { $reader.Dispose() } }
}

# The file of test-run records: $EXMOD_RUN_LOG, else ~/.cache/exmod/test-runs.jsonl.
function Get-ExmodRunLogPath {
  if ($env:EXMOD_RUN_LOG) { return $env:EXMOD_RUN_LOG }
  return Join-Path $HOME '.cache/exmod/test-runs.jsonl'
}

# The records of the run log in file order, each as an object; a line that is not a JSON object with a `run` key
# (concurrent appends can interleave) is skipped. Empty when the file is missing or unreadable.
function Read-ExmodRunLog {
  $path = Get-ExmodRunLogPath
  $rows = @()
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $rows }
  try { $lines = @(Get-Content -LiteralPath $path -ErrorAction Stop) } catch { return $rows }
  foreach ($line in $lines) {
    if (-not "$line".Trim()) { continue }
    try { $row = ConvertFrom-Json "$line" -ErrorAction Stop } catch { continue }
    if ($row -is [pscustomobject] -and $row.PSObject.Properties['run']) { $rows += $row }
  }
  return $rows
}

# The labels of the partition hosts, in the newest run at $Root whose records name a part, that exited non-zero or
# were stopped (a record without `stopped` was not); $null when no such run is in the log. The newest run is the one
# of the last such record in file order.
function Get-ExmodRedParts([string]$Root) {
  $rows = @(Read-ExmodRunLog | Where-Object { $_.root -eq $Root -and $_.part })
  if (-not $rows) { return $null }
  $run = $rows[-1].run
  return @($rows | Where-Object { $_.run -eq $run -and ($_.exit -ne 0 -or $_.stopped -eq $true) } |
      ForEach-Object { $_.label } | Select-Object -Unique)
}

# Appends one JSON line for a finished test host to $EXMOD_RUN_LOG (default ~/.cache/exmod/test-runs.jsonl): run
# ($RunId), trigger ($EXMOD_RUN_TRIGGER, else "direct"), root, commit, dirty ($Tree), label, part, filter, filtered,
# exit, seconds, UTC time, the tests/passed/failed/skipped counts of the host's summary line (null without one), the
# names of up to 50 failing tests (and truncated: true when there were more) and the seconds of the 40 largest test
# classes from $TrxPath (null when it cannot be read). $Output is the host's output lines. $TrxPath is deleted. The
# append holds the file exclusively (retrying while another host holds it), as concurrent appends overwrite each other.
# Writes nothing else and never throws.
function Add-ExmodTestRunRecord([string]$RunId, [string]$Root, $Tree, [string]$Label, [string]$Part, [bool]$Filtered, [string]$Filter, [int]$ExitCode, [double]$Seconds, $Output, [string]$TrxPath) {
  try {
    $classes = $null
    if ($TrxPath -and (Test-Path -LiteralPath $TrxPath)) {
      $classes = Read-ExmodTrxClassSeconds $TrxPath
      Remove-Item -LiteralPath $TrxPath -Force -ErrorAction SilentlyContinue
    }
    $path = Get-ExmodRunLogPath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    $counts = @($Output | Select-String -Pattern 'Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)' -AllMatches |
        ForEach-Object { $_.Matches } | Select-Object -Last 1)
    $failures = @(Get-ExmodTestFailures @($Output | ForEach-Object { "$_" }))
    $names = @($failures | Select-Object -First 50 | ForEach-Object { $_.Name })
    $row = [ordered]@{
      run = $RunId
      trigger = if ($env:EXMOD_RUN_TRIGGER) { $env:EXMOD_RUN_TRIGGER } else { 'direct' }
      root = $Root; commit = $Tree.Commit; dirty = $Tree.Dirty
      label = $Label; part = if ($Part) { $Part } else { $null }
      filter = if ($Filter) { $Filter } else { $null }; filtered = $Filtered
      exit = $ExitCode; seconds = [Math]::Round($Seconds, 1)
      time = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture)
      tests = if ($counts) { [int]$counts[0].Groups[4].Value } else { $null }
      passed = if ($counts) { [int]$counts[0].Groups[2].Value } else { $null }
      failed = if ($counts) { [int]$counts[0].Groups[1].Value } else { $null }
      skipped = if ($counts) { [int]$counts[0].Groups[3].Value } else { $null }
      failures = $names
    }
    if ($failures.Count -gt 50) { $row['truncated'] = $true }
    $row['classes'] = $classes
    $bytes = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json $row -Compress -Depth 4) + "`n")
    $log = $null
    for ($try = 0; $try -lt 300 -and -not $log; $try++) {
      try { $log = [System.IO.File]::Open($path, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None) }
      catch [System.IO.IOException] { Start-Sleep -Milliseconds (10 + (Get-Random -Maximum 20)) }
    }
    try { $log.Write($bytes, 0, $bytes.Length) }
    finally { $log.Dispose() }
  }
  catch { }
}

# The `dotnet test` filter that selects what none of $Filters selects. Each filter is `|`-joined terms
# of the form <Property>~<value> or <Property>=<value>; the complement joins the negated terms
# (!~, !=) with `&`. Throws on a filter with parentheses, `&` or an already negated term.
function Get-ExmodComplementFilter([string[]]$Filters) {
  $terms = foreach ($f in $Filters) {
    foreach ($t in ($f -split '\|')) {
      if ($t -notmatch '^\s*(\w+)\s*([~=])\s*([^()&|!]+?)\s*$') {
        throw "Partition filter term '$t' is not <Property>~<value> or <Property>=<value>; a complement cannot be built from it."
      }
      "$($Matches[1])!$($Matches[2])$($Matches[3])"
    }
  }
  return ($terms -join '&')
}

# One result per assembly from the results of its partition runs (those carrying a Part): Ok when
# every part is, Total the sum (null when a part has none), Failures and Log gathered, Parts kept.
# Results without a Part pass through. Order follows each name's first result.
function Merge-ExmodPartitionResults($Results) {
  $out = @()
  foreach ($group in ($Results | Group-Object Name)) {
    $parts = @($group.Group | Where-Object { $_.Part })
    if (-not $parts) { $out += $group.Group; continue }
    $out += [pscustomobject]@{
      Name = $group.Name
      Ok = -not @($parts | Where-Object { -not $_.Ok })
      Line = "$($parts.Count) partitions"
      Failures = @($parts | ForEach-Object { $_.Failures })
      Total = if (@($parts | Where-Object { $null -eq $_.Total })) { $null } else { [int](($parts | Measure-Object Total -Sum).Sum) }
      Log = (($parts | ForEach-Object { $_.Log }) -join ', ')
      Parts = $parts
    }
  }
  return $out
}

function Invoke-Test([string[]]$Argv) {
  $positional = @(Get-Positional $Argv @('-Throttle', '-Filter', '-Parts') @('-Coverage', '-AcceptDrop', '-NoBuild'))
  $version = if ($positional.Count -gt 0) { $positional[0] } else { 'latest' }
  $throttle = [int](Get-Opt $Argv '-Throttle' 0)
  $coverage = Get-Flag $Argv '-Coverage'
  $acceptDrop = Get-Flag $Argv '-AcceptDrop'
  $noBuild = Get-Flag $Argv '-NoBuild'
  # Passed straight to `dotnet test --filter`, so it takes that expression grammar
  # (`FullyQualifiedName~Boiler`, `Name=X|Name=Y`). A bare class name works because the runner treats
  # an unqualified term as a substring match on the fully qualified name.
  $filter = Get-Opt $Argv '-Filter' ''
  $partsArg = Get-Opt $Argv '-Parts' ''
  if ($partsArg -and $filter) { throw "-Parts and -Filter cannot be combined: -Filter runs an assembly whole." }
  $census = Get-ExmodCensusPath

  # The units wait for memory shares while the caller's flock holds the build lock, and a -Coverage run that holds
  # the whole budget waits for that lock; exmod takes it itself.
  if ($env:EXMOD_BUILD_LOCK) {
    $outer = Get-FlockAncestorHolder $env:EXMOD_BUILD_LOCK
    if ($outer) { throw "exmod test takes the build lock itself: $env:EXMOD_BUILD_LOCK is held by parent process $outer; run it without the outer flock on that lock." }
  }

  # Dependency order and project paths come from Get-ExmodTestProjects, shared with
  # `build -Tests` so the two commands can't drift on what "every test project" means.
  $projects = Get-ExmodTestProjects

  $wanted = Resolve-GameVersions $version
  $dotnet = Resolve-DotnetHost $wanted
  Write-Host "Using dotnet host: $dotnet"

  # Mirrors .github/workflows/tests.yml: collect cobertura over the solution and ratchet against
  # coverage_gate.py. The gate floors track the current build, so this always uses the latest version.
  # -Filter does not apply here; the solution run is always whole.
  if ($coverage) {
    $reservation = Lock-ExmodMemory -Gb (Get-ExmodMemoryBudget) -Label 'coverage'
    try {
      Invoke-WithBuildLock {
        $toolsDir = Join-Path (Get-ExmodDotnetDir) 'tools'
        & $dotnet tool install dotnet-coverage --tool-path $toolsDir 2>$null | Out-Null
        $dc = Join-Path $toolsDir "dotnet-coverage$ExeSuffix"
        $cov = Join-Path $RepoRoot '.exmod/out/coverage.xml'
        New-Item -ItemType Directory -Force -Path (Split-Path $cov) | Out-Null
        Write-Host "Collecting coverage over the latest suite..."
        & $dc collect -f cobertura -o $cov "$dotnet test `"$(Get-ExmodSolution)`" -c Debug --nologo" 2>&1 |
          Tee-Object -Variable collected | Out-Host
        $collectOk = ($LASTEXITCODE -eq 0)

        # One VSTest summary per assembly: "Passed!  - Failed: 0, Passed: 12, Skipped: 0, Total: 12,
        # Duration: 3 s - ExpandedLib.Tests.dll (net10.0)". A latest-series test project with no such
        # line discovered no tests.
        $series = $CurrentGameVersion
        $counted = @{}
        foreach ($line in $collected) {
          if ("$line" -match '(Passed|Failed)!.*Total:\s*(\d+).*\s-\s+(\S+)\.dll\b') {
            $counted["$series/$($Matches[3])"] = [pscustomobject]@{
              Name = "$series/$($Matches[3])"; Ok = ($Matches[1] -eq 'Passed' -and [int]$Matches[2] -gt 0); Total = [int]$Matches[2]
            }
          }
        }
        foreach ($p in $projects.Values | Where-Object { $series -in $_.Series }) {
          $name = "$series/$($p.Project)"
          if (-not $counted.Contains($name)) { $counted[$name] = [pscustomobject]@{ Name = $name; Ok = $false; Total = $null } }
        }
        $results = @($counted.Values | Sort-Object Name)
        Write-Host ""
        foreach ($r in $results | Where-Object { $null -eq $_.Total }) {
          Write-Host ("FAIL  {0,-40} NO TEST SUMMARY - the assembly discovered no tests" -f $r.Name)
        }
        foreach ($r in $results | Where-Object { $_.Total -eq 0 }) {
          Write-Host ("FAIL  {0,-40} zero tests discovered" -f $r.Name)
        }
        $drops = Compare-ExmodCensus $results $census $acceptDrop
        if (-not $collectOk) { throw "Coverage collection failed." }
        Assert-ExmodTestRun $results $drops $census
        Write-ExmodCensus $results $census

        $py = (Get-Command python -ErrorAction SilentlyContinue) ?? (Get-Command python3 -ErrorAction SilentlyContinue)
        if (-not $py) { throw "Python is required for the coverage gate but was not found (coverage.xml was still written)." }
        $floors = Get-ExmodCoverageFloors
        if (-not $floors) {
          Write-Host "No coverage floors file (manifest coverageFloors, tests/ or infra/test/) - coverage collected, no gate."
          return
        }
        & $py.Source (Join-Path $ToolsRoot 'tools/coverage_gate.py') $cov $floors
        $floorsOk = ($LASTEXITCODE -eq 0)
        # Every client-guarded block needs a covered line or an entry in the allowlist beside the floors.
        Write-Host ""
        Push-Location $RepoRoot
        try {
          & $py.Source (Join-Path $ToolsRoot 'tools/side_gate_coverage.py') $cov $floors (Join-Path (Split-Path $floors -Parent) 'side-gate-allowlist.json')
          $sideOk = ($LASTEXITCODE -eq 0)
        } finally { Pop-Location }
        if (-not $floorsOk) { throw "Coverage gate failed." }
        if (-not $sideOk) { throw "Side gate failed." }
        Write-Host "Coverage gate passed."
      }
    }
    finally { Unlock-ExmodMemory $reservation }
    return
  }

  # -Parts: the partition names to run, or `red`, the parts that failed or were stopped in the last partitioned run.
  $partNames = $null
  $redLabels = $null
  if ($partsArg) {
    $declared = @()
    foreach ($v in $wanted) {
      foreach ($p in $projects.Values | Where-Object { $v -in $_.Series }) {
        $declared += @((Get-ExmodPartitions $p.Project).Parts | ForEach-Object { $_.Name })
      }
    }
    $declared = @($declared | Select-Object -Unique)
    if ($partsArg -eq 'red') {
      $redLabels = Get-ExmodRedParts $RepoRoot
      if (-not $redLabels) {
        Write-Host "-Parts red: no red part in the last partitioned run at $RepoRoot ($(Get-ExmodRunLogPath))."
        exit 2
      }
    }
    else {
      $partNames = @($partsArg -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
      $unknown = @($partNames | Where-Object { $_ -notin $declared })
      if ($unknown) { throw "-Parts: no partition named $($unknown -join ', '); the parts are $($declared -join ', '), or red." }
    }
  }

  $work = foreach ($v in $wanted) {
    foreach ($mod in $projects.Keys) {
      # A sample or an extra project (samples/HelloExpanded.csproj, verify/ExlibVerify.Tests/ExlibVerify.Tests.csproj)
      # only targets $(CurrentGameTfm) - a legacy matrix buys it nothing, so it is skipped rather
      # than failed on '-f net8.0'/'net7.0'.
      if ($v -notin $projects[$mod].Series) { continue }
      [pscustomobject]@{
        Version = $v
        Tfm     = $GameTfms[$v]
        Project = $projects[$mod].Project
        Proj    = $projects[$mod].Proj
        Legacy  = ($GameTfms[$v] -ne 'net10.0')   # legacy TFMs need the multi-target opt-in
        Log     = Join-Path $RepoRoot ".exmod/test-logs/$v-$($projects[$mod].Project).log"
        Blame   = Join-Path $RepoRoot ".exmod/test-results/$v-$($projects[$mod].Project)"
      }
    }
  }

  # One parallel MSBuild call per series builds the shared mod projects once. This also auto-provisions each version's game binaries once, up
  # front, letting the test phase run in parallel with --no-build. With -NoBuild nothing is built:
  # an item counts as built when its Debug test assembly for the TFM exists.
  if ($noBuild) { Write-Host "Not building: testing $($work.Count) target(s) across version(s): $($wanted -join ', ')" }
  else { Write-Host "Building $($work.Count) test target(s) across version(s): $($wanted -join ', ')" }
  $failedProj = @()
  if (-not $noBuild) {
    $failedProj += @(Invoke-WithBuildLock {
        foreach ($group in ($work | Group-Object Version)) {
          Invoke-ExmodParallelBuild $dotnet @($group.Group) 'Debug' @('--nologo', '-v', 'q') $true
        }
      })
  }
  $built = foreach ($item in $work) {
    if ($noBuild) {
      $projDir = if (Test-Path -LiteralPath $item.Proj -PathType Container) { $item.Proj } else { Split-Path -Parent $item.Proj }
      $dll = Join-Path $projDir "bin/Debug/$($item.Tfm)/$($item.Project).dll"
      $have = Test-Path -LiteralPath $dll -PathType Leaf
      $item | Add-Member -NotePropertyName BuildOk -NotePropertyValue $have -PassThru |
        Add-Member -NotePropertyName BuildLine -NotePropertyValue "Not built for $($item.Version): $dll is missing; drop -NoBuild or build first" -PassThru
      continue
    }
    $item | Add-Member -NotePropertyName BuildOk -NotePropertyValue ($item.Proj -notin $failedProj) -PassThru |
      Add-Member -NotePropertyName BuildLine -NotePropertyValue 'build failed' -PassThru
  }

  # Without -Filter an assembly with manifest partitions runs as one process per partition; with it, whole.
  $units = foreach ($item in $built) {
    $partitions = if ($filter) { $null } else { Get-ExmodPartitions $item.Project }
    if (-not $partitions) {
      if ($partsArg) { continue }
      $share = Get-ExmodTestShare $item.Project $null ([bool]$filter) $filter
      $item | Select-Object * |
        Add-Member -NotePropertyName Share -NotePropertyValue $share -PassThru -Force
      continue
    }
    foreach ($part in $partitions.Parts) {
      if ($partNames -and $part.Name -notin $partNames) { continue }
      if ($redLabels -and "$($item.Version)/$($item.Project)/$($part.Name)" -notin $redLabels) { continue }
      $share = Get-ExmodTestShare $item.Project $part.HeapGb $false
      $item | Select-Object * |
        Add-Member -NotePropertyName Part -NotePropertyValue $part.Name -PassThru -Force |
        Add-Member -NotePropertyName PartFilter -NotePropertyValue $part.Filter -PassThru -Force |
        Add-Member -NotePropertyName Share -NotePropertyValue $share -PassThru -Force |
        Add-Member -NotePropertyName Log -NotePropertyValue ($item.Log -replace '\.log$', "-$($part.Name).log") -PassThru -Force |
        Add-Member -NotePropertyName Blame -NotePropertyValue "$($item.Blame)-$($part.Name)" -PassThru -Force
    }
  }
  if ($partsArg -and -not @($units)) {
    Write-Host "-Parts $partsArg selects no partition of the series run ($($wanted -join ', '))."
    exit 2
  }
  $manifestIndex = 0
  $units = @($units | ForEach-Object { $_ | Add-Member -NotePropertyName ManifestIndex -NotePropertyValue ($manifestIndex++) -PassThru -Force } |
      Sort-Object @{ Expression = { $_.Share.ShareGb }; Descending = $true }, ManifestIndex)
  if ($throttle -le 0) {
    $throttle = $units.Count
    $budget = Get-ExmodMemoryBudget
    $partUnits = @($units | Where-Object { $_.Part })
    if ($null -ne $budget -and $partUnits.Count -gt 0) {
      $shares = ($partUnits | ForEach-Object { "$($_.Part) $($_.Share.ShareGb)" }) -join ', '
      Write-Host "Partitions: $($units.Count) test process(es), each started when the $budget GiB memory budget holds its share ($shares)."
    }
  }

  $waves = @($units | ForEach-Object { Get-ExmodTestWave $_.Version } | Sort-Object -Unique)
  foreach ($wave in $waves) {
    $seq = 0
    foreach ($u in @($units | Where-Object { (Get-ExmodTestWave $_.Version) -eq $wave })) {
      $u | Add-Member -NotePropertyName Seq -NotePropertyValue ($seq++) -Force
    }
  }
  if ($waves.Count -gt 1) {
    Write-Host "EXLIB_WRITE_GOLDENS is set: the $CurrentGameVersion lanes run first, the older series after them."
  }
  $results = @(Invoke-WithTestPhase {
      foreach ($item in $units) {
        if (Test-Path -LiteralPath $item.Blame) { Remove-Item -LiteralPath $item.Blame -Recurse -Force }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $item.Log) | Out-Null
      }
      Write-Host "Running tests in parallel..."
      # A -Parallel runspace sees none of this script's functions; the parser goes in as its text.
      $failuresFn = ${function:Get-ExmodTestFailures}.ToString()
      $holderFn = ${function:Get-FlockAncestorHolder}.ToString()
      $lockFn = ${function:Lock-ExmodMemory}.ToString()
      $unlockFn = ${function:Unlock-ExmodMemory}.ToString()
      $startSamplerFn = ${function:Start-ExmodMemorySampler}.ToString()
      $stopSamplerFn = ${function:Stop-ExmodMemorySampler}.ToString()
      $recordFn = ${function:Add-ExmodMemoryPeakRecord}.ToString()
      $runRecordFn = ${function:Add-ExmodTestRunRecord}.ToString()
      $runLogPathFn = ${function:Get-ExmodRunLogPath}.ToString()
      $trxFn = ${function:Read-ExmodTrxClassSeconds}.ToString()
      $runId = Get-ExmodRunId
      $runRoot = $RepoRoot
      $runTree = Get-ExmodRunTree $RepoRoot
      $started = [System.Collections.Concurrent.ConcurrentDictionary[string, bool]]::new()
      foreach ($wave in $waves) {
        $units | Where-Object { (Get-ExmodTestWave $_.Version) -eq $wave } | ForEach-Object -ThrottleLimit $throttle -Parallel {
          $dotnet = $using:dotnet
          $filter = $using:filter
          ${function:Get-ExmodTestFailures} = $using:failuresFn
          ${function:Get-FlockAncestorHolder} = $using:holderFn
          ${function:Lock-ExmodMemory} = $using:lockFn
          ${function:Unlock-ExmodMemory} = $using:unlockFn
          ${function:Start-ExmodMemorySampler} = $using:startSamplerFn
          ${function:Stop-ExmodMemorySampler} = $using:stopSamplerFn
          ${function:Add-ExmodMemoryPeakRecord} = $using:recordFn
          ${function:Add-ExmodTestRunRecord} = $using:runRecordFn
          ${function:Get-ExmodRunLogPath} = $using:runLogPathFn
          ${function:Read-ExmodTrxClassSeconds} = $using:trxFn
          $runId = $using:runId
          $runRoot = $using:runRoot
          $runTree = $using:runTree
          $started = $using:started
          $item = $_
          $wave = $using:wave
          $gateKey = { param($seq) "$wave/$seq" }
          if (-not $item.BuildOk) {
            $started[(& $gateKey $item.Seq)] = $true
            return [pscustomobject]@{ Name = "$($item.Version)/$($item.Project)"; Ok = $false; Line = $item.BuildLine; Total = $null; Part = $item.Part }
          }
          $trxName = "run-$($item.Version)-$($item.Project)$(if ($item.Part) { "-$($item.Part)" }).trx"
          $trxPath = Join-Path $item.Blame $trxName
          $testArgs = @('test', $item.Proj, '-f', $item.Tfm, '--no-build', '--nologo', '--blame', '--results-directory', $item.Blame,
            '--logger', "trx;LogFileName=$trxName")
          $runFilter = if ($item.PartFilter) { $item.PartFilter } else { $filter }
          if ($runFilter) { $testArgs += @('--filter', $runFilter) }
          if ($item.Legacy) { $testArgs += '-p:Legacy=true' }
          # Units ask for their share largest first, manifest order breaking ties: this one waits until the unit
          # before it has taken its share or is waiting for it.
          if ($item.Seq -gt 0) {
            $before = & $gateKey ($item.Seq - 1)
            while (-not $started.ContainsKey($before)) { Start-Sleep -Milliseconds 50 }
          }
          $mine = & $gateKey $item.Seq
          $label = "$($item.Version)/$($item.Project)$(if ($item.Part) { "/$($item.Part)" })"
          $limitGb = $item.Share.LimitGb
          $shareGb = $item.Share.ShareGb
          $retryGb = $item.Share.RetryLimitGb
          while ($true) {
            $attemptArgs = $testArgs
            if ($null -ne $limitGb) { $attemptArgs += @('-e', ('DOTNET_GCHeapHardLimit=0x{0:X}' -f ([int64]$limitGb * 1GB))) }
            $reservation = $null
            $sampler = $null
            $hostWatch = $null
            $hostOut = $null
            $hostExit = $null
            try {
              $reservation = Lock-ExmodMemory -Gb $shareGb -Label $label -Filtered:([bool]$filter) -OnWait { $started[$mine] = $true }
              $started[$mine] = $true
              $sampler = Start-ExmodMemorySampler $item.Blame $reservation.Record
              $hostWatch = [System.Diagnostics.Stopwatch]::StartNew()
              $out = & $dotnet @attemptArgs 2>&1
              $out = @($out | Where-Object { "$_" -notmatch '^\s*Results File: .*run-.*\.trx\s*$' })
              $ranExit = $LASTEXITCODE
              $hostWatch.Stop()
              $hostOut = $out
              $hostExit = $ranExit
            }
            finally {
              $started[$mine] = $true
              $peakGb = Stop-ExmodMemorySampler $sampler
              Unlock-ExmodMemory $reservation -PeakGb $peakGb
              if ($reservation) { Add-ExmodMemoryPeakRecord $label ([bool]$filter) $runFilter $reservation $limitGb $peakGb $ranExit }
              if ($null -ne $hostExit) {
                [void](Add-ExmodTestRunRecord $runId $runRoot $runTree $label $item.Part ([bool]$filter) $runFilter $hostExit $hostWatch.Elapsed.TotalSeconds $hostOut $trxPath)
              }
            }
            if ($null -ne $retryGb -and $ranExit -ne 0 -and ($out | Select-String -Pattern 'OutOfMemoryException' -Quiet)) {
              Write-Host "memory: $label ran out of memory under $limitGb GiB, retrying once under $retryGb GiB"
              $limitGb = $retryGb
              $shareGb = $item.Share.RetryShareGb
              $retryGb = $null
              continue
            }
            break
          }
          $LASTEXITCODE = $ranExit
          $out | Set-Content -LiteralPath $item.Log
          $ok = ($LASTEXITCODE -eq 0)
          $line = ($out | Select-String -Pattern 'Passed!|Failed!|error' | Select-Object -Last 1)

          # The exit code is not enough. An assembly that fails to load during discovery exits 0 with no
          # summary line, so the run would read as a blank PASS while every test in the suite had vanished.
          # Xunit names the cause on a "Skipping:" line and dotnet adds "No test is available in ...".
          $total = ($out | Select-String -Pattern 'Total:\s*(\d+)' -AllMatches |
            ForEach-Object { $_.Matches } | Select-Object -Last 1)
          if (-not $total) {
            $skipped = ($out | Select-String -Pattern 'Skipping:' | Select-Object -Last 1)
            $unavailable = ($out | Select-String -Pattern 'No test is available in' | Select-Object -Last 1)
            $noMatch = ($out | Select-String -Pattern 'No test matches the given testcase filter' | Select-Object -Last 1)
            # Under a filter, a suite holding nothing that matches is the ordinary case - the filter names a
            # class that lives in one project of three - and reports zero. Only dotnet's no-match line shows it:
            # a skipped or unavailable assembly fails with its reason.
            if ($filter -and $ok -and $noMatch -and -not $skipped -and -not $unavailable) {
              $line = 'no test matched the filter'
            }
            elseif ($skipped) {
              $ok = $false
              $line = 'DISCOVERY SKIPPED THE ASSEMBLY - ' + ($skipped.Line -replace '^.*?Skipping:\s*', '')
            }
            else {
              $ok = $false
              $line = 'NO TEST SUMMARY - the assembly discovered no tests (a type-load failure during ' +
              'discovery does this and still exits 0).'
            }
          }
          elseif (-not $filter -and [int]$total.Groups[1].Value -eq 0) {
            $ok = $false
            $line = 'zero tests discovered'
          }

          if (-not $ok -and $null -ne $item.Share.FilterLimitGb -and ($out | Select-String -Pattern 'OutOfMemoryException' -Quiet)) {
            $line = "$(($line | Out-String).Trim()) (ran under the $limitGb GiB filter limit; EXMOD_HEAVY=1 runs it under $($item.Share.HeavyGb) GiB)"
          }

          $failures = @(Get-ExmodTestFailures $out)

          [pscustomobject]@{
            Name = "$($item.Version)/$($item.Project)"; Ok = $ok; Line = $line; Failures = $failures
            Total = if ($total) { [int]$total.Groups[1].Value } else { $null }; Log = $item.Log; Part = $item.Part
          }
        }
      }

  })
  $results = @(Merge-ExmodPartitionResults $results)

  Write-Host ""
  Write-Host "===== Results ====="
  foreach ($r in $results | Sort-Object Name) {
    $tag = if ($r.Ok) { 'PASS' } else { 'FAIL' }
    $where = if (-not $r.Ok -and $r.Log) { " (log: $($r.Log))" } else { '' }
    $line = if ($r.Parts) { "$($r.Total) tests in $($r.Line)" } else { ($r.Line -replace '\s+', ' ').Trim() }
    Write-Host ("{0}  {1,-40} {2}{3}" -f $tag, $r.Name, $line, $where)
    foreach ($p in $r.Parts) {
      Write-Host ("      {0,-10} {1}" -f $p.Part, ($p.Line -replace '\s+', ' ').Trim())
    }
    foreach ($f in $r.Failures) {
      Write-Host ("         {0}: {1}" -f $f.Name, $f.Message)
    }
  }

  if ($filter -or $partsArg) {
    Write-Host "Census not read or written: $(if ($filter) { '-Filter' } else { '-Parts' }) counts a subset."
    Assert-ExmodTestRun $results @() $census
  }
  else {
    $drops = Compare-ExmodCensus $results $census $acceptDrop
    Assert-ExmodTestRun $results $drops $census
    Write-ExmodCensus $results $census
  }
  Write-Host "All $($results.Count) test run(s) passed."
}


Add-ExmodCommand -Group source -Name test -Summary 'run the test suites, one lane per game version' -Action {
  param([string[]]$Argv) Invoke-Test $Argv
} -Detail @'
exmod test [latest|all|1.22|1.21|1.20] [-Filter <expr>] [-Parts <names>|red] [-Throttle <n>] [-Coverage] [-AcceptDrop] [-NoBuild]

Builds every test project for the requested series (unless -NoBuild), then runs the suites in parallel - one lane per
(series, project). Missing runtimes and game binaries are provisioned on demand, so a fresh clone
needs nothing installed first. A series' test projects build in one parallel MSBuild call, so the
mod projects they share build once.

  latest      1.22 only (the default)
  all         every supported series
  -Filter     handed to dotnet test --filter, so it takes that grammar: FullyQualifiedName~Boiler,
              Name=X|Name=Y, or a bare class name (an unqualified term is a substring match)
  -Parts      run only the named partitions (comma-separated) of the assemblies the manifest partitions, or `red`: the
              parts that exited non-zero or were stopped in the last run at this repository root that has partition
              records in $EXMOD_RUN_LOG. An unknown name errors naming the valid ones, `red` with none exits 2, and
              -Parts with -Filter errors. Like -Filter it neither reads nor writes the census
  -Throttle   lanes to run at once; the default is all of them
  -Coverage   instead of the lanes, collect cobertura over the solution and ratchet it against
              the manifest's coverageFloors file, else tests/ or infra/test/coverage-floors.json;
              no floors file, no gate - the same gate CI runs. The side gate follows: every
              client-guarded block needs a covered line or an entry in side-gate-allowlist.json
              beside the floors file
  -AcceptDrop record a test count below the census instead of failing on it
  -NoBuild    skip the build and run the lanes against what is already built; a lane whose
              Debug test assembly for its framework is missing fails with 'Not built'.
              Ignored by -Coverage

With EXMOD_BUILD_LOCK set to a file, the build and provisioning part of the run holds that file's flock
(waiting up to EXMOD_BUILD_LOCK_WAIT seconds, default 3000) and the lanes run with it free; -Coverage holds
the whole step. The lanes run under this checkout's .exmod/test-phase.lock, so two runs here take turns,
and print `test phase:` lines with their clock time. Do not also wrap the command in `flock` on that file.

With EXLIB_WRITE_GOLDENS set, the current series' lanes run first and the older series' after them:
an older series' golden write compares against the shared goldens the current series' write rewrites.

A test assembly the manifest lists under `partitions` runs, without -Filter, as one process per partition
(see Get-ExmodPartitions): each partition but the last is a `|`-joined filter, the last runs everything
they leave out. The counts, failures and logs are summed, so the census, zero and drop rules see one
assembly. A -Filter run is not partitioned.

Every test host takes a share of the machine's memory budget (MemTotal less 10 GiB, $EXMOD_MEMORY_GB sets it) before it
starts, waiting when the budget is held, and gives it back when it ends; the units ask largest share first, manifest order breaking ties.
The manifest's `testMemory` is { "<assembly>": { "heapGb": n, "filterHeapGb": m, "filterStartGb": k } } (whole GiB, at least 1;
filterHeapGb and filterStartGb optional): the host runs under a GC heap limit of heapGb (DOTNET_GCHeapHardLimit, passed as `dotnet test -e`) and its
share is heapGb + 1 GiB. A partition may carry its own `heapGb`. A -Filter run of an assembly with filterHeapGb runs under
that limit, and under heapGb with EXMOD_HEAVY=1; when it fails with an OutOfMemoryException its result line says so.
With filterStartGb too, a -Filter run starts under filterStartGb (share filterStartGb + 1 GiB) and, when its output
shows an OutOfMemoryException, reruns once under filterHeapGb. While a host runs, its process tree's resident memory is
sampled every EXMOD_MEMORY_SAMPLE_MS (3000) ms on Linux; the `released` line ends with `, peak <p> GiB`, and one JSON
line per run (label, filtered, filter, shareGb, limitGb, peakGb, seconds, exit, time) is appended to $EXMOD_MEMORY_LOG
(~/.cache/exmod/memory-peaks.jsonl).
After each host ends one JSON line is appended to $EXMOD_RUN_LOG (~/.cache/exmod/test-runs.jsonl): run ($EXMOD_RUN_ID, else
one id per exmod process), trigger ($EXMOD_RUN_TRIGGER, else direct), root, commit, dirty, label, part, filter, filtered,
exit, seconds, time, tests, passed, failed, skipped, failures (at most 50, then truncated: true) and classes (seconds of the
40 largest test classes, from a trx file the host writes beside its blame results and exmod deletes). A -Coverage run writes
no record. A failure to write or read it changes nothing else about the run.
Where there is a process table (EXMOD_PROC_DIR, /proc; none on Windows) a share is granted on real use and no waiter holds
a lock while it waits: each second one decision grants it when MemAvailable less EXMOD_MEMORY_FLOOR_GB (10) less what the
running hosts may still grow to (their charge, the share until recorded peaks say less, less the resident size of their
process tree) is at least its charge, or when nothing is running. The charge is the p95 of the last 50 peaks in
EXMOD_MEMORY_LOG plus 1 GiB once 5 are recorded for the label and share, at most the share. A share above
EXMOD_MEMORY_SMALL_GB (6) is granted in order (priority first, then arrival); a smaller one goes ahead of it, except that
once the oldest big share has waited EXMOD_MEMORY_BIG_WAIT_S (600) s it leaves that share's charge free. The took line ends
`(real use: <h> GiB available)`. Without a process table the booking files in EXMOD_MEMORY_DIR decide, a waiter holding the
turn and its files.
An assembly with no entry takes 4 GiB and runs under no limit. -Coverage takes the whole budget and runs alone. The
share lines are `memory: <label> took <k> GiB of <B> ...` and `... released ...`.

Each run's full output is kept in .exmod/test-logs/<series>-<assembly>.log (<series>-<assembly>-<partition>.log
for a partition), overwritten by the next run,
and a failed run or one below the census prints that log's path. dotnet test runs with --blame, its
results in .exmod/test-results/<series>-<assembly>/, so an aborted test host names the tests it was
running.

The census, .exmod/census/<branch>.json, holds each assembly's last green test count per series.
An unfiltered run fails on an assembly that discovers no tests and on a count below the census, and
writes the census when every assembly passed; a -Filter run neither reads nor writes it.
'@

#endregion

#region format

# Formats every C# file under the manifest's mods, samples and tests entries, and under infra/, in
# two passes, and the order is load-bearing. CSharpier wraps lines to the printWidth in
# .csharpierrc but always emits Allman braces and cannot be configured; dotnet format then applies
# .editorconfig, which moves the braces onto the same line.
# Running the pair is idempotent. Running CSharpier alone afterwards would undo the brace style.
#
# `csharpier check` exits 1 on the finished result, so -Check formats and compares against git rather
# than using the tool's own check mode.
# The formatter, installed into .dotnet/tools (Get-ExmodDotnetDir) when the machine has none - the same
# bootstrap scripts/exmod.sh does for pwsh, and for the same reason: a contributor should not have to
# install anything by hand before the format gate will run.
# Pinned so every checkout formats identically; a local install of another version is removed
# from .dotnet/tools and reinstalled at this one.
$CSharpierVersion = '1.3.0'

function Resolve-CSharpier() {
  $toolsDir = Join-Path (Get-ExmodDotnetDir) 'tools'
  $local = Join-Path $toolsDir "csharpier$ExeSuffix"
  if (Test-Path $local) {
    $have = @(& $local --version 2>$null)[0]
    if ($have -eq $CSharpierVersion) { return $local }
    Write-Host "CSharpier $have found, $CSharpierVersion pinned - reinstalling into .dotnet/tools ..."
    dotnet tool uninstall csharpier --tool-path $toolsDir | Out-Null
  } else {
    Write-Host 'CSharpier not found - installing it into .dotnet/tools ...'
  }
  dotnet tool install csharpier --version $CSharpierVersion --tool-path $toolsDir | Out-Null
  if (-not (Test-Path $local)) { throw 'Could not install CSharpier into .dotnet/tools.' }
  return $local
}

function Invoke-Format([string[]]$Argv) {
  $check = Get-Flag $Argv '-Check'
  $dirs = @(Get-ExmodSourceRoots) + @(@('infra') | Where-Object { Test-Path (Join-Path $RepoRoot $_) })
  Push-Location $RepoRoot
  try {
    if ($check -and (git status --porcelain -- @dirs)) {
      Write-Host "the mod/sample directories or infra/ have uncommitted changes - -Check needs a clean tree." -ForegroundColor Red
      exit 1
    }

    & (Resolve-CSharpier) format @dirs
    if ($LASTEXITCODE -ne 0) { throw "csharpier exited $LASTEXITCODE" }

    foreach ($dir in $dirs) {
      dotnet format whitespace $dir --folder
      if ($LASTEXITCODE -ne 0) { throw "dotnet format exited $LASTEXITCODE on $dir" }
    }

    if ($check) {
      if (git status --porcelain -- @dirs) {
        Write-Host "`nThese files are not formatted:" -ForegroundColor Red
        git diff --name-only -- @dirs | ForEach-Object { Write-Host "  $_" }
        Write-Host "`nRun scripts/exmod format and commit the result." -ForegroundColor Yellow
        exit 1
      }
      Write-Host "Formatting is clean." -ForegroundColor Green
    }
  } finally {
    Pop-Location
  }
}


Add-ExmodCommand -Group source -Name format -Summary 'rewrite with CSharpier, then dotnet format' -Action {
  param([string[]]$Argv) Invoke-Format $Argv
} -Detail @'
exmod format [-Check]

Formats every C# file under every mod, sample and tests directory the manifest names, and under
infra/, in two passes, and the order is load-bearing: CSharpier wraps lines but always emits Allman
braces and cannot be configured out of it, then dotnet format applies .editorconfig and puts the
braces back. The pair is idempotent; CSharpier alone afterwards would undo the brace style.

  -Check   format, then fail if git sees a change. It needs a clean tree and refuses on a dirty
           one, where every finding would be an edit of your own.

CSharpier is installed into .dotnet/tools on first use when the machine has none, the same way the
POSIX launcher bootstraps pwsh.
'@

#endregion

#region verify

# Every mod/sample this repo can verify, as folder-name key -> its build-output source directory
# (mods/<mod>/src, or samples/<Sample> directly) - the same directories Get-BuiltModDirs builds
# from. Keyed by folder name, which is also every mod's own modid here.
function Get-ExmodVerifySources {
  $out = [ordered]@{}
  foreach ($target in (Get-ExmodBuildTargets).GetEnumerator()) {
    $out[$target.Key] = Split-Path $target.Value -Parent
  }
  return $out
}

# Runs $Body with an exclusive OS-level lock on $LockPath held for its duration: a second call
# (any process, not just this one) blocks here until the first releases it, rather than racing it.
# ExlibVerify's own build is the one thing this guards - two `exmod verify` runs (two mods, two
# lanes, whatever launched them) building it at the same time hit MSBuild mid-write on the shared
# obj directory of its ExlibAssets dependency (CS0006 metadata file not found), or on a
# self-contained apphost's own libhostpolicy.so.
function Invoke-WithLock([string]$LockPath, [scriptblock]$Body) {
  New-Item -ItemType Directory -Force -Path (Split-Path $LockPath -Parent) | Out-Null
  $stream = $null
  $deadline = (Get-Date).AddMinutes(5)
  while (-not $stream) {
    try {
      $stream = [System.IO.File]::Open($LockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    } catch [System.IO.IOException] {
      if ((Get-Date) -gt $deadline) { throw "Timed out waiting for the build lock at $LockPath (another `exmod verify` still holds it?)." }
      Start-Sleep -Milliseconds 200
    }
  }
  try { & $Body } finally { $stream.Close() }
}

# Runs verify/ExlibVerify - the headless check of shipped assets (patch application, recipe
# codes, handbook lang coverage) - over one or more built mods, with no game running. Each named
# mod is checked in its own run, with every OTHER named mod's built folder passed as --mods, so a
# cross-mod patch (e.g. iiex patching into exlib's or the game's own assets) still resolves
# against its real target the way it would when both are loaded together.
function Invoke-Verify([string[]]$Argv) {
  $modsCsv = Get-Opt $Argv '-Mod' $null
  $version = Get-Opt $Argv '-Version' $CurrentGameVersion
  $configuration = Get-Opt $Argv '-Configuration' 'Debug'
  $strict = Get-Flag $Argv '-Strict'
  $json = Get-Flag $Argv '-Json'
  if (-not $GameTfms.Contains($version)) {
    throw "Unknown version '$version'. Use one of: $($GameTfms.Keys -join ', ')."
  }

  $sources = Get-ExmodVerifySources
  $names = if ($modsCsv) {
    @($modsCsv -split ',' | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
  } else {
    @($sources.Keys)
  }
  foreach ($n in $names) {
    if (-not $sources.Contains($n)) { throw "Unknown mod '$n'. Known: $($sources.Keys -join ', ')." }
  }

  # Get-BuiltModDirs already builds anything missing when it is asked for every mod; a named
  # subset gets the same on-demand build here, one at a time, so `exmod verify -Mod exlib` on a
  # fresh clone works without building iiex/siex first.
  $built = [ordered]@{}
  foreach ($n in $names) {
    $srcDir = $sources[$n]
    $dir = Join-Path $srcDir "bin/$configuration/Mods/mod"
    if (-not (Test-Path (Join-Path $dir 'modinfo.json'))) {
      $csproj = Get-ChildItem $srcDir -Filter '*.csproj' -File | Select-Object -First 1
      if (-not $csproj) { throw "No .csproj under $srcDir to build." }
      Write-Host "Building $n (not yet built) ..."
      dotnet build $csproj.FullName -c $configuration -clp:ErrorsOnly
      if ($LASTEXITCODE -ne 0) { throw "Build of $csproj failed." }
    }
    $built[$n] = $dir
  }

  $game = Resolve-GameInstall $version 'server'
  $exlibVerifyProj = Join-Path $ToolsRoot 'verify/ExlibVerify/ExlibVerify.csproj'

  # Built once, up front, under the lock: every per-mod run below shares this same build (and its
  # ExlibAssets dependency) and passes --no-build, rather than each one triggering `dotnet run`'s
  # own implicit build of it.
  Invoke-WithLock (Join-Path $ToolsRoot 'verify/ExlibVerify/.build.lock') {
    dotnet build $exlibVerifyProj -c Debug -p:GamePath=$game -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw "Build of $exlibVerifyProj failed." }
  }

  $results = @()
  foreach ($n in $names) {
    $others = @($built.Keys | Where-Object { $_ -ne $n } | ForEach-Object { $built[$_] })
    $toolArgs = @($built[$n], '--game', $game)
    if ($others) { $toolArgs += @('--mods') + $others }
    if ($strict) { $toolArgs += '--strict' }
    if ($json) { $toolArgs += '--json' }

    Write-Host "`n-- $n --"
    dotnet run --project $exlibVerifyProj --no-build -p:GamePath=$game -- @toolArgs
    $exit = $LASTEXITCODE
    $results += [pscustomobject]@{ Name = $n; Ok = ($exit -eq 0); Exit = $exit }
  }

  Write-Host ""
  Write-Host "===== Results ====="
  foreach ($r in $results) {
    $tag = if ($r.Ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("{0}  {1} (exit {2})" -f $tag, $r.Name, $r.Exit)
  }

  $failed = @($results | Where-Object { -not $_.Ok })
  if ($failed) { throw "$($failed.Count) mod(s) failed verification: $($failed.Name -join ', ')" }
  Write-Host "All $($results.Count) mod(s) verified."
}


Add-ExmodCommand -Group source -Name verify -Summary 'headless-check shipped assets, no game running' -Action {
  param([string[]]$Argv) Invoke-Verify $Argv
} -Detail @'
exmod verify [-Mod <name>[,<name>...]] [-Version <x.y>] [-Configuration Debug] [-Strict] [-Json]

Runs verify/ExlibVerify over the built mods: does every patch apply against its real target,
does every recipe/handbook code resolve, all without the game running. Builds anything not yet
built first. Each mod is checked in its own run, against a --game install, with every other named
mod's built folder passed as --mods so a cross-mod patch still resolves the way it would with both
mods loaded together.

  -Mod            one or more mods/samples to check, comma-separated (default: every mod built)
  -Version <x.y>  the game series to check against (default: 1.22)
  -Configuration  the build configuration to read (default: Debug)
  -Strict         an informational finding fails the run too, not only an error
  -Json           ExlibVerify's own --json report instead of its plain-text one
'@

#endregion

#region codes

# Regenerates one mod's `{Mod}Blocks.g.cs` code-code table from its code-first block definitions.
# There is no separate emitter tool: every mod's suite already carries the test that checks the
# table against the definitions and, with EXLIB_WRITE_BLOCKCODES=1 in the environment, writes it
# instead - [Trait("exmod", "codes")] on that test class is the one filter every mod's table
# regenerates through. Builds the mod first (so the test reads today's definitions), runs that
# test to write the table, then rebuilds so a change that no longer compiles is caught here rather
# than by the next `exmod test`.
function Invoke-Codes([string[]]$Argv) {
  $mod = if ($Argv.Count -gt 0) { $Argv[0] } else { $null }
  $mods = Get-ExmodMods
  $samples = Get-ExmodSamples
  $entry = if ($mod -and $mods.Contains($mod)) { $mods[$mod] }
  elseif ($mod -and $samples.Contains($mod)) { $samples[$mod] }
  else { $null }
  if (-not $mod -or -not $entry) {
    $known = @($mods.Keys) + @($samples.Keys)
    throw "codes needs a mod: one of $($known -join ', ')."
  }
  if (-not $entry.Tests) {
    Write-Host "$mod has no test project - nothing to regenerate."
    return
  }

  Push-Location $RepoRoot
  try {
    Write-Host "Building $mod..."
    dotnet build $entry.Project -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw "Build of $mod failed." }

    Write-Host "Regenerating $mod's block-code table..."
    $env:EXLIB_WRITE_BLOCKCODES = '1'
    try {
      dotnet test $entry.Tests --filter 'exmod=codes'
      if ($LASTEXITCODE -ne 0) { throw "Regeneration test run failed for $mod." }
    } finally {
      Remove-Item Env:\EXLIB_WRITE_BLOCKCODES -ErrorAction SilentlyContinue
    }

    Write-Host "Rebuilding $mod against the regenerated table..."
    dotnet build $entry.Project -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw "Rebuild of $mod failed after regenerating its block codes." }
  } finally {
    Pop-Location
  }
}

Add-ExmodCommand -Group source -Name codes -Summary "regenerate a mod's block-code table" -Action {
  param([string[]]$Argv) Invoke-Codes $Argv
} -Detail @'
exmod codes <exlib|iiex|siex>

Regenerates one mod's {Mod}Blocks.g.cs from its code-first block definitions. There is no separate
emitter tool: builds the mod, runs its test project with EXLIB_WRITE_BLOCKCODES=1 in the
environment and --filter "exmod=codes" (the trait every mod's *BlocksCodeTests class carries, so
one filter reaches whichever mod is asked for), then rebuilds - so a table that no longer compiles
fails here rather than in the next test run.

Takes a mod id from exmod.json's mods, or a sample id if its test project carries a test with that
trait - none of the samples do today, so `codes <sample>` reports it has nothing to regenerate.
'@

#endregion

#region check

# The gate: format, build, verify, test, in that order, stopping at the first failure. `format
# -Check` runs in a child pwsh (its own -Check fails by calling `exit`, which would otherwise take
# this whole process down with it before the summary below ever printed) so its result is read
# back the ordinary way, from $LASTEXITCODE.
function Invoke-Check([string[]]$Argv) {
  $positional = @(Get-Positional $Argv @() @('-Coverage', '-NoFormat', '-AcceptDrop'))
  $version = if ($positional.Count -gt 0) { $positional[0] } else { 'latest' }
  $coverage = Get-Flag $Argv '-Coverage'
  $noFormat = Get-Flag $Argv '-NoFormat'

  $results = [ordered]@{
    format = 'PENDING'; build = 'PENDING'; verify = 'PENDING'; test = 'PENDING'; smoke = 'PENDING'
  }
  $state = @{ Stop = $false }

  Invoke-WithBuildLock {
    Write-Step 'format'
    if ($noFormat) {
      Write-Host "Skipped: -NoFormat was given."
      $results.format = 'SKIPPED'
    }
    elseif (git -C $RepoRoot status --porcelain -- @(Get-ExmodSourceRoots) infra) {
      Write-Host ("Skipped: the mod/sample directories or infra/ have uncommitted changes - the format " +
        "gate compares against git, so on a dirty tree every finding would be one of this run's own edits.")
      $results.format = 'SKIPPED'
    }
    else {
      $pwshExe = Join-Path $PSHOME "pwsh$ExeSuffix"
      & $pwshExe -NoProfile -File (Join-Path $ToolsRoot 'exmod.ps1') -RepoRoot $RepoRoot format -Check
      if ($LASTEXITCODE -eq 0) { $results.format = 'PASS' } else { $results.format = 'FAIL'; $state.Stop = $true }
    }

    if (-not $state.Stop) {
      Write-Step 'build'
      try { Invoke-Build @($version); $results.build = 'PASS' }
      catch { Write-Host $_.Exception.Message -ForegroundColor Red; $results.build = 'FAIL'; $state.Stop = $true }
    } else { $results.build = 'SKIPPED' }

    if (-not $state.Stop) {
      Write-Step 'verify'
      try { Invoke-Verify @(); $results.verify = 'PASS' }
      catch { Write-Host $_.Exception.Message -ForegroundColor Red; $results.verify = 'FAIL'; $state.Stop = $true }
    } else { $results.verify = 'SKIPPED' }
  }

  if (-not $state.Stop) {
    Write-Step 'test'
    $testArgv = @($version)
    if ($coverage) { $testArgv += '-Coverage' }
    if (Get-Flag $Argv '-AcceptDrop') { $testArgv += '-AcceptDrop' }
    try { Invoke-Test $testArgv; $results.test = 'PASS' }
    catch { Write-Host $_.Exception.Message -ForegroundColor Red; $results.test = 'FAIL'; $state.Stop = $true }
  } else { $results.test = 'SKIPPED' }

  # Smoke loads the current series' build output (Get-BuiltModDirs), so it runs on that series
  # alone, and only when this check built it.
  if (-not $state.Stop) {
    Write-Step 'smoke'
    if ((Resolve-GameVersions $version) -notcontains $CurrentGameVersion) {
      Write-Host "Skipped: smoke runs on $CurrentGameVersion only, which this check did not build."
      $results.smoke = 'SKIPPED'
    }
    else {
      if ($version -eq 'all') { Write-Host "Smoke runs once, on ${CurrentGameVersion}: the only series it loads a build of." }
      try { Invoke-Smoke @('-Version', $CurrentGameVersion); $results.smoke = 'PASS' }
      catch { Write-Host $_.Exception.Message -ForegroundColor Red; $results.smoke = 'FAIL'; $state.Stop = $true }
    }
  } else { $results.smoke = 'SKIPPED' }

  Write-Host ''
  Write-Host '===== check summary =====' -ForegroundColor Cyan
  foreach ($step in $results.Keys) { Write-Host ("{0,-8} {1}" -f $step, $results[$step]) }

  if ($results.Values -contains 'FAIL') { throw "exmod check failed." }
  Write-Host "`nAll steps passed." -ForegroundColor Green
}


Add-ExmodCommand -Group source -Name check -Summary 'the gate: format, build, verify, test, smoke' -Action {
  param([string[]]$Argv) Invoke-Check $Argv
} -Detail @'
exmod check [latest|all|1.22|1.21|1.20] [-Coverage] [-NoFormat] [-AcceptDrop]

One command that answers "is this tree good". Runs format -Check, build, verify, test, then smoke
(a dedicated server boots the built mods and runs /exmod verify, the loaded checks among them), in
that order, stopping at the first failing step. Ends with a PASS/FAIL/SKIPPED summary of every
step and exits nonzero if any of them did.

With EXMOD_BUILD_LOCK set to a file, format, build and verify hold that file's flock as one hold, the
test build holds it again, the test lanes run with it free, and the smoke step holds it while it stages
its server and boots with it free (waiting up to EXMOD_BUILD_LOCK_WAIT seconds, default 3000, for each take). Do not also wrap the
command in `flock` on that file.

  latest      1.22 only (the default), for the build, test and smoke steps
  all         every supported series, for the build and test steps; smoke runs once, on 1.22, the
              only series whose build it loads
  1.21, 1.20  that series for the build and test steps; smoke is skipped
  -Coverage   passed through to the test step, in place of its own per-version lanes
  -AcceptDrop passed through to the test step, which records a count below the census
  -NoFormat   skip the format step outright. It is also skipped, for a different reason, when
              mods/ or infra/ already has uncommitted changes - the format gate compares against
              git, so on a dirty tree every finding would be one of your own edits, not a real one.
'@

#endregion

#region clean

# Every directory literally named bin or obj under $Roots (each absolute or repo-relative), at any
# depth, collected before any deletion happens and sorted shallowest-first - so removing a parent
# (mods/exlib/src/obj) doesn't leave a later Remove-Item call finding nothing left where one of its
# own children used to be.
function Get-ExmodCleanTargets([string[]]$Roots) {
  $found = @()
  foreach ($root in $Roots) {
    $base = if ([System.IO.Path]::IsPathRooted($root)) { $root } else { Join-Path $RepoRoot $root }
    if (-not (Test-Path $base)) { continue }
    $found += Get-ChildItem $base -Recurse -Directory -Force |
      Where-Object { $_.Name -in @('bin', 'obj') }
  }
  return @($found | Sort-Object { $_.FullName.Length })
}

# Deletes build output. Never touches .game/, .dotnet/ or .compat/: those are the expensive
# downloads `exmod provision` fetches, and `exmod provision -Force` is how they get replaced -
# not this command.
function Invoke-Clean([string[]]$Argv) {
  $deep = Get-Flag $Argv '-Deep'
  $removed = 0

  foreach ($dir in Get-ExmodCleanTargets (@(Get-ExmodSourceRoots) + @('infra', 'templates'))) {
    if (-not (Test-Path $dir.FullName)) { continue }   # a parent already removed this one
    Write-Host "Removing $($dir.FullName)"
    Remove-Item -Recurse -Force $dir.FullName
    $removed++
  }

  $extras = @((Join-Path $RepoRoot 'TestResults'))
  $extras += @(Get-ChildItem $RepoRoot -Filter 'coverage.*' -File -ErrorAction SilentlyContinue |
    ForEach-Object { $_.FullName })
  if ($deep) {
    # Never Saves/, Backups/, BackupSaves/ or Playerdata/ under .gamedata - those hold the owner's
    # worlds. Cache/ and Logs/ are the only .gamedata folders that are pure churn.
    $extras += @(
      (Join-Path $RepoRoot 'dist/Releases'),
      (Join-Path $RepoRoot 'dist/nuget'),
      (Join-Path $RepoRoot 'dist/assets'),
      (Join-Path $RepoRoot '.gamedata/Cache'),
      (Join-Path $RepoRoot '.gamedata/Logs')
    )
  }
  foreach ($path in $extras) {
    if (-not (Test-Path $path)) { continue }
    Write-Host "Removing $path"
    Remove-Item -Recurse -Force $path
    $removed++
  }

  Write-Host "`nRemoved $removed path(s)."
}


Add-ExmodCommand -Group source -Name clean -Summary 'delete build output (bin/obj, TestResults)' -Action {
  param([string[]]$Argv) Invoke-Clean $Argv
} -Detail @'
exmod clean [-Deep]

Deletes build output: every bin/ and obj/ under the manifest's mods, samples and tests entries,
and under infra/ and templates/, plus the root-level TestResults/ folder and any coverage.* file.
Never touches .game/, .dotnet/ or .compat/ - those are expensive downloads, and `exmod provision
-Force` is how they get replaced, not this command.

  -Deep   also remove dist/Releases, dist/nuget, dist/assets, and .gamedata/Cache and
          .gamedata/Logs. Never Saves/, Backups/, BackupSaves/ or Playerdata/ under .gamedata -
          those hold the owner's worlds.
'@

#endregion
