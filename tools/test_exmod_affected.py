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

# Appends each test call's argv as a JSON line to $TEST_ARGV and passes.
DOTNET = """#!/usr/bin/env python3
import json, os, sys
a = sys.argv[1:]
if a[:1] != ["test"]:
    sys.exit(0)
open(os.environ["TEST_ARGV"], "a").write(json.dumps(a) + "\\n")
print("Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1")
"""


def class_source(name, body="[Fact] public void T() { }"):
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
        self.env = {}

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
                        TEST_ARGV=self.argv, EXMOD_HEAVY_TESTS=self.heavy, **self.env)
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
        # Fails when the changed file's classes are not added or helpers are taken for test classes.
        r = self.repo()
        r.write("tests/A.Tests/One.cs", class_source("OneTests"))
        r.write("tests/A.Tests/Other.cs", class_source("OtherTests"))
        r.commit()
        r.write("tests/A.Tests/One.cs", class_source("OneTests") + "\npublic class OneHelper\n{\n  public int X;\n}\n")
        r.commit()
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD~1'")
        self.assertEqual(["OneTests"], classes_of(out), out)
        self.assertIn("changed test file tests/A.Tests/One.cs", out)
        self.assertIn("1 class(es).", out)

    def test_a_changed_source_type_selects_the_classes_whose_file_names_it_as_a_whole_word(self):
        # Fails when types of changed source files select no test class, or when the name match is a substring match (FooBarTests would be in).
        r = self.repo()
        r.write("src/Foo.cs", "namespace A;\n\npublic class Foo\n{\n}\n")
        r.write("tests/A.Tests/FooTests.cs", class_source("FooTests", "[Fact] public void T() { var f = new Foo(); }"))
        r.write("tests/A.Tests/FooBarTests.cs", class_source("FooBarTests", "[Fact] public void T() { var f = new FooBar(); }"))
        r.write("tests/A.Tests/NoneTests.cs", class_source("NoneTests"))
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
        r.write("tests/A.Tests/GoneTests.cs", class_source("GoneTests", "[Fact] public void T() { var g = default(Gone); }"))
        r.commit()
        os.remove(os.path.join(r.root, "src/Gone.cs"))
        r.commit()
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD~1'")
        self.assertEqual(["GoneTests"], classes_of(out), out)

    def test_affected_always_joins_whatever_changed_and_a_name_no_test_declares_is_an_error(self):
        # Fails when affectedAlways adds nothing, or a stale name passes silently.
        r = self.repo(manifest={"affectedAlways": ["GuardTests"]})
        r.write("tests/A.Tests/Guard.cs", class_source("GuardTests"))
        r.write("tests/A.Tests/One.cs", class_source("OneTests"))
        r.commit()
        r.write("tests/A.Tests/One.cs", class_source("OneTests", "[Fact] public void U() { }"))
        r.commit()
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD~1'")
        self.assertEqual(["GuardTests", "OneTests"], classes_of(out), out)
        self.assertRegex(out, r"GuardTests\s+affectedAlways")
        stale = self.repo(manifest={"affectedAlways": ["MissingTests"]})
        stale.write("tests/A.Tests/One.cs", class_source("OneTests"))
        stale.commit()
        out, _ = stale.run("'-Affected', '-List', '-Base', 'HEAD'")
        self.assertIn("affectedAlways names MissingTests, which no test file declares", out)

    def test_a_heavy_class_is_not_let_in_by_affected_always_or_an_asset_but_is_by_a_changed_file_or_type(self):
        # Fails when a heavy class listed in affectedAlways, or one naming a changed asset, runs.
        r = self.repo(manifest={"affectedAlways": ["HeavyGuards"]}, heavy=["HeavyGuards", "HeavyType", "HeavyEdit"])
        r.write("src/Thing.cs", "namespace A;\n\npublic class Thing\n{\n}\n")
        r.write("assets/data/widget.json", "{}")
        r.write("tests/A.Tests/HeavyGuards.cs", class_source("HeavyGuards", "[Fact] public void T() { var s = \"widget\"; }"))
        r.write("tests/A.Tests/HeavyType.cs", class_source("HeavyType", "[Fact] public void T() { var t = new Thing(); }"))
        r.write("tests/A.Tests/HeavyEdit.cs", class_source("HeavyEdit"))
        r.write("tests/A.Tests/Light.cs", class_source("LightTests"))
        r.commit()
        r.write("src/Thing.cs", "namespace A;\n\npublic class Thing\n{\n  public int X;\n}\n")
        r.write("assets/data/widget.json", "{ \"a\": 1 }")
        r.write("tests/A.Tests/HeavyEdit.cs", class_source("HeavyEdit", "[Fact] public void U() { }"))
        r.commit()
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD~1'")
        self.assertEqual(["HeavyEdit", "HeavyType"], classes_of(out), out)
        self.assertIn("Left out, heavy and not directly affected: HeavyGuards", out)

    def test_a_changed_json_names_the_test_files_that_name_its_base_name_and_a_two_letter_name_names_none(self):
        # Fails when an asset maps to no test file, or a locale file en.json matches every test that says "en".
        r = self.repo()
        r.write("assets/lang/en.json", "{}")
        r.write("assets/recipes/shingle.json", "{}")
        r.write("tests/A.Tests/Recipe.cs", class_source("RecipeTests", "[Fact] public void T() { var f = \"shingle\"; }"))
        r.write("tests/A.Tests/Lang.cs", class_source("LangTests", "[Fact] public void T() { var f = \"en\"; }"))
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
        r.write("tests/A.Tests/One.cs", class_source("OneTests"))
        r.write("tests/A.Tests/Two.cs", class_source("TwoTests"))
        r.commit()
        r.write("tests/A.Tests/One.cs", class_source("OneTests", "[Fact] public void U() { }"))
        first = r.commit()
        r.write("tests/A.Tests/Two.cs", class_source("TwoTests", "[Fact] public void U() { }"))
        r.commit()
        out, _ = r.run(f"'-Affected', '-List', '-Base', '{first}'")
        self.assertEqual(["TwoTests"], classes_of(out), out)
        self.assertIn("base " + first, out)
        git(r.root, "checkout", "-q", "-b", "work", "HEAD~2")
        r.write("tests/A.Tests/One.cs", class_source("OneTests", "[Fact] public void V() { }"))
        r.commit()
        out, _ = r.run("'-Affected', '-List'")
        self.assertEqual(["OneTests"], classes_of(out), out)
        self.assertIn("base dev", out)

    def test_a_list_run_builds_and_tests_nothing_and_a_run_hands_dotnet_the_filter(self):
        # Fails when -List runs the tests, or the run's --filter is not the plan's terms.
        r = self.repo()
        r.write("tests/A.Tests/One.cs", class_source("OneTests"))
        r.write("tests/A.Tests/Two.cs", class_source("TwoTests"))
        r.commit()
        r.write("tests/A.Tests/One.cs", class_source("OneTests", "[Fact] public void U() { }"))
        r.write("tests/A.Tests/Two.cs", class_source("TwoTests", "[Fact] public void U() { }"))
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
        r.write("tests/A.Tests/One.cs", class_source("OneTests"))
        r.write("README.md", "a")
        r.commit()
        r.write("README.md", "b")
        r.commit()
        out, runs = r.run("'-Affected', '-Base', 'HEAD~1'")
        self.assertEqual([], runs, out)
        self.assertIn("Nothing affected", out)
        out, _ = r.run("'-Affected', '-Filter', 'X'")
        self.assertIn("-Affected builds its own filter", out)
        out, runs = r.run("'-List'")
        self.assertEqual([], runs, out)
        out, _ = r.run("'-Base', 'HEAD'")
        self.assertIn("-Base needs -Affected", out)
        out, _ = r.run("'-Affected', '-Base', 'nosuchref'")
        self.assertIn("git diff nosuchref...HEAD failed", out)


