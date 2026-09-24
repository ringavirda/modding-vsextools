"""Tests for the VS Code files Write-ExmodVsCode generates (exmod/new.ps1) and for the pwsh search in
the launcher wrapper wrappers/exmod.sh. The generator runs through pwsh ($PWSH, PATH, or the
checkout's .dotnet/tools) with exmod_harness's prelude and is skipped when no pwsh is found; the
wrapper tests run bash against stub pwsh and dotnet executables. Each test names the mutation it
fails under."""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import PWSH, ROOT, exmod_env, exmod_script  # noqa: E402

STORES = {
    "linux": "${env:HOME}/.local/share/exmod",
    "osx": "${env:HOME}/Library/Application Support/exmod",
    "windows": "${env:LOCALAPPDATA}/exmod",
}
ENTRY = {"linux": "Vintagestory.dll", "osx": "Vintagestory.dll", "windows": "Vintagestory.exe"}


def touch(path, text="", mode=None):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        f.write(text)
    if mode is not None:
        os.chmod(path, mode)


def read_jsonc(path):
    """The JSON at `path` with its whole-line `//` comments dropped."""
    with open(path) as f:
        return json.loads("\n".join(l for l in f.read().splitlines() if not l.lstrip().startswith("//")))


def os_view(config, name):
    """The program and args VS Code launches `config` with on OS `name`: the OS block's values over
    the top-level ones."""
    block = config.get(name, {})
    return block.get("program", config["program"]), block.get("args", config["args"])


@unittest.skipUnless(PWSH, "pwsh not found")
class TemplateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        assert PWSH
        cls.tmp = os.path.realpath(tempfile.mkdtemp())
        touch(os.path.join(cls.tmp, "exmod.json"), "{}")
        script = exmod_script("Write-ExmodVsCode -Dest $env:TEST_REPO -RepoName 'demo' -Series @('1.22', '1.21')")
        env = exmod_env(os.path.join(cls.tmp, "local"), TEST_REPO=cls.tmp)
        out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", script],
                             env=env, capture_output=True, text=True)
        if out.returncode != 0:
            shutil.rmtree(cls.tmp)
            raise AssertionError(f"pwsh failed ({out.returncode}): {out.stderr}")
        cls.launch = read_jsonc(os.path.join(cls.tmp, ".vscode", "launch.json"))
        cls.tasks = read_jsonc(os.path.join(cls.tmp, ".vscode", "tasks.json"))

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp)

    def configs(self):
        return {c["name"]: c for c in self.launch["configurations"]}

    def test_no_program_or_argument_names_an_in_tree_install_or_data_folder(self):
        # Fails if any OS keeps the .game/<series>-<platform> program or the .gamedata data path.
        for config in self.launch["configurations"]:
            for name in STORES:
                program, args = os_view(config, name)
                for value in [program] + args:
                    self.assertNotIn(".game/", value, (config["name"], name))
                    self.assertNotIn(".gamedata", value, (config["name"], name))

    def test_each_os_runs_its_store_client_with_store_data_logs_and_the_staged_mods(self):
        # Fails if an OS block reads another OS's store, Windows runs Vintagestory.dll instead of the
        # apphost, the data folder is named for the workspace folder, or the log path drops its
        # repository folder.
        for label, slug, mods in [("demo (latest)", "1.22", "bin/Mods"), ("demo (1.21)", "1.21", "bin/Mods-1.21")]:
            config = self.configs()[label]
            for name, store in STORES.items():
                program, args = os_view(config, name)
                self.assertEqual(f"{store}/game/{slug}/{ENTRY[name]}", program, (label, name))
                self.assertEqual([
                    "--tracelog",
                    "--dataPath", f"{store}/data/default",
                    "--logPath", f"{store}/data/default/Logs/${{workspaceFolderBasename}}",
                    "--addModPath", f"${{workspaceFolder}}/{mods}",
                ], args, (label, name))

    def test_every_configuration_maps_the_path_map_prefix_back_to_the_workspace_folder(self):
        # Fails if a configuration drops sourceFileMap or maps another prefix.
        self.assertEqual(2, len(self.launch["configurations"]))
        for config in self.launch["configurations"]:
            self.assertEqual({"/exmod/${workspaceFolderBasename}/": "${workspaceFolder}/"},
                             config.get("sourceFileMap"), config["name"])

    def test_linux_runs_on_x11(self):
        # Fails if the linux block loses WAYLAND_DISPLAY=none.
        for config in self.launch["configurations"]:
            self.assertEqual("none", config["linux"]["env"]["WAYLAND_DISPLAY"], config["name"])

    def test_dotnet_root_reaches_only_the_linux_and_osx_launches_of_a_legacy_series(self):
        # Fails if the legacy configuration keeps a top-level env, which the Windows apphost inherits.
        legacy = self.configs()["demo (1.21)"]
        self.assertNotIn("env", legacy)
        self.assertNotIn("env", legacy["windows"])
        for name in ("linux", "osx"):
            self.assertEqual("${workspaceFolder}/.dotnet", legacy[name]["env"]["DOTNET_ROOT"], name)
        self.assertNotIn("DOTNET_ROOT", self.configs()["demo (latest)"]["linux"]["env"])

    def test_the_provision_tasks_install_a_client_into_the_store_on_every_os(self):
        # Fails if a provision task passes -Dest, or keeps an osx block with its own arguments.
        tasks = {t["label"]: t for t in self.tasks["tasks"]}
        for label, version in [("provision-game (1.22)", "1.22"), ("provision-game (1.21)", "1.21.0")]:
            task = tasks[label]
            want = ["provision", "game", "-Version", version, "-Kind", "client"]
            self.assertEqual(["${workspaceFolder}/scripts/exmod.sh"] + want, task["args"], label)
            self.assertEqual(want, task["windows"]["args"][-len(want):], label)
            self.assertNotIn("osx", task, label)
            for args in (task["args"], task["windows"]["args"]):
                self.assertNotIn("-Dest", args, label)


