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
    def test_the_coverage_run_collects_over_one_plain_debug_test_of_the_solution(self):
        # Fails if -Coverage adds a property to its dotnet test line, such as the dropped
        # -p:ExmodMapSourcePaths=false, or tests another configuration or project.
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
                          'stub-dotnet test "demo.sln" -c Debug --nologo'],
                         args, out)


# Invoke-Test all -Filter x over one test project on every series, with a dotnet stub whose test run
# appends "start <tfm>" and "end <tfm>" to $env:TEST_ORDER, the current series' run holding for 2 s
# in between.
WAVES = r"""
$Manifest = [pscustomobject]@{ series = @('1.22', '1.21', '1.20') }
$CurrentGameVersion = '1.22'
function Resolve-DotnetHost { $env:TEST_STUB }
function Get-ExmodTestProjects {
  [ordered]@{ a = [pscustomobject]@{ Project = 'A.Tests'; Proj = 'a.csproj'; Series = $Manifest.series } }
}
Invoke-Test @('all', '-Filter', 'x')
"""

STUB = """#!/bin/sh
[ "$1" = test ] || exit 0
echo "start $4" >> "$TEST_ORDER"
[ "$4" = net10.0 ] && sleep 2
echo "end $4" >> "$TEST_ORDER"
echo "Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1"
"""


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "the dotnet stub is a shell script")
class GoldenWriteOrderTests(unittest.TestCase):
    def run_all(self, write):
        assert PWSH
        with tempfile.TemporaryDirectory() as d:
            repo = os.path.join(d, "repo")
            touch(os.path.join(repo, "exmod.json"), "{}")
            stub = os.path.join(d, "dotnet")
            touch(stub, STUB)
            os.chmod(stub, os.stat(stub).st_mode | stat.S_IEXEC)
            order = os.path.join(d, "order.log")
            env = exmod_env(os.path.join(d, "local"), TEST_REPO=repo, TEST_STUB=stub, TEST_ORDER=order)
            env.pop("EXLIB_WRITE_GOLDENS", None)
            if write:
                env["EXLIB_WRITE_GOLDENS"] = "1"
            out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", exmod_script(WAVES)],
                                 env=env, capture_output=True, text=True).stdout
            with open(order) as f:
                return f.read().split(), out

    def test_a_golden_write_runs_the_older_series_after_the_current_series_has_finished(self):
        # Fails if a golden write starts every lane at once: an older series' write would then read
        # the shared goldens while the current series' write rewrites them.
        lines, out = self.run_all(write=True)
        self.assertEqual(["start", "net10.0", "end", "net10.0"], lines[:4], out)
        self.assertIn("EXLIB_WRITE_GOLDENS is set: the 1.22 lanes run first", out)

    def test_any_other_run_starts_every_lane_at_once(self):
        # Fails if the lanes run one series after another when no golden write is asked for.
        lines, out = self.run_all(write=False)
        self.assertEqual(["end", "net10.0"], lines[-2:], out)
        self.assertNotIn("EXLIB_WRITE_GOLDENS is set", out)

if __name__ == "__main__":
    unittest.main()
