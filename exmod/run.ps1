# Running the game against this checkout: a client to play in, a dedicated server to host, a headless
# boot that proves the mods load, and the staging step all three share.
#
#   exmod client    the game client with the built mods
#   exmod server    a dedicated server with the built mods
#   exmod smoke     boot a server, verify it, stop it
#   exmod stage     copy built mods into a Mods folder
#   exmod logs      the newest client or server log

#region shared

# Where `exmod stage`'s zero-argument rung, and client/server after it, land the mods for one game
# series: the flat bin/Mods the current series has always used, or a series-suffixed sibling for a
# legacy one, so the three series never overwrite each other's staged copy.
function Get-StageDest([string]$Version) {
  if ($Version -eq $CurrentGameVersion) { return Join-Path $RepoRoot 'bin/Mods' }
  return Join-Path $RepoRoot "bin/Mods-$Version"
}

# Built mod output for one game series. Get-BuiltModDirs (exmod.ps1) already does this for the
# current series, whose build keeps a flat bin/$Configuration/Mods/mod path; a legacy series builds
# with its own TargetFramework in the path (bin/$Configuration/$tfm/Mods/mod - see
# mods/Directory.Build.props), which Get-BuiltModDirs knows nothing about, so this covers both
# uniformly and is what every run command's "no mods named" rung uses.
function Get-RunModDirs([string]$Version, [string]$Configuration, [switch]$NoBuild) {
  $tfm = $GameTfms[$Version]
  $isLegacy = $tfm -ne 'net10.0'
  $targets = Get-ExmodBuildTargets
  $sampleIds = @((Get-ExmodSamples).Keys)
  # A sample only ever targets the current series (see samples/*/*.csproj) - a legacy series buys
  # it nothing, so it drops out here the same way `exmod build` skips it.
  $mods = @($targets.Keys | Where-Object { -not ($isLegacy -and $_ -in $sampleIds) })

  $outputDir = {
    param([string]$mod)
    $srcDir = Split-Path $targets[$mod] -Parent
    if ($isLegacy) { return Join-Path $srcDir "bin/$Configuration/$tfm/Mods/mod" }
    return Join-Path $srcDir "bin/$Configuration/Mods/mod"
  }

  $missing = @($mods | Where-Object { -not (Test-Path (Join-Path (& $outputDir $_) 'modinfo.json')) })
  if ($NoBuild) {
    if ($missing) {
      throw "Not built for $Version ($Configuration): $($missing -join ', '). Drop -NoBuild, or run 'exmod build $Version' first."
    }
  } else {
    # An incremental build every time, so a staged mod is never older than its source; the build's
    # console lines go to the host and only the directories below are this function's output.
    Write-Host "Building $Version ..."
    Invoke-Build @($Version, '-Configuration', $Configuration) | Out-Host
  }

  $ownDirs = @($mods | ForEach-Object { & $outputDir $_ })
  # Runtime dependencies this repo does not build itself (see Resolve-DependencyMods, exmod.ps1) -
  # appended after the repo's own mods, the same order `provision mods` prints them in.
  $depDirs = @((Resolve-DependencyMods $Configuration) | ForEach-Object { $_.Path })
  return @($ownDirs + $depDirs)
}

# Copies mod directories into $Dest, one subfolder per mod named for its own modid (read from
# modinfo.json, never guessed from the path - a built mod's output folder is always literally named
# "mod"). Shared by stage's zero-argument rung and by client/server, so all three land on an
# identical layout.
function Publish-ModDirs([string[]]$ModDirs, [string]$Dest) {
  if (Test-Path $Dest) { Remove-Item -Recurse -Force $Dest }
  New-Item -ItemType Directory -Force -Path $Dest | Out-Null
  foreach ($m in $ModDirs) {
    $modinfo = $null
    try {
      $modinfo = Get-Content (Join-Path $m 'modinfo.json') -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    } catch { }
    if (-not $modinfo -or -not $modinfo.modid) { throw "$m/modinfo.json has no modid to stage under." }
    Copy-Item -Recurse -Force -Path $m -Destination (Join-Path $Dest $modinfo.modid)
    Write-Host "Staged '$($modinfo.modid)' from $m"
  }
  Write-Host "Staged $($ModDirs.Count) mod(s) into $Dest"
}

# The build-and-stage step client and server share: builds (unless -NoBuild) and stages every built
# mod, or the explicit -Mods folders instead, into that series' Mods folder. Returns the folder to
# pass as --addModPath.
function Publish-RunMods([string]$Version, [string]$Configuration, [string]$ModsOpt, [bool]$NoBuild) {
  $dest = Get-StageDest $Version
  $modDirs = if ($ModsOpt) { Resolve-ModDirs @($ModsOpt -split ',') }
  else { Get-RunModDirs $Version $Configuration -NoBuild:$NoBuild }
  if (-not $modDirs) { throw "No mods found to stage." }
  Publish-ModDirs $modDirs $dest
  return $dest
}

