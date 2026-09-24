"""Tests for exmod_harness: the environment and prelude every exmod.ps1 test runs in, through pwsh
($PWSH, PATH, or the checkout's .dotnet/tools) on a temporary repository; skipped when no pwsh is
found. Each test names the mutation it fails under."""

import os
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import PWSH, exmod_env, exmod_script, touch  # noqa: E402


@unittest.skipUnless(PWSH, "pwsh not found")
class HarnessTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.repo = os.path.join(self.tmp, "repo")
        touch(os.path.join(self.repo, "exmod.json"), "{}")
        self.local = os.path.join(self.tmp, "local")

    def run_body(self, body):
        out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", exmod_script(body)],
                             env=exmod_env(self.local, TEST_REPO=self.repo), capture_output=True, text=True)
        return out.stdout.splitlines()

    def test_the_windows_store_is_under_the_test_folder(self):
        # Fails if exmod_env passes the real LOCALAPPDATA through.
        got = self.run_body("$OnWindows = $true; Get-ExmodUserStore")
        self.assertEqual([os.path.join(self.local, "exmod")], got)

    def test_downloads_and_start_process_throw(self):
        # Fails if the prelude drops the Start-Process, Invoke-WebRequest or Invoke-RestMethod stub.
        absent = os.path.join(self.tmp, "absent")
        got = self.run_body("foreach ($c in 'Start-Process', 'Invoke-WebRequest', 'Invoke-RestMethod') { "
                            f"try {{ & $c '{absent}'; \"$c ran\" }} catch {{ \"$c threw: $_\" }} }}")
        self.assertEqual([f"Start-Process threw: Start-Process reached in a test: {absent}",
                          f"Invoke-WebRequest threw: download reached in a test: {absent}",
                          f"Invoke-RestMethod threw: download reached in a test: {absent}"], got)


if __name__ == "__main__":
    unittest.main()