SETUP_MANIFEST = {"affectedSetups": [{"class": "SetupTests", "files": "tests/A.Tests/live/setups",
                                      "recordings": "tests/A.Tests/Recordings"}]}


def setup_file(setup, extra=""):
    return json.dumps({"setup": setup, "steps": []}) + extra


def filter_of(runs):
    return runs[0][runs[0].index("--filter") + 1]


@unittest.skipUnless(PWSH, "pwsh not available")
class AffectedSightTests(unittest.TestCase):
    def repo(self, **kw):
        r = Repo(**kw)
        self.addCleanup(r.close)
        return r

    def setups_repo(self):
        r = self.repo(manifest=SETUP_MANIFEST, heavy=["SetupTests"])
        r.write("tests/A.Tests/SetupTests.cs", class_source("SetupTests"))
        r.write("tests/A.Tests/live/setups/cupola.json", setup_file("cupola"))
        r.write("tests/A.Tests/live/setups/cupola-cases.json", setup_file("cupola-cases"))
        r.write("tests/A.Tests/live/setups/lathe.json", setup_file("lathe"))
        r.write("tests/A.Tests/live/setups/play.json", setup_file("play", ' "after": {"iiex/setups/cupola": 1}'))
        r.write("tests/A.Tests/Recordings/1.22/lathe.json", "{}")
        r.commit()
        return r

    def test_a_changed_setup_file_selects_exactly_its_rows_of_a_heavy_class_and_those_that_follow_it(self):
        # Fails when a row term is a bare substring (cupola-cases comes in), the heavy class hides the rows, or a file that names the changed setup after it is left out.
        r = self.setups_repo()
        r.write("tests/A.Tests/live/setups/cupola.json", setup_file("cupola", " "))
        r.commit()
        out, runs = r.run("'-Affected', '-Base', 'HEAD~1'")
        self.assertEqual(["SetupTests"], classes_of(out), out)
        self.assertNotIn("Left out", out)
        self.assertIn("rows of cupola: changed setup file tests/A.Tests/live/setups/cupola.json", out)
        self.assertIn("rows of play: setup file play.json follows changed", out)
        self.assertEqual("DisplayName~row: cupola\\)|DisplayName~row: cupola/|DisplayName~row: cupola@|"
                         "DisplayName~row: play\\)|DisplayName~row: play/|DisplayName~row: play@",
                         filter_of(runs), runs)

    def test_a_changed_recording_selects_its_setups_rows_and_a_changed_class_takes_them_whole(self):
        # Fails when a recording selects nothing, or rows are listed beside the whole class that already holds them.
        r = self.setups_repo()
        r.write("tests/A.Tests/Recordings/1.22/lathe.json", "{ }")
        r.commit()
        out, runs = r.run("'-Affected', '-Base', 'HEAD~1'")
        self.assertIn("changed recording tests/A.Tests/Recordings/1.22/lathe.json", out)
        self.assertEqual("DisplayName~row: lathe\\)|DisplayName~row: lathe/|DisplayName~row: lathe@", filter_of(runs), runs)
        r.write("tests/A.Tests/live/setups/lathe.json", setup_file("lathe", " "))
        r.write("tests/A.Tests/SetupTests.cs", class_source("SetupTests", "[Fact] public void U() { }"))
        r.commit()
        out, runs = r.run("'-Affected', '-Base', 'HEAD~1'")
        self.assertEqual("FullyQualifiedName~.SetupTests.", filter_of(runs), runs)

    def member_repo(self):
        r = self.repo(manifest={"affectedMembers": ["CfgConfig"]})
        r.write("src/CfgConfig.cs", "namespace A;\n\npublic static class CfgConfig\n{\n  public const int Alpha = 1;\n  /// <summary>Beta.</summary>\n  [Range(0, 9)]\n  public const int Beta = 2;\n"
                "  public static int Gamma()\n  {\n    var x = 3;\n    return x;\n  }\n}\n")
        r.write("tests/A.Tests/Alpha.cs", class_source("AlphaTests", "[Fact] public void T() { var v = CfgValues.Alpha; }"))
        r.write("tests/A.Tests/Beta.cs", class_source("BetaTests", "[Fact] public void T() { var v = CfgValues.Beta; }"))
        r.write("tests/A.Tests/Gamma.cs", class_source("GammaTests", "[Fact] public void T() { var v = CfgValues.Gamma(); }"))
        r.write("tests/A.Tests/Other.cs", class_source("OtherTests", "[Fact] public void T() { var v = CfgValues.Alpha + CfgValues.Beta; }"))
        r.write("tests/A.Tests/Unrelated.cs", class_source("UnrelatedTests", "[Fact] public void T() { var v = Alpha; }"))
        r.commit()
        return r

    def test_a_changed_value_selects_the_classes_naming_that_member_and_a_changed_body_its_member(self):
        # Fails when the whole type is taken (every class naming CfgValues runs), when a comment or attribute line is not traced down to its member, when a body line is not traced up to its member, or when the member name alone (no CfgValues) selects.
        r = self.member_repo()
        text = open(os.path.join(r.root, "src/CfgConfig.cs")).read()
        r.write("src/CfgConfig.cs", text.replace("Beta = 2", "Beta = 5"))
        r.commit()
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD~1'")
        self.assertEqual(["BetaTests", "OtherTests"], classes_of(out), out)
        self.assertIn("names CfgValues.Beta (src/CfgConfig.cs)", out)
        r.write("src/CfgConfig.cs", text.replace("Beta = 2", "Beta = 5").replace("var x = 3;", "var x = 4;"))
        r.commit()
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD~2'")
        self.assertEqual(["BetaTests", "GammaTests", "OtherTests"], classes_of(out), out)
        text = text.replace("Beta = 2", "Beta = 5").replace("var x = 3;", "var x = 4;")
        r.write("src/CfgConfig.cs", text.replace("/// <summary>Beta.", "/// <summary>The beta.").replace("Range(0, 9)", "Range(0, 8)"))
        r.commit()
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD~1'")
        self.assertEqual(["BetaTests", "OtherTests"], classes_of(out), out)

    def test_a_change_outside_every_member_of_a_member_type_takes_every_class_naming_the_type(self):
        # Fails when a changed line with no member above it is skipped, so the member beside it alone selects.
        r = self.member_repo()
        text = open(os.path.join(r.root, "src/CfgConfig.cs")).read()
        r.write("src/CfgConfig.cs", "using System;\n" + text.replace("Beta = 2", "Beta = 5"))
        r.commit()
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD~1'")
        self.assertEqual(["AlphaTests", "BetaTests", "GammaTests", "OtherTests"], classes_of(out), out)

    def exlib_repo(self, repo):
        lib = os.path.join(repo.dir.name, "exlib")
        os.makedirs(lib)
        git(lib, "init", "-q", "-b", "dev")
        touch(os.path.join(lib, "src/Widget.cs"), "namespace L;\n\npublic class Widget\n{\n}\n")
        touch(os.path.join(lib, "src/Idle.cs"), "namespace L;\n\npublic class Idle\n{\n}\n")
        git(lib, "add", "-A")
        git(lib, "commit", "-q", "-m", "base")
        git(lib, "branch", "int")
        repo.env["ExlibRoot"] = lib
        return lib

    def exlib_consumer(self):
        r = self.repo(manifest={"depends": {"exlib": {}}})
        r.write("tests/A.Tests/Widget.cs", class_source("WidgetTests", "[Fact] public void T() { var w = new Widget(); }"))
        r.write("tests/A.Tests/Idle.cs", class_source("IdleTests", "[Fact] public void T() { var w = new Idle(); }"))
        r.commit()
        return r

    def test_an_exlib_lane_selects_the_consumers_classes_by_type_and_prints_the_checkout_and_base(self):
        # Fails when exlib's changed types are not read, or the base is not the merge-base with int (the int commits would show too).
        r = self.exlib_consumer()
        lib = self.exlib_repo(r)
        git(lib, "checkout", "-q", "int")
        touch(os.path.join(lib, "src/Idle.cs"), "namespace L;\n\npublic class Idle\n{\n  public int Y;\n}\n")
        git(lib, "add", "-A")
        git(lib, "commit", "-q", "-m", "int moves")
        git(lib, "checkout", "-q", "-b", "lane")
        touch(os.path.join(lib, "src/Widget.cs"), "namespace L;\n\npublic class Widget\n{\n  public int X;\n}\n")
        git(lib, "add", "-A")
        git(lib, "commit", "-q", "-m", "lane")
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD'")
        self.assertEqual(["WidgetTests"], classes_of(out), out)
        self.assertIn("names Widget (exlib src/Widget.cs)", out)
        self.assertIn(f"exlib: read {lib}, base merge-base HEAD int (", out)
        self.assertIn("1 changed file(s).", out)

    def test_an_exlib_checkout_at_int_diffs_from_dev_and_a_missing_checkout_is_said(self):
        # Fails when a checkout at int takes int as its base (nothing changed), or the missing checkout passes silently.
        r = self.exlib_consumer()
        lib = self.exlib_repo(r)
        git(lib, "checkout", "-q", "int")
        touch(os.path.join(lib, "src/Idle.cs"), "namespace L;\n\npublic class Idle\n{\n  public int Y;\n}\n")
        git(lib, "add", "-A")
        git(lib, "commit", "-q", "-m", "int moves")
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD'")
        self.assertEqual(["IdleTests"], classes_of(out), out)
        self.assertIn("base merge-base HEAD dev (", out)
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD', '-ExlibBase', 'int'")
        self.assertEqual([], classes_of(out), out)
        r.env["ExlibRoot"] = os.path.join(r.dir.name, "nowhere")
        out, _ = r.run("'-Affected', '-List', '-Base', 'HEAD'")
        self.assertIn("exlib: no exlib checkout found", out)


if __name__ == "__main__":
    unittest.main()
