"""Tests for the WSL hand-over, the Windows client from WSL and on Windows, and the GPU preference
registered for it, in exmod.ps1 and exmod/run.ps1, run through pwsh ($PWSH, PATH, or the checkout's
.dotnet/tools) on a temporary repository with exmod_harness's prelude and the Windows and interop
lookups stubbed; skipped when no pwsh is found. Each test names the mutation it fails under."""

import json
import os
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import PWSH, ROOT, exmod_env, exmod_script, touch  # noqa: E402


def run(repo, body, home, extra_env=None, stub_gpu=True):
    """Runs `body` after exmod.ps1 and the prelude (exmod_script, stub_gpu passed on) against `repo`
    and returns (exit code, stdout lines). HOME is `home`, LOCALAPPDATA `home`/local; `extra_env`
    goes over both."""
    assert PWSH
    script = exmod_script(body, stub_gpu)
    env = exmod_env(os.path.join(home, "local"), TEST_REPO=repo, HOME=home, **(extra_env or {}))
    out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", script],
                         env=env, capture_output=True, text=True)
    return out.returncode, out.stdout.splitlines()


@unittest.skipUnless(PWSH, "pwsh not found")
class ShareAndSideTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.repo = os.path.join(self.tmp, "repo")
        touch(os.path.join(self.repo, "exmod.json"), "{}")

    def json(self, body):
        code, lines = run(self.repo, body, self.tmp)
        self.assertEqual(0, code, lines)
        return json.loads(lines[-1])

    def test_wsl_share_paths_map_to_distro_and_linux_path(self):
        # Fails if the distro group accepts a backslash, if wsl$ is missed, or if the share root does
        # not map to '/'.
        paths = [r"\\wsl.localhost\archlinux\home\me\src", "\\\\WSL$\\Ubuntu-24.04\\",
                 "//wsl.localhost/arch/x y", r"\\wsl.localhost\arch", r"C:\src", r"\\server\share\x"]
        os.environ["TEST_PATHS"] = json.dumps(paths)
        try:
            got = self.json("ConvertTo-Json -Compress @(($env:TEST_PATHS | ConvertFrom-Json) | "
                            "ForEach-Object { $s = Get-WslShare $_; if ($s) { \"$($s.Distro)|$($s.LinuxPath)\" } else { 'none' } })")
        finally:
            del os.environ["TEST_PATHS"]
        self.assertEqual(["archlinux|/home/me/src", "Ubuntu-24.04|/", "arch|/x y", "arch|/", "none", "none"], got)

    def test_windows_side_commands_are_client_logs_machine_and_client_and_vsdbg_provisioning_only(self):
        # Fails if provision is treated as one case, a machine-group command is handed to WSL, or
        # provision vsdbg is.
        got = self.json("ConvertTo-Json -Compress @("
                        "(Test-ExmodWindowsSideCommand 'client' @()), (Test-ExmodWindowsSideCommand 'logs' @()), "
                        "(Test-ExmodWindowsSideCommand 'fix-registry' @()), "
                        "(Test-ExmodWindowsSideCommand 'provision' @('game', '-Version', '1.22', '-Kind', 'client')), "
                        "(Test-ExmodWindowsSideCommand 'provision' @('game', '-Version', '1.22', '-Kind', 'server')), "
                        "(Test-ExmodWindowsSideCommand 'provision' @('game', '-Version', '1.22')), "
                        "(Test-ExmodWindowsSideCommand 'provision' @('vsdbg')), "
                        "(Test-ExmodWindowsSideCommand 'provision' @('dotnet')), "
                        "(Test-ExmodWindowsSideCommand 'stage' @()), (Test-ExmodWindowsSideCommand 'build' @()))")
        self.assertEqual([True, True, True, True, False, False, True, False, False, False], got)

    def test_the_hand_over_passes_every_argument_through_wsl_exec_unchanged(self):
        # Fails if the hand-over uses `--` instead of `--exec`, or drops an empty argument.
        got = self.json("function wsl.exe { $global:said = @($args); $global:LASTEXITCODE = 0 }; "
                        "$null = Invoke-ExmodInWsl 'arch' '/src/x y' @('stage', 'x$HOME', 'C:\\a b\\c', 'q\"z', ''); "
                        "ConvertTo-Json -Compress $said")
        self.assertEqual(["-d", "arch", "--cd", "/src/x y", "--exec", "bash", "scripts/exmod.sh",
                          "stage", "x$HOME", "C:\\a b\\c", 'q"z', ""], got)


