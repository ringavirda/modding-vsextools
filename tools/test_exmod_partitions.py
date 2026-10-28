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
# matches, failing when TEST_FAIL names a test among them (or, with TEST_OOM, the first match, printing an
# OutOfMemoryException). A test run appends start/end to TEST_ORDER and its argv as JSON to TEST_ARGV.
STUB = """#!/usr/bin/env python3
import json, os, re, sys, time
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
argv = os.environ.get("TEST_ARGV")
if argv:
    open(argv, "a").write(json.dumps(a) + "\\n")
order = os.environ.get("TEST_ORDER")
if order:
    open(order, "a").write("start\\n")
    time.sleep(1)
    open(order, "a").write("end\\n")
bad = [n for n in hit if n == os.environ.get("TEST_FAIL")]
if os.environ.get("TEST_OOM") and hit:
    bad = hit[:1]
    print("System.OutOfMemoryException: Exception of type was thrown.")
if bad:
    print("  Failed %%s [1 ms]" %% bad[0])
    print("  Error Message:")
    print("   boom")
print("%%s!  - Failed: %%d, Passed: %%d, Skipped: 0, Total: %%d" %% ("Failed" if bad else "Passed", len(bad), len(hit) - len(bad), len(hit)))
sys.exit(1 if bad else 0)
""" % (TESTS,)

MANIFEST = {"partitions": {"A.Tests": {"parts": [
    {"name": "walks", "filter": "FullyQualifiedName~.WalkA.|FullyQualifiedName~.WalkB."},
    {"name": "worlds", "filter": "FullyQualifiedName~.World."},
    {"name": "rest"}]}}}


def manifest(heap=None, filter_heap=None, part_heaps=(None, None, None), **extra):
    """MANIFEST with testMemory for A.Tests (when heap is given) and heapGb on the parts that have one."""
    m = json.loads(json.dumps(MANIFEST))
    for part, h in zip(m["partitions"]["A.Tests"]["parts"], part_heaps):
        if h is not None:
            part["heapGb"] = h
    if heap is not None:
        m["testMemory"] = {"A.Tests": {"heapGb": heap}}
        if filter_heap is not None:
            m["testMemory"]["A.Tests"]["filterHeapGb"] = filter_heap
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


def run_pwsh(body, env_extra=None, args="", manifest=MANIFEST, stub=STUB):
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
        out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command",
                              exmod_script(RUN % (body, args))],
                             env=env, capture_output=True, text=True).stdout
        seen = []
        if os.path.exists(order):
            with open(order) as f:
                seen = f.read().split()
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

    def test_a_filtered_run_takes_the_filter_limit_and_the_heavy_flag_the_heap_limit(self):
        # Fails when EXMOD_HEAVY is ignored: the heavy run then still takes filterHeapGb + 1.
        m = manifest(heap=8, filter_heap=3)
        out, _, _, _ = run_pwsh("", args="'-Filter', 'FullyQualifiedName~.Other.'", manifest=m)
        self.assertRegex(out, r"memory: 1\.22/A\.Tests took 4 GiB of 54")
        self.assertEqual(["DOTNET_GCHeapHardLimit=0x%X" % (3 << 30)], [a[a.index("-e") + 1] for a in RUNS])
        out, _, _, _ = run_pwsh("", {"EXMOD_HEAVY": "1"}, args="'-Filter', 'FullyQualifiedName~.Other.'", manifest=m)
        self.assertRegex(out, r"memory: 1\.22/A\.Tests took 9 GiB of 54")
        self.assertEqual(["DOTNET_GCHeapHardLimit=0x%X" % (8 << 30)], [a[a.index("-e") + 1] for a in RUNS])

    def test_an_out_of_memory_under_the_filter_limit_names_the_heavy_run_and_other_failures_do_not(self):
        # Fails when the hint is added to every failure: the plain failure then carries it too.
        args = "'-Filter', 'FullyQualifiedName~.Other.'"
        out, _, _, _ = run_pwsh("", {"TEST_OOM": "1"}, args=args, manifest=manifest(heap=8, filter_heap=3))
        self.assertIn("(ran under the 3 GiB filter limit; EXMOD_HEAVY=1 runs it under 8 GiB)", out)
        out, _, _, _ = run_pwsh("", {"TEST_FAIL": "Mod.Tests.Other.One"}, args=args,
                                manifest=manifest(heap=8, filter_heap=3))
        self.assertIn("FAIL  1.22/A.Tests", out)
        self.assertNotIn("filter limit", out)

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


if __name__ == "__main__":
    unittest.main()
