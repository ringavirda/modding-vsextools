"""exmod results: the newest recorded run at a repository root, read from $EXMOD_RUN_LOG."""
import json
import os
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import PWSH, exmod_env, exmod_script, touch  # noqa: E402


def row(run, part, exit=0, root="{repo}", commit="abc1234def", **extra):
    return {"run": run, "trigger": "direct", "root": root, "commit": commit, "dirty": False,
            "label": f"1.22/A.Tests/{part}", "part": part, "exit": exit, "seconds": 12.5, "time": "2026-11-09T10:00:00Z",
            "tests": 5, "passed": 5, "failed": 0, **extra}


def results(log, args=""):
    """(exit code, stdout) of `exmod results <args>` in a checkout whose run log holds `log`: dicts (their "{repo}"
    values become the checkout's path) or raw strings."""
    assert PWSH
    with tempfile.TemporaryDirectory() as d:
        repo = os.path.join(d, "repo")
        touch(os.path.join(repo, "exmod.json"), "{}")
        path = os.path.join(d, "runs.jsonl")
        with open(path, "w") as f:
            for r in log:
                if isinstance(r, dict):
                    r = json.dumps({k: v.replace("{repo}", repo) if isinstance(v, str) else v for k, v in r.items()})
                f.write(r + "\n")
        env = exmod_env(os.path.join(d, "local"), TEST_REPO=repo, EXMOD_RUN_LOG=path)
        done = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command",
                               exmod_script(f"Invoke-Results @({args})")],
                              env=env, capture_output=True, text=True)
        return done.returncode, done.stdout.replace("\r", "")


@unittest.skipUnless(PWSH, "pwsh not available")
class ResultsTests(unittest.TestCase):
    def test_last_is_the_newest_run_at_this_root_and_ignores_a_newer_run_elsewhere(self):
        # Fails when last reads the final record of the file whatever its root, or the oldest run at this root.
        log = [row("old", "walks"), row("new", "worlds", commit="fff0000"), row("else", "walks", root="/elsewhere")]
        code, out = results(log)
        self.assertEqual(0, code, out)
        self.assertIn("run new  trigger direct  commit fff0000  dirty False  time 2026-11-09T10:00:00Z", out)
        self.assertIn("1.22/A.Tests/worlds  part worlds  exit 0  tests 5  failed 0  12.5 s", out)
        self.assertNotIn("run old", out)
        self.assertNotIn("run else", out)

    def test_a_repeated_label_counts_its_last_record_and_the_header_takes_the_newest(self):
        # Fails when an earlier red attempt outweighs a later green one, or the header reads the oldest record.
        log = [row("r", "walks", exit=1, commit="1111111"), row("r", "rest", commit="1111111"),
               row("r", "walks", exit=0, commit="2222222"), row("r", "rest", commit="2222222")]
        code, out = results(log)
        self.assertEqual(0, code, out)
        self.assertIn("commit 2222222", out)
        self.assertEqual(2, out.count("part "), out)

    def test_a_commit_prefix_picks_that_commits_newest_run(self):
        # Fails when the prefix is ignored (the newest run prints) or matched anywhere but the start of the hash.
        log = [row("a1", "walks", commit="abc1234"), row("a2", "rest", commit="abc1234"),
               row("b1", "walks", commit="def5678")]
        code, out = results(log, "'abc'")
        self.assertEqual(0, code, out)
        self.assertIn("run a2 ", out)
        self.assertNotIn("run a1", out)
        self.assertNotIn("run b1", out)
        code, out = results(log, "'bc1'")
        self.assertEqual(2, code, out)

    def test_every_record_of_the_run_prints_a_line_and_a_stopped_one_says_so(self):
        # Fails when a stopped record loses its `stopped` mark, or a stopped record with exit 0 exits the run green.
        log = [row("r", "walks", exit=1, failed=1, failures=["A.Tests.X.T1"]), row("r", "worlds", exit=None, stopped=True)]
        code, out = results(log)
        self.assertEqual(1, code, out)
        self.assertIn("1.22/A.Tests/walks  part walks  exit 1  tests 5  failed 1  12.5 s", out)
        self.assertIn("1.22/A.Tests/worlds  part worlds  exit -  tests 5  failed 0  12.5 s  stopped", out)
        code, out = results([row("s", "walks", stopped=True)])
        self.assertEqual(1, code, out)

    def test_failing_names_print_and_a_truncated_list_says_so(self):
        # Fails when the names are dropped or the truncation line is missing.
        log = [row("r", "walks", exit=1, failed=60, failures=["A.Tests.X.T1", "A.Tests.X.T2"], truncated=True)]
        code, out = results(log)
        self.assertEqual(1, code, out)
        self.assertIn("Failing:\n  A.Tests.X.T1\n  A.Tests.X.T2\n  (the list is truncated at 50 per part)", out)
        code, out = results([row("r", "walks", exit=1, failures=["A.Tests.X.T1"])])
        self.assertNotIn("truncated", out)

    def test_corrupt_lines_are_skipped(self):
        # Fails when a corrupt line ends the read or throws.
        log = ["{not json", row("r", "walks"), "{\"half\":", "[1,2]", row("r", "worlds")]
        code, out = results(log)
        self.assertEqual(0, code, out)
        self.assertIn("part walks", out)
        self.assertIn("part worlds", out)

    def test_exit_two_when_no_run_matches_and_zero_when_green(self):
        # Fails when an empty log or an unmatched root exits 0 or 1.
        code, out = results([])
        self.assertEqual(2, code, out)
        self.assertIn("No run at", out)
        code, out = results([row("x", "walks", root="/elsewhere")])
        self.assertEqual(2, code, out)
        code, out = results([row("g", "walks")])
        self.assertEqual(0, code, out)
        self.assertNotIn("Failing:", out)


if __name__ == "__main__":
    unittest.main()
