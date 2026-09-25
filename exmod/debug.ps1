# Debugging the client from VS Code on Linux and in WSL: the debug adapter a generated launch
# configuration's pipeTransport starts in place of vsdbg.
#
#   exmod debug-adapter    relay VS Code's debug protocol to vsdbg (left out of the command list)

#region debug-adapter

# One diagnostic line on stderr; the adapter's stdout carries the debug protocol alone.
function Write-AdapterError([string]$Text) {
  [Console]::Error.WriteLine($Text)
}

# $Path as the Windows client sees it: a path under the Linux user store (Get-ExmodUserStore) is the
# same path under $WinStore, any other absolute Linux path its \\wsl.localhost form (Convert-WslPath),
# anything else is returned unchanged.
function ConvertTo-WindowsLaunchPath([string]$Path, [string]$WinStore) {
  $linuxStore = Get-ExmodUserStore
  if ($Path -eq $linuxStore -or $Path.StartsWith("$linuxStore/")) {
    return $WinStore + $Path.Substring($linuxStore.Length).Replace('/', '\')
  }
  if ($Path.StartsWith('/')) { return Convert-WslPath -ToWindows $Path }
  return $Path
}

# Rewrites the arguments of a debug-protocol launch request, in place, to start the Windows client
# from $WinStore, Windows' user store: a program <Linux store>/game/<series>/Vintagestory.dll becomes
# $WinStore\game\<series>\Vintagestory.exe, and the program otherwise, each string argument and cwd
# go through ConvertTo-WindowsLaunchPath; env.WAYLAND_DISPLAY is removed. Then, as `exmod client`
# does, registers the high-performance GPU for that Vintagestory.exe and seeds the settings of the
# folder after --dataPath, their output sent to stderr.
function Update-WindowsLaunch([System.Text.Json.Nodes.JsonObject]$Launch, [string]$WinStore) {
  $string = { param($node) $node -and $node.GetValueKind() -eq [System.Text.Json.JsonValueKind]::String }
  $clientExe = $null
  if (& $string $Launch['program']) {
    $program = "$($Launch['program'])"
    $game = [regex]::Match($program, "^$([regex]::Escape((Get-ExmodUserStore)))/game/([^/]+)/Vintagestory\.dll$")
    $programWin = if ($game.Success) { $clientExe = "$WinStore\game\$($game.Groups[1].Value)\Vintagestory.exe"; $clientExe }
    else { ConvertTo-WindowsLaunchPath $program $WinStore }
    $Launch['program'] = [System.Text.Json.Nodes.JsonValue]::Create([string]$programWin)
  }
  if (& $string $Launch['cwd']) {
    $Launch['cwd'] = [System.Text.Json.Nodes.JsonValue]::Create([string](ConvertTo-WindowsLaunchPath "$($Launch['cwd'])" $WinStore))
  }
  $dataWin = $null
  $gameArgs = $Launch['args']
  if ($gameArgs -is [System.Text.Json.Nodes.JsonArray]) {
    for ($i = 0; $i -lt $gameArgs.Count; $i++) {
      if (-not (& $string $gameArgs[$i])) { continue }
      $gameArgs[$i] = [System.Text.Json.Nodes.JsonValue]::Create([string](ConvertTo-WindowsLaunchPath "$($gameArgs[$i])" $WinStore))
      if ($i -gt 0 -and "$($gameArgs[$i - 1])" -eq '--dataPath') { $dataWin = "$($gameArgs[$i])" }
    }
  }
  if ($Launch['env'] -is [System.Text.Json.Nodes.JsonObject]) { $null = $Launch['env'].Remove('WAYLAND_DISPLAY') }

  & {
    if ($clientExe) { Register-ClientGpuPreference $clientExe }
    if ($dataWin) { Initialize-ClientSettings (Convert-WslPath $dataWin) }
  } *>&1 | ForEach-Object { Write-AdapterError "$_" }
}

# The frame to send in place of one client-to-adapter message whose body is $Body (the bytes after
# its header): a launch request's arguments rewritten by Update-WindowsLaunch under a new
# Content-Length header, or $null for any other message, which goes on as it came.
function ConvertTo-WindowsLaunchFrame([byte[]]$Body, [string]$WinStore) {
  $message = $null
  try { $message = [System.Text.Json.Nodes.JsonNode]::Parse([System.Text.Encoding]::UTF8.GetString($Body)) } catch { return $null }
  if ($message -isnot [System.Text.Json.Nodes.JsonObject]) { return $null }
  if ("$($message['type'])" -ne 'request' -or "$($message['command'])" -ne 'launch') { return $null }
  if ($message['arguments'] -isnot [System.Text.Json.Nodes.JsonObject]) { return $null }
  Update-WindowsLaunch $message['arguments'] $WinStore
  $options = [System.Text.Json.JsonSerializerOptions]::new()
  $options.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
  $json = [System.Text.Encoding]::UTF8.GetBytes($message.ToJsonString($options))
  $header = [System.Text.Encoding]::ASCII.GetBytes("Content-Length: $($json.Length)`r`n`r`n")
  return , [byte[]]($header + $json)
}

# Starts $Program --interpreter=vscode and relays between it and this process: stdin to its stdin,
# its stdout to stdout, byte for byte, its stderr left on this process's. With $WinStore set, the
# client's messages are read by their Content-Length framing and each launch request is replaced by
# ConvertTo-WindowsLaunchFrame's; a message split across reads or several in one read are relayed
# whole and in order. When stdin ends, the adapter's stdin is closed and the adapter killed if it
# has not exited within 2 seconds; the process exits 0. When the adapter ends first, the process
# exits with its exit code.
function Start-DebugAdapterRelay([string]$Program, [string]$WinStore) {
  $psi = [System.Diagnostics.ProcessStartInfo]::new($Program)
  $psi.ArgumentList.Add('--interpreter=vscode')
  $psi.UseShellExecute = $false
  $psi.RedirectStandardInput = $true
  $psi.RedirectStandardOutput = $true
  $adapter = [System.Diagnostics.Process]::Start($psi)

  $in = [Console]::OpenStandardInput()
  $out = [Console]::OpenStandardOutput()
  $toClient = $adapter.StandardOutput.BaseStream.CopyToAsync($out)
  $toAdapter = $adapter.StandardInput.BaseStream
  $latin1 = [System.Text.Encoding]::Latin1
  $buffer = [byte[]]::new(65536)
  # Latin-1 maps each byte to one char and back, so offsets here are byte offsets.
  $pending = ''
  $clientEnded = $false
  try {
    while ($true) {
      $read = $in.ReadAsync($buffer, 0, $buffer.Length)
      if ([System.Threading.Tasks.Task]::WaitAny([System.Threading.Tasks.Task[]]@($read, $toClient)) -eq 1) { break }
      $count = $read.Result
      if ($count -eq 0) { $clientEnded = $true; break }
      if (-not $WinStore) {
        $toAdapter.Write($buffer, 0, $count)
        $toAdapter.Flush()
        continue
      }
      $pending += $latin1.GetString($buffer, 0, $count)
      while ($true) {
        $end = $pending.IndexOf("`r`n`r`n", [StringComparison]::Ordinal)
        if ($end -lt 0) { break }
        $frame = $null
        if ($pending.Substring(0, $end) -match '(?im)^Content-Length:\s*(\d+)\s*$') {
          $total = $end + 4 + [int]$Matches[1]
          if ($pending.Length -lt $total) { break }
          $frame = ConvertTo-WindowsLaunchFrame ($latin1.GetBytes($pending.Substring($end + 4, $total - $end - 4))) $WinStore
        }
        else { $total = $end + 4 }
        if ($null -eq $frame) { $frame = $latin1.GetBytes($pending.Substring(0, $total)) }
        $pending = $pending.Substring($total)
        $toAdapter.Write($frame, 0, $frame.Length)
      }
      $toAdapter.Flush()
    }
  }
  catch [System.IO.IOException] { }
  finally {
    try { $toAdapter.Close() } catch { }
    if (-not $adapter.WaitForExit(2000)) {
      try { $adapter.Kill($true) } catch { }
      $adapter.WaitForExit()
    }
    try { $null = $toClient.Wait(2000) } catch { }
    $out.Flush()
  }
  exit ($clientEnded ? 0 : $adapter.ExitCode)
}

# `exmod debug-adapter`: in WSL with interop, Windows' vsdbg from Windows' user store
# (<store>\vsdbg\vsdbg.exe) through Start-DebugAdapterRelay with each launch request rewritten for
# the Windows client; otherwise <user store>/vsdbg/vsdbg with nothing rewritten. $env:EXMOD_VSDBG,
# when set, names the program to start instead. A missing vsdbg or an unreadable Windows store
# prints one line on stderr and exits 1. Never writes anything but the adapter's bytes to stdout.
function Invoke-DebugAdapter {
  $winStore = $null
  if (Test-WslInterop) {
    $winStore = Get-WindowsUserStore
    if (-not $winStore) {
      Write-AdapterError 'exmod debug-adapter: Windows %LOCALAPPDATA% could not be read through cmd.exe.'
      exit 1
    }
    $vsdbg = "$winStore\vsdbg\vsdbg.exe"
    $vsdbgHere = Convert-WslPath $vsdbg
  }
  else {
    $vsdbg = Join-Path (Get-ExmodUserStore) "vsdbg/vsdbg$ExeSuffix"
    $vsdbgHere = $vsdbg
  }
  if ($env:EXMOD_VSDBG) { $vsdbgHere = $env:EXMOD_VSDBG }
  elseif (-not (Test-Path -LiteralPath $vsdbgHere -PathType Leaf)) {
    Write-AdapterError "exmod debug-adapter: no vsdbg at $vsdbg; install it with: exmod provision vsdbg"
    exit 1
  }
  Start-DebugAdapterRelay $vsdbgHere $winStore
}


Add-ExmodCommand -Group run -Name debug-adapter -Hidden -Summary 'relay VS Code''s debug protocol to vsdbg' -Action {
  param([string[]]$Argv) Invoke-DebugAdapter
} -Detail @'
exmod debug-adapter [anything ...]

The debug adapter a generated launch configuration's linux block starts through its
pipeTransport. It speaks the debug protocol on stdin and stdout and writes nothing else there;
diagnostics go to stderr. Arguments are ignored: the C# extension appends `<debuggerPath>
--interpreter=vscode` to the pipe's arguments.

In WSL with interop it starts Windows' vsdbg from %LOCALAPPDATA%\exmod\vsdbg and passes every
message through unchanged but the launch request, which it rewrites for the Windows client: the
program <Linux store>/game/<series>/Vintagestory.dll becomes the Windows store's
game\<series>\Vintagestory.exe; each argument and the cwd under the Linux store
(~/.local/share/exmod) becomes the same path under the Windows store, and any other absolute Linux
path its \\wsl.localhost path; WAYLAND_DISPLAY leaves the environment. Before that launch it
registers the high-performance GPU for the exe and seeds a fresh --dataPath folder's settings, as
`exmod client` does. Elsewhere on Linux it starts ~/.local/share/exmod/vsdbg/vsdbg and rewrites
nothing.

A missing vsdbg prints one line naming `exmod provision vsdbg` and exits 1. EXMOD_VSDBG names a
program to start in place of vsdbg.
'@

#endregion
