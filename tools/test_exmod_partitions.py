"""Tests for the test partitions of `exmod test` in exmod/src.ps1: the complement filter, the
partitions covering a stubbed assembly once, the memory shares and heap limits of the test processes and
the sum of failures. Run through pwsh; skipped when none is found."""

import json
import os
import re
import stat
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import PWSH, ROOT, exmod_env, exmod_script, touch  # noqa: E402

TESTS = ["Mod.Tests.WalkA.One", "Mod.Tests.WalkA.Two", "Mod.Tests.WalkB.One",
         "Mod.Tests.World.One", "Mod.Tests.World.Two", "Mod.Tests.Other.One",
         "Mod.Tests.Other.Two", "Mod.Tests.Other.Three", "Mod.Tests.Late.One"]

# A dotnet that knows TESTS: `test --list-tests` lists them all, `test --filter F` evaluates F (terms
# of one kind, `|` or `&` joined, FullyQualifiedName~ ~ != !=) and prints the VSTest summary of the
# matches, failing when TEST_FAIL names a test among them (or, with TEST_OOM, or TEST_OOM_AT=<GiB> when the run's heap
# limit is that many GiB, the first match, printing an OutOfMemoryException). TEST_HOLD_MB holds that many MiB resident
# for TEST_HOLD_S seconds. A test run appends start/end to TEST_ORDER and its argv as JSON to TEST_ARGV. TEST_FAIL_MANY=N
# makes the run's matches N failing tests, Mod.Tests.Many.T0 on; TEST_TRX_SRC names a file copied to the trx logger's
# LogFileName in the results directory. A run with the trx logger prints dotnet's `Results File:` line.
STUB = """#!/usr/bin/env python3
import json, os, re, shutil, sys, time
tests = %r
a = sys.argv[1:]
if a[:1] != ["test"]:
    sys.exit(0)
if "--list-tests" in a:
    print("The following Tests are available:")
    for t in tests:
        print("    " + t)
    sys.exit(0)
f = a[a.index("--filter") + 1] if "--filter" in a else ""
def term(t):
    m = re.fullmatch(r"FullyQualifiedName(!?)([~=])(.+)", t)
    neg, op, v = m.groups()
    return lambda n: ((v in n) if op == "~" else (n == v)) != (neg == "!")
def match(n):
    if not f:
        return True
    if "&" in f:
        return all(term(t)(n) for t in f.split("&"))
    return any(term(t)(n) for t in f.split("|"))
hit = [n for n in tests if match(n)]
limit = [x for x in a if x.startswith("DOTNET_GCHeapHardLimit=")]
oom = os.environ.get("TEST_OOM") or (os.environ.get("TEST_OOM_AT") and limit == ["DOTNET_GCHeapHardLimit=0x%%X" %% (int(os.environ["TEST_OOM_AT"]) << 30)])
held = b"x" * (int(os.environ.get("TEST_HOLD_MB", "0")) << 20)
time.sleep(float(os.environ.get("TEST_HOLD_S", "0")))
argv = os.environ.get("TEST_ARGV")
if argv:
    open(argv, "a").write(json.dumps(a) + "\\n")
    oom = oom and len(open(argv).read().splitlines()) < 4
order = os.environ.get("TEST_ORDER")
if order:
    open(order, "a").write("start\\n")
    time.sleep(1)
    open(order, "a").write("end\\n")
lg = [x for x in a if x.startswith("trx;LogFileName=")]
if lg and os.environ.get("TEST_TRX_SRC"):
    rd = a[a.index("--results-directory") + 1]
    os.makedirs(rd, exist_ok=True)
    shutil.copy(os.environ["TEST_TRX_SRC"], os.path.join(rd, lg[0].split("=", 1)[1]))
if lg:
    print("Results File: " + os.path.join(a[a.index("--results-directory") + 1], lg[0].split("=", 1)[1]))
many = int(os.environ.get("TEST_FAIL_MANY", "0"))
if many:
    hit = ["Mod.Tests.Many.T%%d" %% i for i in range(many)]
    os.environ["TEST_FAIL"] = ""
bad = [n for n in hit if n == os.environ.get("TEST_FAIL")] if not many else hit
if oom and hit:
    bad = hit[:1]
    print("System.OutOfMemoryException: Exception of type was thrown.")
for b in bad:
    print("  Failed %%s [1 ms]" %% b)
    print("  Error Message:")
    print("   boom")
print("%%s!  - Failed: %%d, Passed: %%d, Skipped: 0, Total: %%d" %% ("Failed" if bad else "Passed", len(bad), len(hit) - len(bad), len(hit)))
sys.exit(1 if bad else 0)
""" % (TESTS,)

MANIFEST = {"partitions": {"A.Tests": {"parts": [
    {"name": "walks", "filter": "FullyQualifiedName~.WalkA.|FullyQualifiedName~.WalkB."},
    {"name": "worlds", "filter": "FullyQualifiedName~.World."},
    {"name": "rest"}]}}}


