"""Tests for the EX_ITEM_VIEWS default of `exmod item` (Invoke-Item in exmod/shapes.ps1), run through
pwsh with `dotnet`, the tool's dll and the game lookup stubbed; skipped when no pwsh is found. Each
test names the mutation it fails under."""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import PWSH, exmod_env, exmod_script, touch  # noqa: E402

HEAD = r"""
function Get-ShapesRoot { 'shapes' }
function Resolve-GameInstall { 'game' }
function Get-ExlibShapesRunDll([string]$Game) { 'tool.dll' }
$global:seen = @()
function dotnet { $global:seen = @($args); $global:LASTEXITCODE = 0 }
"""


@unittest.skipUnless(PWSH, "pwsh not found")
class ItemViewsDumpTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.repo = os.path.join(self.tmp, "repo")
        touch(os.path.join(self.repo, "exmod.json"), "{}")
        self.dumps = os.path.join(self.tmp, "dumps")
        os.makedirs(self.dumps)

    def item_args(self, argv, views_env):
        """The argv `exmod item` hands the tool, with EX_ITEM_VIEWS set to views_env (None unsets it)."""
        env = exmod_env(os.path.join(self.tmp, "local"), TEST_REPO=self.repo)
        env.pop("EX_ITEM_VIEWS", None)
        if views_env is not None:
            env["EX_ITEM_VIEWS"] = views_env
        body = HEAD + "Invoke-Item @(%s)\nConvertTo-Json -Compress @($global:seen)" % ",".join("'%s'" % a for a in argv)
        out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", exmod_script(body)],
                             env=env, capture_output=True, text=True)
        self.assertEqual(0, out.returncode, out.stdout + out.stderr)
        return json.loads(out.stdout.splitlines()[-1])

    def test_env_folder_is_passed_when_no_flag_is_given(self):
        # Fails if the EX_ITEM_VIEWS default is dropped.
        got = self.item_args(["a.json"], self.dumps)
        i = got.index("--views-dump")
        self.assertEqual(self.dumps, got[i + 1])
        self.assertEqual(1, got.count("--views-dump"))

    def test_explicit_flag_wins_over_the_env_folder(self):
        # Fails if the env default is added over an explicit --views-dump.
        other = os.path.join(self.tmp, "other")
        got = self.item_args(["a.json", "--views-dump", other], self.dumps)
        self.assertEqual(1, got.count("--views-dump"))
        self.assertEqual(other, got[got.index("--views-dump") + 1])
        self.assertNotIn(self.dumps, got)

    def test_explicit_equals_form_wins_over_the_env_folder(self):
        # Fails if only the spaced form of --views-dump counts as given.
        got = self.item_args(["a.json", "--views-dump=x"], self.dumps)
        self.assertNotIn("--views-dump", got)
        self.assertIn("--views-dump=x", got)

    def test_unset_env_adds_nothing(self):
        # Fails if the flag is added with no EX_ITEM_VIEWS.
        self.assertNotIn("--views-dump", self.item_args(["a.json"], None))

    def test_missing_folder_adds_nothing(self):
        # Fails if the existence check on the EX_ITEM_VIEWS folder is removed.
        got = self.item_args(["a.json"], os.path.join(self.tmp, "absent"))
        self.assertNotIn("--views-dump", got)


if __name__ == "__main__":
    unittest.main()