# The series a run command was given, as positional [latest|1.22|1.21|1.20] rather than smoke's
# -Version option - 'all' makes no sense for something that launches one game.
function Resolve-RunVersion([string]$Spec) {
  if ($Spec -eq 'latest') { return $CurrentGameVersion }
  if ($GameTfms.Contains($Spec)) { return $Spec }
  throw "Unknown version '$Spec'. Use latest or one of: $($GameTfms.Keys -join ', ')."
}

#endregion
#region client

# The first usable $Kind install for $Version among Get-GameInstallCandidates (exmod.ps1), or $null,
# without provisioning one - the same test Resolve-GameInstall applies, duplicated here because
# `client` must never let that helper's own auto-provisioning reach the network before -Provision
# says so. -SlotOnly looks in the user store's client slot alone, for a checkout on a network share
# whose in-tree installs cannot run from there.
function Find-UsableGameInstall([string]$Version, [string]$Kind, [switch]$SlotOnly) {
  $slug = ($Version -split '\.')[0..1] -join '.'
  $entry = if ($Kind -eq 'server') { 'VintagestoryServer.dll' } else { 'Vintagestory.dll' }
  $candidates = if ($SlotOnly) { @(Get-ClientSlot $slug) } else { Get-GameInstallCandidates $slug $Kind }
  foreach ($c in $candidates) {
    if (-not (Test-Path (Join-Path $c $entry))) { continue }
    if (Test-Path (Get-NativeMarker $c)) { return $c }
  }
  return $null
}

# A data folder the game has never written starts fullscreen. A fresh folder gets a windowed,
# vsync-off settings file, so a debug session keeps the editor in reach; the game fills in every
# other setting itself and an existing file is never touched.
function Initialize-ClientSettings([string]$DataPath) {
  $settings = Join-Path $DataPath 'clientsettings.json'
  if (Test-Path $settings) { return }
  New-Item -ItemType Directory -Force -Path $DataPath | Out-Null
  Set-Content -Path $settings -Value '{ "intSettings": { "gameWindowMode": 0, "vsyncMode": 0 } }'
}

# What `client -DryRun` prints: the program, each argument, then each environment variable the
# launch would set as NAME=value, one per line.
function Write-ClientLaunch([string]$Program, [string[]]$Arguments, [System.Collections.IDictionary]$Environment) {
  Write-Host "program: $Program"
  foreach ($a in $Arguments) { Write-Host "arg: $a" }
  foreach ($k in $Environment.Keys) { Write-Host "env: $k=$($Environment[$k])" }
}

# Prints $Message as one red line and exits 1.
function Stop-Client([string]$Message) {
  Write-Host $Message -ForegroundColor Red
  exit 1
}

# `client` from a WSL distro with interop: the Windows client's Vintagestory.exe from Windows' user
# store, on Windows' .NET runtime, on data and logs in that store's profile, with the mods built and
# staged on the Linux side and passed as their \\wsl.localhost path. Registers the high-performance
# GPU for that exe before the launch (Register-ClientGpuPreference). Stops with one line when Windows
# has no readable %LOCALAPPDATA%, no dotnet, or no client and -Provision is not given; with
# -Provision a missing client is installed by this tools checkout's provision game run in Windows'
# pwsh.exe, and a missing pwsh.exe stops it the same way. $DataOpt is a Windows path or a Linux one.
# Exits with the game's exit code; returns after printing under -DryRun.
function Invoke-WindowsClientFromWsl([string]$Version, [string]$Configuration, [string]$ModsOpt,
  [bool]$NoBuild, [string]$DataOpt, [bool]$Provision, [bool]$DryRun) {
  $store = Get-WindowsUserStore
  if (-not $store) { Stop-Client 'exmod: Windows %LOCALAPPDATA% could not be read through cmd.exe; pass -Linux to run the Linux client.' }
  $dotnetWin = Get-WindowsProgram 'dotnet'
  if (-not $dotnetWin) {
    Stop-Client "exmod: Windows has no dotnet on its PATH; install the .NET $($GameRuntimeMajors[$Version]) runtime for Windows (https://dot.net), or pass -Linux."
  }

  $slot = "$store\game\$Version"
  $slotLinux = Convert-WslPath $slot
  $usable = { (Test-Path (Join-Path $slotLinux 'Vintagestory.exe')) -and (Test-Path (Join-Path $slotLinux 'Lib/e_sqlite3.dll')) }
  if (-not (& $usable)) {
    $script = Convert-WslPath -ToWindows (Join-Path $ToolsRoot 'exmod.ps1')
    $repo = Convert-WslPath -ToWindows $RepoRoot
    $provisionArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script, '-RepoRoot', $repo,
      'provision', 'game', '-Version', $Version, '-Kind', 'client')
    if (-not $Provision -or $DryRun) {
      Stop-Client "exmod: no Windows client for $Version in $slot. Provision one with: exmod client -Provision, or on Windows: pwsh.exe -NoProfile -ExecutionPolicy Bypass -File '$script' -RepoRoot '$repo' provision game -Version $Version -Kind client"
    }
    $pwshWin = Get-WindowsProgram 'pwsh.exe'
    if (-not $pwshWin) {
      Stop-Client 'exmod: Windows has no pwsh.exe on its PATH; install PowerShell 7 for Windows (winget install Microsoft.PowerShell) to provision the Windows client.'
    }
    Write-Host "Provisioning a Windows client install for $Version ..."
    & (Convert-WslPath $pwshWin) @provisionArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (-not (& $usable)) { Stop-Client "exmod: provisioning completed but no usable Windows client was found in $slot." }
  }

  $modsDest = if ($DryRun) { Get-StageDest $Version } else { Publish-RunMods $Version $Configuration $ModsOpt $NoBuild }
  $dataWin = if (-not $DataOpt) { "$store\data\$(Get-ExmodProfile)" }
  elseif ($DataOpt -match '^[A-Za-z]:[\\/]') { $DataOpt }
  else { Convert-WslPath -ToWindows $DataOpt }
  if (-not $DryRun) { Initialize-ClientSettings (Convert-WslPath $dataWin) }

  $exe = "$slot\Vintagestory.exe"
  $program = Convert-WslPath $exe
  $gameArgs = @('--tracelog', '--dataPath', $dataWin,
    '--logPath', "$dataWin\Logs\$(Split-Path $RepoRoot -Leaf)", '--addModPath', (Convert-WslPath -ToWindows $modsDest))
  if ($DryRun) {
    Write-ClientLaunch $program $gameArgs @{}
    Register-ClientGpuPreference $exe -DryRun
    return
  }
  Register-ClientGpuPreference $exe
  Write-Step "Launching the Windows client ($Version)"
  & $program @gameArgs
  exit $LASTEXITCODE
}