def manifest(heap=None, filter_heap=None, part_heaps=(None, None, None), filter_start=None, **extra):
    """MANIFEST with testMemory for A.Tests (when heap is given) and heapGb on the parts that have one."""
    m = json.loads(json.dumps(MANIFEST))
    for part, h in zip(m["partitions"]["A.Tests"]["parts"], part_heaps):
        if h is not None:
            part["heapGb"] = h
    if heap is not None:
        m["testMemory"] = {"A.Tests": {"heapGb": heap}}
        if filter_heap is not None:
            m["testMemory"]["A.Tests"]["filterHeapGb"] = filter_heap
        if filter_start is not None:
            m["testMemory"]["A.Tests"]["filterStartGb"] = filter_start
    m.update(extra)
    return m

RUN = r"""
function Resolve-DotnetHost { $env:TEST_STUB }
function Get-ExmodTestProjects {
  [ordered]@{ a = [pscustomobject]@{ Project = 'A.Tests'; Proj = 'a.csproj'; Series = @('1.22') } }
}
%s
try { Invoke-Test (@('1.22') + @(%s)) } catch { Write-Host "threw: $_" }
"""


RUNS = []  # the argv of each stub test run of the last run_pwsh, in start order
LOGS = []  # the text of each host log of the last run_pwsh
EXITS = []  # the exit code of the last run_pwsh's pwsh process


def run_pwsh(body, env_extra=None, args="", manifest=MANIFEST, stub=STUB, runlog=None):
    """runlog: lines written to the run log before the run, each a dict (its "{repo}" values become the checkout's
    path) or a raw string; EXMOD_RUN_LOG names that file."""
    assert PWSH
    with tempfile.TemporaryDirectory() as d:
        repo = os.path.join(d, "repo")
        touch(os.path.join(repo, "exmod.json"), json.dumps(manifest))
        stubpath = os.path.join(d, "dotnet")
        touch(stubpath, stub)
        os.chmod(stubpath, os.stat(stubpath).st_mode | stat.S_IEXEC)
        order = os.path.join(d, "order.log")
        argv = os.path.join(d, "argv.log")
        env = exmod_env(os.path.join(d, "local"), TEST_REPO=repo, TEST_STUB=stubpath, TEST_ORDER=order,
                        TEST_ARGV=argv, **(env_extra or {}))
        if runlog is not None:
            env["EXMOD_RUN_LOG"] = os.path.join(d, "seeded-runs.jsonl")
            with open(env["EXMOD_RUN_LOG"], "w") as f:
                for row in runlog:
                    if isinstance(row, dict):
                        row = json.dumps({k: v.replace("{repo}", repo) if isinstance(v, str) else v
                                          for k, v in row.items()})
                    f.write(row + "\n")
        done = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command",
                               exmod_script(RUN % (body, args))],
                              env=env, capture_output=True, text=True)
        out = done.stdout
        EXITS[:] = [done.returncode]
        seen = []
        if os.path.exists(order):
            with open(order) as f:
                seen = f.read().split()
        LOGS[:] = []
        logdir = os.path.join(repo, ".exmod", "test-logs")
        if os.path.isdir(logdir):
            for name in sorted(os.listdir(logdir)):
                with open(os.path.join(logdir, name)) as f:
                    LOGS.append(f.read())
        RUNS[:] = []
        if os.path.exists(argv):
            with open(argv) as f:
                RUNS.extend(json.loads(l) for l in f if l.strip())
        census = os.path.join(repo, ".exmod", "census", "detached.json")
        recorded = None
        if os.path.exists(census):
            with open(census) as f:
                recorded = json.load(f)
        return out, seen, recorded, listed(stubpath)


def listed(stub):
    out = subprocess.run([stub, "test", "x", "--list-tests"], capture_output=True, text=True).stdout
    return len([l for l in out.splitlines() if l.startswith("    ")])


TRX_NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"


def write_trx(path, classes, per_class, pad=0):
    """A trx file with `classes` classes of `per_class` tests each; class i (0-based) has tests of (i+1) ms and
    each result carries `pad` bytes of stdout. Results come before the test definitions, as dotnet writes them."""
    filler = "x" * pad
    with open(path, "w") as f:
        f.write(f'<?xml version="1.0" encoding="UTF-8"?><TestRun xmlns="{TRX_NS}"><Results>')
        for c in range(classes):
            for t in range(per_class):
                f.write(f'<UnitTestResult executionId="e{c}-{t}" testId="t{c}-{t}" testName="T{t}" '
                        f'duration="00:00:00.{(c + 1) * 10000:07d}" outcome="Passed"><Output><StdOut>{filler}</StdOut></Output>'
                        f'</UnitTestResult>')
        f.write("</Results><TestDefinitions>")
        for c in range(classes):
            for t in range(per_class):
                f.write(f'<UnitTest name="T{t}" id="t{c}-{t}"><Execution id="e{c}-{t}"/>'
                        f'<TestMethod codeBase="a.dll" className="Mod.Tests.C{c:02d}" name="T{t}"/></UnitTest>')
        f.write("</TestDefinitions></TestRun>")


