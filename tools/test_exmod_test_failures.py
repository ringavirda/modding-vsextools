"""Tests for `exmod test` in exmod/src.ps1: Get-ExmodTestFailures, and the dotnet test line the
-Coverage run collects over. Run through pwsh ($PWSH, PATH, or the checkout's .dotnet/tools);
skipped when no pwsh is found."""

import json
import os
import stat
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import PWSH, ROOT, exmod_env, exmod_script, touch  # noqa: E402


def failures(lines):
    """[(name, message)] Get-ExmodTestFailures pulls from the console log lines."""
    assert PWSH
    script = ("function Add-ExmodCommand { }; . (Join-Path $env:EXTOOLS_ROOT 'exmod/src.ps1'); "
              "$lines = @(Get-Content -LiteralPath $env:FAILURE_LOG); "
              "ConvertTo-Json -Compress -InputObject @(Get-ExmodTestFailures $lines)")
    with tempfile.TemporaryDirectory() as d:
        log = os.path.join(d, "console.log")
        with open(log, "w") as f:
            f.write("\n".join(lines) + "\n")
        out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", script],
                             env=dict(os.environ, EXTOOLS_ROOT=ROOT, FAILURE_LOG=log),
                             capture_output=True, text=True, check=True).stdout
    return [(f["Name"], f["Message"]) for f in json.loads(out)]


@unittest.skipUnless(PWSH, "pwsh not found")
class FailureLineTests(unittest.TestCase):
    def test_a_plain_test_and_a_theory_row_with_spaces_are_named_with_their_messages(self):
        found = failures([
            "  Failed Mod.Tests.Boiler.Holds_pressure [12 ms]",
            "  Error Message:",
            "   Assert.Equal() Failure",
            '  Failed Mod.Tests.Pipe.Bursts(tier: "cast iron", atm: [1, 2]) [3 ms]',
            "  Error Message:",
            "   Assert.True() Failure",
        ])
        self.assertEqual([
            ("Mod.Tests.Boiler.Holds_pressure", "Assert.Equal() Failure"),
            ('Mod.Tests.Pipe.Bursts(tier: "cast iron", atm: [1, 2])', "Assert.True() Failure"),
        ], found)

    def test_a_logged_failed_line_is_not_a_failure_and_does_not_end_the_message_search(self):
        found = failures([
            "  Failed Mod.Tests.Loader.Reads_assets [40 ms]",
            "  Standard Output Messages:",
            "     Failed to load asset [game:x]",
            "  Error Message:",
            "   Assert.NotNull() Failure",
            "Failed!  - Failed:     1, Passed:    12, Skipped:     0, Total:    13",
        ])
        self.assertEqual([("Mod.Tests.Loader.Reads_assets", "Assert.NotNull() Failure")], found)


# Invoke-Test -Coverage with the dotnet host, the projects and the solution stubbed, and a
# dotnet-coverage in $env:TEST_DOTNET/tools that prints each argument on its own line and fails, so
# the run stops right after the collect step.
COVERAGE = r"""
function Get-ExmodDotnetDir { $env:TEST_DOTNET }
function Resolve-DotnetHost { 'stub-dotnet' }
function stub-dotnet { }
function Get-ExmodTestProjects { [ordered]@{} }
function Get-ExmodSolution { 'demo.sln' }
try { Invoke-Test @('-Coverage') } catch { }
"""


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "the dotnet-coverage stub is a shell script")
class CoverageRunTests(unittest.TestCase):
    def test_the_coverage_run_builds_with_source_paths_unmapped(self):
        # Fails if -Coverage drops -p:ExmodMapSourcePaths=false from its dotnet test line.
        assert PWSH
        with tempfile.TemporaryDirectory() as d:
            repo = os.path.join(d, "repo")
            touch(os.path.join(repo, "exmod.json"), "{}")
            stub = os.path.join(d, "dotnet", "tools", "dotnet-coverage")
            touch(stub, '#!/bin/sh\nfor a in "$@"; do echo "coverage-arg: $a"; done\nexit 1\n')
            os.chmod(stub, os.stat(stub).st_mode | stat.S_IEXEC)
            out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", exmod_script(COVERAGE)],
                                 env=exmod_env(os.path.join(d, "local"), TEST_REPO=repo,
                                               TEST_DOTNET=os.path.join(d, "dotnet")),
                                 capture_output=True, text=True).stdout
        args = [l[len("coverage-arg: "):] for l in out.splitlines() if l.startswith("coverage-arg: ")]
        self.assertEqual(["collect", "-f", "cobertura", "-o", os.path.join(repo, "coverage.xml"),
                          'stub-dotnet test "demo.sln" -c Debug --nologo -p:ExmodMapSourcePaths=false'],
                         args, out)


if __name__ == "__main__":
    unittest.main()
