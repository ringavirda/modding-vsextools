"""Tests for the install lookup, the user store and the data profile in exmod.ps1 and the commands
that use them, run through pwsh ($PWSH, PATH, or the checkout's .dotnet/tools) on a temporary
repository with exmod_harness's prelude; skipped when no pwsh is found. Each test names the mutation
it fails under."""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import PWSH, exmod_env, exmod_script, touch  # noqa: E402


def make_repo(path):
    touch(os.path.join(path, "exmod.json"), "{}")
    return path


def run(repo, body, home, xdg=None, extra_env=None):
    """Runs `body` after exmod.ps1 and the prelude (exmod_script) against `repo` and returns the
    last line of its stdout parsed as JSON. HOME is `home`, LOCALAPPDATA `home`/local; XDG_DATA_HOME
    is `xdg`, or unset when None; `extra_env` goes over all three."""
    assert PWSH
    script = exmod_script(body)
    env = exmod_env(os.path.join(home, "local"), TEST_REPO=repo, HOME=home)
    env.pop("XDG_DATA_HOME", None)
    if xdg is not None:
        env["XDG_DATA_HOME"] = xdg
    env.update(extra_env or {})
    out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", script],
                         env=env, capture_output=True, text=True)
    if out.returncode != 0 and not out.stdout.strip():
        raise AssertionError(f"pwsh failed ({out.returncode}): {out.stderr}")
    return json.loads(out.stdout.strip().splitlines()[-1])


def places(repo, home, xdg=None):
    """Every resolver's answer for `repo`, by name."""
    return run(repo, "ConvertTo-Json -Compress @{ "
                     "Workspace = Get-ExmodWorkspaceRoot; ProvisionRoot = Get-ExmodProvisionRoot; "
                     "Store = Get-ExmodUserStore; Profile = Get-ExmodProfile; "
                     "Slot = Get-ClientSlot '1.22'; Data = Get-ClientDataPath; "
                     "Log = Get-ClientLogPath (Get-ClientDataPath); Dotnet = Get-ExmodDotnetDir }",
               home, xdg)