# Launches the real client in the foreground with this checkout's mods loaded, passing its exit
# code straight through. Builds and stages first unless -NoBuild. Never provisions a client on its
# own - the archive is about a gigabyte - unless -Provision is given; otherwise it prints the exact
# command to fetch one and exits 1. -DryRun prints the launch (Write-ClientLaunch) and returns
# without building, staging, seeding settings, provisioning .NET or starting anything; a series
# whose runtime the system dotnet lacks prints the .dotnet muxer it would provision.
#
# In WSL with interop and without -Linux, the Windows client runs (Invoke-WindowsClientFromWsl). On
# Windows the program is the install's Vintagestory.exe, the game's apphost, with the high-performance
# GPU registered for it first (Register-ClientGpuPreference). A checkout on a WSL share
# (Get-WslShare) stages inside the distro unless -NoBuild, -DryRun or -Mods, and runs the client from
# the user store's slot on the staged folder's share path on the system .NET runtime; when that lacks
# the series' runtime it stops with one line.
function Invoke-Client([string[]]$Argv) {
  $positional = @(Get-Positional $Argv @('-Configuration', '-Mods', '-DataPath') @('-NoBuild', '-Provision', '-Software', '-DryRun', '-Linux'))
  $versionArg = if ($positional.Count -gt 0) { $positional[0] } else { 'latest' }
  $version = Resolve-RunVersion $versionArg

  $configuration = Get-Opt $Argv '-Configuration' 'Debug'
  $modsOpt = Get-Opt $Argv '-Mods' $null
  $noBuild = Get-Flag $Argv '-NoBuild'
  $dataOpt = Get-Opt $Argv '-DataPath' $null
  $provision = Get-Flag $Argv '-Provision'
  $software = Get-Flag $Argv '-Software'
  $dryRun = Get-Flag $Argv '-DryRun'
  $linux = Get-Flag $Argv '-Linux'

  if (-not $OnWindows -and -not $IsMacOS -and -not $linux) {
    if (Test-WslInterop) {
      Invoke-WindowsClientFromWsl $version $configuration $modsOpt $noBuild $dataOpt $provision $dryRun
      return
    }
    if ($env:WSL_DISTRO_NAME) {
      Write-Host 'WSL interop is off (no /proc/sys/fs/binfmt_misc/WSLInterop), so the Linux client runs.'
    }
  }

  $dataPath = if ($dataOpt) { $dataOpt } else { Get-ClientDataPath }
  if (-not $dryRun) { Initialize-ClientSettings $dataPath }
  $share = if ($OnWindows) { Get-WslShare $RepoRoot } else { $null }

  $install = Find-UsableGameInstall $version 'client' -SlotOnly:([bool]$share)
  if (-not $install) {
    $provisionCmd = "exmod provision game -Version $version -Kind client"
    if (-not $provision -or $dryRun) {
      throw "No usable client install for $version on this platform.`nProvision one with: $provisionCmd`n(or pass -Provision to fetch it here now - it is about a gigabyte)."
    }
    Write-Host "Provisioning a client install for $version ..."
    Invoke-ProvisionGame @('-Version', $version, '-Kind', 'client')
    $install = Find-UsableGameInstall $version 'client' -SlotOnly:([bool]$share)
    if (-not $install) { throw "Provisioning completed but no usable client install was found for $version." }
  }

  if ($dryRun -or ($share -and -not $modsOpt)) {
    if ($share -and -not $modsOpt -and -not $noBuild -and -not $dryRun) {
      $code = Invoke-ExmodInWsl $share.Distro $share.LinuxPath @('stage', '-Version', $version, '-Configuration', $configuration)
      if ($code -ne 0) { exit $code }
    }
    $modsDest = Get-StageDest $version
  }
  else { $modsDest = Publish-RunMods $version $configuration $modsOpt $noBuild }

  $launchEnv = [ordered]@{}
  if ($share) {
    # The share's .dotnet belongs to the distro, so nothing is provisioned into it for Windows.
    $missing = @(Get-MissingRuntimeMajors @($version))
    if ($missing.Count -gt 0) {
      Stop-Client "exmod: Windows has no .NET $($missing -join ', ') runtime; install it for Windows (https://dot.net)."
    }
  }
  # The system dotnet muxer ignores DOTNET_ROOT; the game only sees .dotnet's runtimes when both the
  # host and this variable point there, same as .vscode/launch.json sets it for the debugger. The
  # Windows apphost reads the variable alone.
  $dotnet = Resolve-DotnetHost @($version) -NoProvision:$dryRun
  $dotnetDir = Get-ExmodDotnetDir
  if ($dotnet -eq (Join-Path $dotnetDir "dotnet$ExeSuffix")) {
    $launchEnv['DOTNET_ROOT'] = $dotnetDir
  }
  if (-not $OnWindows -and -not $IsMacOS) {
    # GLFW's Wayland backend cannot place the cursor, which mouse look needs; a display name no
    # compositor answers to sends GLFW to X11, which XWayland serves.
    $launchEnv['WAYLAND_DISPLAY'] = 'none'
  }
  if ($software) {
    # Mesa's software rasterizer, for a GPU driver that hangs the game (WSLg's D3D12 layer in Mesa
    # 26.2 locks up on the first settings screen).
    $launchEnv['LIBGL_ALWAYS_SOFTWARE'] = '1'
    $launchEnv['GALLIUM_DRIVER'] = 'llvmpipe'
  }

  $gameArgs = @('--tracelog', '--dataPath', $dataPath, '--logPath', (Get-ClientLogPath $dataPath),
    '--addModPath', $modsDest)
  if ($OnWindows) { $program = [System.IO.Path]::GetFullPath((Join-Path $install 'Vintagestory.exe')) }
  else {
    $program = $dotnet
    $gameArgs = @((Join-Path $install 'Vintagestory.dll')) + $gameArgs
  }
  if ($dryRun) {
    Write-ClientLaunch $program $gameArgs $launchEnv
    if ($OnWindows) { Register-ClientGpuPreference $program -DryRun }
    return
  }
  foreach ($k in $launchEnv.Keys) { Set-Item "env:$k" $launchEnv[$k] }
  if ($OnWindows) { Register-ClientGpuPreference $program }
  Write-Step "Launching the client ($version)"
  & $program @gameArgs
  exit $LASTEXITCODE
}


