"""Tests for the shapes tool's published copy in exmod/shapes.ps1 (Publish-ExlibShapes,
Remove-OldShapesPublishes, Test-ShapesPublished, Test-ExlibShapesStale, Get-ShapesBuildStamp,
Invoke-ShapeTool and the EXMOD_SHAPES_NO_BUILD path of Invoke-ExlibShapes), run through pwsh on a temporary publish root and a fake build folder with
`dotnet`, Build-ExlibShapes and the game lookup stubbed; skipped when no pwsh is found. Each test
names the mutation it fails under."""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import PWSH, exmod_env, exmod_script, touch  # noqa: E402

HEAD = r"""
$root = Join-Path $env:TEST_TMP 'publish'
$build = Join-Path $env:TEST_TMP 'build'
function Get-ShapesPublishRoot { $root }
function Resolve-GameInstall { 'game' }
function Build-ExlibShapes { throw 'Build-ExlibShapes reached' }
function Pub([string]$stamp) { Start-Sleep -Milliseconds 5; Publish-ExlibShapes $root $build $stamp }
"""


@unittest.skipUnless(PWSH, "pwsh not found")
class ShapesPublishTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.repo = os.path.join(self.tmp, "repo")
        touch(os.path.join(self.repo, "exmod.json"), "{}")
        touch(os.path.join(self.tmp, "build", "exlib-shapes.dll"), "tool")
        touch(os.path.join(self.tmp, "build", "runtimes", "x.so"), "native")
        self.root = os.path.join(self.tmp, "publish")

    def run_body(self, body, expect=0):
        script = exmod_script(HEAD + body)
        env = exmod_env(os.path.join(self.tmp, "local"), TEST_REPO=self.repo, TEST_TMP=self.tmp)
        out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", script],
                             env=env, capture_output=True, text=True)
        self.assertEqual(expect, out.returncode, out.stdout + out.stderr)
        return out.stdout.splitlines()

    def read(self, path):
        with open(path) as f:
            return f.read()

    def publishes(self):
        return sorted(d for d in os.listdir(self.root) if d.startswith("publish-"))

    def age(self, name, minutes):
        t = os.path.getmtime(os.path.join(self.root, name)) - minutes * 60
        os.utime(os.path.join(self.root, name), (t, t))

    def test_publish_copies_the_build_and_points_current_at_it(self):
        # Fails if current is not switched to the new folder, or the copy leaves out the nested files.
        self.run_body("Pub 's1' | Out-Null")
        link = os.path.join(self.root, "current")
        self.assertTrue(os.path.islink(link))
        self.assertEqual(self.publishes()[0], os.readlink(link))
        self.assertEqual("native", self.read(os.path.join(link, "runtimes", "x.so")))
        self.assertEqual("s1", self.read(os.path.join(link, "source-stamp")))

    def test_second_publish_leaves_the_first_intact_and_current_on_the_second(self):
        # Fails if a publish writes into or deletes the previous folder, or current stays on the first.
        self.run_body("Pub 's1' | Out-Null")
        first = self.publishes()[0]
        touch(os.path.join(self.root, first, "probe"), "mine")
        touch(os.path.join(self.tmp, "build", "exlib-shapes.dll"), "tool2")
        self.run_body("Pub 's2' | Out-Null")
        names = self.publishes()
        self.assertEqual(2, len(names))
        self.assertEqual("tool", self.read(os.path.join(self.root, first, "exlib-shapes.dll")))
        self.assertEqual("mine", self.read(os.path.join(self.root, first, "probe")))
        self.assertEqual(names[1], os.readlink(os.path.join(self.root, "current")))
        self.assertEqual("tool2", self.read(os.path.join(self.root, "current", "exlib-shapes.dll")))

    def test_prune_keeps_current_the_previous_and_anything_under_ten_minutes(self):
        # Fails if prune drops the previous publish, ignores the age, or removes the current one.
        for i in range(4):
            self.run_body("Pub 's%d' | Out-Null" % i)
        names = self.publishes()
        for n in names:
            self.age(n, 30)
        os.utime(os.path.join(self.root, names[1]))
        self.run_body("Remove-OldShapesPublishes $root")
        self.assertEqual(names[1:], self.publishes())
        for n in names[1:]:
            self.age(n, 30)
        self.run_body("Remove-OldShapesPublishes $root")
        self.assertEqual(names[2:], self.publishes())
        self.assertEqual(names[3], os.readlink(os.path.join(self.root, "current")))

    def test_test_published_matches_only_the_stamp_current_was_made_from(self):
        # Fails if the stamp is not compared, or an empty root counts as published.
        got = self.run_body("""
$none = Test-ShapesPublished $root 's1'
Pub 's1' | Out-Null
ConvertTo-Json -Compress @($none, (Test-ShapesPublished $root 's1'), (Test-ShapesPublished $root 's2'))""")
        self.assertEqual([False, True, False], json.loads(got[-1]))

    def test_render_with_no_build_runs_the_published_folder_and_never_builds(self):
        # Fails if the render runs the build output, follows the link instead of the folder it
        # resolved at start, or calls Build-ExlibShapes.
        got = self.run_body("""
Pub 's1' | Out-Null
$global:seen = @()
function dotnet { $global:seen += "$($args[0])"; $global:LASTEXITCODE = 0 }
$env:EXMOD_SHAPES_NO_BUILD = '1'
Invoke-ExlibShapes 'render' @('a.json')
Pub 's2' | Out-Null
Invoke-ExlibShapes 'render' @('a.json')
ConvertTo-Json -Compress $global:seen""")
        seen = json.loads(got[-1])
        first, second = self.publishes()
        self.assertEqual([os.path.join(self.root, first, "exlib-shapes.dll"),
                          os.path.join(self.root, second, "exlib-shapes.dll")], seen)

    def test_render_with_no_build_and_nothing_published_names_shapes_build(self):
        # Fails if the missing publish falls through to a build or to a silent run.
        got = self.run_body("""
$env:EXMOD_SHAPES_NO_BUILD = '1'
try { Invoke-ExlibShapes 'render' @('a.json'); 'ran' } catch { $_.Exception.Message }""")
        self.assertIn("not published", got[-1])
        self.assertIn("exmod shapes-build", got[-1])

    def fake_tools_root(self):
        """A tools checkout with every input of the shapes build one hour old and a built exe dll now."""
        fake = os.path.join(self.tmp, "tools")
        dirs = ["ExlibShapes", "ExlibShapes.Core", "ExlibShapes.Checks", "ExlibShapes.Generators", "ExlibShapes.Ops"]
        files = [os.path.join("shapes", d, "x.cs") for d in dirs]
        files += [os.path.join("assets", "ExlibAssets", "x.cs"), os.path.join("shapes", "Directory.Build.targets")]
        files += ["Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp",
                  "Directory.Packages.props", "global.json", "exmod.json"]
        old = os.path.getmtime(self.tmp) - 3600
        for f in files:
            path = os.path.join(fake, f)
            touch(path, "x")
            os.utime(path, (old, old))
        dll = os.path.join(fake, "shapes", "ExlibShapes", "bin", "Debug", "net10.0", "exlib-shapes.dll")
        touch(dll, "tool")
        return fake, files

    def test_stale_sees_a_change_in_each_project_the_tool_is_built_from(self):
        # Fails if Core, Checks, Generators, Ops or shapes/Directory.Build.targets is left out of the
        # inputs, so a change confined to one of them would run the old build.
        fake, files = self.fake_tools_root()
        env_fake = "$ToolsRoot = $env:TEST_FAKE"
        for f in files:
            if f.endswith("ExlibShapes" + os.sep + "x.cs") or f.startswith("assets"):
                continue
            path = os.path.join(fake, f)
            old = os.path.getmtime(path)
            body = env_fake + "\nTest-ExlibShapesStale (Get-ExlibShapesDll) 'nogame'"
            script = exmod_script(HEAD + body)
            env = exmod_env(os.path.join(self.tmp, "local"), TEST_REPO=self.repo, TEST_TMP=self.tmp, TEST_FAKE=fake)
            def stale():
                out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", script],
                                     env=env, capture_output=True, text=True)
                self.assertEqual(0, out.returncode, out.stdout + out.stderr)
                return out.stdout.splitlines()[-1]
            with self.subTest(changed=f):
                self.assertEqual("False", stale())
                os.utime(path, (old + 7200, old + 7200))
                self.assertEqual("True", stale())
                os.utime(path, (old, old))

    def test_publish_stamp_follows_the_newest_dll_so_a_core_only_change_republishes(self):
        # Fails if the stamp is the exe dll's write time alone: a rebuild that rewrites only
        # ExlibShapes.Core.dll then matches the stamp of the publish already made.
        got = self.run_body("""
$a = Get-ShapesBuildStamp $build
Pub $a | Out-Null
$core = Join-Path $build 'ExlibShapes.Core.dll'
Set-Content -LiteralPath $core -Value 'core2'
(Get-Item -LiteralPath $core).LastWriteTimeUtc = [datetime]::UtcNow.AddMinutes(5)
$b = Get-ShapesBuildStamp $build
ConvertTo-Json -Compress @(($a -ne $b), (Test-ShapesPublished $root $a), (Test-ShapesPublished $root $b))""")
        self.assertEqual([True, True, False], json.loads(got[-1]))

    def test_shapetool_runs_the_published_tool_and_returns_its_exit_code(self):
        # Fails if a nonzero tool exit (3, an open question) becomes an exmod error, the verb is not
        # passed through, the run builds, or the tool's own output joins the returned code.
        got = self.run_body("""
Pub 's1' | Out-Null
$global:seen = @()
function dotnet { $global:seen += ($args -join '|'); 'tool output'; $global:LASTEXITCODE = 3 }
$env:EXMOD_SHAPES_NO_BUILD = '1'
$code = Invoke-ShapeTool @('check', 'a.json', '--catalogue')
ConvertTo-Json -Compress @($code, $global:seen)""")
        code, seen = json.loads(got[-1])
        self.assertEqual(3, code)
        self.assertEqual(os.path.join(self.root, self.publishes()[0], "exlib-shapes.dll") + "|check|a.json|--catalogue", seen[0] if isinstance(seen, list) else seen)


if __name__ == "__main__":
    unittest.main()
