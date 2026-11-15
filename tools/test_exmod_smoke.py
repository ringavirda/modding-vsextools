"""Tests for the manifest check at the start of Invoke-Smoke in exmod/run.ps1: a repository that declares no mod skips
smoke with a printed line, one that declares mods still fails when none is found. Run through pwsh ($PWSH, PATH, or the
nearest .dotnet/tools above the checkout) with exmod_harness's prelude; skipped when no pwsh is found."""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import PWSH, exmod_env, exmod_script, touch  # noqa: E402

# The server lookup and the mod lookup are replaced so a run that gets past the manifest check ends at the throw for
# no mods found instead of at a game install.
BODY = r"""
function Resolve-SmokeServer([string]$version) { return 'server' }
function Get-ScratchDefaultModDirs { return @() }
try { Invoke-Smoke @(); Write-Host 'returned' }
catch { Write-Host "threw: $($_.Exception.Message)" }
"""


def run_smoke(manifest):
    assert PWSH
    tmp = os.path.realpath(tempfile.mkdtemp())
    try:
        repo = os.path.join(tmp, "repo")
        touch(os.path.join(repo, "exmod.json"), json.dumps(manifest))
        for path in ("mods/a/src/A.csproj",):
            touch(os.path.join(repo, path), "<Project />")
        done = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", exmod_script(BODY)],
                              env=exmod_env(os.path.join(tmp, "local"), TEST_REPO=repo),
                              capture_output=True, text=True)
        return done.stdout
    finally:
        shutil.rmtree(tmp)


@unittest.skipUnless(PWSH, "pwsh not found")
class SmokeSkipTests(unittest.TestCase):
    def test_a_manifest_with_no_mod_skips_smoke_with_a_line(self):
        # Fails when the skip is removed: smoke then goes on and throws for no mods found.
        out = run_smoke({"tests": ["verify/V.Tests"]})
        self.assertIn("no mods in this repository: smoke skipped", out)
        self.assertIn("returned", out)
        self.assertNotIn("threw:", out)

    def test_a_manifest_with_a_mod_and_none_found_still_fails(self):
        # Fails when the skip is unconditional: smoke then prints the skip line and returns instead of throwing.
        out = run_smoke({"mods": {"a": {"path": "mods/a"}}})
        self.assertIn("threw: No mods found to smoke-test", out)
        self.assertNotIn("smoke skipped", out)


if __name__ == "__main__":
    unittest.main()