Add-ExmodCommand -Group run -Name client -Summary 'build, stage and launch the client' -Action {
  param([string[]]$Argv) Invoke-Client $Argv
} -Detail @'
exmod client [latest|1.22|1.21|1.20] [-Configuration Debug] [-Mods <dir>[,...]] [-NoBuild]
             [-DataPath <path>] [-Provision] [-Software] [-Linux] [-DryRun]

Builds this checkout's mods for the series (unless -NoBuild), stages them the way `exmod stage`
would, and runs the real client in the foreground, passing its exit code through. Never downloads a
client on its own - the archive is about a gigabyte - unless -Provision is given; otherwise it
prints the exact `exmod provision game` command to run and exits 1.

The client and its data live in the user store: %LOCALAPPDATA%\exmod on Windows,
~/Library/Application Support/exmod on macOS, $XDG_DATA_HOME/exmod (default ~/.local/share/exmod) on
Linux. The client is the folder the series' override variable names (VINTAGE_STORY,
VINTAGE_STORY_121, VINTAGE_STORY_120), else the store's game/<series>, else the nearest
.game/<series>-client, .game/<series>-<platform> or .game/<series> from the checkout upward. The
data path is the store's data/<profile>, the profile being the name of the workspace folder (the
nearest one above the checkout holding exmod.workspace.json), else of the checkout's folder. Logs go
to <data path>/Logs/<checkout folder>, so two repositories sharing a profile keep separate logs.