@unittest.skipUnless(PWSH, "pwsh not found")
class PlacesTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.home = os.path.join(self.tmp, "home dir")
        os.makedirs(self.home)
        self.ws = os.path.join(self.tmp, "ws")
        touch(os.path.join(self.ws, "exmod.workspace.json"), "{}")

    def test_a_marker_two_levels_up_is_the_workspace_and_leaves_the_profile_default(self):
        # Fails if the walk stops at the parent, or the profile is named for the workspace folder.
        repo = make_repo(os.path.join(self.ws, "lines", "exmods"))
        got = places(repo, self.home)
        self.assertEqual(self.ws, got["Workspace"])
        self.assertEqual("default", got["Profile"])
        self.assertEqual(self.ws, got["ProvisionRoot"])

    def test_no_marker_gives_no_workspace_the_default_profile_and_the_repository_as_provision_root(self):
        # Fails if the profile is named for the repository folder, or the provision root is the
        # parent.
        repo = make_repo(os.path.join(self.tmp, "alone", "starter"))
        got = places(repo, self.home)
        self.assertIsNone(got["Workspace"])
        self.assertEqual("default", got["Profile"])
        self.assertEqual(repo, got["ProvisionRoot"])

    def test_a_marker_in_the_repository_itself_is_not_a_workspace(self):
        # Fails if the walk starts at $RepoRoot.
        repo = make_repo(os.path.join(self.tmp, "alone", "starter"))
        touch(os.path.join(repo, "exmod.workspace.json"), "{}")
        self.assertIsNone(places(repo, self.home)["Workspace"])

    def test_the_repositorys_own_install_beats_the_workspaces(self):
        # Fails if the workspace is searched before the repository.
        repo = make_repo(os.path.join(self.ws, "exlib"))
        touch(os.path.join(repo, ".game", "1.22", "VintagestoryAPI.dll"))
        touch(os.path.join(self.ws, ".game", "1.22", "VintagestoryAPI.dll"))
        got = run(repo, "ConvertTo-Json -Compress @(Find-ExmodAbove '.game/1.22/VintagestoryAPI.dll')", self.home)
        self.assertEqual([os.path.join(repo, ".game", "1.22", "VintagestoryAPI.dll")], got)

    def test_only_the_workspaces_install_is_found_from_the_repository(self):
        # Fails if the search does not climb past $RepoRoot.
        repo = make_repo(os.path.join(self.ws, "exlib"))
        touch(os.path.join(self.ws, ".game", "1.22", "VintagestoryAPI.dll"))
        got = run(repo, "ConvertTo-Json -Compress @{ Present = Find-ExmodAbove '.game/1.22/VintagestoryAPI.dll'; "
                        "Absent = Find-ExmodAbove '.game/1.21/VintagestoryAPI.dll' }", self.home)
        self.assertEqual(os.path.join(self.ws, ".game", "1.22", "VintagestoryAPI.dll"), got["Present"])
        self.assertIsNone(got["Absent"])

    def test_install_candidates_are_the_client_slot_then_the_nearest_game_folders_holding_the_entry(self):
        # Fails if the slot is dropped, an entry-less folder is listed, or the platform slot is skipped.
        repo = make_repo(os.path.join(self.ws, "exlib"))
        touch(os.path.join(self.ws, ".game", "1.22-client", "Vintagestory.dll"))
        touch(os.path.join(self.ws, ".game", "1.22-linux", "Vintagestory.dll"))
        touch(os.path.join(repo, ".game", "1.22", "VintagestoryAPI.dll"))
        touch(os.path.join(self.ws, ".game", "1.22", "Vintagestory.dll"))
        got = run(repo, "ConvertTo-Json -Compress @(Get-GameInstallCandidates '1.22' 'client')", self.home)
        self.assertEqual([
            os.path.join(self.home, ".local", "share", "exmod", "game", "1.22"),
            os.path.join(self.ws, ".game", "1.22-client"),
            os.path.join(self.ws, ".game", "1.22-linux"),
            os.path.join(self.ws, ".game", "1.22"),
        ], got)

    def test_a_usable_client_in_the_store_is_found_before_one_in_the_tree(self):
        # Fails if Find-UsableGameInstall searches the tree before the client slot.
        repo = make_repo(os.path.join(self.ws, "exlib"))
        slot = os.path.join(self.home, ".local", "share", "exmod", "game", "1.22")
        for d in (slot, os.path.join(self.ws, ".game", "1.22")):
            touch(os.path.join(d, "Vintagestory.dll"))
            touch(os.path.join(d, "Lib", "libe_sqlite3.so"))
        got = run(repo, "ConvertTo-Json -Compress @(Find-UsableGameInstall '1.22' 'client')", self.home)
        self.assertEqual([slot], got)

    def test_resolve_game_install_and_the_smoke_server_find_the_workspaces_installs(self):
        # Fails if Resolve-GameInstall skips the client slot, or Resolve-SmokeServer looks only
        # under $RepoRoot (either then provisions, which the stub turns into a throw).
        repo = make_repo(os.path.join(self.ws, "exlib"))
        slot = os.path.join(self.home, ".local", "share", "exmod", "game", "1.22")
        touch(os.path.join(slot, "Vintagestory.dll"))
        touch(os.path.join(slot, "Lib", "libe_sqlite3.so"))
        touch(os.path.join(self.ws, ".game", "1.22-server", "VintagestoryServer.dll"))
        got = run(repo, "function Invoke-ProvisionGame { throw 'provisioned' }; "
                        "ConvertTo-Json -Compress @{ Client = Resolve-GameInstall '1.22' 'client'; "
                        "Smoke = Resolve-SmokeServer '1.22' }", self.home)
        self.assertEqual(slot, got["Client"])
        self.assertEqual(os.path.join(self.ws, ".game", "1.22-server"), got["Smoke"])

    def test_the_series_override_variable_is_the_first_install_candidate(self):
        # Fails if Get-GameInstallCandidates ignores the override, or reads another series' variable.
        repo = make_repo(os.path.join(self.ws, "exlib"))
        got = run(repo, "$env:VINTAGE_STORY_121 = '/override 121'; $env:VINTAGE_STORY = '/override'; "
                        "ConvertTo-Json -Compress @{ S121 = @(Get-GameInstallCandidates '1.21' 'server'); "
                        "S122 = @(Get-GameInstallCandidates '1.22' 'client'); S120 = @(Get-GameInstallCandidates '1.20' 'client') }",
                  self.home)
        store = os.path.join(self.home, ".local", "share", "exmod", "game")
        self.assertEqual(["/override 121", os.path.join(store, "1.21")], got["S121"])
        self.assertEqual(["/override", os.path.join(store, "1.22")], got["S122"])
        self.assertEqual([os.path.join(store, "1.20")], got["S120"])

    def test_the_smoke_server_takes_the_override_before_the_workspaces_install(self):
        # Fails if Resolve-SmokeServer ignores the override, or takes one that holds no server.
        repo = make_repo(os.path.join(self.ws, "exlib"))
        override = os.path.join(self.tmp, "vs override")
        touch(os.path.join(self.ws, ".game", "1.22-server", "VintagestoryServer.dll"))
        body = "function Invoke-ProvisionGame { throw 'provisioned' }; ConvertTo-Json -Compress @(Resolve-SmokeServer '1.22')"
        env = {"VINTAGE_STORY": override}
        self.assertEqual([os.path.join(self.ws, ".game", "1.22-server")], run(repo, body, self.home, extra_env=env))
        touch(os.path.join(override, "VintagestoryServer.dll"))
        self.assertEqual([override], run(repo, body, self.home, extra_env=env))

    def test_the_store_is_under_home_whatever_xdg_data_home_holds(self):
        # Fails if XDG_DATA_HOME is read, or the store is anywhere but ~/.local/share/exmod.
        repo = make_repo(os.path.join(self.ws, "exlib"))
        store = os.path.join(self.home, ".local", "share", "exmod")
        self.assertEqual(store, places(repo, self.home)["Store"])
        self.assertEqual(store, places(repo, self.home, os.path.join(self.tmp, "xdg data"))["Store"])

    def test_slot_data_and_log_paths_are_under_the_store_by_series_profile_and_repository(self):
        # Fails if the log folder is named for the profile, or the data folder for the workspace.
        repo = make_repo(os.path.join(self.ws, "exmods-legacy"))
        store = os.path.join(self.home, ".local", "share", "exmod")
        got = places(repo, self.home)
        self.assertEqual(os.path.join(store, "game", "1.22"), got["Slot"])
        self.assertEqual(os.path.join(store, "data", "default"), got["Data"])
        self.assertEqual(os.path.join(store, "data", "default", "Logs", "exmods-legacy"), got["Log"])

    def test_dotnet_is_the_nearest_one_holding_a_muxer_else_under_the_provision_root(self):
        # Fails if .dotnet stays pinned to $RepoRoot, or a .dotnet with no muxer is taken (the dotnet
        # CLI keeps one in the home and temp folders).
        repo = make_repo(os.path.join(self.ws, "exlib"))
        os.makedirs(os.path.join(repo, ".dotnet", "tools"))
        self.assertEqual(os.path.join(self.ws, ".dotnet"), places(repo, self.home)["Dotnet"])
        touch(os.path.join(self.ws, ".dotnet", "dotnet"))
        self.assertEqual(os.path.join(self.ws, ".dotnet"), places(repo, self.home)["Dotnet"])
        touch(os.path.join(repo, ".dotnet", "dotnet"))
        self.assertEqual(os.path.join(repo, ".dotnet"), places(repo, self.home)["Dotnet"])

    def test_dotnet_outside_a_workspace_is_never_taken_from_above_the_repository(self):
        # Fails if the search climbs past $RepoRoot without a marker (a home ~/.dotnet holding the
        # user's own dotnet would be taken).
        touch(os.path.join(self.home, ".dotnet", "dotnet"))
        repo = make_repo(os.path.join(self.home, "src", "starter"))
        self.assertEqual(os.path.join(repo, ".dotnet"), places(repo, self.home)["Dotnet"])

    def test_dotnet_inside_a_workspace_is_never_taken_from_above_the_workspace_root(self):
        # Fails if the search climbs past the workspace root.
        touch(os.path.join(self.tmp, ".dotnet", "dotnet"))
        repo = make_repo(os.path.join(self.ws, "exlib"))
        self.assertEqual(os.path.join(self.ws, ".dotnet"), places(repo, self.home)["Dotnet"])


