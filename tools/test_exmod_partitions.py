"""Tests for the test partitions of `exmod test` in exmod/src.ps1: the complement filter, the
partitions covering a stubbed assembly once, the memory limit on concurrent processes and the sum of
failures. Run through pwsh; skipped when none is found."""

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
# matches, failing when TEST_FAIL names a test among them. A test run appends start/end to TEST_ORDER.
STUB = """#!/usr/bin/env python3
import os, re, sys, time
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
order = os.environ.get("TEST_ORDER")
if order:
    open(order, "a").write("start\\n")
    time.sleep(1)
    open(order, "a").write("end\\n")
bad = [n for n in hit if n == os.environ.get("TEST_FAIL")]
if bad:
    print("  Failed %%s [1 ms]" %% bad[0])
    print("  Error Message:")
    print("   boom")
print("%%s!  - Failed: %%d, Passed: %%d, Skipped: 0, Total: %%d" %% ("Failed" if bad else "Passed", len(bad), len(hit) - len(bad), len(hit)))
sys.exit(1 if bad else 0)
""" % (TESTS,)

MANIFEST = {"partitions": {"A.Tests": {"memoryGb": 13, "parts": [
    {"name": "walks", "filter": "FullyQualifiedName~.WalkA.|FullyQualifiedName~.WalkB."},
    {"name": "worlds", "filter": "FullyQualifiedName~.World."},
    {"name": "rest"}]}}}

RUN = r"""
function Resolve-DotnetHost { $env:TEST_STUB }
function Get-ExmodTestProjects {
  [ordered]@{ a = [pscustomobject]@{ Project = 'A.Tests'; Proj = 'a.csproj'; Series = @('1.22') } }
}
%s
try { Invoke-Test (@('1.22') + @(%s)) } catch { Write-Host "threw: $_" }
"""


def run_pwsh(body, env_extra=None, args="", manifest=MANIFEST, stub=STUB):
    assert PWSH
    with tempfile.TemporaryDirectory() as d:
        repo = os.path.join(d, "repo")
        touch(os.path.join(repo, "exmod.json"), json.dumps(manifest))
        stubpath = os.path.join(d, "dotnet")
        touch(stubpath, stub)
        os.chmod(stubpath, os.stat(stubpath).st_mode | stat.S_IEXEC)
        order = os.path.join(d, "order.log")
        env = exmod_env(os.path.join(d, "local"), TEST_REPO=repo, TEST_STUB=stubpath, TEST_ORDER=order,
                        **(env_extra or {}))
        out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command",
                              exmod_script(RUN % (body, args))],
                             env=env, capture_output=True, text=True).stdout
        seen = []
        if os.path.exists(order):
            with open(order) as f:
                seen = f.read().split()
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

    def test_one_slot_runs_the_partitions_one_after_another(self):
        # Fails when the memory slots no longer cap the throttle: the three 1 s processes then overlap.
        _, seen, _, _ = run_pwsh("function Get-ExmodPartitionSlots { 1 }")
        self.assertEqual(["start", "end"] * 3, seen)

    def test_the_slots_are_whole_hosts_in_available_memory_and_unknown_without_a_limit(self):
        out, _, _, _ = run_pwsh(
            "Write-Host \"slots: $(Get-ExmodPartitionSlots 0.001) none: [$(Get-ExmodPartitionSlots 0)]\"")
        slots = int(re.search(r"slots: (\d+) none: \[\]", out).group(1))
        self.assertGreater(slots, 1)

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
