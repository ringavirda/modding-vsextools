# Running the game against this checkout: a client to play in, a dedicated server to host, a headless
# boot that proves the mods load, and the staging step all three share.
#
#   exmod client    the game client with the built mods
#   exmod server    a dedicated server with the built mods
#   exmod smoke     boot a server, verify it, stop it
#   exmod scenario  boot a server, run live scenarios on it, stop it
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

# The pwsh.exe arguments that run this tools checkout's `provision game -Version $Version -Kind
# client` on Windows against this repository, the script and the repository named by their Windows
# paths; -Force is passed on. WSL only.
function Get-WindowsClientProvisionArgs([string]$Version, [switch]$Force) {
  $provisionArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Convert-WslPath -ToWindows (Join-Path $ToolsRoot 'exmod.ps1')),
    '-RepoRoot', (Convert-WslPath -ToWindows $RepoRoot), 'provision', 'game', '-Version', $Version, '-Kind', 'client')
  if ($Force) { $provisionArgs += '-Force' }
  return $provisionArgs
}

# Installs the Windows client for $Version into Windows' user store by running
# Get-WindowsClientProvisionArgs in Windows' pwsh.exe, its output passed to the host. Stops with one
# line when Windows has no pwsh.exe, and exits with the provision's exit code when it fails. WSL only.
function Invoke-WindowsClientProvision([string]$Version, [switch]$Force) {
  $pwshWin = Get-WindowsProgram 'pwsh.exe'
  if (-not $pwshWin) {
    Stop-Client 'exmod: Windows has no pwsh.exe on its PATH; install PowerShell 7 for Windows (winget install Microsoft.PowerShell) to provision the Windows client.'
  }
  Write-Host "Provisioning a Windows client install for $Version ..."
  & (Convert-WslPath $pwshWin) @(Get-WindowsClientProvisionArgs $Version -Force:$Force) | Out-Host
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# `client` from a WSL distro with interop: the Windows client's Vintagestory.exe from Windows' user
# store, on Windows' .NET runtime, on data and logs in that store's data/default, with the mods
# built and staged on the Linux side and passed as their \\wsl.localhost path. Registers the
# high-performance GPU for that exe before the launch (Register-ClientGpuPreference). Stops with one
# line when Windows has no readable %LOCALAPPDATA%, no dotnet, or no client and -Provision is not
# given; with -Provision a missing client is installed by Invoke-WindowsClientProvision, and a
# missing pwsh.exe stops it the same way. $DataOpt is a Windows path or a Linux one. Exits with the
# game's exit code; returns after printing under -DryRun.
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
    if (-not $Provision -or $DryRun) {
      $script = Convert-WslPath -ToWindows (Join-Path $ToolsRoot 'exmod.ps1')
      $repo = Convert-WslPath -ToWindows $RepoRoot
      Stop-Client "exmod: no Windows client for $Version in $slot. Provision one with: exmod client -Provision, or on Windows: pwsh.exe -NoProfile -ExecutionPolicy Bypass -File '$script' -RepoRoot '$repo' provision game -Version $Version -Kind client"
    }
    Invoke-WindowsClientProvision $Version
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
# Windows the program is the install's Vintagestory.exe, the game's apphost, with the
# high-performance GPU registered for it first (Register-ClientGpuPreference). A checkout on a WSL
# share (Get-WslShare) stages inside the distro unless -NoBuild, -DryRun or -Mods, and runs the
# client from the user store's slot on the staged folder's share path on the system .NET runtime;
# when that lacks the series' runtime it stops with one line.
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
~/Library/Application Support/exmod on macOS, ~/.local/share/exmod on Linux. The client is the
folder the series' override variable names (VINTAGE_STORY, VINTAGE_STORY_121, VINTAGE_STORY_120),
else the store's game/<series>, else the nearest .game/<series>-client, .game/<series>-<platform>
or .game/<series> from the checkout upward. The data path is the store's data/default, shared by
every checkout and every generated launch configuration. Logs go to <data path>/Logs/<checkout
folder>, so two repositories keep separate logs.

In WSL with interop, the Windows client runs: the Vintagestory.exe in
%LOCALAPPDATA%\exmod\game\<series> on Windows' .NET runtime, data and logs in Windows' store, and
the mods built and staged in WSL, passed by their \\wsl.localhost path. -Provision installs a
missing Windows client through Windows' pwsh.exe. Without interop, or with -Linux, the Linux client
runs.

On Windows and from WSL, the program is the client's Vintagestory.exe, and before it starts the
high-performance GPU is registered for it under HKCU\Software\Microsoft\DirectX\UserGpuPreferences:
an exe with no value there gets GpuPreference=2;, a text value without a GpuPreference= entry gets
it appended, and a value that has one, or is not text, is left as it is. Without a preference,
Windows renders the game on the GPU driving the display, an integrated one on some machines.

On Windows, a checkout on a WSL share (\\wsl.localhost\<distro>\... or \\wsl$\<distro>\...) is
built and staged inside that distro (`exmod stage` there, unless -NoBuild, -DryRun or -Mods), and the
client runs from the store's game/<series> only, on Windows' own .NET runtime.

  -Mods       mod folder(s), or folder(s) of mod folders, instead of every built mod in the checkout
              and its resolved dependencies
  -NoBuild    skip the build step; the mods must already be built (on a WSL share: already staged)
  -DataPath   client data path (default: <store>/data/default)
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

# The mods every scratch server loads when none are named: every built mod and sample in the checkout,
# then the runtime dependency mods (see Resolve-DependencyMods).
function Get-ScratchDefaultModDirs {
  return @(Get-BuiltModDirs) + @((Resolve-DependencyMods) | ForEach-Object { $_.Path })
}

# Makes a scratch server's folder, .game/.<Tag>-<guid>, and fills it: each mod folder copied in under its
# modid (read from modinfo.json; a file that does not parse is copied under its folder name so the server
# reports it), each $DataCopies key copied into the data path at the relative path it maps to, and
# serverconfig.json written when $ServerConfig is given. Starts nothing; returns the handle the other
# scratch-server functions take.
function New-ScratchServer([string]$Tag, [string[]]$ModDirs, [System.Collections.IDictionary]$DataCopies = @{}, [System.Collections.IDictionary]$ServerConfig = $null) {
  $root = Join-Path $RepoRoot ".game/.$Tag-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
  $server = [pscustomobject]@{
    Root = $root
    Data = Join-Path $root 'data'
    Mods = Join-Path $root 'mods'
    Log  = Join-Path $root 'data/Logs/server-main.log'
    Proc = $null
  }
  New-Item -ItemType Directory -Force -Path $server.Data, $server.Mods | Out-Null

  foreach ($m in $ModDirs) {
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
    Copy-Item -Recurse -Force -Path $m -Destination (Join-Path $server.Mods $modid)
  }

  foreach ($src in $DataCopies.Keys) {
    $dest = Join-Path $server.Data $DataCopies[$src]
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -Recurse -Force -Path (Join-Path $src '*') -Destination $dest
  }
  if ($ServerConfig) {
    $ServerConfig | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $server.Data 'serverconfig.json')
  }
  return $server
}