In WSL with interop, the Windows client runs: the Vintagestory.exe in
%LOCALAPPDATA%\exmod\game\<series> on Windows' .NET runtime, data and logs in Windows' store, and the
mods built and staged in WSL, passed by their \\wsl.localhost path. -Provision installs a missing
Windows client through Windows' pwsh.exe. Without interop, or with -Linux, the Linux client runs.

On Windows and from WSL, the program is the client's Vintagestory.exe, and before it starts the
high-performance GPU is registered for it (GpuPreference=2; under
HKCU\Software\Microsoft\DirectX\UserGpuPreferences) unless a value for that exe already exists.
Without one, Windows renders the game on the GPU driving the display, an integrated one on some
machines.

On Windows, a checkout on a WSL share (\\wsl.localhost\<distro>\... or \\wsl$\<distro>\...) is
built and staged inside that distro (`exmod stage` there, unless -NoBuild, -DryRun or -Mods), and the
client runs from the store's game/<series> only, on Windows' own .NET runtime.

  -Mods       mod folder(s), or folder(s) of mod folders, instead of every built mod in the checkout
              and its resolved dependencies
  -NoBuild    skip the build step; the mods must already be built (on a WSL share: already staged)
  -DataPath   client data path (default: <store>/data/<profile>)
  -Provision  fetch a client install for this series into the store, if none is usable yet
  -Software   Mesa's software renderer, for a GPU driver that hangs the game
  -Linux      in WSL, run the Linux client instead of the Windows one
  -DryRun     print the program, its arguments and the environment it would set, one per line
              (program: , arg: , env: ), then for a Windows client the GPU preference its exe holds
              or would get (gpu: ), and exit 0 without building, staging, provisioning .NET,
              registering or launching

Without -Mods, this repo's runtime dependency mods (see `exmod provision mods`) are staged after
its own, built or fetched first if needed.
'@

#endregion
#region server

# Boots a real dedicated server in the foreground with this checkout's mods loaded, and keeps it
# running until Ctrl+C stops it. Its data path is persistent by default rather than a scratch one,
# so a world survives between runs, and unlike smoke it never sends /stop or kills the process
# itself: it just runs in the foreground and lets Ctrl+C reach it the ordinary way, which is how
# the dedicated server saves and shuts down cleanly on its own.
function Invoke-Server([string[]]$Argv) {
  $positional = @(Get-Positional $Argv @('-Port', '-Configuration', '-Mods', '-DataPath') @('-NoBuild'))
  $versionArg = if ($positional.Count -gt 0) { $positional[0] } else { 'latest' }
  $version = Resolve-RunVersion $versionArg

  $port = [int](Get-Opt $Argv '-Port' 42420)
  $configuration = Get-Opt $Argv '-Configuration' 'Debug'
  $modsOpt = Get-Opt $Argv '-Mods' $null
  $noBuild = Get-Flag $Argv '-NoBuild'
  $dataPath = Get-Opt $Argv '-DataPath' (Join-Path $RepoRoot '.gamedata/server')

  $serverDir = Resolve-SmokeServer $version
  $modsDest = Publish-RunMods $version $configuration $modsOpt $noBuild

  Write-Step "Running the dedicated server ($version) on port $port"
  & dotnet (Join-Path $serverDir 'VintagestoryServer.dll') --dataPath $dataPath --addModPath $modsDest --port $port
  exit $LASTEXITCODE
}


Add-ExmodCommand -Group run -Name server -Summary 'build, stage and run a dedicated server' -Action {
  param([string[]]$Argv) Invoke-Server $Argv
} -Detail @'
exmod server [latest|1.22|1.21|1.20] [-Port <n>] [-Configuration Debug] [-Mods <dir>[,...]]
             [-NoBuild] [-DataPath <path>]