class WrapperTests(unittest.TestCase):
    """bash scripts/exmod.sh help in a repository below a workspace marker, with PWSH unset and no
    pwsh on PATH, so only the .dotnet/tools steps can supply one."""

    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.ws = os.path.join(self.tmp, "work space")
        self.repo = os.path.join(self.ws, "lines", "mods")
        touch(os.path.join(self.repo, "exmod.json"), "{}")
        os.makedirs(os.path.join(self.repo, "scripts"))
        shutil.copy2(os.path.join(ROOT, "wrappers", "exmod.sh"), os.path.join(self.repo, "scripts", "exmod.sh"))
        self.bin = os.path.join(self.tmp, "bin")
        os.makedirs(self.bin)

    def stub_pwsh(self, folder, text):
        touch(os.path.join(folder, ".dotnet", "tools", "pwsh"), f"#!/bin/sh\necho {text}\n", 0o755)

    def run_wrapper(self):
        """Runs the wrapper with PATH holding only the stub folder, into which the few programs the
        wrapper and the stubs call are linked."""
        for tool in ("bash", "sh", "sed", "head", "dirname", "mkdir", "chmod"):
            link = os.path.join(self.bin, tool)
            found = shutil.which(tool)
            assert found, tool
            if not os.path.exists(link):
                os.symlink(found, link)
        env = dict(os.environ, EXTOOLS_HOME=ROOT, PATH=self.bin)
        env.pop("PWSH", None)
        out = subprocess.run([os.path.join(self.bin, "bash"), "scripts/exmod.sh", "help"], cwd=self.repo, env=env,
                             capture_output=True, text=True)
        return out.stdout.strip(), out.stderr

    def test_the_workspace_roots_pwsh_is_found_from_a_repository_below_it(self):
        # Fails if the workspace step is dropped, or the walk stops at the repository's parent.
        touch(os.path.join(self.ws, "exmod.workspace.json"), "{}")
        self.stub_pwsh(self.ws, "stub-pwsh")
        self.assertEqual("stub-pwsh", self.run_wrapper()[0])

    def test_the_repositorys_pwsh_beats_the_workspace_roots(self):
        # Fails if the workspace root is searched before the repository.
        touch(os.path.join(self.ws, "exmod.workspace.json"), "{}")
        self.stub_pwsh(self.ws, "workspace-pwsh")
        self.stub_pwsh(self.repo, "repo-pwsh")
        self.assertEqual("repo-pwsh", self.run_wrapper()[0])

    def bootstrap(self):
        """Runs the wrapper with a stub dotnet whose `tool install --tool-path DIR` writes a pwsh into
        DIR, and returns the wrapper's output."""
        touch(os.path.join(self.bin, "dotnet"),
              '#!/bin/sh\nwhile [ "$1" != --tool-path ]; do shift; done\n'
              'mkdir -p "$2" && printf \'#!/bin/sh\\necho boot-pwsh\\n\' > "$2/pwsh" && chmod +x "$2/pwsh"\n', 0o755)
        return self.run_wrapper()

    def test_pwsh_is_bootstrapped_into_the_workspace_root_when_a_marker_is_above(self):
        # Fails if the bootstrap writes the repository's .dotnet/tools inside a workspace.
        touch(os.path.join(self.ws, "exmod.workspace.json"), "{}")
        out, err = self.bootstrap()
        self.assertEqual("boot-pwsh", out, err)
        self.assertTrue(os.path.exists(os.path.join(self.ws, ".dotnet", "tools", "pwsh")))
        self.assertFalse(os.path.exists(os.path.join(self.repo, ".dotnet")))

    def test_pwsh_is_bootstrapped_into_the_repository_without_a_marker(self):
        # Fails if the bootstrap ignores the missing marker and writes above the repository.
        out, err = self.bootstrap()
        self.assertEqual("boot-pwsh", out, err)
        self.assertTrue(os.path.exists(os.path.join(self.repo, ".dotnet", "tools", "pwsh")))
        self.assertFalse(os.path.exists(os.path.join(self.ws, ".dotnet")))


if __name__ == "__main__":
    unittest.main()