def results_block(out):
    return out[out.index("===== Results ====="):]


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "the dotnet stub is a shell script")
class PartitionTests(unittest.TestCase):
    def test_the_complement_negates_every_term_of_every_filter(self):
        out, _, _, _ = run_pwsh(
            "Write-Host (Get-ExmodComplementFilter @('FullyQualifiedName~.A.|FullyQualifiedName=X.B', 'Name~c'))")
        self.assertIn("FullyQualifiedName!~.A.&FullyQualifiedName!=X.B&Name!~c", out)

    def test_a_filter_with_a_group_or_a_negation_is_refused(self):
        out, _, _, _ = run_pwsh(
            "try { Get-ExmodComplementFilter @('(Name~a)|Name~b') } catch { Write-Host \"refused: $_\" }")
        self.assertIn("refused: Partition filter term '(Name~a)' is not", out)

    def test_the_partitions_cover_every_test_once_so_their_sum_equals_the_list(self):
        # Fails when the last part's complement is dropped: the tests no declared part names (Other, Late)
        # then run in no process and the sum falls below the list.
        out, _, recorded, count = run_pwsh("")
        self.assertEqual(count, len(TESTS))
        self.assertEqual({"1.22/A.Tests": len(TESTS)}, recorded, out)
        self.assertIn(f"{len(TESTS)} tests in 3 partitions", out)
        self.assertRegex(out, r"walks\s+Passed!.*Total:\s+3")
        self.assertRegex(out, r"rest\s+Passed!.*Total:\s+4")

    def test_a_failure_in_one_partition_fails_the_assembly_and_names_the_test(self):
        out, _, recorded, _ = run_pwsh("", {"TEST_FAIL": "Mod.Tests.World.Two"})
        self.assertIn("FAIL  1.22/A.Tests", out)
        self.assertIn("Mod.Tests.World.Two: boom", out)
        self.assertIn("1 test run(s) failed: 1.22/A.Tests", out)
        self.assertIsNone(recorded)

    def test_a_filter_run_is_not_partitioned(self):
        out, seen, _, _ = run_pwsh("", args="'-Filter', 'FullyQualifiedName~.Other.'", env_extra={})
        self.assertRegex(out, r"PASS  1\.22/A\.Tests\s+Passed!.*Total:\s+3")
        self.assertNotIn("partitions", out)
        self.assertEqual(["start", "end"], seen)

    @staticmethod
    def ran_filters():
        return sorted(a[a.index("--filter") + 1] for a in RUNS)

    def test_parts_names_run_only_that_part_and_leave_the_census_alone(self):
        # Fails when -Parts is ignored (all three parts run) or when a -Parts run writes the census.
        out, _, recorded, _ = run_pwsh("", args="'-Parts', 'worlds'")
        self.assertEqual(["FullyQualifiedName~.World."], self.ran_filters(), out)
        self.assertRegex(out, r"worlds\s+Passed!.*Total:\s+2")
        self.assertIn("Census not read or written: -Parts", out)
        self.assertIsNone(recorded)

    def test_parts_takes_two_names_and_runs_those_two(self):
        # Fails when only the first name of the list is taken or the names are not split on commas.
        out, _, _, _ = run_pwsh("", args="'-Parts', 'worlds,rest'")
        self.assertEqual(2, len(RUNS), out)
        self.assertIn("FullyQualifiedName~.World.", self.ran_filters())
        self.assertNotIn("FullyQualifiedName~.WalkA.|FullyQualifiedName~.WalkB.", self.ran_filters())

    def test_parts_red_runs_the_red_parts_of_the_newest_run_at_this_root(self):
        # Fails when red reads an older run, another checkout's rows, counts a stopped part as green, or a corrupt
        # line ends the read.
        def row(run, part, exit, root="{repo}", **extra):
            return {"run": run, "root": root, "part": part, "label": f"1.22/A.Tests/{part}", "exit": exit, **extra}
        log = [row("old", "rest", 1), row("new", "walks", 0), "{not json", row("new", "worlds", 1),
               row("new", "rest", 0, stopped=True), row("other", "walks", 1, root="/elsewhere"), "{\"half\":"]
        out, _, recorded, _ = run_pwsh("", args="'-Parts', 'red'", runlog=log)
        self.assertEqual(2, len(RUNS), out)
        self.assertEqual(sorted(["FullyQualifiedName~.World.", "FullyQualifiedName!~.WalkA.&FullyQualifiedName!~.WalkB.&FullyQualifiedName!~.World."]),
                         self.ran_filters())
        self.assertIsNone(recorded)

    def test_parts_red_with_no_red_part_exits_two_and_runs_nothing(self):
        # Fails when red with nothing red runs every part, or exits 0 or 1.
        log = [{"run": "r", "root": "{repo}", "part": "walks", "label": "1.22/A.Tests/walks", "exit": 0},
               {"run": "x", "root": "/elsewhere", "part": "walks", "label": "1.22/A.Tests/walks", "exit": 1}]
        out, _, _, _ = run_pwsh("", args="'-Parts', 'red'", runlog=log)
        self.assertEqual([2], EXITS)
        self.assertEqual([], RUNS)
        self.assertIn("no red part", out)

    def test_parts_red_with_no_log_exits_two(self):
        # Fails when a missing run log reads as a green run of nothing.
        out, _, _, _ = run_pwsh("", args="'-Parts', 'red'", runlog=[])
        self.assertEqual([2], EXITS)
        self.assertEqual([], RUNS)

    def test_an_unknown_part_is_an_error_naming_the_valid_parts(self):
        # Fails when an unknown name is ignored (the known ones run) or the message omits the valid names.
        out, _, _, _ = run_pwsh("", args="'-Parts', 'worlds,nope'")
        self.assertIn("threw: -Parts: no partition named nope; the parts are walks, worlds, rest, or red.", out)
        self.assertEqual([], RUNS)

    def test_parts_with_filter_is_an_error(self):
        # Fails when -Filter and -Parts are accepted together.
        out, _, _, _ = run_pwsh("", args="'-Parts', 'worlds', '-Filter', 'FullyQualifiedName~.Other.'")
        self.assertIn("threw: -Parts and -Filter cannot be combined", out)
        self.assertEqual([], RUNS)

    def test_a_part_runs_under_its_heap_limit_in_hex_bytes(self):
        # Fails when the -e DOTNET_GCHeapHardLimit argument is not passed to dotnet test.
        run_pwsh("", manifest=manifest(part_heaps=(3, 5, 7)))
        limits = sorted(a[a.index("-e") + 1] for a in RUNS)
        self.assertEqual(sorted("DOTNET_GCHeapHardLimit=0x%X" % (g << 30) for g in (3, 5, 7)), limits)

    def test_a_part_without_heap_gb_takes_the_assemblys(self):
        # Fails when a part with no heapGb of its own runs without the assembly's limit.
        run_pwsh("", manifest=manifest(heap=4, part_heaps=(2, None, None)))
        limits = sorted(a[a.index("-e") + 1] for a in RUNS)
        self.assertEqual(sorted("DOTNET_GCHeapHardLimit=0x%X" % (g << 30) for g in (2, 4, 4)), limits)

    def test_three_parts_of_five_in_twelve_never_run_three_at_once_and_the_first_two_start_first(self):
        # Fails when no share is taken (all three overlap) and when the units start in reverse manifest order
        # (the complement part starts first).
        out, seen, _, _ = run_pwsh("", {"EXMOD_MEMORY_GB": "12"}, manifest=manifest(heap=5))
        running = peak = 0
        for e in seen:
            running += 1 if e == "start" else -1
            peak = max(peak, running)
        self.assertEqual(2, peak, out)
        filters = [a[a.index("--filter") + 1] for a in RUNS]
        self.assertNotIn("!~", filters[0] + filters[1], out)
        self.assertIn("!~", filters[2], out)
        self.assertIn("each started when the 12 GiB memory budget holds its share (walks 6, worlds 6, rest 6)", out)

    def test_the_unit_with_the_largest_share_starts_first_though_it_is_last_in_the_manifest(self):
        # Fails when the units keep manifest order: the walks part then starts first and the complement last.
        out, _, _, _ = run_pwsh("", {"EXMOD_MEMORY_GB": "12"}, manifest=manifest(heap=2, part_heaps=(2, 2, 9)))
        filters = [a[a.index("--filter") + 1] for a in RUNS]
        self.assertIn("!~", filters[0], out)
        self.assertNotIn("!~", filters[1] + filters[2], out)
        self.assertIn("(rest 10, walks 3, worlds 3)", out)

    def test_a_filtered_run_takes_the_filter_limit_and_the_heavy_flag_the_heap_limit(self):
        # Fails when EXMOD_HEAVY is ignored: the heavy run then still takes filterHeapGb + 1.
        m = manifest(heap=8, filter_heap=3)
        out, _, _, _ = run_pwsh("", args="'-Filter', 'FullyQualifiedName~.Other.'", manifest=m)
        self.assertRegex(out, r"memory: 1\.22/A\.Tests took 4 GiB of 54")
        self.assertEqual(["DOTNET_GCHeapHardLimit=0x%X" % (3 << 30)], [a[a.index("-e") + 1] for a in RUNS])
        out, _, _, _ = run_pwsh("", {"EXMOD_HEAVY": "1"}, args="'-Filter', 'FullyQualifiedName~.Other.'", manifest=m)
        self.assertRegex(out, r"memory: 1\.22/A\.Tests took 9 GiB of 54")
        self.assertEqual(["DOTNET_GCHeapHardLimit=0x%X" % (8 << 30)], [a[a.index("-e") + 1] for a in RUNS])

    def test_a_heavy_filtered_run_books_what_the_parts_its_filter_reaches_need(self):
        # Fails when EXMOD_HEAVY=1 books the whole heap whatever the filter reaches (the worlds run takes 21 not 5), when a term
        # that contains no part's value is taken as reaching only one part (the bare `Walk` run takes 6, not 21), or when
        # one reached heavy part is missed in a `|` union (the worlds-and-walks run takes 6), or when a negated term is read as reaching its part.
        m = manifest(heap=20, filter_heap=15, filter_start=4, part_heaps=(20, 4, 15))
        limit = lambda g: "DOTNET_GCHeapHardLimit=0x%X" % (g << 30)
        for filt, took, lim in (("FullyQualifiedName~.World.One", 5, 4),
                                ("FullyQualifiedName~.Mod.Tests.WalkA.One", 21, 20),
                                ("FullyQualifiedName~Walk", 21, 20),
                                ("FullyQualifiedName~.World.One|FullyQualifiedName~.WalkB.One", 21, 20),
                                ("FullyQualifiedName~.World.One|FullyQualifiedName~.Other.", 21, 20),
                                ("FullyQualifiedName!~.World.One", 21, 20),
                                ("FullyQualifiedName~.World.One&FullyQualifiedName!~.Two", 21, 20)):
            out, _, _, _ = run_pwsh("", {"EXMOD_HEAVY": "1"}, args="'-Filter', '%s'" % filt, manifest=m)
            self.assertRegex(out, r"memory: 1\.22/A\.Tests took %d GiB of 54" % took, filt + "\n" + out)
            self.assertEqual([limit(lim)], [a[a.index("-e") + 1] for a in RUNS], filt)

    def test_a_heavy_filtered_run_of_a_part_heavier_than_the_filter_share_books_that_part_not_the_whole_heap(self):
        # Fails when the booked heap is the assembly's (21) or the filter share (5) instead of the reached part's 9.
        m = manifest(heap=20, filter_heap=15, filter_start=4, part_heaps=(20, 9, 15))
        out, _, _, _ = run_pwsh("", {"EXMOD_HEAVY": "1"}, args="'-Filter', 'FullyQualifiedName~.World.One'", manifest=m)
        self.assertRegex(out, r"memory: 1\.22/A\.Tests took 10 GiB of 54")
        self.assertEqual(["DOTNET_GCHeapHardLimit=0x%X" % (9 << 30)], [a[a.index("-e") + 1] for a in RUNS])

    def test_an_unfiltered_heavy_run_still_books_each_part_its_own_heap(self):
        # Fails when the heavy reach rule is applied to a run with no filter.
        m = manifest(heap=20, filter_heap=15, filter_start=4, part_heaps=(20, 4, 15))
        out, _, _, _ = run_pwsh("", {"EXMOD_HEAVY": "1", "EXMOD_MEMORY_GB": "60"}, manifest=m)
        self.assertIn("(walks 21, rest 16, worlds 5)", out)

    def test_an_out_of_memory_under_the_filter_limit_names_the_heavy_run_and_other_failures_do_not(self):
        # Fails when the hint is added to every failure: the plain failure then carries it too.
        args = "'-Filter', 'FullyQualifiedName~.Other.'"
        out, _, _, _ = run_pwsh("", {"TEST_OOM": "1"}, args=args, manifest=manifest(heap=8, filter_heap=3))
        self.assertIn("(ran under the 3 GiB filter limit; EXMOD_HEAVY=1 runs it under 8 GiB)", out)
        out, _, _, _ = run_pwsh("", {"TEST_FAIL": "Mod.Tests.Other.One"}, args=args,
                                manifest=manifest(heap=8, filter_heap=3))
        self.assertIn("FAIL  1.22/A.Tests", out)
        self.assertNotIn("filter limit", out)

    def test_a_filtered_run_starts_under_filter_start_and_reruns_once_under_the_filter_limit_after_an_oom(self):
        # Fails when filterStartGb is ignored (the run goes straight to 8 GiB), when no retry follows the OutOfMemoryException,
        # or when a second retry is made (the stub runs out of memory under 3 and under 8 GiB alike).
        args = "'-Filter', 'FullyQualifiedName~.Other.'"
        limit = lambda g: "DOTNET_GCHeapHardLimit=0x%X" % (g << 30)
        m = manifest(heap=12, filter_heap=8, filter_start=3)
        out, _, _, _ = run_pwsh("", args=args, manifest=m)
        self.assertEqual([limit(3)], [a[a.index("-e") + 1] for a in RUNS], out)
        self.assertRegex(out, r"memory: 1\.22/A\.Tests took 4 GiB of 54")
        self.assertNotIn("retrying", out)
        out, _, _, _ = run_pwsh("", {"TEST_OOM_AT": "3"}, args=args, manifest=m)
        self.assertEqual([limit(3), limit(8)], [a[a.index("-e") + 1] for a in RUNS], out)
        self.assertIn("memory: 1.22/A.Tests ran out of memory under 3 GiB, retrying once under 8 GiB", out)
        self.assertRegex(out, r"memory: 1\.22/A\.Tests took 9 GiB of 54")
        self.assertIn("PASS  1.22/A.Tests", out)
        out, _, _, _ = run_pwsh("", {"TEST_OOM": "1"}, args=args, manifest=m)
        self.assertEqual([limit(3), limit(8)], [a[a.index("-e") + 1] for a in RUNS], out)
        self.assertEqual(1, out.count("retrying once"), out)
        self.assertIn("(ran under the 8 GiB filter limit; EXMOD_HEAVY=1 runs it under 12 GiB)", out)

    def test_a_filter_start_run_that_fails_for_another_reason_is_not_retried_and_heavy_skips_it(self):
        # Fails when the retry fires on any failure (the plain failure is run twice) or EXMOD_HEAVY=1 still starts under filterStartGb.
        args = "'-Filter', 'FullyQualifiedName~.Other.'"
        m = manifest(heap=12, filter_heap=8, filter_start=3)
        out, _, _, _ = run_pwsh("", {"TEST_FAIL": "Mod.Tests.Other.One"}, args=args, manifest=m)
        self.assertEqual(1, len(RUNS), out)
        out, _, _, _ = run_pwsh("", {"EXMOD_HEAVY": "1"}, args=args, manifest=m)
        self.assertEqual(["DOTNET_GCHeapHardLimit=0x%X" % (12 << 30)], [a[a.index("-e") + 1] for a in RUNS], out)

    def test_a_filter_start_gb_that_is_not_a_whole_number_above_zero_is_refused(self):
        # Fails when Get-ExmodTestMemory does not validate filterStartGb.
        for bad in (0, 2.5, "x"):
            out, _, _, _ = run_pwsh("", manifest=manifest(heap=8, filter_heap=3, filter_start=bad))
            self.assertIn("exmod.json: testMemory.A.Tests.filterStartGb must be a whole number of GiB above 0", out)

    def test_the_release_line_carries_the_peak_and_one_json_line_is_appended_per_run(self):
        # Fails when the peak is left off the release line, or when no JSON line is written to EXMOD_MEMORY_LOG.
        if not os.path.exists("/proc/self/stat"):
            self.skipTest("no /proc")
        with tempfile.TemporaryDirectory() as d:
            log = os.path.join(d, "peaks.jsonl")
            env = {"EXMOD_MEMORY_LOG": log, "EXMOD_MEMORY_SAMPLE_MS": "200", "EXMOD_PROC_DIR": "/proc", "TEST_HOLD_MB": "300", "TEST_HOLD_S": "2"}
            out, _, _, _ = run_pwsh("", env, args="'-Filter', 'FullyQualifiedName~.Other.'",
                                    manifest=manifest(heap=8, filter_heap=3))
            m = re.search(r"memory: 1\.22/A\.Tests released 4 GiB at \d\d:\d\d:\d\d, held \d+ s, peak (\d+\.\d) GiB", out)
            self.assertTrue(m, out)
            self.assertGreaterEqual(float(m.group(1)), 0.3, out)
            with open(log) as f:
                rows = [json.loads(l) for l in f]
            self.assertEqual(1, len(rows), rows)
            row = rows[0]
            self.assertEqual(["label", "filtered", "filter", "shareGb", "limitGb", "peakGb", "seconds", "exit", "time"], list(row))
            self.assertEqual(("1.22/A.Tests", True, "FullyQualifiedName~.Other.", 4, 3, 0),
                             (row["label"], row["filtered"], row["filter"], row["shareGb"], row["limitGb"], row["exit"]))
            self.assertGreaterEqual(row["peakGb"], 0.29)
            self.assertGreaterEqual(row["seconds"], 1)
            self.assertRegex(row["time"], r"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$")

    def test_an_assembly_without_an_entry_takes_four_and_passes_no_limit(self):
        # Fails when the default share is 0 or a limit is passed for an assembly with no figure.
        out, _, _, _ = run_pwsh("", manifest={}, args="")
        self.assertRegex(out, r"memory: 1\.22/A\.Tests took 4 GiB of 54")
        self.assertEqual([], [a for a in RUNS if "-e" in a])

    def test_a_heap_gb_that_is_not_a_whole_number_above_zero_is_refused(self):
        # Fails when Get-ExmodTestMemory or the part check does not validate heapGb.
        for bad in (0, 2.5, "x"):
            out, _, _, _ = run_pwsh("", manifest=manifest(heap=bad))
            self.assertIn("exmod.json: testMemory.A.Tests.heapGb must be a whole number of GiB above 0", out)
            out, _, _, _ = run_pwsh("", manifest=manifest(part_heaps=(bad, None, None)))
            self.assertIn("exmod.json: partitions.A.Tests.parts[0].heapGb must be a whole number of GiB above 0", out)

    def test_an_entry_with_only_memory_gb_runs_with_no_error(self):
        # Fails when a manifest that still says memoryGb throws for want of a heapGb.
        m = json.loads(json.dumps(MANIFEST))
        m["partitions"]["A.Tests"]["memoryGb"] = 13
        out, _, recorded, _ = run_pwsh("", manifest=m)
        self.assertNotIn("threw", out)
        self.assertEqual({"1.22/A.Tests": len(TESTS)}, recorded, out)

    def test_a_manifest_whose_last_part_carries_a_filter_is_refused(self):
        bad = {"partitions": {"A.Tests": {"parts": [{"name": "a", "filter": "Name~a"},
                                                    {"name": "b", "filter": "Name~b"}]}}}
        out, _, _, _ = run_pwsh("", manifest=bad)
        self.assertIn("the last part of partitions.A.Tests is the complement", out)

    def test_an_assembly_without_partitions_runs_as_one_process(self):
        out, seen, recorded, _ = run_pwsh("", manifest={})
        self.assertEqual({"1.22/A.Tests": len(TESTS)}, recorded, out)
        self.assertEqual(["start", "end"], seen)



