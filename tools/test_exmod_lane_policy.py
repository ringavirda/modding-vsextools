"""Tests for the lane policy of the `exmod test` command in exmod/src.ps1 (Get-ExmodLaneTestRefusal): in a checkout under a
sessions/.worktrees folder it refuses an unfiltered run, a namespace-only -Filter and a -Filter naming a heavy class
before anything builds, with exit code 2. Each case builds a checkout with a stub dotnet. Run through pwsh; skipped when
none is found."""

import json
import os
import stat
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import PWSH, exmod_env, exmod_script, touch  # noqa: E402
from test_exmod_affected import CSPROJ, DOTNET, git  # noqa: E402
from test_exmod_affected import class_source  # noqa: E402

LANE = os.path.join("sessions", ".worktrees", "lane")


class Checkout:
    """A checkout with one test project, `exmod test <args>` run in it through the command's own action."""

    def __init__(self, under=LANE):
        self.dir = tempfile.TemporaryDirectory()
        self.root = os.path.join(self.dir.name, under)
        os.makedirs(self.root)
        git(self.root, "init", "-q", "-b", "dev")
        touch(os.path.join(self.root, "exmod.json"), json.dumps({"series": ["1.22"], "tests": ["tests/A.Tests"]}))
        touch(os.path.join(self.root, "tests/A.Tests/A.Tests.csproj"), CSPROJ)
        touch(os.path.join(self.root, "tests/A.Tests/ATests.cs"), class_source("ATests"))
        git(self.root, "add", "-A")
        git(self.root, "commit", "-q", "-m", "base")
        self.heavy = os.path.join(self.dir.name, "heavy-tests.txt")
        touch(self.heavy, "CraftWalkGuards # 10\n")
        self.stub = os.path.join(self.dir.name, "dotnet")
        touch(self.stub, DOTNET)
        os.chmod(self.stub, os.stat(self.stub).st_mode | stat.S_IEXEC)
        self.argv = os.path.join(self.dir.name, "argv.log")

    def close(self):
        self.dir.cleanup()

    def run(self, args, **env):
        """(exit code, output, dotnet test argv lines): the `test` command with `args` and the environment `env`."""
        assert PWSH
        body = ("function Resolve-DotnetHost { $env:TEST_STUB }\n"
                f"& (Resolve-ExmodCommand 'test').Action -Argv @({args})\n")
        full = exmod_env(os.path.join(self.dir.name, "local"), TEST_REPO=self.root, TEST_STUB=self.stub,
                         TEST_ARGV=self.argv, EXMOD_HEAVY_TESTS=self.heavy, **env)
        done = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", exmod_script(body)],
                              env=full, capture_output=True, text=True)
        runs = []
        if os.path.exists(self.argv):
            with open(self.argv) as f:
                runs = [json.loads(l) for l in f if l.strip()]
            os.remove(self.argv)
        return done.returncode, (done.stdout + done.stderr).replace("\r", ""), runs


@unittest.skipUnless(PWSH, "needs pwsh")
class LanePolicy(unittest.TestCase):
    def setUp(self):
        self.lane = Checkout()
        self.addCleanup(self.lane.close)

    def refused(self, args, reason, **env):
        code, out, runs = self.lane.run(args, **env)
        self.assertEqual(code, 2, out)
        self.assertIn(reason, out)
        self.assertEqual(runs, [])

    def test_unfiltered_run_is_refused(self):
        """Fails when the no -Filter, no -Affected refusal is removed."""
        self.refused("'1.22', '-NoBuild'", "it has no -Filter")

    def test_coverage_and_parts_without_filter_are_refused(self):
        """Fails when -Coverage or -Parts without -Filter stops counting as unfiltered."""
        self.refused("'1.22', '-Coverage'", "it has no -Filter")
        self.refused("'1.22', '-Parts', 'red'", "it has no -Filter")

    def test_namespace_only_filter_is_refused(self):
        """Fails when a -Filter term that is only the IronIndustryExpanded namespace is let through."""
        self.refused("'1.22', '-Filter', 'FullyQualifiedName~IronIndustryExpanded'", "only a namespace")
        self.refused("'1.22', '-Filter', 'FullyQualifiedName~IronIndustryExpanded.Tests'", "only a namespace")
        self.refused("'1.22', '-Filter', '(Name=ATests)|FullyQualifiedName=IronIndustryExpanded'", "only a namespace")

    def test_namespace_term_with_a_class_is_not_a_namespace_only_filter(self):
        """Fails when a term naming a class under the namespace is judged as the namespace alone."""
        code, out, runs = self.lane.run("'1.22', '-Filter', 'FullyQualifiedName~IronIndustryExpanded.GreaseTests'")
        self.assertEqual(code, 0, out)
        self.assertEqual(len(runs), 1)

    def test_filter_naming_a_heavy_class_is_refused(self):
        """Fails when the heavy list is not matched against the -Filter, or a longer name matches as a prefix."""
        self.refused("'1.22', '-Filter', 'FullyQualifiedName~Light|FullyQualifiedName~CraftWalkGuards'",
                     "names CraftWalkGuards, a heavy class")
        code, out, runs = self.lane.run("'1.22', '-Filter', 'FullyQualifiedName~CraftWalkGuardsHelperTests'")
        self.assertEqual(code, 0, out)
        self.assertEqual(len(runs), 1)

    def test_heavy_env_lifts_each_refusal(self):
        """Fails when EXMOD_HEAVY=1 stops lifting a refusal."""
        for args in ("'1.22'",
                     "'1.22', '-Filter', 'FullyQualifiedName~IronIndustryExpanded'",
                     "'1.22', '-Filter', 'FullyQualifiedName~CraftWalkGuards'"):
            code, out, runs = self.lane.run(args, EXMOD_HEAVY="1")
            self.assertEqual(code, 0, f"{args}: {out}")
            self.assertEqual(len(runs), 1, args)

    def test_landing_run_is_not_refused(self):
        """Fails when a run with EXMOD_RUN_TRIGGER=landing:... is refused in a lane worktree."""
        code, out, runs = self.lane.run("'1.22'", EXMOD_RUN_TRIGGER="landing:x")
        self.assertEqual(code, 0, out)
        self.assertEqual(len(runs), 1)

    def test_filtered_light_run_goes_through(self):
        """Fails when a filtered run naming no heavy class and not the namespace is refused."""
        code, out, runs = self.lane.run("'1.22', '-Filter', 'FullyQualifiedName~ATests'")
        self.assertEqual(code, 0, out)
        self.assertEqual(len(runs), 1)
        self.assertIn("FullyQualifiedName~ATests", runs[0])

    def test_affected_without_filter_is_not_refused_as_unfiltered(self):
        """Fails when -Affected stops exempting a run from the no -Filter refusal."""
        code, out, _ = self.lane.run("'1.22', '-Affected', '-List'")
        self.assertEqual(code, 0, out)
        self.assertNotIn("it has no -Filter", out)


@unittest.skipUnless(PWSH, "needs pwsh")
class OutsideALane(unittest.TestCase):
    def test_unfiltered_run_outside_sessions_worktrees_is_not_refused(self):
        """Fails when the refusal applies to a checkout that is not under sessions/.worktrees."""
        for under in ("repo", os.path.join("sessions", "other", "repo")):
            checkout = Checkout(under)
            self.addCleanup(checkout.close)
            code, out, runs = checkout.run("'1.22'")
            self.assertEqual(code, 0, f"{under}: {out}")
            self.assertEqual(len(runs), 1, under)


if __name__ == "__main__":
    unittest.main()
