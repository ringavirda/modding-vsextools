"""Tests for the WSL hand-over and the Windows client from WSL in exmod.ps1 and exmod/run.ps1, run
through pwsh ($PWSH, PATH, or the checkout's .dotnet/tools) on a temporary repository with the
Windows and interop lookups stubbed; skipped when no pwsh is found. Each test names the mutation it
fails under."""

import json
import os
import shutil
import stat
import subprocess
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PWSH = (os.environ.get("PWSH") or shutil.which("pwsh")
        or next((p for p in [os.path.join(ROOT, ".dotnet", "tools", "pwsh")] if os.access(p, os.X_OK)), None))


def touch(path, text=""):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        f.write(text)


def run(repo, body, home, extra_env=None):
    """Dot-sources exmod.ps1 against `repo` with its load output discarded, runs `body`, and returns
    (exit code, stdout lines). HOME is `home`; XDG_DATA_HOME is unset."""
    script = f". (Join-Path $env:EXTOOLS_ROOT 'exmod.ps1') -RepoRoot $env:TEST_REPO 6>$null; {body}"
    env = dict(os.environ, EXTOOLS_ROOT=ROOT, TEST_REPO=repo, HOME=home, **(extra_env or {}))
    env.pop("XDG_DATA_HOME", None)
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

    def test_windows_side_commands_are_client_logs_machine_and_client_provisioning_only(self):
        # Fails if provision is treated as one case, or a machine-group command is handed to WSL.
        got = self.json("ConvertTo-Json -Compress @("
                        "(Test-ExmodWindowsSideCommand 'client' @()), (Test-ExmodWindowsSideCommand 'logs' @()), "
                        "(Test-ExmodWindowsSideCommand 'fix-registry' @()), "
                        "(Test-ExmodWindowsSideCommand 'provision' @('game', '-Version', '1.22', '-Kind', 'client')), "
                        "(Test-ExmodWindowsSideCommand 'provision' @('game', '-Version', '1.22', '-Kind', 'server')), "
                        "(Test-ExmodWindowsSideCommand 'provision' @('game', '-Version', '1.22')), "
                        "(Test-ExmodWindowsSideCommand 'stage' @()), (Test-ExmodWindowsSideCommand 'build' @()))")
        self.assertEqual([True, True, True, True, False, False, False, False], got)