# Stubs every Windows and interop lookup for the WSL-to-Windows client. The Windows store and dotnet
# carry spaces; a Windows path C:\... maps to $env:TEST_C/... and a Linux path to
# \\wsl.localhost\test<path>. $env:TEST_INTEROP, $env:TEST_STORE, $env:TEST_DOTNET and $env:TEST_PWSH
# switch the interop check, the store and the two programs off when set to 0. The build and the stage return the stage
# folder without touching it. A launch of the Linux client prints its arguments instead of running.
STUBS = r"""
function Test-WslInterop { $env:TEST_INTEROP -ne '0' }
function Get-WindowsUserStore { if ($env:TEST_STORE -ne '0') { 'C:\Users\A B\AppData\Local\exmod' } }
function Get-WindowsProgram([string]$Name) {
  if ($Name -eq 'dotnet' -and $env:TEST_DOTNET -ne '0') { return 'C:\Program Files\dotnet\dotnet.exe' }
  if ($Name -eq 'pwsh.exe' -and $env:TEST_PWSH -ne '0') { return 'C:\Program Files\PowerShell\7\pwsh.exe' }
  return $null
}
function Convert-WslPath([string]$Path, [switch]$ToWindows) {
  if ($ToWindows) { return '\\wsl.localhost\test' + $Path.Replace('/', '\') }
  return $env:TEST_C + $Path.Substring(2).Replace('\', '/')
}
function Publish-RunMods { Write-Host 'staged'; Get-StageDest $args[0] }
function Resolve-DotnetHost { 'Show-Args' }
function Show-Args { Write-Host "launch: $($args -join ' ')" }
"""


