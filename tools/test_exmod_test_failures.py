"""Tests for `exmod test` in exmod/src.ps1: Get-ExmodTestFailures, and the dotnet test line the
-Coverage run collects over. Run through pwsh ($PWSH, PATH, or the checkout's .dotnet/tools);
skipped when no pwsh is found."""

import json
import os
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

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

# Invoke-Test all over one assembly on the 1.22 series, with a dotnet stub that appends its test
# arguments, one per line, to $env:TEST_ARGS, prints a VSTest summary with $env:TEST_TOTAL when that
# is set, and exits with $env:TEST_EXIT (0 when unset); a build appends a line to $env:TEST_BUILDS and
# exits with $env:TEST_BUILD_EXIT.
OUTPUT = r"""
$Manifest = [pscustomobject]@{ series = @('1.22') }
$CurrentGameVersion = '1.22'
function Resolve-DotnetHost { $env:TEST_STUB }
function Get-ExmodTestProjects {
  [ordered]@{ a = [pscustomobject]@{ Project = 'A.Tests'; Proj = $env:TEST_PROJ; Series = $Manifest.series } }
}
Invoke-Test (@('all') + $(if ($env:TEST_FILTER) { @('-Filter', $env:TEST_FILTER) }) + $(if ($env:TEST_NOBUILD) { @('-NoBuild') }))
"""