# Stubs every Windows and interop lookup for the WSL-to-Windows client. The Windows store and dotnet
# carry spaces; a Windows path C:\... maps to $env:TEST_C/... and a Linux path to
# \\wsl.localhost\test<path>. $env:TEST_INTEROP, $env:TEST_STORE, $env:TEST_DOTNET and $env:TEST_PWSH
# switch the interop check, the store and the two programs off when set to 0. The build and the stage return the stage
# folder without touching it.
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
function Resolve-DotnetHost { 'dotnet' }
"""


def executable(path, text):
    touch(path, "#!/usr/bin/env bash\n" + text)
    os.chmod(path, os.stat(path).st_mode | stat.S_IEXEC)


@unittest.skipUnless(PWSH, "pwsh not found")
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
        touch(os.path.join(self.slot, "Vintagestory.dll"))
        touch(os.path.join(self.slot, "Lib", "e_sqlite3.dll"))

    def seed_linux_client(self):
        touch(os.path.join(self.linux_slot, "Vintagestory.dll"))
        touch(os.path.join(self.linux_slot, "Lib", "libe_sqlite3.so"))

    def client(self, args, **env):
        base = {"TEST_C": self.c, "WSL_DISTRO_NAME": "test"}
        base.update(env)
        argv = ", ".join(f"'{a}'" for a in args)
        return run(self.repo, STUBS + f"Invoke-Client @({argv})", self.home, base)

    def test_dry_run_with_interop_prints_the_windows_client_with_spaces_unsplit(self):
        # Fails if a Windows path is split at its space, the program is not dotnet's Linux view, or a
        # dry run stages or seeds settings.
        self.seed_windows_client()
        code, lines = self.client(["-NoBuild", "-DryRun"])
        self.assertEqual(0, code, lines)
        data = self.win_store + r"\data\ws"
        self.assertEqual([
            f"program: {self.c}/Program Files/dotnet/dotnet.exe",
            f"arg: {self.win_store}\\game\\1.22\\Vintagestory.dll",
            "arg: --tracelog", "arg: --dataPath", f"arg: {data}",
            "arg: --logPath", f"arg: {data}\\Logs\\exmods",
            "arg: --addModPath", "arg: \\\\wsl.localhost\\test" + os.path.join(self.repo, "bin", "Mods").replace("/", "\\"),
        ], lines)
        self.assertFalse(os.path.exists(os.path.join(self.c, "Users", "A B", "AppData", "Local", "exmod", "data")))

    def test_dry_run_with_interop_off_prints_the_linux_client_and_why(self):
        # Fails if the WSL branch ignores the interop check, or the reason line is dropped.
        self.seed_windows_client()
        self.seed_linux_client()
        code, lines = self.client(["-NoBuild", "-DryRun"], TEST_INTEROP="0")
        self.assertEqual(0, code, lines)
        self.assertIn("WSL interop is off (no /proc/sys/fs/binfmt_misc/WSLInterop), so the Linux client runs.", lines)
        self.assertIn("program: dotnet", lines)
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
        # Fails if -Provision does not run this tools checkout's provision game in Windows' pwsh, or
        # the launch does not seed settings through the Linux view of the Windows data path.
        record = os.path.join(self.tmp, "calls.txt")
        pwsh = os.path.join(self.c, "Program Files", "PowerShell", "7", "pwsh.exe")
        executable(pwsh, f'printf "pwsh:%s\\n" "$@" >> "{record}"\n'
                         f'mkdir -p "{self.slot}/Lib"; touch "{self.slot}/Vintagestory.dll" "{self.slot}/Lib/e_sqlite3.dll"\n')
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
        self.assertEqual(f"dotnet:{self.win_store}\\game\\1.22\\Vintagestory.dll", calls[13])
        self.assertIn("staged", lines)
        data = os.path.join(self.c, "Users", "A B", "AppData", "Local", "exmod", "data", "ws")
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

    def test_logs_reads_the_windows_store_under_interop(self):
        # Fails if logs client ignores the interop check or -Linux, or reads on past an unreadable store.
        data = os.path.join(self.c, "Users", "A B", "AppData", "Local", "exmod", "data", "ws")
        touch(os.path.join(data, "Logs", "exmods", "client-main.log"), "windows\n")
        touch(os.path.join(self.home, ".local", "share", "exmod", "data", "ws", "Logs", "exmods", "client-main.log"), "linux\n")
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
# hand-over and the client lookup recorded, the launch printed.
ON_SHARE = r"""
$OnWindows = $true
function Get-WslShare { [pscustomobject]@{ Distro = 'arch'; LinuxPath = '/src/repo' } }
function Invoke-ExmodInWsl([string]$Distro, [string]$LinuxPath, [string[]]$Argv) { Write-Host "wsl: $Distro $LinuxPath $($Argv -join ' ')"; 0 }
function Find-UsableGameInstall([string]$Version, [string]$Kind, [switch]$SlotOnly) { if ($SlotOnly) { '/slot' } else { '/tree' } }
function Publish-RunMods { Write-Host 'staged on windows'; '/windows-stage' }
function Resolve-DotnetHost { 'Show-Args' }
function Show-Args { Write-Host "launch: $($args -join ' ')" }
"""


@unittest.skipUnless(PWSH, "pwsh not found")
class ClientOnWslShareTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.repo = os.path.join(self.tmp, "repo")
        touch(os.path.join(self.repo, "exmod.json"), "{}")

    def client(self, args):
        argv = ", ".join(f"'{a}'" for a in args)
        code, lines = run(self.repo, ON_SHARE + f"Invoke-Client @({argv})", self.tmp,
                          {"LOCALAPPDATA": os.path.join(self.tmp, "local")})
        self.assertEqual(0, code, lines)
        return lines

    def test_client_stages_inside_wsl_and_runs_the_store_client_on_the_stage_folder(self):
        # Fails if the client stages on Windows, the stage is not handed to the distro, or an in-tree
        # client on the share is taken.
        lines = self.client([])
        self.assertIn("wsl: arch /src/repo stage -Version 1.22 -Configuration Debug", lines)
        self.assertNotIn("staged on windows", lines)
        launch = [l for l in lines if l.startswith("launch: ")]
        self.assertEqual(1, len(launch), lines)
        self.assertTrue(launch[0].startswith("launch: /slot/Vintagestory.dll --tracelog"), launch)
        self.assertTrue(launch[0].endswith(f"--addModPath {os.path.join(self.repo, 'bin', 'Mods')}"), launch)

    def test_no_build_uses_the_staged_folder_without_a_hand_over(self):
        # Fails if -NoBuild still stages inside WSL.
        lines = self.client(["-NoBuild"])
        self.assertFalse([l for l in lines if l.startswith("wsl: ")], lines)
        self.assertNotIn("staged on windows", lines)


if __name__ == "__main__":
    unittest.main()