Builds this checkout's mods for the series (unless -NoBuild), stages them the way `exmod stage`
would, and runs a real dedicated server in the foreground until Ctrl+C stops it. Unlike `smoke`,
its data path is persistent by default, so a world survives between runs, and it binds the game's
own default port instead of smoke's deliberately odd one - this is the server you actually play on,
not a throwaway boot.

  -Port       port to listen on (default: 42420, the game's own default)
  -Mods       mod folder(s), or folder(s) of mod folders, instead of every built mod in the checkout
              and its resolved dependencies
  -NoBuild    skip the build step; the mods must already be built
  -DataPath   persistent server data path (default: .gamedata/server)

Without -Mods, this repo's runtime dependency mods (see `exmod provision mods`) are staged after
its own, built or fetched first if needed.
'@

#endregion
#region smoke

# An unusual, fixed port for every smoke boot: a game the owner is actually playing on this machine
# binds the default 42420 (or whatever their own serverconfig.json says), and this must never collide
# with it.
$SmokePort = 42499

# Finds a working dedicated-server install for $version's slug: the series' override variable
# (Get-GameInstallOverride) when it names a folder holding VintagestoryServer.dll, else the nearest
# from $RepoRoot upward (".game/<slug>-server" first, since that is where Invoke-ProvisionGame redirects a server request
# away from an existing client that cannot serve it - see its header comment - then the plain
# ".game/<slug>"), provisioning one if neither is present. Returns the full path to the install
# directory.
function Resolve-SmokeServer([string]$version) {
  $slug = ($version -split '\.')[0..1] -join '.'
  $candidates = @(".game/$slug-server", ".game/$slug")
  $override = Get-GameInstallOverride $slug
  $find = {
    if ($override -and (Test-Path (Join-Path $override 'VintagestoryServer.dll'))) { return $override }
    foreach ($c in $candidates) {
      $hit = Find-ExmodAbove "$c/VintagestoryServer.dll"
      if ($hit) { return Split-Path $hit -Parent }
    }
    return $null
  }
  $found = & $find
  if ($found) { return $found }
  Write-Host "No dedicated-server install found for $version - provisioning one..."
  Invoke-ProvisionGame @('-Version', $version, '-Kind', 'server')
  $found = & $find
  if ($found) { return $found }
  throw "Provisioning completed but no VintagestoryServer.dll was found in $($candidates -join ' or ') above $RepoRoot."
}

# Boots the real dedicated server with the given mods, waits for it to come up, runs /exmod verify and
# stops it - the zero-effort rung: no test code, just "does the server load this without erroring".
#
#   -Version <x.y>     game series to smoke against (default 1.22).
#   -Mods <dir>[,...]  mod folder(s) or folder(s)-of-mod-folders to load instead of every built mod.
#   -Timeout N         seconds to wait for "Dedicated Server now running" (default 180).
#   -KeepData          don't delete the scratch dataPath/mods afterwards (for inspecting the failure).
function Invoke-Smoke([string[]]$Argv) {
  $version = Get-Opt $Argv '-Version' '1.22'
  $modsOpt = Get-Opt $Argv '-Mods' $null
  $timeout = [int](Get-Opt $Argv '-Timeout' 180)
  $keepData = Get-Flag $Argv '-KeepData'

  $serverDir = Resolve-SmokeServer $version

  $modDirs = if ($modsOpt) { Resolve-ModDirs @($modsOpt -split ',') }
  else { @(Get-BuiltModDirs) + @((Resolve-DependencyMods) | ForEach-Object { $_.Path }) }
  if (-not $modDirs) { throw "No mods found to smoke-test (nothing under mods/*/src/bin/Debug/Mods/mod, and -Mods was not given)." }

  $scratch = Join-Path $RepoRoot ".game/.smoke-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
  $scratchData = Join-Path $scratch 'data'
  $scratchMods = Join-Path $scratch 'mods'
  New-Item -ItemType Directory -Force -Path $scratchData, $scratchMods | Out-Null

  foreach ($m in $modDirs) {
    # A modinfo.json broken enough that this can't even read it is exactly the kind of mistake the
    # smoke lane exists to catch, so it is copied in under its folder name regardless and left for the
    # real mod loader to report - not failed here, which would only ever say "some folder's modinfo.json
    # doesn't parse" instead of naming the mod and the game's own error.
    $modid = (Split-Path $m -Leaf)
    try {
      $modinfo = Get-Content (Join-Path $m 'modinfo.json') -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
      if ($modinfo.modid) { $modid = $modinfo.modid }
    } catch {
      Write-Host "Warning: $m/modinfo.json did not parse ($($_.Exception.Message)) - copying it in under '$modid' anyway so the server reports it." -ForegroundColor Yellow
    }
    Copy-Item -Recurse -Force -Path $m -Destination (Join-Path $scratchMods $modid)
  }
  Write-Host "Smoke-testing $($modDirs.Count) mod(s) against Vintage Story $version at $serverDir"

  $logPath = Join-Path $scratchData 'Logs/server-main.log'
  $proc = $null
  try {
    # GLIBC_TUNABLES and MSBUILDDISABLENODEREUSE are already in $env: from the top of this script and
    # are inherited by the child unchanged; nothing extra to set here.
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = 'dotnet'
    foreach ($a in @(
        (Join-Path $serverDir 'VintagestoryServer.dll'), '--dataPath', $scratchData,
        '--addModPath', $scratchMods, '--port', $SmokePort
      )) { $psi.ArgumentList.Add($a) }
    $psi.RedirectStandardInput = $true
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = $serverDir
    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.StandardInput.AutoFlush = $true

    Write-Host "Waiting up to ${timeout}s for the server to come up..."
    $deadline = [DateTime]::UtcNow.AddSeconds($timeout)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
      if ((Test-Path $logPath) -and (Select-String -Path $logPath -Pattern 'Dedicated Server now running' -Quiet)) {
        $ready = $true
        break
      }
      if ($proc.HasExited) { break }
      Start-Sleep -Seconds 1
    }
    if (-not $ready) {
      $tail = if (Test-Path $logPath) { Get-Content $logPath -Tail 60 } else { '(no log written)' }
      throw "Server did not report ready within ${timeout}s (exited: $($proc.HasExited)).`n$($tail -join "`n")"
    }
    Write-Host "Server is up; running /exmod verify..."

    $proc.StandardInput.WriteLine('/exmod verify')
    $verifyDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $verifyDeadline) {
      if (Select-String -Path $logPath -Pattern 'check\(s\) run, \d+ error\(s\) found\.' -Quiet) { break }
      Start-Sleep -Milliseconds 500
    }

    $proc.StandardInput.WriteLine('/stop')
    if (-not $proc.WaitForExit(30000)) {
      Write-Host "Server did not exit after /stop within 30s - killing it."
      $proc.Kill($true)
      $proc.WaitForExit(10000) | Out-Null
    }

    $log = Get-Content $logPath -Raw
    Write-Host ""
    Write-Host "===== [exlib] notification lines ====="
    Select-String -Path $logPath -Pattern '\[exlib\]' | ForEach-Object { Write-Host $_.Line }
    Write-Host ""
    Write-Host "===== Error/Fatal lines ====="
    Select-String -Path $logPath -Pattern '\[Error\]|\[Fatal\]' | ForEach-Object { Write-Host $_.Line }

    $hasErrors = [bool](Select-String -Path $logPath -Pattern '\[Error\]|\[Fatal\]' -Quiet)
    $verifyMatch = [regex]::Match($log, '(\d+) check\(s\) run, (\d+) error\(s\) found\.')

    Write-Host ""
    if ($hasErrors) { throw "Smoke failed: the server log holds [Error] or [Fatal] line(s)." }
    if (-not $verifyMatch.Success) { throw "Smoke failed: /exmod verify printed no summary, so no loaded check ran." }
    $verifyErrors = [int]$verifyMatch.Groups[2].Value
    if ($verifyErrors -gt 0) { throw "Smoke failed: /exmod verify reported $verifyErrors error(s)." }
    Write-Host "Smoke passed: server booted, verified clean and stopped."
  }
  finally {
    if ($proc -and -not $proc.HasExited) {
      try { $proc.Kill($true) } catch { }
    }
    if ($proc) { $proc.Dispose() }
    if (-not $keepData) {
      Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
    }
    else {
      Write-Host "Kept scratch data at $scratch"
    }
  }
}


Add-ExmodCommand -Group run -Name smoke -Summary 'boot a server, verify it, stop it' -Action {
  param([string[]]$Argv) Invoke-Smoke $Argv
} -Detail @'
exmod smoke [-Version <x.y>] [-Mods <dir>[,<dir>...]] [-Timeout <seconds>] [-KeepData]

Boots a real dedicated server with the mods loaded, waits for it to come up, runs /exmod verify and
stops it. No test code is involved: the question is only whether a server loads this without
erroring, which is the one check that covers assets, patches and registration at once. It binds an
unusual port so it can never collide with a game you are actually playing on this machine.

  -Mods      mod folders, or folders of mod folders, instead of every built mod in the checkout and
             its resolved dependencies
  -Timeout   seconds to wait for the server to report itself running (default 180)
  -KeepData  keep the scratch data path and the staged mods, to inspect a failure

Without -Mods, this repo's runtime dependency mods (see `exmod provision mods`) are copied in after
its own built mods, resolved or fetched first if needed.
'@

#endregion
#region stage

# Copies built mods into a Mods folder for a manual playtest, or for `client`/`server` to launch
# against. With no <name>=<src> pairs, every built mod and sample in the checkout is staged (built
# first, if needed) via Get-RunModDirs; with no -Dest, the destination is the flat bin/Mods the
# current series has always used, or a series-suffixed bin/Mods-<x.y> for a legacy one. Either can
# still be given explicitly, in which case the behaviour is exactly what it always was: copy each
# named <name>=<src> pair as given, no build, no defaulting.
function Invoke-Stage([string[]]$Argv) {
  $version = Get-Opt $Argv '-Version' $CurrentGameVersion
  if (-not $GameTfms.Contains($version)) {
    throw "Unknown version '$version'. Use one of: $($GameTfms.Keys -join ', ')."
  }
  $configuration = Get-Opt $Argv '-Configuration' 'Debug'
  $dest = Get-Opt $Argv '-Dest'
  if (-not $dest) { $dest = Get-StageDest $version }
  $mods = @(Get-Positional $Argv @('-Dest', '-Version', '-Configuration') @())

  if (-not $mods) {
    Publish-ModDirs (Get-RunModDirs $version $configuration) $dest
    return
  }

  if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
  New-Item -ItemType Directory -Force -Path $dest | Out-Null

  foreach ($entry in $mods) {
    $name, $src = $entry -split '=', 2
    if (-not $src) { throw "Bad stage entry '$entry' - expected <name>=<src>." }
    if (-not (Test-Path $src)) { throw "Mod source not found: $src" }
    Copy-Item -Recurse -Force -Path $src -Destination (Join-Path $dest $name)
    Write-Host "Staged '$name' from $src"
  }
  Write-Host "Staged $($mods.Count) mod(s) into $dest"
}


Add-ExmodCommand -Group run -Name stage -Summary 'copy built mods into a Mods folder' -Action {
  param([string[]]$Argv) Invoke-Stage $Argv
} -Detail @'
exmod stage [-Dest <path>] [-Version <x.y>] [-Configuration Debug] [<name>=<src> ...]

Copies built mod output into one Mods folder, a subfolder per mod. This is what `exmod client` and
`exmod server` use to lay out the mods they launch, and what the VS Code launch-prep tasks call.

With no <name>=<src> pairs, every built mod and sample in the checkout is staged - built first if
it isn't yet - under its own modid, followed by this repo's runtime dependency mods (see `exmod
provision mods`). With no -Dest, the destination is bin/Mods for the current series, or
bin/Mods-<x.y> for a legacy one named with -Version.

Either can still be given explicitly: -Dest <path> <name>=<src> [<name>=<src> ...] copies exactly
those sources under those names, unbuilt, into exactly that folder - the same as it always has.
'@

#endregion
#region logs

# Prints the tail of one client or server log. Fails with the list of logs that do exist when the
# one asked for is not among them - more useful than "file not found" when, say, a fresh server
# that has never crashed is asked for its crash log. In WSL with interop and without -Linux, the
# client's default data path is Windows' store, the one `client` runs the Windows client on, read
# through its /mnt path.
function Invoke-Logs([string[]]$Argv) {
  $positional = @(Get-Positional $Argv @('-Kind', '-Lines', '-DataPath') @('-Follow', '-Linux'))
  $target = if ($positional.Count -gt 0) { $positional[0] } else { 'client' }
  if ($target -notin @('client', 'server')) { throw "logs needs 'client' or 'server', got '$target'." }

  $kind = Get-Opt $Argv '-Kind' 'main'
  $lines = [int](Get-Opt $Argv '-Lines' 200)
  $follow = Get-Flag $Argv '-Follow'
  $dataPath = Get-Opt $Argv '-DataPath' $null
  if (-not $dataPath) {
    $dataPath = if ($target -eq 'server') { Join-Path $RepoRoot '.gamedata/server' }
    elseif (-not (Get-Flag $Argv '-Linux') -and (Test-WslInterop)) {
      $store = Get-WindowsUserStore
      if (-not $store) { throw 'Windows %LOCALAPPDATA% could not be read through cmd.exe; pass -Linux for the Linux client''s logs.' }
      Convert-WslPath "$store\data\$(Get-ExmodProfile)"
    }
    else { Get-ClientDataPath }
  }
  Initialize-ClientSettings $dataPath

  # A client run logs into its repository's own folder under Logs; a data path the client last ran
  # in without one still has its logs directly in Logs.
  $logsDir = Join-Path $dataPath 'Logs'
  if ($target -eq 'client') {
    $repoLogs = Get-ClientLogPath $dataPath
    if (Test-Path $repoLogs) { $logsDir = $repoLogs }
  }
  $logPath = Join-Path $logsDir "$target-$kind.log"

  if (-not (Test-Path $logPath)) {
    $existing = if (Test-Path $logsDir) {
      @(Get-ChildItem $logsDir -Filter "$target-*.log" -File | ForEach-Object { $_.Name })
    } else { @() }
    $have = if ($existing) { $existing -join ', ' } else { '(none - the data path has no Logs folder yet)' }
    throw "No $target-$kind.log under $logsDir. Logs that do exist: $have"
  }

  Write-Host "Reading $logPath"
  Get-Content -Path $logPath -Tail $lines -Wait:$follow
}


Add-ExmodCommand -Group run -Name logs -Summary 'tail a client or server log' -Action {
  param([string[]]$Argv) Invoke-Logs $Argv
} -Detail @'
exmod logs [client|server] [-Kind main|debug|audit|chat|crash|build] [-Lines <n>] [-Follow]
           [-DataPath <path>] [-Linux]

Prints the tail of one log. Client logs live at <dataPath>/Logs/<checkout folder>/client-<kind>.log,
or <dataPath>/Logs/client-<kind>.log when that folder does not exist; the default data path is the
one `exmod client` uses, <store>/data/<profile> (see `exmod help client`), which in WSL with interop
is Windows' store unless -Linux is given. Server logs live at
<dataPath>/Logs/server-<kind>.log (default: .gamedata/server, the same default `exmod server` uses).
Fails with the list of logs that do exist when the one asked for is not among them.

  -Kind      main (the default), debug, audit, chat, crash, or build
  -Lines     lines to print from the end (default: 200)
  -Follow    keep tailing it live, like tail -f
  -DataPath  data path to read from instead of the default for client/server
  -Linux     in WSL, read the Linux client's logs instead of the Windows one's
'@

#endregion