# Stubs the api patch step of provision game. With an install already in place the call downloads
# nothing and only reports where it looked.
PROVISION = ("function Publicize-GameApi { }; "
             "$said = @(Invoke-ProvisionGame @('-Version', '1.22.7', '-Kind', $env:TEST_KIND) 6>&1 | "
             "ForEach-Object { \"$_\" }); ConvertTo-Json -Compress $said")


def seed_install(path, client):
    touch(os.path.join(path, "VintagestoryAPI.dll"))
    touch(os.path.join(path, ".vsversion"), "1.22.7")
    if client:
        touch(os.path.join(path, "Vintagestory.dll"))
        touch(os.path.join(path, "Lib", "libe_sqlite3.so"))


@unittest.skipUnless(PWSH, "pwsh not found")
class ProvisionDefaultsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.home = os.path.join(self.tmp, "home")
        os.makedirs(self.home)
        self.ws = os.path.join(self.tmp, "ws")
        touch(os.path.join(self.ws, "exmod.workspace.json"), "{}")
        self.repo = make_repo(os.path.join(self.ws, "exlib"))

    def provision(self, kind):
        return run(self.repo, PROVISION, self.home, extra_env={"TEST_KIND": kind})

    def test_a_server_defaults_to_the_workspace_game_folder_and_cache(self):
        # Fails if the default -Dest or the cache stays under $RepoRoot.
        seed_install(os.path.join(self.ws, ".game", "1.22"), client=False)
        said = self.provision("server")
        self.assertIn(f"Vintage Story 1.22.7 (server) already provisioned at {os.path.join(self.ws, '.game', '1.22')}", said)
        self.assertTrue(os.path.isdir(os.path.join(self.ws, ".game", ".cache")))
        self.assertFalse(os.path.exists(os.path.join(self.repo, ".game")))

    def test_a_client_defaults_to_the_store_slot(self):
        # Fails if a client defaults to the provision root's .game.
        slot = os.path.join(self.home, ".local", "share", "exmod", "game", "1.22")
        seed_install(slot, client=True)
        said = self.provision("client")
        self.assertIn(f"Vintage Story 1.22.7 (client) already provisioned at {slot}", said)

    def test_a_server_beside_another_platforms_client_is_redirected_under_the_same_root(self):
        # Fails if the redirect joins the suffixed slot onto $RepoRoot.
        foreign = os.path.join(self.ws, ".game", "1.22")
        touch(os.path.join(foreign, "Vintagestory.dll"))
        seed_install(os.path.join(self.ws, ".game", "1.22-server"), client=False)
        said = self.provision("server")
        self.assertIn(f"Vintage Story 1.22.7 (server) already provisioned at {foreign}-server", said)

    @unittest.skipUnless(sys.platform == "win32", "only Windows has paths rooted but not fully qualified")
    def test_a_dest_rooted_on_the_current_drive_lands_under_the_repository(self):
        # Fails if -Dest is taken as given when rooted but not fully qualified (IsPathRooted): the
        # install then goes to <current drive>:\<name>, which holds none, and the stubbed download
        # throws.
        name = "exmod-dest-" + os.path.basename(self.tmp)
        seed_install(os.path.join(self.repo, name), client=False)
        said = run(self.repo, "function Publicize-GameApi { }; "
                              "$said = @(Invoke-ProvisionGame @('-Version', '1.22.7', '-Dest', $env:TEST_DEST) 6>&1 | "
                              "ForEach-Object { \"$_\" }); ConvertTo-Json -Compress $said",
                   self.home, extra_env={"TEST_DEST": "\\" + name})
        self.assertIn(f"Vintage Story 1.22.7 (server) already provisioned at \\{name}", said)