# Boots the dedicated server in $ServerDir on $Port against a scratch server's folders. With $Quiet the
# server's console output is discarded, the log file being what callers read.
function Start-ScratchServer($Server, [string]$ServerDir, [int]$Port, [bool]$Quiet = $false) {
  # GLIBC_TUNABLES and MSBUILDDISABLENODEREUSE are already in $env: from the top of this script and
  # are inherited by the child unchanged; nothing extra to set here.
  $psi = [System.Diagnostics.ProcessStartInfo]::new()
  $psi.FileName = 'dotnet'
  foreach ($a in @(
      (Join-Path $ServerDir 'VintagestoryServer.dll'), '--dataPath', $Server.Data,
      '--addModPath', $Server.Mods, '--port', $Port
    )) { $psi.ArgumentList.Add($a) }
  $psi.RedirectStandardInput = $true
  $psi.RedirectStandardOutput = $Quiet
  $psi.UseShellExecute = $false
  $psi.WorkingDirectory = $ServerDir
  $Server.Proc = [System.Diagnostics.Process]::Start($psi)
  if ($Quiet) { $null = $Server.Proc.StandardOutput.BaseStream.CopyToAsync([System.IO.Stream]::Null) }
  $Server.Proc.StandardInput.AutoFlush = $true
}