def executable(path, text):
    touch(path, "#!/usr/bin/env bash\n" + text)
    os.chmod(path, os.stat(path).st_mode | stat.S_IEXEC)


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "on Windows Invoke-Client takes the Windows launch, not the WSL one")
class WindowsClientFromWslTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.home = os.path.join(self.tmp, "home")
        os.makedirs(self.home)
        self.ws = os.path.join(self.tmp, "ws")
        touch(os.path.join(self.ws, "exmod.workspace.json"), "{}")
        self.repo = os.path.join(self.ws, "exmods")
        touch(os.path.join(self.repo, "exmod.json"), "{}")
        self.c = os.path.join(self.tmp, "c")
        self.slot = os.path.join(self.c, "Users", "A B", "AppData", "Local", "exmod", "game", "1.22")
        self.win_store = r"C:\Users\A B\AppData\Local\exmod"
        self.linux_slot = os.path.join(self.home, ".local", "share", "exmod", "game", "1.22")

    def seed_windows_client(self):
        touch(os.path.join(self.slot, "Vintagestory.exe"))
        touch(os.path.join(self.slot, "Lib", "e_sqlite3.dll"))

    def seed_linux_client(self):
        touch(os.path.join(self.linux_slot, "Vintagestory.dll"))
        touch(os.path.join(self.linux_slot, "Lib", "libe_sqlite3.so"))

    def client(self, args, **env):
        base = {"TEST_C": self.c, "WSL_DISTRO_NAME": "test", "LOCALAPPDATA": os.path.join(self.tmp, "local")}
        base.update(env)
        argv = ", ".join(f"'{a}'" for a in args)
        return run(self.repo, STUBS + f"Invoke-Client @({argv})", self.home, base)

    def test_dry_run_with_interop_prints_the_windows_client_with_spaces_unsplit(self):
        # Fails if a Windows path is split at its space, the program is dotnet or not the slot exe's
        # Linux view, a slot holding the exe but no Vintagestory.dll is not usable, the GPU
        # preference is not printed for the slot's exe or registered for real, or a dry run stages
        # or seeds settings.
        self.seed_windows_client()
        code, lines = self.client(["-NoBuild", "-DryRun"])
        self.assertEqual(0, code, lines)
        data = self.win_store + r"\data\default"
        self.assertEqual([
            f"program: {self.slot}/Vintagestory.exe",
            "arg: --tracelog", "arg: --dataPath", f"arg: {data}",
            "arg: --logPath", f"arg: {data}\\Logs\\exmods",
            "arg: --addModPath", "arg: \\\\wsl.localhost\\test" + os.path.join(self.repo, "bin", "Mods").replace("/", "\\"),
            f"gpu-stub: {self.win_store}\\game\\1.22\\Vintagestory.exe dry-run",
        ], lines)
        self.assertFalse(os.path.exists(os.path.join(self.c, "Users", "A B", "AppData", "Local", "exmod", "data")))

    def test_dry_run_with_interop_off_prints_the_linux_client_and_why(self):
        # Fails if the WSL branch ignores the interop check, or the reason line is dropped.
        self.seed_windows_client()
        self.seed_linux_client()
        code, lines = self.client(["-NoBuild", "-DryRun"], TEST_INTEROP="0")
        self.assertEqual(0, code, lines)
        self.assertIn("WSL interop is off (no /proc/sys/fs/binfmt_misc/WSLInterop), so the Linux client runs.", lines)
        self.assertIn("program: Show-Args", lines)
        self.assertIn(f"arg: {os.path.join(self.linux_slot, 'Vintagestory.dll')}", lines)
        self.assertIn("env: WAYLAND_DISPLAY=none", lines)

    def test_linux_flag_runs_the_linux_client_with_interop_on(self):
        # Fails if -Linux does not bypass the interop branch.
        self.seed_windows_client()
        self.seed_linux_client()
        code, lines = self.client(["-NoBuild", "-DryRun", "-Linux"])
        self.assertEqual(0, code, lines)
        self.assertIn(f"arg: {os.path.join(self.linux_slot, 'Vintagestory.dll')}", lines)
        self.assertNotIn("WSL interop is off (no /proc/sys/fs/binfmt_misc/WSLInterop), so the Linux client runs.", lines)

    def test_no_dotnet_on_windows_stops_naming_the_runtime(self):
        # Fails if the dotnet check is dropped (the launch then runs a $null program).
        self.seed_windows_client()
        code, lines = self.client(["-NoBuild", "-DryRun"], TEST_DOTNET="0")
        self.assertEqual(1, code)
        self.assertEqual(["exmod: Windows has no dotnet on its PATH; install the .NET 10 runtime for Windows "
                          "(https://dot.net), or pass -Linux."], lines)

    def test_a_missing_windows_client_stops_with_the_provision_command(self):
        # Fails if a missing client falls through to the launch, or the command names Linux paths.
        code, lines = self.client(["-NoBuild"])
        self.assertEqual(1, code)
        script = "\\\\wsl.localhost\\test" + os.path.join(ROOT, "exmod.ps1").replace("/", "\\")
        repo = "\\\\wsl.localhost\\test" + self.repo.replace("/", "\\")
        self.assertEqual([f"exmod: no Windows client for 1.22 in {self.win_store}\\game\\1.22. Provision one with: "
                          f"exmod client -Provision, or on Windows: pwsh.exe -NoProfile -ExecutionPolicy Bypass "
                          f"-File '{script}' -RepoRoot '{repo}' provision game -Version 1.22 -Kind client"], lines)

    def test_no_pwsh_on_windows_stops_provisioning_naming_powershell(self):
        # Fails if the pwsh.exe check is dropped.
        code, lines = self.client(["-NoBuild", "-Provision"], TEST_PWSH="0")
        self.assertEqual(1, code)
        self.assertEqual(["exmod: Windows has no pwsh.exe on its PATH; install PowerShell 7 for Windows "
                          "(winget install Microsoft.PowerShell) to provision the Windows client."], lines)

    def test_provision_runs_windows_pwsh_then_launches_with_settings_seeded(self):
        # Fails if -Provision does not run this tools checkout's provision game in Windows' pwsh,
        # the launch runs dotnet instead of the slot's exe or registers no GPU preference for it
        # first, or the launch does not seed settings through the Linux view of the Windows data
        # path.
        record = os.path.join(self.tmp, "calls.txt")
        pwsh = os.path.join(self.c, "Program Files", "PowerShell", "7", "pwsh.exe")
        executable(pwsh, f'printf "pwsh:%s\\n" "$@" >> "{record}"\n'
                         f'mkdir -p "{self.slot}/Lib"; touch "{self.slot}/Lib/e_sqlite3.dll"\n')
        executable(os.path.join(self.slot, "Vintagestory.exe"), f'printf "exe:%s\\n" "$@" >> "{record}"\necho exe ran\n')
        executable(os.path.join(self.c, "Program Files", "dotnet", "dotnet.exe"),
                   f'printf "dotnet:%s\\n" "$@" >> "{record}"\n')
        code, lines = self.client(["-Provision"])
        self.assertEqual(0, code, lines)
        with open(record) as f:
            calls = f.read().splitlines()
        script = "\\\\wsl.localhost\\test" + os.path.join(ROOT, "exmod.ps1").replace("/", "\\")
        repo = "\\\\wsl.localhost\\test" + self.repo.replace("/", "\\")
        self.assertEqual([f"pwsh:{a}" for a in ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                                               "-RepoRoot", repo, "provision", "game", "-Version", "1.22",
                                               "-Kind", "client"]], calls[:13])
        self.assertEqual("exe:--tracelog", calls[13])
        self.assertFalse([c for c in calls if c.startswith("dotnet:")], calls)
        gpu = f"gpu-stub: {self.win_store}\\game\\1.22\\Vintagestory.exe"
        self.assertIn(gpu, lines)
        self.assertLess(lines.index(gpu), lines.index("exe ran"), lines)
        self.assertIn("staged", lines)
        data = os.path.join(self.c, "Users", "A B", "AppData", "Local", "exmod", "data", "default")
        self.assertTrue(os.path.isfile(os.path.join(data, "clientsettings.json")))

    def test_an_unreadable_windows_store_stops_pointing_at_linux(self):
        # Fails if the store check is dropped (the paths then start with a bare backslash).
        self.seed_windows_client()
        code, lines = self.client(["-NoBuild", "-DryRun"], TEST_STORE="0")
        self.assertEqual(1, code)
        self.assertEqual(["exmod: Windows %LOCALAPPDATA% could not be read through cmd.exe; pass -Linux to run "
                          "the Linux client."], lines)

    def test_a_failed_or_empty_provision_stops_the_launch(self):
        # Fails if the provision's exit code is ignored, or the slot is not checked again after it.
        executable(os.path.join(self.c, "Program Files", "PowerShell", "7", "pwsh.exe"), "exit 3\n")
        code, lines = self.client(["-NoBuild", "-Provision"])
        self.assertEqual(3, code, lines)
        executable(os.path.join(self.c, "Program Files", "PowerShell", "7", "pwsh.exe"), "exit 0\n")
        code, lines = self.client(["-NoBuild", "-Provision"])
        self.assertEqual(1, code)
        self.assertEqual(f"exmod: provisioning completed but no usable Windows client was found in "
                         f"{self.win_store}\\game\\1.22.", lines[-1])

    def provision_game(self, args, **env):
        base = {"TEST_C": self.c, "LOCALAPPDATA": os.path.join(self.tmp, "local")}
        base.update(env)
        argv = ", ".join(f"'{a}'" for a in args)
        return run(self.repo, STUBS + f"Invoke-ProvisionGame @({argv})", self.home, base)

    def test_provision_game_client_dry_run_names_the_windows_slot_and_command(self):
        # Fails if a client provision under interop goes to the Linux store, or its Windows command
        # is not this tools checkout's provision game on this repository.
        code, lines = self.provision_game(["-Version", "1.22", "-Kind", "client", "-DryRun"])
        self.assertEqual(0, code, lines)
        script = "\\\\wsl.localhost\\test" + os.path.join(ROOT, "exmod.ps1").replace("/", "\\")
        repo = "\\\\wsl.localhost\\test" + self.repo.replace("/", "\\")
        self.assertEqual([f"dest: {self.win_store}\\game\\1.22",
                          f"run: pwsh.exe -NoProfile -ExecutionPolicy Bypass -File {script} -RepoRoot {repo} "
                          "provision game -Version 1.22 -Kind client"], lines)

    def test_provision_game_leaves_the_windows_store_for_server_dest_linux_or_no_interop(self):
        # Fails if any one guard of the Windows branch is dropped: a server, a -Dest, -Linux or
        # interop off each keeps the install on this side.
        ws_game = os.path.join(self.ws, ".game", "1.22")
        cases = [
            (["-Version", "1.22", "-Kind", "server", "-DryRun"], {}, ws_game),
            (["-Version", "1.22", "-Kind", "client", "-Dest", "/opt/vs", "-DryRun"], {}, "/opt/vs"),
            (["-Version", "1.22", "-Kind", "client", "-Linux", "-DryRun"], {}, self.linux_slot),
            (["-Version", "1.22", "-Kind", "client", "-DryRun"], {"TEST_INTEROP": "0"}, self.linux_slot),
        ]
        for args, env, dest in cases:
            code, lines = self.provision_game(args, **env)
            self.assertEqual((0, [f"dest: {dest}"]), (code, lines), args)

    def test_provision_game_client_runs_windows_pwsh_with_force_passed_on(self):
        # Fails if the WSL branch provisions on this side, or drops -Force on its way to Windows.
        record = os.path.join(self.tmp, "calls.txt")
        executable(os.path.join(self.c, "Program Files", "PowerShell", "7", "pwsh.exe"),
                   f'printf "%s\\n" "$@" >> "{record}"\n')
        code, lines = self.provision_game(["-Version", "1.22", "-Kind", "client", "-Force"])
        self.assertEqual(0, code, lines)
        with open(record) as f:
            calls = f.read().splitlines()
        self.assertEqual(["provision", "game", "-Version", "1.22", "-Kind", "client", "-Force"], calls[-7:])
        self.assertIn("Provisioning a Windows client install for 1.22 ...", lines)

    def test_provision_game_client_with_an_unreadable_windows_store_stops(self):
        # Fails if the store check is dropped (the dry run then names a bare \game path).
        code, lines = self.provision_game(["-Version", "1.22", "-Kind", "client", "-DryRun"], TEST_STORE="0")
        self.assertEqual((1, ["exmod: Windows %LOCALAPPDATA% could not be read through cmd.exe; pass -Linux to "
                              "provision the Linux client."]), (code, lines))

    def test_logs_reads_the_windows_store_under_interop(self):
        # Fails if logs client ignores the interop check or -Linux, or reads on past an unreadable store.
        data = os.path.join(self.c, "Users", "A B", "AppData", "Local", "exmod", "data", "default")
        touch(os.path.join(data, "Logs", "exmods", "client-main.log"), "windows\n")
        touch(os.path.join(self.home, ".local", "share", "exmod", "data", "default", "Logs", "exmods", "client-main.log"), "linux\n")
        code, lines = run(self.repo, STUBS + "Invoke-Logs @('client', '-Lines', '1')", self.home, {"TEST_C": self.c})
        self.assertEqual(0, code, lines)
        self.assertEqual("windows", lines[-1])
        code, lines = run(self.repo, STUBS + "Invoke-Logs @('client', '-Lines', '1', '-Linux')", self.home, {"TEST_C": self.c})
        self.assertEqual("linux", lines[-1])
        code, lines = run(self.repo, STUBS + "try { Invoke-Logs @('client', '-Lines', '1') } "
                                             "catch { Write-Host \"error: $($_.Exception.Message)\" }",
                          self.home, {"TEST_C": self.c, "TEST_STORE": "0"})
        self.assertEqual(["error: Windows %LOCALAPPDATA% could not be read through cmd.exe; pass -Linux for the "
                          "Linux client's logs."], lines)