# provision game as Windows runs it, with the download, the installer and reg stubbed: the installer
# fills the folder its /DIR= names with a client, and the uninstall entry is never found.
ON_WINDOWS = r"""
$OnWindows = $true
function Publicize-GameApi { }
function reg { $global:LASTEXITCODE = 1 }
function Invoke-WebRequest([string]$Uri, [string]$OutFile) { Set-Content -LiteralPath $OutFile -Value '' }
function Start-Process([string]$FilePath, [string[]]$ArgumentList) {
  $dir = ($ArgumentList | Where-Object { $_ -like '/DIR=*' }).Substring(5)
  New-Item -ItemType Directory -Force -Path (Join-Path $dir 'Lib') | Out-Null
  foreach ($f in 'VintagestoryAPI.dll', 'Vintagestory.dll', 'Vintagestory.exe', 'Lib/e_sqlite3.dll') {
    Set-Content -LiteralPath (Join-Path $dir $f) -Value ''
  }
  [pscustomobject]@{ ExitCode = 0 }
}
$said = @(Invoke-ProvisionGame @('-Version', '1.22.7', '-Kind', $env:TEST_KIND) 6>&1 | ForEach-Object { "$_" })
ConvertTo-Json -Compress $said
"""


@unittest.skipUnless(PWSH, "pwsh not found")
class ProvisionGpuTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.repo = make_repo(os.path.join(self.tmp, "repo"))
        self.slot = os.path.join(self.tmp, "local", "exmod", "game", "1.22")
        self.exe = os.path.join(self.slot, "Vintagestory.exe")

    def provision(self, kind):
        return run(self.repo, ON_WINDOWS, self.tmp,
                   extra_env={"TEST_KIND": kind, "LOCALAPPDATA": os.path.join(self.tmp, "local")})

    def test_installing_a_windows_client_registers_its_exe_after_the_install(self):
        # Fails if a fresh Windows client install registers no GPU preference, or registers it
        # before the install lands.
        said = self.provision("client")
        self.assertEqual([f"Provisioned Vintage Story 1.22.7 (client) at {self.slot}", f"gpu-stub: {self.exe}"], said[-2:])

    def test_a_windows_client_already_in_place_registers_its_exe(self):
        # Fails if the already-provisioned return skips the registration.
        seed_install(self.slot, client=True)
        touch(os.path.join(self.slot, "Lib", "e_sqlite3.dll"))
        said = self.provision("client")
        self.assertEqual([f"Vintage Story 1.22.7 (client) already provisioned at {self.slot}", f"gpu-stub: {self.exe}"], said)

    def test_a_windows_server_registers_nothing(self):
        # Fails if the registration runs for a server install.
        seed_install(os.path.join(self.repo, ".game", "1.22"), client=False)
        said = self.provision("server")
        self.assertFalse([s for s in said if s.startswith("gpu-stub: ")], said)


