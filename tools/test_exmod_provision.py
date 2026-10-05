"""Tests for Publicize-GameApi in exmod/provision.ps1, run through pwsh ($PWSH, PATH, or the
checkout's .dotnet/tools) from a temporary folder holding a Library project, on copies of the game
API taken from the cached 1.22.7 server archive of the nearest .game/.cache above this checkout;
skipped when either is missing. Each test names the mutation it fails under."""

import os
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import PWSH, ROOT, exmod_env, exmod_script, touch  # noqa: E402

ARCHIVE = "vs_server_linux-x64_1.22.7.tar.gz"


def find_archive():
    here = ROOT
    while True:
        candidate = os.path.join(here, ".game", ".cache", ARCHIVE)
        if os.path.isfile(candidate):
            return candidate
        parent = os.path.dirname(here)
        if parent == here:
            return None
        here = parent


def read(path):
    with open(path, "rb") as f:
        return f.read()


CACHED = find_archive()


@unittest.skipUnless(PWSH and CACHED, "pwsh or the cached 1.22.7 server archive not found")
class PublicizeGameApiTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.project = os.path.join(self.tmp, "project")
        touch(os.path.join(self.project, "Mod.csproj"),
              '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
              "<TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")
        self.repo = os.path.join(self.tmp, "repo")
        touch(os.path.join(self.repo, "exmod.json"), "{}")
        self.dll = os.path.join(self.tmp, "VintagestoryAPI.dll")

    def publicize(self):
        script = exmod_script("Publicize-GameApi $env:TEST_DLL; 'ok'")
        env = exmod_env(os.path.join(self.tmp, "local"), TEST_REPO=self.repo, TEST_DLL=self.dll)
        return subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", script],
                              cwd=self.project, env=env, capture_output=True, text=True)

    def test_a_project_in_the_working_directory_does_not_stop_the_patch(self):
        # Fails if the patcher is run without `--file` and from the working directory: dotnet runs
        # the folder's Library project and the copy stays unpatched.
        with tarfile.open(CACHED) as tar:
            with tar.extractfile("VintagestoryAPI.dll") as src, open(self.dll, "wb") as out:
                shutil.copyfileobj(src, out)
        before = read(self.dll)
        done = self.publicize()
        self.assertEqual(0, done.returncode, done.stderr)
        after = read(self.dll)
        self.assertEqual(len(before), len(after))
        self.assertNotEqual(before, after)

    def test_a_patcher_failure_fails_the_call(self):
        # Fails if a patcher failure is only warned about instead of thrown.
        touch(self.dll, "not an assembly")
        done = self.publicize()
        self.assertNotEqual(0, done.returncode)
        self.assertIn("patch-api failed", done.stdout + done.stderr)


if __name__ == "__main__":
    unittest.main()