# Windows reaching the checkout through \\wsl.localhost: $OnWindows set, the share stubbed, the
# hand-over recorded, the client lookup answering $env:TEST_SLOT for the store's slot and
# $env:TEST_TREE otherwise, and the system dotnet holding every runtime but $env:TEST_MISSING's.
ON_SHARE = r"""
$OnWindows = $true
function Get-WslShare { [pscustomobject]@{ Distro = 'arch'; LinuxPath = '/src/repo' } }
function Invoke-ExmodInWsl([string]$Distro, [string]$LinuxPath, [string[]]$Argv) { Write-Host "wsl: $Distro $LinuxPath $($Argv -join ' ')"; 0 }
function Find-UsableGameInstall([string]$Version, [string]$Kind, [switch]$SlotOnly) { if ($SlotOnly) { $env:TEST_SLOT } else { $env:TEST_TREE } }
function Publish-RunMods { Write-Host 'staged on windows'; '/windows-stage' }
function Get-MissingRuntimeMajors { @($env:TEST_MISSING -split ',' | Where-Object { $_ }) }
function Invoke-ProvisionDotnet { throw 'provisioned' }
function Resolve-DotnetHost { 'dotnet' }
"""


@unittest.skipUnless(PWSH, "pwsh not found")
class ClientOnWslShareTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.repo = os.path.join(self.tmp, "repo")
        touch(os.path.join(self.repo, "exmod.json"), "{}")
        self.slot = os.path.join(self.tmp, "store slot")
        self.tree = os.path.join(self.tmp, "tree")
        executable(os.path.join(self.slot, "Vintagestory.exe"), 'echo "launch: $*"\n')
        executable(os.path.join(self.tree, "Vintagestory.exe"), 'echo "tree launch: $*"\n')

    def client(self, args, missing="", want_code=0):
        argv = ", ".join(f"'{a}'" for a in args)
        code, lines = run(self.repo, ON_SHARE + f"Invoke-Client @({argv})", self.tmp,
                          {"LOCALAPPDATA": os.path.join(self.tmp, "local"), "TEST_MISSING": missing,
                           "TEST_SLOT": self.slot, "TEST_TREE": self.tree})
        self.assertEqual(want_code, code, lines)
        return lines

    def test_client_stages_inside_wsl_and_runs_the_store_client_on_the_stage_folder(self):
        # Fails if the client stages on Windows, the stage is not handed to the distro, an in-tree
        # client on the share is taken, or the store client's exe runs without its GPU preference
        # registered first.
        lines = self.client([])
        self.assertIn("wsl: arch /src/repo stage -Version 1.22 -Configuration Debug", lines)
        self.assertNotIn("staged on windows", lines)
        self.assertFalse([l for l in lines if l.startswith("tree launch: ")], lines)
        launch = [l for l in lines if l.startswith("launch: ")]
        self.assertEqual(1, len(launch), lines)
        self.assertTrue(launch[0].startswith("launch: --tracelog --dataPath"), launch)
        self.assertTrue(launch[0].endswith(f"--addModPath {os.path.join(self.repo, 'bin', 'Mods')}"), launch)
        gpu = f"gpu-stub: {os.path.join(self.slot, 'Vintagestory.exe')}"
        self.assertIn(gpu, lines)
        self.assertLess(lines.index(gpu), lines.index(launch[0]), lines)

    def test_a_windows_dry_run_prints_the_apphost_and_its_gpu_preference_last(self):
        # Fails if the Windows client runs dotnet on Vintagestory.dll instead of the slot's exe, or
        # a dry run registers the preference for real or does not print it.
        lines = self.client(["-NoBuild", "-DryRun"])
        self.assertEqual(f"program: {os.path.join(self.slot, 'Vintagestory.exe')}", lines[0])
        self.assertEqual("arg: --tracelog", lines[1])
        self.assertFalse([l for l in lines if "Vintagestory.dll" in l], lines)
        self.assertEqual(f"gpu-stub: {os.path.join(self.slot, 'Vintagestory.exe')} dry-run", lines[-1])

    def test_no_build_uses_the_staged_folder_without_a_hand_over(self):
        # Fails if -NoBuild still stages inside WSL.
        lines = self.client(["-NoBuild"])
        self.assertFalse([l for l in lines if l.startswith("wsl: ")], lines)
        self.assertNotIn("staged on windows", lines)

    def test_a_missing_windows_runtime_stops_naming_it_without_provisioning(self):
        # Fails if a checkout on a share provisions .NET into the share's .dotnet (the stub then
        # throws) instead of stopping with one line.
        lines = self.client(["-NoBuild", "-DryRun"], missing="10", want_code=1)
        self.assertEqual(["exmod: Windows has no .NET 10 runtime; install it for Windows (https://dot.net)."], lines)