OUTPUT_STUB = """#!/bin/sh
if [ "$1" = build ]; then echo built >> "$TEST_BUILDS"; exit "${TEST_BUILD_EXIT:-0}"; fi
[ "$1" = test ] || exit 0
printf '%s\\n' "$@" >> "$TEST_ARGS"
echo "stub-output: $*"
[ -n "$TEST_EXTRA" ] && echo "$TEST_EXTRA"
if [ -n "$TEST_TOTAL" ]; then echo "Passed!  - Failed:     0, Passed:     $TEST_TOTAL, Skipped:     0, Total:     $TEST_TOTAL"; fi
exit "${TEST_EXIT:-0}"
"""


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "the dotnet stub is a shell script")
class RunOutputTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.repo = os.path.join(self.tmp, "repo")
        touch(os.path.join(self.repo, "exmod.json"), "{}")
        self.stub = os.path.join(self.tmp, "dotnet")
        touch(self.stub, OUTPUT_STUB)
        os.chmod(self.stub, os.stat(self.stub).st_mode | stat.S_IEXEC)
        self.args = os.path.join(self.tmp, "args.txt")
        self.builds = os.path.join(self.tmp, "builds.txt")
        self.proj = os.path.join(self.repo, "tests", "A.Tests", "A.Tests.csproj")
        self.dll = os.path.join(self.repo, "tests", "A.Tests", "bin", "Debug", "net10.0", "A.Tests.dll")
        self.results = os.path.join(self.repo, ".exmod", "test-results", "1.22-A.Tests")
        self.log = os.path.join(self.repo, ".exmod", "test-logs", "1.22-A.Tests.log")
        self.census = os.path.join(self.repo, ".exmod", "census", "detached.json")

    def run_all(self, total="1", exit_code="0", build_exit="0", census=None, filter=None, extra=None,
                no_build=False):
        # A repository outside git has no branch, so its census is detached.json.
        if census is not None:
            touch(self.census, json.dumps(census))
        env = exmod_env(os.path.join(self.tmp, "local"), TEST_REPO=self.repo, TEST_STUB=self.stub,
                        TEST_ARGS=self.args, TEST_BUILDS=self.builds, TEST_PROJ=self.proj, TEST_TOTAL=total, TEST_EXIT=exit_code, TEST_BUILD_EXIT=build_exit)
        env.pop("EXLIB_WRITE_GOLDENS", None)
        if total == "":
            del env["TEST_TOTAL"]
        if filter:
            env["TEST_FILTER"] = filter
        if extra:
            env["TEST_EXTRA"] = extra
        if no_build:
            env["TEST_NOBUILD"] = "1"
        out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", exmod_script(OUTPUT)],
                             env=env, capture_output=True, text=True)
        return out.returncode, out.stdout.splitlines()

    def test_each_run_passes_blame_and_a_results_directory_under_exmod(self):
        # Fails if --blame is dropped from the dotnet test line, or the results directory is not the
        # series' and assembly's folder under .exmod/test-results.
        code, lines = self.run_all()
        self.assertEqual(0, code, lines)
        with open(self.args) as f:
            args = f.read().splitlines()
        self.assertIn("--blame", args)
        self.assertEqual(self.results, args[args.index("--results-directory") + 1], args)

    def test_a_results_directory_from_an_earlier_run_is_cleared_before_the_next(self):
        # Fails if the stale results are kept, so a second run's blame folder holds the first run's files.
        touch(os.path.join(self.results, "Sequence_old.xml"), "stale")
        code, lines = self.run_all()
        self.assertEqual(0, code, lines)
        self.assertFalse(os.path.exists(os.path.join(self.results, "Sequence_old.xml")))

    def test_the_full_output_of_a_run_is_written_to_its_log(self):
        # Fails if the log is not written, is written under another name, or keeps only the summary line.
        code, lines = self.run_all()
        self.assertEqual(0, code, lines)
        with open(self.log) as f:
            text = f.read()
        self.assertIn("stub-output: test", text)
        self.assertIn("--blame", text)
        self.assertIn("Total:     1", text)

    def test_a_failed_run_prints_its_log_path_on_its_fail_line(self):
        # Fails if a FAIL line drops the log path, or a passing line prints one.
        code, lines = self.run_all(exit_code="1")
        self.assertEqual(1, code, lines)
        fail = [l for l in lines if l.startswith("FAIL")]
        self.assertEqual(1, len(fail), lines)
        self.assertTrue(fail[0].endswith(f"(log: {self.log})"), fail)
        code, lines = self.run_all()
        self.assertEqual(0, code, lines)
        self.assertFalse([l for l in lines if l.startswith("PASS") and "log:" in l], lines)

    def test_a_run_with_no_summary_line_prints_its_log_path(self):
        # Fails if the missing-summary failure does not keep its log, the case an aborted host leaves.
        code, lines = self.run_all(total="")
        self.assertEqual(1, code, lines)
        fail = [l for l in lines if l.startswith("FAIL")]
        self.assertTrue(fail and "NO TEST SUMMARY" in fail[0] and fail[0].endswith(f"(log: {self.log})"), lines)

    def test_a_build_failure_prints_no_log_path_because_no_run_wrote_one(self):
        # Fails if a result with no log prints an empty or stale "(log: )" path.
        code, lines = self.run_all(build_exit="1")
        self.assertEqual(1, code, lines)
        fail = [l for l in lines if l.startswith("FAIL")]
        self.assertEqual(1, len(fail), lines)
        self.assertIn("build failed", fail[0])
        self.assertNotIn("log:", fail[0])

    def test_a_count_below_the_census_prints_the_log_path_on_its_census_line(self):
        # Fails if the census line drops the log path on a drop, or prints it on a count that held.
        code, lines = self.run_all(census={"1.22/A.Tests": 5})
        self.assertEqual(1, code, lines)
        line = [l for l in lines if l.startswith("census")]
        self.assertEqual(1, len(line), lines)
        self.assertTrue(line[0].endswith(f"(was 5, a drop; log: {self.log})"), line)

    def test_a_count_at_the_census_prints_no_log_path(self):
        # Fails if the log path is printed on a census line whose count did not fall.
        code, lines = self.run_all(total="5", census={"1.22/A.Tests": 5})
        self.assertEqual(0, code, lines)
        line = [l for l in lines if l.startswith("census")]
        self.assertEqual(1, len(line), lines)
        self.assertNotIn("log:", line[0])

    def test_a_filtered_run_whose_assembly_discovery_skipped_fails_naming_it(self):
        # Fails if a filtered run with no summary passes whatever the output says, as it did when a
        # type-load failure skipped the whole assembly and the run read "no test matched the filter".
        skip = ("[xUnit.net 00:00:00.50]   Skipping: A.Tests (could not find dependent assembly "
                "'VintagestoryAPI, Version=1.22.6')")
        code, lines = self.run_all(total="", filter="FullyQualifiedName~Boiler", extra=skip)
        self.assertEqual(1, code, lines)
        fail = [l for l in lines if l.startswith("FAIL")]
        self.assertEqual(1, len(fail), lines)
        self.assertIn("DISCOVERY SKIPPED", fail[0])
        self.assertIn("A.Tests (could not find dependent assembly 'VintagestoryAPI, Version=1.22.6')", fail[0])

    def test_a_filtered_run_with_the_no_match_line_still_passes_as_no_test_matched(self):
        # Fails if every missing summary fails under a filter, so a filter naming a class of another
        # project turns that project red.
        no_match = "No test matches the given testcase filter `FullyQualifiedName~Boiler` in /x/A.Tests.dll"
        code, lines = self.run_all(total="", filter="FullyQualifiedName~Boiler", extra=no_match)
        self.assertEqual(0, code, lines)
        self.assertTrue([l for l in lines if l.startswith("PASS") and "no test matched the filter" in l], lines)

    def test_no_build_runs_the_tests_of_a_built_assembly_without_building(self):
        # Fails if -NoBuild still runs dotnet build, or does not reach dotnet test --no-build.
        touch(self.dll, "")
        code, lines = self.run_all(no_build=True)
        self.assertEqual(0, code, lines)
        self.assertFalse(os.path.exists(self.builds), "dotnet build ran under -NoBuild")
        with open(self.args) as f:
            args = f.read().splitlines()
        self.assertIn("--no-build", args)
        self.assertTrue([l for l in lines if l.startswith("PASS")], lines)

    def test_a_run_without_no_build_builds_first(self):
        # Fails if the build loop is skipped when -NoBuild is not given.
        touch(self.dll, "")
        code, lines = self.run_all()
        self.assertEqual(0, code, lines)
        self.assertTrue(os.path.exists(self.builds), "dotnet build did not run")

    def test_no_build_fails_a_lane_whose_test_assembly_is_missing(self):
        # Fails if a missing assembly passes under -NoBuild, or the lane still runs dotnet test.
        code, lines = self.run_all(no_build=True)
        self.assertEqual(1, code, lines)
        fail = [l for l in lines if l.startswith("FAIL")]
        self.assertEqual(1, len(fail), lines)
        self.assertIn("Not built for 1.22", fail[0])
        self.assertIn("drop -NoBuild or build first", fail[0])
        self.assertFalse(os.path.exists(self.args), "dotnet test ran for a missing assembly")
        self.assertFalse(os.path.exists(self.builds), "dotnet build ran under -NoBuild")

    def test_no_build_keeps_the_census_drop_check(self):
        # Fails if -NoBuild skips the count-drop gate that follows the lanes.
        touch(self.dll, "")
        code, lines = self.run_all(no_build=True, census={"1.22/A.Tests": 5})
        self.assertEqual(1, code, lines)
        self.assertTrue([l for l in lines if l.startswith("census") and "a drop" in l], lines)