def run_records(args="", env_extra=None, manifest=MANIFEST, body=""):
    """(output, rows) of a stubbed run with EXMOD_RUN_LOG in a folder of its own; rows is [] when nothing was written."""
    with tempfile.TemporaryDirectory() as d:
        log = os.path.join(d, "test-runs.jsonl")
        out, _, _, _ = run_pwsh(body, {"EXMOD_RUN_LOG": log, **(env_extra or {})}, args=args, manifest=manifest)
        rows = []
        if os.path.exists(log):
            with open(log) as f:
                rows = [json.loads(l) for l in f if l.strip()]
        return out, rows


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "the dotnet stub is a shell script")
class RunRecordTests(unittest.TestCase):
    KEYS = ["run", "trigger", "root", "commit", "dirty", "label", "part", "filter", "filtered", "exit", "seconds",
            "time", "tests", "passed", "failed", "skipped", "failures", "classes"]

    def test_a_partitioned_host_writes_one_row_with_every_key(self):
        # Fails when a key is dropped or renamed, when part is left out of a partition's row, or when the counts
        # come from the wrong summary field.
        out, rows = run_records(env_extra={"TEST_FAIL": "Mod.Tests.World.Two"})
        self.assertEqual(3, len(rows), out)
        by = {r["part"]: r for r in rows}
        self.assertEqual({"walks", "worlds", "rest"}, set(by))
        for r in rows:
            self.assertEqual(self.KEYS, list(r))
        w = by["worlds"]
        self.assertEqual(("direct", "1.22/A.Tests/worlds", "FullyQualifiedName~.World.", False, 1),
                         (w["trigger"], w["label"], w["filter"], w["filtered"], w["exit"]))
        self.assertEqual((2, 1, 1, 0), (w["tests"], w["passed"], w["failed"], w["skipped"]))
        self.assertEqual(["Mod.Tests.World.Two"], w["failures"])
        self.assertEqual("repo", os.path.basename(w["root"]))
        self.assertIsNone(w["commit"])
        self.assertIsNone(w["dirty"])
        self.assertIsNone(w["classes"])
        self.assertIsInstance(w["seconds"], (int, float))
        self.assertRegex(w["time"], r"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$")
        self.assertEqual((0, 3, []), (by["walks"]["exit"], by["walks"]["tests"], by["walks"]["failures"]))
        self.assertEqual((3, 0), (by["walks"]["passed"], by["walks"]["failed"]))

    def test_the_host_log_keeps_no_results_file_line(self):
        # Fails when the trx logger's `Results File:` line stays in the host's output and so in its log.
        out, rows = run_records(args="'-Filter', 'FullyQualifiedName~.Other.'")
        self.assertEqual(1, len(rows), out)
        self.assertEqual(1, len(LOGS))
        self.assertIn("Passed!", LOGS[0])
        self.assertNotIn("Results File", LOGS[0])
        self.assertNotIn("Results File", out)

    def test_commit_and_dirty_come_from_the_checkout(self):
        # Fails when commit is not HEAD, or dirty is not true exactly when a tracked file is modified.
        body = (
            "$r = $env:TEST_REPO; git -C $r init -q; git -C $r config user.email a@b; git -C $r config user.name a; "
            "git -C $r add -A; git -C $r commit -q -m x; Write-Host \"HEAD=$(git -C $r rev-parse HEAD)\"; "
            "if ($env:TEST_DIRTY) { Add-Content -LiteralPath (Join-Path $r 'exmod.json') ' ' }; "
            "'untracked' | Set-Content -LiteralPath (Join-Path $r 'new.txt')")
        args = "'-Filter', 'FullyQualifiedName~.Other.'"
        out, rows = run_records(args=args, body=body)
        head = re.search(r"HEAD=([0-9a-f]{40,64})", out).group(1)
        self.assertEqual((head, False), (rows[0]["commit"], rows[0]["dirty"]), out)
        _, rows = run_records(args=args, body=body, env_extra={"TEST_DIRTY": "1"})
        self.assertIs(True, rows[0]["dirty"])

    def test_a_filtered_whole_assembly_row_has_no_part_and_the_filter(self):
        # Fails when part is written for an unpartitioned host or filtered is not the -Filter flag.
        _, rows = run_records(args="'-Filter', 'FullyQualifiedName~.Other.'")
        self.assertEqual(1, len(rows))
        self.assertEqual((None, "FullyQualifiedName~.Other.", True, 3), (rows[0]["part"], rows[0]["filter"], rows[0]["filtered"], rows[0]["tests"]))

    def test_one_run_id_covers_all_hosts_of_a_partitioned_run(self):
        # Fails when the id is made again for each host.
        _, rows = run_records()
        ids = {r["run"] for r in rows}
        self.assertEqual(3, len(rows))
        self.assertEqual(1, len(ids), ids)
        self.assertRegex(ids.pop(), r"^[0-9a-f]{12}$")

    def test_run_id_and_trigger_come_from_the_environment(self):
        # Fails when either variable is ignored.
        _, rows = run_records(env_extra={"EXMOD_RUN_ID": "lane-7", "EXMOD_RUN_TRIGGER": "landing"})
        self.assertEqual({("lane-7", "landing")}, {(r["run"], r["trigger"]) for r in rows})

    def test_failures_are_capped_at_fifty_and_truncated_marks_more(self):
        # Fails when the cap is dropped (60 names), moved to 49, or truncated is written at exactly 50.
        _, rows = run_records(args="'-Filter', 'FullyQualifiedName~.Other.'", env_extra={"TEST_FAIL_MANY": "60"})
        self.assertEqual(50, len(rows[0]["failures"]))
        self.assertEqual(("Mod.Tests.Many.T0", "Mod.Tests.Many.T49"), (rows[0]["failures"][0], rows[0]["failures"][-1]))
        self.assertIs(True, rows[0]["truncated"])
        self.assertEqual(["failures", "truncated", "classes"], list(rows[0])[-3:])
        _, rows = run_records(args="'-Filter', 'FullyQualifiedName~.Other.'", env_extra={"TEST_FAIL_MANY": "50"})
        self.assertEqual(50, len(rows[0]["failures"]))
        self.assertNotIn("truncated", rows[0])

    def test_classes_hold_the_forty_largest_and_the_trx_is_deleted(self):
        # Fails when the classes are not summed per class, not cut to 40, not largest first, or when the trx file stays.
        with tempfile.TemporaryDirectory() as d:
            trx = os.path.join(d, "in.trx")
            write_trx(trx, 45, 3)
            out, rows = run_records(args="'-Filter', 'FullyQualifiedName~.Other.'", env_extra={"TEST_TRX_SRC": trx})
            self.assertEqual(1, len(rows), out)
            classes = rows[0]["classes"]
            self.assertEqual(40, len(classes))
            self.assertEqual(["Mod.Tests.C44", "Mod.Tests.C43"], list(classes)[:2])
            self.assertEqual({f"Mod.Tests.C{i:02d}" for i in range(5, 45)}, set(classes))
            self.assertAlmostEqual(0.135, classes["Mod.Tests.C44"], places=3)
            self.assertNotIn("Mod.Tests.C04", classes)
        with tempfile.TemporaryDirectory() as d:
            trx = os.path.join(d, "x.trx")
            write_trx(trx, 2, 1)
            body = ("$r = Read-ExmodTrxClassSeconds $env:TRX_FILE; Write-Host \"classes: $($r.Count)\"; "
                    "Add-ExmodTestRunRecord 'r' 'root' ([pscustomobject]@{ Commit = $null; Dirty = $null }) 'l' '' $false '' 0 1.0 @() $env:TRX_FILE; "
                    "Write-Host \"left: $(Test-Path -LiteralPath $env:TRX_FILE)\"")
            out, _ = run_records(env_extra={"TRX_FILE": trx}, body=body)
            self.assertIn("classes: 2", out)
            self.assertIn("left: False", out)

    def test_a_large_trx_is_read_without_loading_it_whole(self):
        # Fails when the reader loads the file whole (XmlDocument or ReadAllText): the process's peak working set then
        # grows by several times the 50 MB file.
        with tempfile.TemporaryDirectory() as d:
            trx = os.path.join(d, "big.trx")
            write_trx(trx, 50, 400, pad=2600)
            self.assertGreaterEqual(os.path.getsize(trx), 50 * 1024 * 1024)
            body = ("$before = [Diagnostics.Process]::GetCurrentProcess().PeakWorkingSet64; "
                    "$r = Read-ExmodTrxClassSeconds $env:TRX_FILE; "
                    "$grew = [Diagnostics.Process]::GetCurrentProcess().PeakWorkingSet64 - $before; "
                    "Write-Host \"big: $($r.Count) $($r['Mod.Tests.C49']) growth-mb: $([int]($grew / 1MB))\"")
            out, _ = run_records(env_extra={"TRX_FILE": trx}, body=body)
            m = re.search(r"big: (\d+) ([\d.]+) growth-mb: (-?\d+)", out)
            self.assertTrue(m, out)
            self.assertEqual(40, int(m.group(1)))
            self.assertAlmostEqual(20.0, float(m.group(2)), places=2)
            self.assertLess(int(m.group(3)), 40, out)

    def test_a_missing_and_a_corrupt_trx_leave_the_run_unchanged(self):
        # Fails when an unreadable trx throws out of the reader into the run (no row is written) or sets classes.
        with tempfile.TemporaryDirectory() as d:
            good = os.path.join(d, "good.trx")
            write_trx(good, 2, 2)
            bad = os.path.join(d, "bad.trx")
            with open(bad, "w") as f:
                f.write("<TestRun><Results><UnitTestResult testId=\"a\" duration=\"00:00:01\"")
            args = "'-Filter', 'FullyQualifiedName~.Other.'"
            base_out, base_rows = run_records(args=args, env_extra={"TEST_TRX_SRC": good})
            self.assertIsNotNone(base_rows[0]["classes"])
            for name, env in (("missing", {}), ("corrupt", {"TEST_TRX_SRC": bad})):
                out, rows = run_records(args=args, env_extra=env)
                self.assertEqual(1, len(rows), name)
                self.assertIsNone(rows[0]["classes"], name)
                self.assertEqual(3, rows[0]["tests"], name)
                self.assertEqual(re.sub(r"held \d+ s", "", results_block(base_out)), re.sub(r"held \d+ s", "", results_block(out)), name)
                self.assertNotIn("threw:", out, name)

    def test_an_unwritable_log_leaves_the_run_unchanged(self):
        # Fails when a write failure escapes Add-ExmodTestRunRecord: the host's result is then lost.
        with tempfile.TemporaryDirectory() as d:
            blocker = os.path.join(d, "file")
            with open(blocker, "w") as f:
                f.write("x")
            args = "'-Filter', 'FullyQualifiedName~.Other.'"
            base_out, _ = run_records(args=args)
            out, rows = run_records(args=args, env_extra={"EXMOD_RUN_LOG": os.path.join(blocker, "sub", "runs.jsonl")})
            self.assertEqual([], rows)
            self.assertNotIn("threw:", out)
            self.assertEqual(re.sub(r"held \d+ s", "", results_block(base_out)), re.sub(r"held \d+ s", "", results_block(out)))
            self.assertIn("PASS  1.22/A.Tests", out)


if __name__ == "__main__":
    unittest.main()