# Stubs reg.exe for Register-ClientGpuPreference from WSL: each call's arguments are kept, joined by
# '|', in $global:RegCalls; a query answers with reg.exe's own layout when $env:TEST_VALUE or
# $env:TEST_EMPTY is set, typed $env:TEST_TYPE (REG_SZ when unset) and holding $env:TEST_VALUE or,
# under TEST_EMPTY, nothing, and exits 1 otherwise; an add exits with $env:TEST_ADD_EXIT, 0 when
# unset.
REG_EXE = r"""
$OnWindows = $false
$global:RegCalls = @()
function reg.exe {
  $global:RegCalls += ,($args -join '|')
  if ($args[0] -eq 'query') {
    if (-not $env:TEST_VALUE -and -not $env:TEST_EMPTY) { $global:LASTEXITCODE = 1; return }
    $global:LASTEXITCODE = 0
    $type = $env:TEST_TYPE ? $env:TEST_TYPE : 'REG_SZ'
    ''; 'HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences'
    "    $($args[3])    $type    $env:TEST_VALUE"; ''
    return
  }
  $global:LASTEXITCODE = [int]$env:TEST_ADD_EXIT
}
"""

# Stubs the registry cmdlets for Register-ClientGpuPreference on Windows: the key exists when
# $env:TEST_KEY is set and holds, under any name, the string $env:TEST_VALUE, an empty string under
# $env:TEST_EMPTY, or the DWORD 1 under $env:TEST_DWORD; New-Item and New-ItemProperty are kept in
# $global:RegCalls as their parameters joined by '|'.
HKCU_DRIVE = r"""
$OnWindows = $true
$global:RegCalls = @()
function Test-Path { [bool]$env:TEST_KEY }
function Get-Item {
  if (-not $env:TEST_KEY) { return $null }
  [pscustomobject]@{} | Add-Member -MemberType ScriptMethod -Name GetValue -Value {
    param($n)
    if ($env:TEST_DWORD) { return [int]1 }
    if ($env:TEST_EMPTY) { return '' }
    $env:TEST_VALUE
  } -PassThru
}
function New-Item([string]$Path, [switch]$Force) { $global:RegCalls += ,"New-Item|$Path" }
function New-ItemProperty([string]$LiteralPath, [string]$Name, [string]$Value, [string]$PropertyType, [switch]$Force) {
  $global:RegCalls += ,"New-ItemProperty|$LiteralPath|$Name|$Value|$PropertyType"
}
"""