# Invoke-Test over three test projects on $env:TEST_SERIES (1.22 and 1.21 by default), with a dotnet stub
# that appends each build's arguments on one line to $env:TEST_BUILDS, fails a build whose arguments
# contain $env:TEST_FAIL_PROJ or whose traversal project lists it, and
# appends the project of each test run to $env:TEST_ARGS.
SERIES = r"""
$Manifest = [pscustomobject]@{ series = @($env:TEST_SERIES -split ',') }
$CurrentGameVersion = '1.22'
function Resolve-DotnetHost { $env:TEST_STUB }
function Get-ExmodBuildTargets { [ordered]@{} }
function Get-ExmodSamples { [ordered]@{} }
function Get-ExmodTestProjects {
  $out = [ordered]@{}
  foreach ($n in 'A', 'B', 'C') {
    $out[$n] = [pscustomobject]@{ Project = "$n.Tests"; Proj = (Join-Path $env:TEST_REPO "$n.Tests.csproj"); Series = $Manifest.series }
  }
  $out
}
if ($env:TEST_VERB -eq 'build') { Invoke-Build @('all', '-Tests') } else { Invoke-Test @('all') }
"""

SERIES_STUB = """#!/bin/sh
if [ "$1" = build ]; then
  echo "$*" >> "$TEST_BUILDS"
  echo "stub-build-output: $2"
  [ -n "$TEST_FAIL_PROJ" ] || exit 0
  case "$*" in *"$TEST_FAIL_PROJ"*) exit 1;; esac
  case "$2" in *.proj) grep -q "$TEST_FAIL_PROJ" "$2" && exit 1;; esac
  exit 0
fi
[ "$1" = test ] || exit 0
echo "$2" >> "$TEST_ARGS"
echo "Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1"
"""