# Stubs the install, staging and dotnet lookup of `client` and prints the arguments it would launch
# the game with, one per line, as a JSON array.
CLIENT = ("function Find-UsableGameInstall { '/game' }; function Publish-RunMods { '/mods' }; "
          "function Resolve-DotnetHost { 'Show-Args' }; "
          "function Show-Args { ConvertTo-Json -Compress @($args) }; "
          "Invoke-Client @('-NoBuild')")


@unittest.skipUnless(PWSH, "pwsh not found")
class ClientAndLogsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.home = os.path.join(self.tmp, "home")
        os.makedirs(self.home)
        self.ws = os.path.join(self.tmp, "ws")
        touch(os.path.join(self.ws, "exmod.workspace.json"), "{}")
        self.repo = make_repo(os.path.join(self.ws, "exmods"))
        self.data = os.path.join(self.home, ".local", "share", "exmod", "data", "default")

    @unittest.skipIf(sys.platform == "win32", "on Windows Invoke-Client launches Vintagestory.exe, not the dotnet host CLIENT stubs")
    def test_client_passes_the_store_data_path_and_the_repository_log_path(self):
        # Fails if --logPath is dropped or --dataPath is not the store's profile.
        got = run(self.repo, CLIENT, self.home)
        self.assertEqual(["/game/Vintagestory.dll", "--tracelog", "--dataPath", self.data,
                          "--logPath", os.path.join(self.data, "Logs", "exmods"), "--addModPath", "/mods"], got)

    def test_a_dry_run_prints_the_local_dotnet_without_provisioning_it(self):
        # Fails if -DryRun still lets Resolve-DotnetHost provision (the stub then throws).
        got = run(self.repo, "function dotnet { }; function Invoke-ProvisionDotnet { throw 'provisioned' }; "
                                          "function Find-UsableGameInstall { '/game' }; "
                                          "$said = @(Invoke-Client @('1.21', '-NoBuild', '-DryRun') 6>&1 | ForEach-Object { \"$_\" }); "
                                          "ConvertTo-Json -Compress $said", self.home)
        muxer = os.path.join(self.ws, ".dotnet", "dotnet")
        self.assertIn(f"program: {muxer}", got)
        self.assertIn(f"env: DOTNET_ROOT={os.path.join(self.ws, '.dotnet')}", got)
        self.assertFalse(os.path.exists(os.path.join(self.ws, ".dotnet")))

    def logs(self):
        return run(self.repo, "$said = @(Invoke-Logs @('client', '-Lines', '1') 6>&1 | ForEach-Object { \"$_\" }); "
                              "ConvertTo-Json -Compress $said", self.home)

    def test_logs_reads_the_repository_folder_when_it_exists(self):
        # Fails if logs client ignores the per-repository folder.
        touch(os.path.join(self.data, "Logs", "client-main.log"), "shared\n")
        touch(os.path.join(self.data, "Logs", "exmods", "client-main.log"), "own\n")
        self.assertEqual("own", self.logs()[-1])

    def test_logs_falls_back_to_the_data_logs_folder(self):
        # Fails if the fallback to <data>/Logs is dropped.
        touch(os.path.join(self.data, "Logs", "client-main.log"), "shared\n")
        self.assertEqual("shared", self.logs()[-1])


if __name__ == "__main__":
    unittest.main()