EXE = r"C:\Users\A B\AppData\Local\exmod\game\1.22\Vintagestory.exe"
KEY = r"HKCU\Software\Microsoft\DirectX\UserGpuPreferences"


@unittest.skipUnless(PWSH, "pwsh not found")
class GpuPreferenceTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.repo = os.path.join(self.tmp, "repo")
        touch(os.path.join(self.repo, "exmod.json"), "{}")

    def register(self, stubs, dry_run=False, **env):
        """(stdout lines, recorded registry calls) of one real Register-ClientGpuPreference for
        EXE."""
        body = (stubs + f"Register-ClientGpuPreference '{EXE}'{' -DryRun' if dry_run else ''} 3>&1 | "
                "ForEach-Object { Write-Host \"$_\" }; $global:RegCalls | ForEach-Object { Write-Host \"call: $_\" }")
        code, lines = run(self.repo, body, self.tmp, env, stub_gpu=False)
        self.assertEqual(0, code, lines)
        said = [l for l in lines if not l.startswith("call: ")]
        return said, [l[len("call: "):] for l in lines if l.startswith("call: ")]

    def test_from_wsl_an_exe_without_a_value_gets_high_performance_through_reg_exe(self):
        # Fails if nothing is written, the exe path is split at its space, or the data is not
        # GpuPreference=2; as REG_SZ.
        said, calls = self.register(REG_EXE)
        self.assertEqual([f"query|{KEY}|/v|{EXE}",
                          f"add|{KEY}|/v|{EXE}|/t|REG_SZ|/d|GpuPreference=2;|/f"], calls)
        self.assertEqual([f"Registered GpuPreference=2; (the high-performance GPU) for {EXE}"], said)

    def test_from_wsl_an_existing_value_is_kept(self):
        # Fails if a value the user set is overwritten.
        said, calls = self.register(REG_EXE, TEST_VALUE="AppStatus=1;GpuPreference=1;")
        self.assertEqual([f"query|{KEY}|/v|{EXE}"], calls)
        self.assertEqual([], said)

    def test_from_wsl_a_value_without_a_preference_gets_it_appended(self):
        # Fails if a value without a GpuPreference= entry is kept as it is, its other entries are
        # dropped, or no ';' goes between them and the preference.
        said, calls = self.register(REG_EXE, TEST_VALUE="AutoHDREnable=2097;")
        self.assertEqual(f"add|{KEY}|/v|{EXE}|/t|REG_SZ|/d|AutoHDREnable=2097;GpuPreference=2;|/f", calls[-1])
        self.assertEqual([f"Registered GpuPreference=2; (the high-performance GPU) for {EXE}"], said)
        _, calls = self.register(REG_EXE, TEST_VALUE="AppStatus=0")
        self.assertEqual(f"add|{KEY}|/v|{EXE}|/t|REG_SZ|/d|AppStatus=0;GpuPreference=2;|/f", calls[-1])

    def test_from_wsl_an_empty_value_is_a_value_and_gets_the_preference_alone(self):
        # Fails if an empty value is read as no value (the dry run then says none), or a ';' is put
        # before the preference in an empty value.
        said, calls = self.register(REG_EXE, TEST_EMPTY="1")
        self.assertEqual(f"add|{KEY}|/v|{EXE}|/t|REG_SZ|/d|GpuPreference=2;|/f", calls[-1])
        said, _ = self.register(REG_EXE, dry_run=True, TEST_EMPTY="1")
        self.assertEqual(["gpu: , would append GpuPreference=2;"], said)

    def test_from_wsl_a_value_that_is_not_a_string_is_kept_with_a_warning(self):
        # Fails if a value of another type is overwritten or read as no value.
        said, calls = self.register(REG_EXE, TEST_TYPE="REG_DWORD", TEST_VALUE="0x1")
        self.assertEqual([f"query|{KEY}|/v|{EXE}"], calls)
        self.assertEqual([f"The GPU preference value for {EXE} is not a string; left as it is."], said)

    def test_from_wsl_a_dry_run_prints_the_value_or_what_it_would_do_and_writes_nothing(self):
        # Fails if a dry run writes, prints nothing, reads the value with reg.exe's column padding
        # left on, or prints a value without a preference as if it held one.
        said, calls = self.register(REG_EXE, dry_run=True, TEST_VALUE="AppStatus=1;GpuPreference=1;")
        self.assertEqual(["gpu: AppStatus=1;GpuPreference=1;"], said)
        self.assertEqual([f"query|{KEY}|/v|{EXE}"], calls)
        said, calls = self.register(REG_EXE, dry_run=True)
        self.assertEqual(["gpu: none, would register GpuPreference=2;"], said)
        self.assertEqual([f"query|{KEY}|/v|{EXE}"], calls)
        said, calls = self.register(REG_EXE, dry_run=True, TEST_VALUE="AppStatus=0;")
        self.assertEqual(["gpu: AppStatus=0;, would append GpuPreference=2;"], said)
        self.assertEqual([f"query|{KEY}|/v|{EXE}"], calls)

    def test_from_wsl_a_failed_write_warns_instead_of_claiming_it_registered(self):
        # Fails if reg.exe add's exit code is ignored.
        said, _ = self.register(REG_EXE, TEST_ADD_EXIT="5")
        self.assertEqual([f"Could not register the high-performance GPU for {EXE} : reg.exe add exited with 5."], said)

    def test_on_windows_the_hkcu_drive_gets_the_value_and_the_key_when_missing(self):
        # Fails if the Windows branch writes through reg.exe (the prelude's stub then throws), skips
        # creating a missing key, or writes another name, value or type.
        drive = "HKCU:\\Software\\Microsoft\\DirectX\\UserGpuPreferences"
        said, calls = self.register(HKCU_DRIVE)
        self.assertEqual([f"New-Item|{drive}", f"New-ItemProperty|{drive}|{EXE}|GpuPreference=2;|String"], calls)
        self.assertEqual([f"Registered GpuPreference=2; (the high-performance GPU) for {EXE}"], said)
        said, calls = self.register(HKCU_DRIVE, TEST_KEY="1")
        self.assertEqual([f"New-ItemProperty|{drive}|{EXE}|GpuPreference=2;|String"], calls)

    def test_on_windows_an_existing_value_is_kept_and_a_dry_run_prints_it(self):
        # Fails if the Windows branch overwrites a value the user set, or a dry run writes.
        said, calls = self.register(HKCU_DRIVE, TEST_KEY="1", TEST_VALUE="GpuPreference=1;")
        self.assertEqual(([], []), (said, calls))
        said, calls = self.register(HKCU_DRIVE, dry_run=True, TEST_KEY="1", TEST_VALUE="GpuPreference=1;")
        self.assertEqual((["gpu: GpuPreference=1;"], []), (said, calls))
        said, calls = self.register(HKCU_DRIVE, dry_run=True)
        self.assertEqual((["gpu: none, would register GpuPreference=2;"], []), (said, calls))


    def test_on_windows_a_value_without_a_preference_gets_it_appended(self):
        # Fails if the Windows branch keeps a value without a GpuPreference= entry, drops its other
        # entries, or its dry run prints the value alone.
        drive = "HKCU:\\Software\\Microsoft\\DirectX\\UserGpuPreferences"
        said, calls = self.register(HKCU_DRIVE, TEST_KEY="1", TEST_VALUE="AppStatus=0")
        self.assertEqual([f"New-ItemProperty|{drive}|{EXE}|AppStatus=0;GpuPreference=2;|String"], calls)
        self.assertEqual([f"Registered GpuPreference=2; (the high-performance GPU) for {EXE}"], said)
        said, calls = self.register(HKCU_DRIVE, dry_run=True, TEST_KEY="1", TEST_VALUE="AppStatus=0")
        self.assertEqual((["gpu: AppStatus=0, would append GpuPreference=2;"], []), (said, calls))

    def test_on_windows_an_empty_value_is_a_value(self):
        # Fails if the Windows branch reads an empty string as no value.
        said, calls = self.register(HKCU_DRIVE, dry_run=True, TEST_KEY="1", TEST_EMPTY="1")
        self.assertEqual((["gpu: , would append GpuPreference=2;"], []), (said, calls))

    def test_on_windows_a_value_that_is_not_a_string_is_kept_with_a_warning(self):
        # Fails if the Windows branch overwrites a value of another type.
        said, calls = self.register(HKCU_DRIVE, TEST_KEY="1", TEST_DWORD="1")
        self.assertEqual(([f"The GPU preference value for {EXE} is not a string; left as it is."], []), (said, calls))

if __name__ == "__main__":
    unittest.main()
