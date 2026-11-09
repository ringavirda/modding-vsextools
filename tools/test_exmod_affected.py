"""Tests for `exmod test -Affected` in exmod/src.ps1: which test classes a change selects (Get-ExmodAffectedPlan), the
base it diffs against and the run it hands to dotnet test. Each case builds a git checkout with a test project, a base
commit on `dev` and a change on top. Run through pwsh; skipped when none is found."""

import json
import os
import re
import stat
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import PWSH, exmod_env, exmod_script, touch  # noqa: E402

CSPROJ = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n"

# Prints what a test run was handed, one JSON argv line per call in $TEST_ARGV, and passes.
DOTNET = """#!/usr/bin/env python3
import json, os, sys
a = sys.argv[1:]
if a[:1] != ["test"]:
    sys.exit(0)
open(os.environ["TEST_ARGV"], "a").write(json.dumps(a) + "\\n")
print("Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1")
"""


def test_class(name, body="[Fact] public void T() { }"):
    return f"namespace A.Tests;\n\npublic class {name}\n{{\n  {body}\n}}\n"


def git(repo, *args):
    done = subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", *args],
                          cwd=repo, capture_output=True, text=True)
    assert done.returncode == 0, done.stderr
    return done.stdout.strip()


class Repo:
    """A checkout in a temporary folder: files written, committed in steps, `exmod test <args>` run in it."""

    def __init__(self, manifest=None, heavy=None):
        self.dir = tempfile.TemporaryDirectory()
        self.root = os.path.join(self.dir.name, "repo")
        self.manifest = {"series": ["1.22"], "tests": ["tests/A.Tests"], **(manifest or {})}
        os.makedirs(self.root)
        git(self.root, "init", "-q", "-b", "dev")
        self.write("exmod.json", json.dumps(self.manifest))
        self.write("tests/A.Tests/A.Tests.csproj", CSPROJ)
        self.heavy = os.path.join(self.dir.name, "heavy-tests.txt")
        touch(self.heavy, "".join(f"{h} # 10\n" for h in (heavy or [])))
        self.stub = os.path.join(self.dir.name, "dotnet")
        touch(self.stub, DOTNET)
        os.chmod(self.stub, os.stat(self.stub).st_mode | stat.S_IEXEC)
        self.argv = os.path.join(self.dir.name, "argv.log")

    def close(self):
        self.dir.cleanup()

    def write(self, rel, text):
        touch(os.path.join(self.root, rel), text)

    def commit(self, message="step"):
        git(self.root, "add", "-A")
        git(self.root, "commit", "-q", "-m", message)
        return git(self.root, "rev-parse", "HEAD")

    def run(self, args, *, expect_ok=True):
        """(stdout, test runs): the output of Invoke-Test with `args` and the dotnet test argv lines it made."""
        assert PWSH
        body = ("function Resolve-DotnetHost { $env:TEST_STUB }\n"
                f"try {{ Invoke-Test @('1.22', {args}) }} catch {{ Write-Host \"threw: $_\" }}\n")
        env = exmod_env(os.path.join(self.dir.name, "local"), TEST_REPO=self.root, TEST_STUB=self.stub,
                        TEST_ARGV=self.argv, EXMOD_HEAVY_TESTS=self.heavy)
        done = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", exmod_script(body)],
                              env=env, capture_output=True, text=True)
        runs = []
        if os.path.exists(self.argv):
            with open(self.argv) as f:
                runs = [json.loads(l) for l in f if l.strip()]
            os.remove(self.argv)
        return done.stdout.replace("\r", ""), runs


def classes_of(out):
    """The class names the plan printed, in order."""
    return re.findall(r"^  (\w+)\s+\S", out, re.M)