# Waits up to $Timeout seconds for the started server to log that it is running; throws with the log's
# tail when it does not.
function Wait-ScratchServer($Server, [int]$Timeout) {
  Write-Host "Waiting up to ${Timeout}s for the server to come up..."
  $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)
  $ready = $false
  while ([DateTime]::UtcNow -lt $deadline) {
    if ((Test-Path $Server.Log) -and (Select-String -Path $Server.Log -Pattern 'Dedicated Server now running' -Quiet)) {
      $ready = $true
      break
    }
    if ($Server.Proc.HasExited) { break }
    Start-Sleep -Seconds 1
  }
  if (-not $ready) {
    $tail = if (Test-Path $Server.Log) { Get-Content $Server.Log -Tail 60 } else { '(no log written)' }
    throw "Server did not report ready within ${Timeout}s (exited: $($Server.Proc.HasExited)).`n$($tail -join "`n")"
  }
}

# Sends /stop and waits for the server to exit, killing it after 30 s.
function Stop-ScratchServer($Server) {
  $Server.Proc.StandardInput.WriteLine('/stop')
  if (-not $Server.Proc.WaitForExit(30000)) {
    Write-Host "Server did not exit after /stop within 30s - killing it."
    $Server.Proc.Kill($true)
    $Server.Proc.WaitForExit(10000) | Out-Null
  }
}

# Kills the server if it still runs, disposes it, and removes the scratch folder unless $KeepData, which
# prints where it is instead.
function Remove-ScratchServer($Server, [bool]$KeepData) {
  if ($Server.Proc -and -not $Server.Proc.HasExited) {
    try { $Server.Proc.Kill($true) } catch { }
  }
  if ($Server.Proc) { $Server.Proc.Dispose() }
  if (-not $KeepData) {
    Remove-Item -Recurse -Force $Server.Root -ErrorAction SilentlyContinue
  }
  else {
    Write-Host "Kept scratch data at $($Server.Root)"
  }
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

  $modDirs = if ($modsOpt) { Resolve-ModDirs @($modsOpt -split ',') } else { Get-ScratchDefaultModDirs }
  if (-not $modDirs) { throw "No mods found to smoke-test (nothing under mods/*/src/bin/Debug/Mods/mod, and -Mods was not given)." }
  Write-Host "Smoke-testing $($modDirs.Count) mod(s) against Vintage Story $version at $serverDir"

  $server = $null
  try {
    $server = New-ScratchServer 'smoke' $modDirs
    Start-ScratchServer $server $serverDir $SmokePort
    Wait-ScratchServer $server $timeout
    Write-Host "Server is up; running /exmod verify..."

    $server.Proc.StandardInput.WriteLine('/exmod verify')
    $verifyDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $verifyDeadline) {
      if (Select-String -Path $server.Log -Pattern 'check\(s\) run, \d+ error\(s\) found\.' -Quiet) { break }
      Start-Sleep -Milliseconds 500
    }

    Stop-ScratchServer $server

    $log = Get-Content $server.Log -Raw
    Write-Host ""
    Write-Host "===== [exlib] notification lines ====="
    Select-String -Path $server.Log -Pattern '\[exlib\]' | ForEach-Object { Write-Host $_.Line }
    Write-Host ""
    Write-Host "===== Error/Fatal lines ====="
    Select-String -Path $server.Log -Pattern '\[Error\]|\[Fatal\]' | ForEach-Object { Write-Host $_.Line }

    $hasErrors = [bool](Select-String -Path $server.Log -Pattern '\[Error\]|\[Fatal\]' -Quiet)
    $verifyMatch = [regex]::Match($log, '(\d+) check\(s\) run, (\d+) error\(s\) found\.')

    Write-Host ""
    if ($hasErrors) { throw "Smoke failed: the server log holds [Error] or [Fatal] line(s)." }
    if (-not $verifyMatch.Success) { throw "Smoke failed: /exmod verify printed no summary, so no loaded check ran." }
    $verifyErrors = [int]$verifyMatch.Groups[2].Value
    if ($verifyErrors -gt 0) { throw "Smoke failed: /exmod verify reported $verifyErrors error(s)." }
    Write-Host "Smoke passed: server booted, verified clean and stopped."
  }
  finally {
    if ($server) { Remove-ScratchServer $server $keepData }
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
#region scenario

# The port `exmod scenario` binds unless -Port says otherwise, and the seconds it waits for the server
# to come up. Two runs on one machine pass different -Port values.
$ScenarioPort = 42498
$ScenarioBootSeconds = 180

# The modid a checkout's tests/live folder is filed under: the one mod this checkout builds for its tests.
$ScenarioMod = 'exlib'

# The options `exmod scenario` takes: Names (as given, possibly none), Version (the current series only,
# the live mod being built for it), Port (the first port; the shards bind the ports from it upward, skipping
# smoke's), Timeout (seconds to wait for the count line, or $null for the run's budget plus 60), Shards
# (servers to boot, 1 or more), ShardsAsked (whether -Shards was given), Mods (extra mod folders, as given),
# Verbose (print every step line of passing cases too), Keep (leave the server up and relay the terminal to
# it) and KeepData (keep each scratch data path).
function Get-ScenarioOptions([string[]]$Argv) {
  $names = @(Get-Positional $Argv @('-Version', '-Port', '-Timeout', '-Shards', '-Mods') @('-Verbose', '-Keep', '-KeepData'))
  $unknown = @($names | Where-Object { $_ -like '-*' })
  if ($unknown) { throw "Unknown option $($unknown[0]) for exmod scenario." }
  $version = Get-Opt $Argv '-Version' $CurrentGameVersion
  if ($version -ne $CurrentGameVersion) {
    throw "exmod scenario runs on $CurrentGameVersion only: the live mod is built for it, and $version was asked."
  }
  $timeout = Get-Opt $Argv '-Timeout' $null
  $shardsGiven = Get-Opt $Argv '-Shards' $null
  $shards = if ($null -eq $shardsGiven) { 1 } else { [int]$shardsGiven }
  if ($shards -lt 1) { throw "-Shards must be 1 or more, and $shards was asked." }
  $mods = Get-Opt $Argv '-Mods' $null
  return [pscustomobject]@{
    Names    = $names
    Version  = $version
    Port     = [int](Get-Opt $Argv '-Port' $ScenarioPort)
    Verbose  = ((Get-Flag $Argv '-Verbose') -or ($VerbosePreference -eq 'Continue'))
    Timeout  = if ($null -eq $timeout) { $null } else { [int]$timeout }
    Shards   = $shards
    ShardsAsked = ($null -ne $shardsGiven)
    Mods     = if ($null -eq $mods) { @() } else { @($mods -split ',') }
    Keep     = (Get-Flag $Argv '-Keep')
    KeepData = (Get-Flag $Argv '-KeepData')
  }
}

# Deals the names out over $Count shards in turn: shard i takes names i, i+$Count, and so on. Returns one
# array of names per shard, in shard order; a shard past the last name gets an empty array.
function Split-ScenarioShards([string[]]$Names, [int]$Count) {
  $groups = @()
  for ($i = 0; $i -lt $Count; $i++) {
    $groups += ,@(for ($j = $i; $j -lt $Names.Count; $j += $Count) { $Names[$j] })
  }
  return ,$groups
}

# The ports for $Count shards: from $First upward, leaving out smoke's port so a smoke run and a scenario
# run share the machine.
function Get-ScenarioPorts([int]$First, [int]$Count) {
  $ports = @()
  for ($port = $First; $ports.Count -lt $Count; $port++) {
    if ($port -ne $SmokePort) { $ports += $port }
  }
  return $ports
}

# The mod folders a scenario boot stages: the defaults, the live mod, then the -Mods folders as given.
function Get-ScenarioModDirs([string[]]$Defaults, [string]$LiveDir, [string[]]$Extra) {
  return @($Defaults) + @($LiveDir) + @(Resolve-ModDirs $Extra)
}

# The modid of the mod at $Path: read from the modinfo.json beside its project (<path>/src, else <path>).
function Get-ScenarioModId([string]$Path) {
  foreach ($dir in @((Join-Path $Path 'src'), $Path)) {
    $file = Join-Path $dir 'modinfo.json'
    if (Test-Path $file) { return (Get-Content $file -Raw | ConvertFrom-Json).modid }
  }
  throw "No modinfo.json under $Path to read the modid from."
}

# The scenario folders this checkout stages, in order, as ModId and Dir (Dir is $null for a mod without a
# tests/live). A checkout whose manifest names a live mod is the framework's own: its tests/live is filed
# under $ScenarioMod. Any other checkout stages each manifest mod's tests/live under that mod's modid.
function Get-ScenarioSources {
  if ((Get-ExmodManifest).PSObject.Properties['live']) {
    return @([pscustomobject]@{ ModId = $ScenarioMod; Dir = Join-Path $RepoRoot 'tests/live' })
  }
  return @((Get-ExmodMods).GetEnumerator() | ForEach-Object {
      $dir = Join-Path $_.Value.Path 'tests/live'
      [pscustomobject]@{ ModId = Get-ScenarioModId $_.Value.Path; Dir = if (Test-Path $dir) { $dir } else { $null } }
    })
}

# Every scenario file under a source's tests/live, as <modid>/<path without .json>: the factory lines and
# setups in their subfolders included, the schematics folder, generic.json and coverage-allow.json not.
function Get-ScenarioNames($Sources) {
  return @($Sources | Where-Object { $_.Dir } | ForEach-Object {
      $modId = $_.ModId
      $dir = (Resolve-Path $_.Dir).ProviderPath
      Get-ChildItem $dir -Filter '*.json' -File -Recurse | ForEach-Object {
        [IO.Path]::GetRelativePath($dir, $_.FullName).Replace('\', '/') -replace '\.json$', ''
      } | Where-Object { $_ -notlike 'schematics/*' -and $_ -notin @('generic', 'coverage-allow') } |
        Sort-Object | ForEach-Object { "$modId/$_" }
    })
}

# A bare stem is filed under $DefaultMod, the repository's first mod; a name with a slash is taken as given.
function Expand-ScenarioNames([string[]]$Names, [string]$DefaultMod = $ScenarioMod) {
  return @($Names | ForEach-Object { if ($_ -like '*/*') { $_ } else { "$DefaultMod/$_" } })
}

# The stdin line that starts a run of the named scenarios; `keep` leaves the plots standing.
function Get-ScenarioCommand([string[]]$Names, [bool]$Keep = $false) {
  $line = "/exmod scenario run $($Names -join ' ')"
  if ($Keep) { $line += ' keep' }
  return $line
}

# The lines -Keep prints for a server left up: its port, its data path and how to join.
function Get-ScenarioKeepLines($Server, [int]$Port) {
  return @("Server kept up on port $Port, data at $($Server.Data)",
    "Join: exmod client, Multiplayer, localhost:$Port")
}

# Writes each line read from $In to every server's console until $In ends (Ctrl+C or a closed stdin).
function Send-ScenarioRelay($Servers, [System.IO.TextReader]$In) {
  while ($null -ne ($line = $In.ReadLine())) {
    foreach ($server in $Servers) {
      if (-not $server.Proc.HasExited) {
        $server.Proc.StandardInput.WriteLine($line)
        $server.Proc.StandardInput.Flush()
      }
    }
  }
}

# The file the runner saves a failed case's schematic to, under the data path's WorldEdit folder.
function Get-ScenarioSchematicName([string]$Name) {
  return "scenario-$($Name -replace '/', '-').json"
}

# The exlib checkout a checkout with no `live` key builds from: $env:ExlibRoot, else the exlib beside the
# nearest ancestor's Directory.Build.props (the workspace root, as the props default ExlibRoot), else the
# sibling exlib.
function Get-ScenarioExlibRoot {
  if ($env:ExlibRoot) { return $env:ExlibRoot }
  $dir = Split-Path $RepoRoot -Parent
  while ($dir) {
    if ((Test-Path (Join-Path $dir 'Directory.Build.props')) -and (Test-Path (Join-Path $dir 'exlib'))) { return Join-Path $dir 'exlib' }
    $dir = Split-Path $dir -Parent
  }
  return Join-Path $RepoRoot '../exlib'
}

# The live mod's project: the manifest's `live` path, resolved the way a mod's project is (Get-ExmodMods).
# A checkout with no `live` key builds the live mod from exlib's source, found by Get-ScenarioExlibRoot.
function Get-ExmodLiveProject {
  $manifest = Get-ExmodManifest
  if (-not $manifest.PSObject.Properties['live'] -or -not $manifest.live) {
    $path = Join-Path (Get-ScenarioExlibRoot) 'src/ExpandedLib.Testing.Live'
    if (-not (Test-Path $path)) {
      throw "exmod scenario builds the live mod from exlib's source and found none at ${path}: set ExlibRoot or put an exlib checkout beside this one."
    }
    return Find-SingleCsproj (Resolve-Path $path).ProviderPath 'live'
  }
  $path = Resolve-ManifestPath 'live.path' $manifest.live.path
  return Find-SingleCsproj (Get-ModProjectDir $path) 'live'
}

# Builds the exlib mod from the checkout the live mod is built from and returns its output folder; $null
# when the manifest names its own live mod, whose checkout is exlib itself.
function Build-ScenarioExlibMod {
  if ((Get-ExmodManifest).PSObject.Properties['live']) { return $null }
  $project = Find-SingleCsproj (Join-Path (Resolve-Path (Get-ScenarioExlibRoot)).ProviderPath 'src/ExpandedLib') 'exlib'
  $dotnet = Resolve-DotnetHost @($CurrentGameVersion)
  Write-Host "Building the exlib mod ..."
  & $dotnet build $project -f $GameTfms[$CurrentGameVersion] -c Debug -clp:ErrorsOnly | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "Build of $project failed." }
  $out = Join-Path (Split-Path $project -Parent) 'bin/Debug/Mods/mod'
  if (-not (Test-Path (Join-Path $out 'modinfo.json'))) { throw "The exlib mod built, but $out holds no modinfo.json." }
  return $out
}

# The mods a scenario server loads without being asked: the built mods, the dependency mods and, when
# $ExlibDir is given, that folder in place of the dependency resolution's exlib.
function Get-ScenarioDefaultModDirs([string]$ExlibDir) {
  if (-not $ExlibDir) { return @(Get-ScratchDefaultModDirs) }
  return @(Get-BuiltModDirs) + @((Resolve-DependencyMods) | Where-Object { $_.Id -ne 'exlib' } | ForEach-Object { $_.Path }) + @($ExlibDir)
}

# Builds the live mod for the current series and returns its output folder.
function Build-ScenarioLiveMod {
  $project = Get-ExmodLiveProject
  $dotnet = Resolve-DotnetHost @($CurrentGameVersion)
  Write-Host "Building the live mod ..."
  & $dotnet build $project -f $GameTfms[$CurrentGameVersion] -c Debug -clp:ErrorsOnly | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "Build of $project failed." }
  $out = Join-Path (Split-Path $project -Parent) 'bin/Debug/Mods/mod'
  if (-not (Test-Path (Join-Path $out 'modinfo.json'))) { throw "The live mod built, but $out holds no modinfo.json." }
  return $out
}

# The serverconfig.json a scenario boot writes: loopback only, a superflat world, a creative player who
# can fly to a plot, and time that keeps moving while no client is in the world. With $Keep the default
# role is the game's admin, so a joining client can run /exmod scenario, /tp and /gamemode.
function Get-ScenarioServerConfig([bool]$Keep = $false) {
  # A file without Roles names no group for DefaultRoleCode and the server refuses to boot, so the
  # default role is written with the game's own suplayer definition.
  $config = [ordered]@{
    Ip                = '127.0.0.1'
    AdvertiseServer   = $false
    PassTimeWhenEmpty = $true
    WhitelistMode     = 'Off'
    DefaultRoleCode   = 'suplayer'
    Roles             = @([ordered]@{
        Code              = 'suplayer'
        PrivilegeLevel    = 0
        Name              = 'Survival Player'
        Description       = 'Can use/place/break blocks in unprotected areas (priv level 0), create/manage player groups and chat. Can claim an area of up to 8 chunks.'
        DefaultSpawn      = $null
        ForcedSpawn       = $null
        Privileges        = @('controlplayergroups', 'manageplayergroups', 'chat', 'areamodify', 'build', 'useblock', 'attackcreatures', 'attackplayers', 'selfkill')
        RuntimePrivileges = @()
        DefaultGameMode   = 1
        Color             = 'White'
        LandClaimAllowance = 262144
        LandClaimMinSize  = [ordered]@{ X = 5; Y = 5; Z = 5 }
        LandClaimMaxAreas = 3
        AutoGrant         = $false
      })
    WorldConfig       = [ordered]@{
      WorldType          = 'superflat'
      PlayStyle          = 'surviveandbuild'
      WorldConfiguration = [ordered]@{
        gameMode           = 'creative'
        snowAccum          = $true
        temporalStorms     = 'off'
        temporalStability  = $false
      }
    }
  }
  if ($Keep) {
    $config.DefaultRoleCode = 'admin'
    $config.Roles += [ordered]@{
      Code               = 'admin'
      PrivilegeLevel     = 99999
      Name               = 'Admin'
      Description        = 'Has all privileges, including giving other players admin status.'
      DefaultSpawn       = $null
      ForcedSpawn        = $null
      Privileges         = @('build', 'useblock', 'buildblockseverywhere', 'useblockseverywhere', 'attackplayers', 'attackcreatures', 'freemove', 'gamemode', 'pickingrange', 'chat', 'kick', 'ban', 'whitelist', 'setwelcome', 'announce', 'readlists', 'give', 'areamodify', 'setspawn', 'controlserver', 'tp', 'time', 'grantrevoke', 'root', 'commandplayer', 'controlplayergroups', 'manageplayergroups', 'selfkill', 'manageotherplayergroups', 'worldedit')
      RuntimePrivileges  = @()
      DefaultGameMode    = 1
      Color              = 'LightBlue'
      LandClaimAllowance = 2147483647
      LandClaimMinSize   = [ordered]@{ X = 5; Y = 5; Z = 5 }
      LandClaimMaxAreas  = 99999
      AutoGrant          = $true
    }
  }
  return $config
}

# The read of one scenario run from the server log's lines, and the rules `exmod scenario` exits by
# (scenarios.md, `exmod scenario` step 7). Shown: the lines to print, in log order - the run and count
# lines, the census, coverage and uncovered lines, each passing case's pass line, and each failed or
# errored case with every line it logged - plus every [Error] or [Fatal] line outside a case. With $Steps
# a passing case shows every line it logged too. Pending: the lines of a case the log has not closed.
# Problems: one reason per rule the run breaks, none when it passes. Budget: the run line's budget in
# seconds, $null before that line. FailedNames: the cases that failed, errored or never finished.
function Get-ScenarioReport([string[]]$Lines, [bool]$Steps = $false) {
  $shown = [System.Collections.Generic.List[string]]::new()
  $problems = [System.Collections.Generic.List[string]]::new()
  $failedNames = [System.Collections.Generic.List[string]]::new()
  $block = $null
  $budget = $null
  $done = $null

  foreach ($line in $Lines) {
    if ($null -ne $block -and $line -match '\[scenario\] (case \S+ plot |done: )') {
      $shown.AddRange($block.Lines)
      $problems.Add("case $($block.Name) did not finish")
      $failedNames.Add($block.Name)
      $block = $null
    }
    if ($null -ne $block -and $line -notmatch '\[scenario\] (pass|fail|error) ') {
      $block.Lines.Add($line)
      continue
    }
    if ($line -match '\[scenario\] case (\S+) plot ') {
      $block = @{ Name = $Matches[1]; Lines = [System.Collections.Generic.List[string]]::new() }
      $block.Lines.Add($line)
      continue
    }
    if ($line -match '\[scenario\] pass ') {
      if ($Steps -and $null -ne $block) { $shown.AddRange($block.Lines) }
      $block = $null
      $shown.Add($line)
      continue
    }
    if ($line -match '\[scenario\] (fail|error) ([^:\s]+)') {
      if ($null -ne $block) { $shown.AddRange($block.Lines); $block = $null }
      $shown.Add($line)
      $problems.Add($line)
      $failedNames.Add($Matches[2])
      continue
    }
    if ($line -match '\[Error\]|\[Fatal\]') {
      $shown.Add($line)
      $problems.Add("error outside a case: $line")
      continue
    }
    if ($line -match '\[scenario\] (census|coverage|uncovered)\b') {
      $shown.Add($line)
      continue
    }
    if ($line -match '\[scenario\] run: .* budget (\d+) s') {
      $budget = [int]$Matches[1]
      $shown.Add($line)
      continue
    }
    if ($line -match '\[scenario\] done: (\d+) case\(s\) run, (\d+) passed, (\d+) failed\.') {
      $done = [pscustomobject]@{ Run = [int]$Matches[1]; Passed = [int]$Matches[2]; Failed = [int]$Matches[3] }
      $shown.Add($line)
      continue
    }
  }

  $pending = @()
  if ($null -ne $block) {
    $pending = @($block.Lines)
    $problems.Add("case $($block.Name) did not finish")
    $failedNames.Add($block.Name)
  }
  if ($null -eq $done) { $problems.Add('no count line') }
  elseif ($done.Run -eq 0) { $problems.Add('0 cases run') }
  if ($null -ne $done -and $done.Failed -gt 0) { $problems.Add("$($done.Failed) case(s) failed") }

  $exitCode = if ($problems.Count -eq 0) { 0 } else { 1 }
  return [pscustomobject]@{
    Shown       = $shown.ToArray()
    Pending     = $pending
    Problems    = $problems.ToArray()
    Budget      = $budget
    Done        = ($null -ne $done)
    Totals      = $done
    FailedNames = $failedNames.ToArray()
    ExitCode    = $exitCode
  }
}

# Reads the server log so far and prints the report lines not printed yet, except a count line, which
# the caller prints once over every shard; $Printed counts the report lines already handled.
function Write-ScenarioProgress($Server, [int]$Printed, [bool]$Steps = $false) {
  $report = Get-ScenarioReport @(Get-Content $Server.Log -ErrorAction SilentlyContinue) $Steps
  $new = @($report.Shown | Select-Object -Skip $Printed)
  foreach ($line in $new) {
    if ($line -notmatch '\[scenario\] done: ') { Write-Host $line }
  }
  return [pscustomobject]@{ Report = $report; Printed = $Printed + $new.Count }
}

# Copies each failed case's schematic from the first of $DataDirs' WorldEdit folders that holds it into
# $Dest, which is emptied first.
function Save-ScenarioSchematics([string[]]$DataDirs, [string[]]$Names, [string]$Dest) {
  if (Test-Path $Dest) { Remove-Item -Recurse -Force $Dest }
  New-Item -ItemType Directory -Force -Path $Dest | Out-Null
  foreach ($name in $Names) {
    foreach ($dir in $DataDirs) {
      $file = Join-Path $dir "WorldEdit/$(Get-ScenarioSchematicName $name)"
      if (Test-Path $file) { Copy-Item -Path $file -Destination $Dest; break }
    }
  }
}

# Empties $Dest, copies the failed cases' schematics into it, then copies every shard's
# Scenarios/recordings/*.json into $Dest/recordings, which is made only when there is a recording. A file name
# two shards both write is warned and the later shard's copy kept. Returns the number of files in the folder.
function Save-ScenarioLast([string[]]$DataDirs, [string[]]$FailedNames, [string]$Dest) {
  Save-ScenarioSchematics $DataDirs $FailedNames $Dest
  $recordings = @($DataDirs | ForEach-Object { Get-ChildItem -Path (Join-Path $_ 'Scenarios/recordings') -Filter '*.json' -File -ErrorAction SilentlyContinue })
  if ($recordings.Count -eq 0) { return 0 }
  $folder = Join-Path $Dest 'recordings'
  New-Item -ItemType Directory -Force -Path $folder | Out-Null
  $landed = @{}
  foreach ($file in $recordings) {
    if ($landed.ContainsKey($file.Name)) { Write-Warning "recording $($file.Name) is written by two shards; the copy from $($file.DirectoryName) replaces the first" }
    Copy-Item -Path $file.FullName -Destination $folder
    $landed[$file.Name] = $true
  }
  Write-Host "recordings: $($landed.Count) in .game/.scenario-last/recordings"
  return $landed.Count
}

# The count line over the shards that printed one: the sums of their run, passed and failed cases. $null
# when no shard printed one.
function Get-ScenarioCountLine($Reports) {
  $totals = @($Reports | Where-Object { $null -ne $_.Totals } | ForEach-Object { $_.Totals })
  if ($totals.Count -eq 0) { return $null }
  $run = ($totals | Measure-Object -Property Run -Sum).Sum
  $passed = ($totals | Measure-Object -Property Passed -Sum).Sum
  $failed = ($totals | Measure-Object -Property Failed -Sum).Sum
  return "[scenario] done: $run case(s) run, $passed passed, $failed failed."
}

# The reasons the run does not pass, over every shard's report, each prefixed "shard <n>: " when there is
# more than one shard. Empty when the run passes.
function Get-ScenarioExitProblems($Reports) {
  $out = @()
  for ($i = 0; $i -lt $Reports.Count; $i++) {
    foreach ($p in $Reports[$i].Problems) {
      $out += if ($Reports.Count -gt 1) { "shard $($i + 1): $p" } else { $p }
    }
  }
  return $out
}

# Boots one server per shard with the built mods, the live mod and the -Mods folders, deals the named
# scenarios out over them (every one in tests/live when none is named), prints the run's lines, stops the
# servers and throws when the run does not pass.
function Invoke-Scenario([string[]]$Argv) {
  $opts = Get-ScenarioOptions $Argv
  $sources = @(Get-ScenarioSources)
  $present = @($sources | Where-Object { $_.Dir -and (Test-Path $_.Dir) })
  if (-not $present) { throw "No tests/live under $RepoRoot." }
  $names = if ($opts.Names) { Expand-ScenarioNames $opts.Names $sources[0].ModId } else { Get-ScenarioNames $present }
  if (-not $names) { throw "No scenario file under tests/live in $RepoRoot." }
  if ($opts.ShardsAsked -and $opts.Shards -gt $names.Count) { throw "-Shards $($opts.Shards) is more than the $($names.Count) scenario name(s) to run." }
  $copies = @{}
  foreach ($source in $present) { $copies[$source.Dir] = "Scenarios/$($source.ModId)" }
  $groups = Split-ScenarioShards $names $opts.Shards
  $ports = @(Get-ScenarioPorts $opts.Port $groups.Count)

  $exlibDir = Build-ScenarioExlibMod
  $liveDir = Build-ScenarioLiveMod
  $serverDir = Resolve-SmokeServer $opts.Version
  $modDirs = Get-ScenarioModDirs (Get-ScenarioDefaultModDirs $exlibDir) $liveDir $opts.Mods
  Write-Host "Running $($names.Count) scenario name(s) against Vintage Story $($opts.Version) at $serverDir on $($groups.Count) shard(s)"

  $shards = [System.Collections.Generic.List[object]]::new()
  try {
    for ($i = 0; $i -lt $groups.Count; $i++) {
      $port = $ports[$i]
      $server = New-ScratchServer 'scenario' $modDirs -DataCopies $copies -ServerConfig (Get-ScenarioServerConfig $opts.Keep)
      $shards.Add([pscustomobject]@{
          Server   = $server
          Port     = $port
          Command  = (Get-ScenarioCommand $groups[$i] $opts.Keep)
          Printed  = 0
          Sent     = $null
          RunSeen  = $null
          Finished = $false
          Report   = $null
        })
      Write-Host "shard $($i + 1) on port ${port}: $($groups[$i] -join ' ')"
    }
    foreach ($s in $shards) { Start-ScratchServer $s.Server $serverDir $s.Port $true }
    foreach ($s in $shards) { Wait-ScratchServer $s.Server $ScenarioBootSeconds }
    foreach ($s in $shards) {
      $s.Server.Proc.StandardInput.WriteLine($s.Command)
      $s.Sent = [DateTime]::UtcNow
    }

    while ($true) {
      $running = @($shards | Where-Object { -not $_.Finished })
      if ($running.Count -eq 0) { break }
      foreach ($s in $running) {
        $progress = Write-ScenarioProgress $s.Server $s.Printed $opts.Verbose
        $s.Printed = $progress.Printed
        $s.Report = $progress.Report
        if ($progress.Report.Done -or $s.Server.Proc.HasExited) { $s.Finished = $true; continue }
        if ($null -eq $s.RunSeen -and $null -ne $progress.Report.Budget) { $s.RunSeen = [DateTime]::UtcNow }
        $anchor = if ($null -ne $s.RunSeen) { $s.RunSeen } else { $s.Sent }
        $span = if ($null -ne $opts.Timeout) { $opts.Timeout } elseif ($null -ne $s.RunSeen) { $progress.Report.Budget + 60 } else { $ScenarioBootSeconds }
        if ([DateTime]::UtcNow -gt $anchor.AddSeconds($span)) { $s.Finished = $true }
      }
      Start-Sleep -Milliseconds 500
    }

    if (-not $opts.Keep) { foreach ($s in $shards) { if (-not $s.Server.Proc.HasExited) { Stop-ScratchServer $s.Server } } }
    $reports = @()
    foreach ($s in $shards) {
      $progress = Write-ScenarioProgress $s.Server $s.Printed $opts.Verbose
      foreach ($line in $progress.Report.Pending) { Write-Host $line }
      $reports += $progress.Report
    }
    $countLine = Get-ScenarioCountLine $reports
    if ($countLine) { Write-Host $countLine }
    $null = Save-ScenarioLast @($shards | ForEach-Object { $_.Server.Data }) @($reports | ForEach-Object { $_.FailedNames }) (Join-Path $RepoRoot '.game/.scenario-last')
    $problems = Get-ScenarioExitProblems $reports

    if ($opts.Keep) {
      foreach ($s in $shards) {
        foreach ($line in (Get-ScenarioKeepLines $s.Server $s.Port)) { Write-Host $line }
      }
      Write-Host 'Type commands for the server console; Ctrl+C stops it.'
      $relayEnded = $false
      try {
        Send-ScenarioRelay @($shards | ForEach-Object { $_.Server }) ([Console]::In)
        $relayEnded = $true
      }
      finally {
        foreach ($s in $shards) { if (-not $s.Server.Proc.HasExited) { Stop-ScratchServer $s.Server } }
        if (-not $relayEnded) {
          foreach ($s in $shards) { Remove-ScratchServer $s.Server $opts.KeepData }
          [Environment]::Exit($(if ($problems) { 1 } else { 0 }))
        }
      }
    }
    if ($problems) { throw "Scenario run failed: $($problems -join '; ')" }
  }
  finally {
    foreach ($s in $shards) { Remove-ScratchServer $s.Server $opts.KeepData }
  }
}


Add-ExmodCommand -Group run -Name scenario -Summary 'run live scenarios on a booted server' -Action {
  param([string[]]$Argv) Invoke-Scenario $Argv
} -Detail @'
exmod scenario [<name>...] [-Version <x.y>] [-Port <n>] [-Timeout <seconds>] [-Shards <n>]
               [-Mods <dir>[,...]] [-Keep] [-KeepData] [-Verbose]

Builds this checkout's live mod (exmod.json's `live` key), boots a dedicated server with the built mods
and the live mod, copies tests/live into the server's Scenarios/<modid> folder, runs the named scenarios
on it, prints the run's lines and stops the server. A name is <modid>/<stem>, such as exlib/sample, or a
bare stem, which belongs to the repository's first mod (exlib in exlib, the first mod of exmod.json in
exmods); with no name, every scenario file under tests/live runs, factory lines and setups included. From exmods the live mod is built from
exlib's source, found through the ExlibRoot environment variable, else the workspace's exlib, and the exlib
mod is built from the same checkout; each mod's tests/live is copied under its own modid.

  -Version   the game series; only the current one, which the live mod is built for (default 1.22)
  -Port      the port the server binds (default 42498), so two runs can share the machine
  -Timeout   seconds to wait for the count line, in place of the run's budget plus 60
  -Shards    servers to boot, each on the next port up from -Port (smoke's 42499 is skipped); the names are dealt out over them in
             turn, each server runs its own, and one count line covers all (default 1)
  -Mods      extra mod folder(s), or folder(s) of mod folders, staged beside the live mod
  -Keep      leave the plots standing and the server up: prints the port, the data path and how to join,
             relays the terminal's lines to the server console until Ctrl+C (or stdin closes), then stops
             it; the exit code is the one a run without -Keep gives
  -KeepData  keep each scratch data path and print it, instead of deleting it
  -Verbose   print every step line, passing cases too

Exits 1 when the count line is missing or reports no case run, a case fails or errors, or an [Error] or
[Fatal] line falls outside a case, on any shard. The schematics of the failed cases are copied to
.game/.scenario-last, and every recording to .game/.scenario-last/recordings.
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
one `exmod client` uses, <store>/data/default (see `exmod help client`), which in WSL with interop
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
