"""Tests for the shapes tool's published copy in exmod/shapes.ps1 (Publish-ExlibShapes,
Remove-OldShapesPublishes, Test-ShapesPublished and the EXMOD_SHAPES_NO_BUILD path of
Invoke-ExlibShapes), run through pwsh on a temporary publish root and a fake build folder with
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


if __name__ == "__main__":
    unittest.main()