@unittest.skipUnless(PWSH, "pwsh not available")
class AffectedTests(unittest.TestCase):
    def repo(self, **kw):
        r = Repo(**kw)
        self.addCleanup(r.close)
        return r

    def test_a_changed_test_file_names_its_classes_with_tests_and_not_its_helpers(self):
        # Fails when rule 1 is dropped (the changed file's classes are not added) or helpers are taken for test classes.
        r = self.repo()
        r.write("tests/A.Tests/One.cs", test_class("OneTests"))
        r.write("tests/A.Tests/Other.cs", test_class("OtherTests"))
        r.commit()
        r.write("tests/A.Tests/One.cs", test_class("OneTests") + "\npublic class OneHelper\n{\n  public int X;\n}\n")
        r.commit()
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD~1'")
        self.assertEqual(["OneTests"], classes_of(out), out)
        self.assertIn("changed test file tests/A.Tests/One.cs", out)
        self.assertIn("1 class(es).", out)

    def test_a_changed_source_type_selects_the_classes_whose_file_names_it_as_a_whole_word(self):
        # Fails when rule 2 is dropped, or when the name match is a substring match (FooBarTests would be in).
        r = self.repo()
        r.write("src/Foo.cs", "namespace A;\n\npublic class Foo\n{\n}\n")
        r.write("tests/A.Tests/FooTests.cs", test_class("FooTests", "[Fact] public void T() { var f = new Foo(); }"))
        r.write("tests/A.Tests/FooBarTests.cs", test_class("FooBarTests", "[Fact] public void T() { var f = new FooBar(); }"))
        r.write("tests/A.Tests/NoneTests.cs", test_class("NoneTests"))
        r.commit()
        r.write("src/Foo.cs", "namespace A;\n\npublic class Foo\n{\n  public int X;\n}\n")
        r.commit()
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD~1'")
        self.assertEqual(["FooTests"], classes_of(out), out)
        self.assertIn("names Foo (src/Foo.cs)", out)

    def test_a_deleted_source_file_selects_by_the_types_it_declared_at_the_base(self):
        # Fails when a deleted file's types are not read from the base.
        r = self.repo()
        r.write("src/Gone.cs", "namespace A;\n\npublic struct Gone\n{\n}\n")
        r.write("tests/A.Tests/GoneTests.cs", test_class("GoneTests", "[Fact] public void T() { var g = default(Gone); }"))
        r.commit()
        os.remove(os.path.join(r.root, "src/Gone.cs"))
        r.commit()
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD~1'")
        self.assertEqual(["GoneTests"], classes_of(out), out)

    def test_affected_always_joins_whatever_changed_and_a_name_no_test_declares_is_an_error(self):
        # Fails when rule 3 is dropped, or a stale name passes silently.
        r = self.repo(manifest={"affectedAlways": ["GuardTests"]})
        r.write("tests/A.Tests/Guard.cs", test_class("GuardTests"))
        r.write("tests/A.Tests/One.cs", test_class("OneTests"))
        r.commit()
        r.write("tests/A.Tests/One.cs", test_class("OneTests", "[Fact] public void U() { }"))
        r.commit()
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD~1'")
        self.assertEqual(["GuardTests", "OneTests"], classes_of(out), out)
        self.assertRegex(out, r"GuardTests\s+affectedAlways")
        stale = self.repo(manifest={"affectedAlways": ["MissingTests"]})
        stale.write("tests/A.Tests/One.cs", test_class("OneTests"))
        stale.commit()
        out, _ = stale.run("'-Affected', '-List', '-Base', 'HEAD'")
        self.assertIn("affectedAlways names MissingTests, which no test file declares", out)

    def test_a_heavy_class_is_not_let_in_by_affected_always_or_an_asset_but_is_by_a_changed_file_or_type(self):
        # Fails when a heavy class listed in affectedAlways, or one naming a changed asset, runs: rule 4 let rule 3 in.
        r = self.repo(manifest={"affectedAlways": ["HeavyGuards"]}, heavy=["HeavyGuards", "HeavyType", "HeavyEdit"])
        r.write("src/Thing.cs", "namespace A;\n\npublic class Thing\n{\n}\n")
        r.write("assets/data/widget.json", "{}")
        r.write("tests/A.Tests/HeavyGuards.cs", test_class("HeavyGuards", "[Fact] public void T() { var s = \"widget\"; }"))
        r.write("tests/A.Tests/HeavyType.cs", test_class("HeavyType", "[Fact] public void T() { var t = new Thing(); }"))
        r.write("tests/A.Tests/HeavyEdit.cs", test_class("HeavyEdit"))
        r.write("tests/A.Tests/Light.cs", test_class("LightTests"))
        r.commit()
        r.write("src/Thing.cs", "namespace A;\n\npublic class Thing\n{\n  public int X;\n}\n")
        r.write("assets/data/widget.json", "{ \"a\": 1 }")
        r.write("tests/A.Tests/HeavyEdit.cs", test_class("HeavyEdit", "[Fact] public void U() { }"))
        r.commit()
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD~1'")
        self.assertEqual(["HeavyEdit", "HeavyType"], classes_of(out), out)
        self.assertIn("Left out, heavy and not directly affected: HeavyGuards", out)

    def test_a_changed_json_names_the_test_files_that_name_its_base_name_and_a_two_letter_name_names_none(self):
        # Fails when an asset maps to no test file, or a locale file en.json matches every test that says "en".
        r = self.repo()
        r.write("assets/lang/en.json", "{}")
        r.write("assets/recipes/shingle.json", "{}")
        r.write("tests/A.Tests/Recipe.cs", test_class("RecipeTests", "[Fact] public void T() { var f = \"shingle\"; }"))
        r.write("tests/A.Tests/Lang.cs", test_class("LangTests", "[Fact] public void T() { var f = \"en\"; }"))
        r.commit()
        r.write("assets/lang/en.json", "{ \"a\": 1 }")
        r.write("assets/recipes/shingle.json", "{ \"a\": 1 }")
        r.commit()
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD~1'")
        self.assertEqual(["RecipeTests"], classes_of(out), out)
        self.assertIn("names shingle (assets/recipes/shingle.json)", out)

    def test_the_base_decides_what_changed_and_dev_is_the_default(self):
        # Fails when -Base is ignored (every run diffs against dev).
        r = self.repo()
        r.write("tests/A.Tests/One.cs", test_class("OneTests"))
        r.write("tests/A.Tests/Two.cs", test_class("TwoTests"))
        r.commit()
        r.write("tests/A.Tests/One.cs", test_class("OneTests", "[Fact] public void U() { }"))
        first = r.commit()
        r.write("tests/A.Tests/Two.cs", test_class("TwoTests", "[Fact] public void U() { }"))
        r.commit()
        out, _ = r.run(f"'-Affected', '-List', '-Base', '{first}'")
        self.assertEqual(["TwoTests"], classes_of(out), out)
        self.assertIn("base " + first, out)
        git(r.root, "checkout", "-q", "-b", "work", "HEAD~2")
        r.write("tests/A.Tests/One.cs", test_class("OneTests", "[Fact] public void V() { }"))
        r.commit()
        out, _ = r.run("'-Affected', '-List'")
        self.assertEqual(["OneTests"], classes_of(out), out)
        self.assertIn("base dev", out)

    def test_a_list_run_builds_and_tests_nothing_and_a_run_hands_dotnet_the_filter(self):
        # Fails when -List runs the tests, or the run's --filter is not the plan's terms.
        r = self.repo()
        r.write("tests/A.Tests/One.cs", test_class("OneTests"))
        r.write("tests/A.Tests/Two.cs", test_class("TwoTests"))
        r.commit()
        r.write("tests/A.Tests/One.cs", test_class("OneTests", "[Fact] public void U() { }"))
        r.write("tests/A.Tests/Two.cs", test_class("TwoTests", "[Fact] public void U() { }"))
        r.commit()
        out, runs = r.run("'-Affected', '-List', '-Base', 'HEAD~1'")
        self.assertEqual([], runs, out)
        out, runs = r.run("'-Affected', '-Base', 'HEAD~1', '-NoBuild'")
        self.assertIn("Not built", out)
        out, runs = r.run("'-Affected', '-Base', 'HEAD~1'")
        self.assertEqual(1, len(runs), out)
        self.assertEqual("FullyQualifiedName~.OneTests.|FullyQualifiedName~.TwoTests.",
                         runs[0][runs[0].index("--filter") + 1], runs)

    def test_a_change_that_selects_nothing_runs_no_test_and_a_bad_combination_or_base_is_refused(self):
        # Fails when an empty plan runs the whole suite, or -Affected combines with -Filter, or -List stands alone.
        r = self.repo()
        r.write("tests/A.Tests/One.cs", test_class("OneTests"))
        r.write("README.md", "a")
        r.commit()
        r.write("README.md", "b")
        r.commit()
        out, runs = r.run("'-Affected', '-Base', 'HEAD~1'")
        self.assertEqual([], runs, out)
        self.assertIn("Nothing affected", out)
        out, _ = r.run("'-Affected', '-Filter', 'X'")
        self.assertIn("-Affected builds its own filter", out)
        out, _ = r.run("'-List'")
        self.assertIn("-Base and -List need -Affected", out)
        out, _ = r.run("'-Affected', '-Base', 'nosuchref'")
        self.assertIn("git diff nosuchref...HEAD failed", out)


if __name__ == "__main__":
    unittest.main()