def default_target(proj):
    # The target `dotnet build` runs after its restore: the project's DefaultTargets, else its first target.
    root = ET.fromstring(proj)
    named = root.get("DefaultTargets")
    return named.split(";")[0].strip() if named else root.find("Target").get("Name")


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "the dotnet stub is a shell script")
class ParallelBuildTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.repo = os.path.join(self.tmp, "repo")
        touch(os.path.join(self.repo, "exmod.json"), "{}")
        for n in "ABC":
            touch(os.path.join(self.repo, f"{n}.Tests.csproj"), "<Project />")
        self.stub = os.path.join(self.tmp, "dotnet")
        touch(self.stub, SERIES_STUB)
        os.chmod(self.stub, os.stat(self.stub).st_mode | stat.S_IEXEC)
        self.args = os.path.join(self.tmp, "args.txt")
        self.builds = os.path.join(self.tmp, "builds.txt")

    def run_series(self, series="1.22,1.21", fail="", verb="test"):
        env = exmod_env(os.path.join(self.tmp, "local"), TEST_REPO=self.repo, TEST_STUB=self.stub,
                        TEST_ARGS=self.args, TEST_BUILDS=self.builds, TEST_SERIES=series, TEST_FAIL_PROJ=fail, TEST_VERB=verb)
        env.pop("EXLIB_WRITE_GOLDENS", None)
        out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", exmod_script(SERIES)],
                             env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        builds, tested = [], []
        if os.path.exists(self.builds):
            with open(self.builds) as f:
                builds = f.read().splitlines()
        if os.path.exists(self.args):
            with open(self.args) as f:
                tested = f.read().splitlines()
        return out.returncode, out.stdout.splitlines(), builds, tested

    def test_a_series_builds_all_its_test_projects_in_one_parallel_call(self):
        # Fails if a project is built on its own again: more than one build line per series, a
        # project missing from the traversal project, -m dropped from the call, or the traversal's default
        # target anything but Build (its first target, Restore, ran twice and nothing compiled).
        code, lines, builds, tested = self.run_series()
        self.assertEqual(0, code, lines)
        self.assertEqual(2, len(builds), builds)
        for series in ("1.22", "1.21"):
            line = [b for b in builds if f"parallel-build-{series}.proj" in b]
            self.assertEqual(1, len(line), builds)
            self.assertIn(" -m", line[0])
        with open(os.path.join(self.repo, ".exmod", "parallel-build-1.22.proj")) as f:
            proj = f.read()
        for n in "ABC":
            self.assertIn(os.path.join(self.repo, f"{n}.Tests.csproj"), proj)
        self.assertIn("TargetFramework=net10.0", proj)
        restore = [l for l in proj.splitlines() if 'Targets="Restore"' in l]
        self.assertEqual(1, len(restore), proj)
        self.assertIn('BuildInParallel="false"', restore[0])
        self.assertIn('Targets="Build"', proj)
        self.assertNotIn("Legacy=true", proj)
        self.assertEqual("Build", default_target(proj))
        with open(os.path.join(self.repo, ".exmod", "parallel-build-1.21.proj")) as f:
            legacy = f.read()
        self.assertIn("Legacy=true", legacy)
        self.assertEqual(6, len(tested), tested)
        self.assertEqual(6, len([l for l in lines if l.startswith("PASS")]), lines)

    def test_a_failing_project_marks_only_its_own_lanes_failed(self):
        # Fails if a failed call fails every lane, or the failing project is not found by building
        # the projects on their own afterwards.
        code, lines, builds, tested = self.run_series(series="1.22", fail="B.Tests.csproj")
        self.assertEqual(1, code, lines)
        fail = [l for l in lines if l.startswith("FAIL")]
        self.assertEqual(1, len(fail), lines)
        self.assertIn("1.22/B.Tests", fail[0])
        self.assertIn("build failed", fail[0])
        self.assertEqual(2, len(tested), tested)
        self.assertFalse([t for t in tested if "B.Tests" in t], tested)
        self.assertEqual(4, len(builds), builds)


    def test_build_tests_builds_a_series_in_one_call_and_names_the_failed_projects(self):
        # Fails if `build -Tests` builds the projects one by one again, or the build output is
        # returned with the failed projects so a green call reads as a failure.
        code, lines, builds, _ = self.run_series(series="1.22", verb="build")
        self.assertEqual(0, code, lines)
        self.assertEqual(1, len(builds), builds)
        self.assertIn("parallel-build-1.22.proj", builds[0])
        self.assertTrue([l for l in lines if l.startswith("stub-build-output")], lines)
        self.assertIn("Build succeeded.", lines)

    def test_build_tests_names_only_the_projects_that_failed(self):
        # Fails if the failure message lists projects whose own build passed.
        code, lines, _, _ = self.run_series(series="1.22", fail="B.Tests.csproj", verb="build")
        self.assertEqual(1, code, lines)
        text = "\n".join(lines)
        self.assertRegex(text, r"Build failed: B\.Tests \(1\.22/net10\.0\)\.")


if __name__ == "__main__":
    unittest.main()