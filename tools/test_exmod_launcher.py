"""Tests for wrappers/exmod.sh: which extools checkout it hands to pwsh. Each case builds a temporary main repository
with an extools/exmod.ps1 sibling and a git worktree elsewhere that holds the launcher, with a stub pwsh that prints
its argv."""

import os
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import ROOT, touch  # noqa: E402

PWSH_STUB = "#!/usr/bin/env bash\nprintf '%s\\n' \"$@\"\n"


def git(repo, *args):
    done = subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", *args],
                          cwd=repo, capture_output=True, text=True)
    assert done.returncode == 0, done.stderr
    return done.stdout.strip()


@unittest.skipUnless(shutil.which("bash") and shutil.which("git"), "needs bash and git")
class WorktreeRung(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        base = os.path.realpath(self.dir.name)
        self.main = os.path.join(base, "ws", "exmods")
        self.tools = os.path.join(base, "ws", "extools")
        os.makedirs(self.main)
        touch(os.path.join(self.tools, "exmod.ps1"))
        shutil.copy(os.path.join(ROOT, "wrappers", "exmod.sh"), os.path.join(self.main, "exmod.sh"))
        touch(os.path.join(self.main, "exmod.json"), "{}")
        git(self.main, "init", "-q", "-b", "dev")
        os.makedirs(os.path.join(self.main, "scripts"))
        shutil.move(os.path.join(self.main, "exmod.sh"), os.path.join(self.main, "scripts", "exmod.sh"))
        git(self.main, "add", "-A")
        git(self.main, "commit", "-q", "-m", "base")
        self.lane = os.path.join(base, "elsewhere", "sessions", "lane")
        git(self.main, "worktree", "add", "-q", self.lane, "-b", "lane")
        self.pwsh = os.path.join(base, "pwsh")
        touch(self.pwsh, PWSH_STUB)
        os.chmod(self.pwsh, os.stat(self.pwsh).st_mode | stat.S_IEXEC)

    def launch(self, repo):
        env = {k: v for k, v in os.environ.items() if k != "EXTOOLS_HOME"}
        return subprocess.run(["bash", os.path.join(repo, "scripts", "exmod.sh"), "arg"], env={**env, "PWSH": self.pwsh},
                              capture_output=True, text=True)

    def test_a_worktree_finds_the_extools_beside_its_main_checkout(self):
        """Fails when the launcher has no rung for the main checkout's sibling extools."""
        done = self.launch(self.lane)
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIn(os.path.join(self.tools, "exmod.ps1"), done.stdout.split("\n"))
        self.assertIn(self.lane, done.stdout.split("\n"))

    def test_outside_a_repository_the_rung_is_skipped_silently(self):
        """Fails when the rung errors or prints when git finds no repository."""
        loose = os.path.join(os.path.realpath(self.dir.name), "loose")
        touch(os.path.join(loose, "scripts", "exmod.sh"), open(os.path.join(self.main, "scripts", "exmod.sh")).read())
        done = self.launch(loose)
        self.assertEqual(done.returncode, 1)
        self.assertIn("no extools checkout found", done.stderr)
        self.assertNotIn("fatal", done.stderr)


if __name__ == "__main__":
    unittest.main()
