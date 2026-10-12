"""Tests for `exmod scenario` in exmod/run.ps1 and the scratch server it shares with `exmod smoke`: the
option parse, the command line and names, the staging layout, the server config, the schematic copy, and the
report over sample log text with the exit rules. Run through pwsh ($PWSH, PATH, or the checkout's
.dotnet/tools) with exmod_harness's prelude, one pwsh per class; skipped when no pwsh is found. Each test
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

TRY_CASE = r"""
$global:CaseOut = [ordered]@{}
function Try-Case([string]$Name, [scriptblock]$Body) {
  try { $global:CaseOut[$Name] = & $Body }
  catch { $global:CaseOut[$Name] = [ordered]@{ Error = $_.Exception.Message } }
}
"""


def run_cases(repo, body, **env):
    """Runs `body` after exmod.ps1 against `repo` and returns the $global:CaseOut it leaves, as a dict."""
    assert PWSH
    script = exmod_script(TRY_CASE + body + "\nConvertTo-Json -Compress -Depth 6 -InputObject $global:CaseOut")
    run_env = exmod_env(os.path.join(repo, "local"), TEST_REPO=repo, HOME=repo, **env)
    out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", script],
                         env=run_env, capture_output=True, text=True)
    if out.returncode != 0 and not out.stdout.strip():
        raise AssertionError(f"pwsh failed ({out.returncode}): {out.stderr}")
    return json.loads(out.stdout.strip().splitlines()[-1])


RUN = "[scenario] run: 2 scenario(s), 2 case(s), budget 60 s, game 1.22.7"
CASE_A = "[scenario] case exlib/sample plot 100 64 100"
CASE_F = "[scenario] case exlib/sample-fails plot 100 64 120"
STEP_OK = "[scenario]   1 block game:air ok"
STEP_BAD = "[scenario]   2 block game:air FAIL: found game:stone"
DETAIL = "[scenario]   | schematic /data/WorldEdit/scenario-exlib-sample-fails.json"
PASS_A = "[scenario] pass exlib/sample (6.0 s)"
FAIL_F = "[scenario] fail exlib/sample-fails step 2 suspect mod (4.0 s)"
ERR_CASE = "[scenario] error exlib/sample: its plot never loaded"
ERR_RUN = "[scenario] error run: no case selected"
ERR_OUT = "[Error] Mod loader: the file is missing"


def as_list(value):
    """A one-element array comes back from the pipeline as its element."""
    return [value] if isinstance(value, str) else value


def done(run, passed, failed):
    return f"[scenario] done: {run} case(s) run, {passed} passed, {failed} failed."


class FixtureRepo(unittest.TestCase):
    """A temporary checkout with exmod.json, a built mod folder named build/mod-a whose modinfo names
    exliblive, a mod folder build/mod-b whose modinfo does not parse, and tests/live with one scenario
    and one schematic."""

    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.repo = os.path.join(self.tmp, "checkout")
        touch(os.path.join(self.repo, "exmod.json"), "{}")
        touch(os.path.join(self.repo, "build", "mod-a", "modinfo.json"), '{"modid": "exliblive"}')
        touch(os.path.join(self.repo, "build", "mod-a", "ExpandedLib.Testing.Live.dll"), "")
        touch(os.path.join(self.repo, "build", "mod-b", "modinfo.json"), "{ not json")
        touch(os.path.join(self.repo, "tests", "live", "sample.json"), "{}")
        touch(os.path.join(self.repo, "tests", "live", "sample-2.json"), "{}")
        touch(os.path.join(self.repo, "tests", "live", "schematics", "s.json"), "{}")


@unittest.skipUnless(PWSH, "pwsh not found")
class ArgumentTests(FixtureRepo):
    @classmethod
    def setUpClass(cls):
        cls.results = None

    def results_of(self):
        if self.results is None:
            type(self).results = run_cases(self.repo, r"""
Try-Case 'ports' { $o = Get-ScenarioOptions @('-Port','42500','exlib/sample','-Version','1.22','exlib/sample-2','-Timeout','90'); [ordered]@{ Names = $o.Names; Port = $o.Port; Timeout = $o.Timeout } }
Try-Case 'defaults' { $o = Get-ScenarioOptions @('exlib/sample'); [ordered]@{ Port = $o.Port; Timeout = $o.Timeout; Version = $o.Version } }
Try-Case 'unknown' { Get-ScenarioOptions @('-Keep','exlib/sample') | Out-Null; 'no error' }
Try-Case 'series' { Get-ScenarioOptions @('-Version','1.21') | Out-Null; 'no error' }
""")
        return self.results

    def test_port_and_timeout_values_are_not_names_and_are_read(self):
        # Mutation: '-Port' removed from the value options, so 42500 is read as a name. Fails here.
        got = self.results_of()["ports"]
        self.assertEqual(["exlib/sample", "exlib/sample-2"], got["Names"])
        self.assertEqual(42500, got["Port"])
        self.assertEqual(90, got["Timeout"])

    def test_port_defaults_to_42498_and_timeout_to_none(self):
        # Mutation: the default port set to 42499 (smoke's port). Fails here.
        got = self.results_of()["defaults"]
        self.assertEqual(42498, got["Port"])
        self.assertIsNone(got["Timeout"])
        self.assertEqual("1.22", got["Version"])

    def test_an_unknown_option_is_refused(self):
        # Mutation: the unknown-option throw removed, so -Keep is passed on as a scenario name. Fails here.
        self.assertIn("Unknown option -Keep", self.results_of()["unknown"].get("Error", "no error"))

    def test_another_game_series_is_refused(self):
        # Mutation: the series throw removed, so -Version 1.21 is accepted. Fails here.
        self.assertIn("runs on 1.22 only", self.results_of()["series"].get("Error", "no error"))


@unittest.skipUnless(PWSH, "pwsh not found")
class CommandAndNamesTests(FixtureRepo):
    def test_the_command_line_runs_the_names_in_order(self):
        # Mutation: the verb 'run' replaced by 'play' in Get-ScenarioCommand. Fails here.
        got = run_cases(self.repo, r"""
Try-Case 'command' { Get-ScenarioCommand @('exlib/sample','exlib/sample-2') }
""")["command"]
        self.assertEqual("/exmod scenario run exlib/sample exlib/sample-2", got)

    def test_no_name_lists_every_scenario_file_directly_under_tests_live(self):
        # Mutation: -Filter '*.json' replaced by a recursive search, so schematics/s.json is listed. Fails here.
        got = run_cases(self.repo, r"""
Try-Case 'names' { Get-ScenarioNames }
""")["names"]
        self.assertEqual(["exlib/sample", "exlib/sample-2"], sorted(got))

    def test_the_schematic_name_replaces_slashes_as_the_runner_does(self):
        # Mutation: the -replace of '/' removed, so the file is looked for under a subfolder. Fails here.
        got = run_cases(self.repo, r"""
Try-Case 'schematic' { Get-ScenarioSchematicName 'exlib/sample-fails' }
""")["schematic"]
        self.assertEqual("scenario-exlib-sample-fails.json", got)


@unittest.skipUnless(PWSH, "pwsh not found")
class StagingTests(FixtureRepo):
    @classmethod
    def setUpClass(cls):
        cls.results = None

    def results_of(self):
        if self.results is None:
            type(self).results = run_cases(self.repo, r"""
function Layout($s) {
  $data = @(Get-ChildItem -Recurse -File $s.Data | ForEach-Object { [IO.Path]::GetRelativePath($s.Data, $_.FullName).Replace('\', '/') } | Sort-Object)
  [ordered]@{ Mods = @(Get-ChildItem $s.Mods | ForEach-Object Name | Sort-Object); Data = $data }
}
Try-Case 'staged' {
  $s = New-ScratchServer 'scenario' @((Join-Path $RepoRoot 'build/mod-a'), (Join-Path $RepoRoot 'build/mod-b')) `
    -DataCopies @{ (Join-Path $RepoRoot 'tests/live') = 'Scenarios/exlib' } -ServerConfig (Get-ScenarioServerConfig)
  $layout = Layout $s
  Remove-ScratchServer $s $false
  $layout
}
Try-Case 'bare' {
  $s = New-ScratchServer 'smoke' @((Join-Path $RepoRoot 'build/mod-a'))
  $layout = Layout $s
  Remove-ScratchServer $s $false
  $layout
}
Try-Case 'kept' { $s = New-ScratchServer 'smoke' @(); Remove-ScratchServer $s $true; [ordered]@{ Kept = (Test-Path $s.Root) } }
Try-Case 'gone' { $s = New-ScratchServer 'smoke' @(); Remove-ScratchServer $s $false; [ordered]@{ Kept = (Test-Path $s.Root) } }
Try-Case 'schematics' {
  $data = Join-Path $RepoRoot 'data-x'
  New-Item -ItemType Directory -Force -Path (Join-Path $data 'WorldEdit') | Out-Null
  Set-Content -Path (Join-Path $data 'WorldEdit/scenario-exlib-sample-fails.json') -Value '{}'
  $dest = Join-Path $RepoRoot 'last'
  New-Item -ItemType Directory -Force -Path $dest | Out-Null
  Set-Content -Path (Join-Path $dest 'stale.json') -Value 'old'
  Save-ScenarioSchematics $data @('exlib/sample-fails', 'exlib/absent') $dest
  @(Get-ChildItem $dest | ForEach-Object Name)
}
Try-Case 'serverconfig' {
  $c = Get-ScenarioServerConfig
  [ordered]@{ Ip = $c.Ip; WorldType = $c.WorldConfig.WorldType; PlayStyle = $c.WorldConfig.PlayStyle;
    GameMode = $c.WorldConfig.WorldConfiguration.gameMode; PassTimeWhenEmpty = $c.PassTimeWhenEmpty;
    DefaultRole = $c.DefaultRoleCode; RoleCodes = @($c.Roles | ForEach-Object { $_.Code }) }
}
""")
        return self.results

    def test_a_mod_is_staged_under_the_modid_in_its_modinfo_and_the_scenarios_under_their_path(self):
        # Mutation: the modid read from modinfo.json dropped, so the folder name 'mod-a' is staged. Fails here.
        got = self.results_of()["staged"]
        self.assertIn("exliblive", got["Mods"])
        self.assertNotIn("mod-a", got["Mods"])
        self.assertIn("Scenarios/exlib/sample.json", got["Data"])
        self.assertIn("Scenarios/exlib/schematics/s.json", got["Data"])

    def test_a_modinfo_that_does_not_parse_is_still_staged_under_its_folder_name(self):
        # Mutation: the warning replaced by a throw, so an unreadable modinfo stops the staging. Fails here.
        got = self.results_of()["staged"]
        self.assertIn("mod-b", got["Mods"])
        self.assertNotIn("error", got)

    def test_the_server_config_is_written_only_when_given(self):
        # Mutation: the serverconfig.json write unconditional, so a bare scratch server gets one. Fails here.
        staged = self.results_of()["staged"]
        bare = self.results_of()["bare"]
        self.assertIn("serverconfig.json", staged["Data"])
        self.assertNotIn("serverconfig.json", bare["Data"])
        self.assertEqual(["exliblive"], bare["Mods"])

    def test_remove_keeps_the_folder_only_when_asked(self):
        # Mutation: the -KeepData test inverted in Remove-ScratchServer. Fails here.
        self.assertTrue(self.results_of()["kept"]["Kept"])
        self.assertFalse(self.results_of()["gone"]["Kept"])

    def test_the_last_run_holds_only_the_failed_cases_schematics(self):
        # Mutation: the emptying of the destination removed, so a stale file survives. Fails here; and
        # the -replace mutation of Get-ScenarioSchematicName makes the copy find nothing and fail here too.
        self.assertEqual(["scenario-exlib-sample-fails.json"], as_list(self.results_of()["schematics"]))

    def test_the_scenario_server_config_is_loopback_superflat_and_creative(self):
        # Mutation: WorldType 'superflat' changed to 'flat'. Fails here.
        got = self.results_of()["serverconfig"]
        self.assertEqual("127.0.0.1", got["Ip"])
        self.assertEqual("superflat", got["WorldType"])
        self.assertEqual("creative", got["GameMode"])
        self.assertTrue(got["PassTimeWhenEmpty"])

    def test_the_default_role_is_a_role_the_config_defines(self):
        # Mutation: the Roles entry removed from the config. Fails here: the default code names no group.
        got = self.results_of()["serverconfig"]
        self.assertEqual("suplayer", got["DefaultRole"])
        self.assertEqual(["suplayer"], as_list(got["RoleCodes"]))


REPORT_CASES = {
    "pass": [RUN, CASE_A, STEP_OK, PASS_A, done(1, 1, 0)],
    "fail": [RUN, CASE_F, STEP_OK, STEP_BAD, DETAIL, FAIL_F, done(1, 0, 1)],
    "errorInsidePassingCase": [RUN, CASE_A, ERR_OUT, PASS_A, done(1, 1, 0)],
    "noCountLine": [RUN, CASE_A, PASS_A],
    "zeroCases": [done(0, 0, 0)],
    "noCaseSelected": [ERR_RUN, done(0, 0, 0)],
    "failedCountOnly": [RUN, CASE_A, PASS_A, done(1, 0, 1)],
    "failLineOnly": [RUN, CASE_F, STEP_BAD, FAIL_F, done(1, 0, 0)],
    "errorLineOnly": [RUN, ERR_CASE, done(1, 0, 0)],
    "errorOutsideCase": [RUN, CASE_A, PASS_A, ERR_OUT, done(1, 1, 0)],
    "unfinishedCase": [RUN, CASE_A, STEP_OK, done(1, 0, 0)],
    "openAtEnd": [RUN, CASE_A, STEP_OK],
    "twoCasesInARow": [CASE_A, STEP_OK, CASE_F, STEP_BAD, FAIL_F, done(2, 0, 1)],
}


@unittest.skipUnless(PWSH, "pwsh not found")
class ReportTests(FixtureRepo):
    @classmethod
    def setUpClass(cls):
        cls.results = None

    def results_of(self):
        if self.results is None:
            cases = os.path.join(self.tmp, "cases.json")
            with open(cases, "w") as f:
                json.dump(REPORT_CASES, f)
            type(self).results = run_cases(self.repo, r"""
$cases = Get-Content -Raw -LiteralPath $env:CASES | ConvertFrom-Json
foreach ($p in $cases.PSObject.Properties) { Try-Case $p.Name { Get-ScenarioReport @($p.Value) } }
""", CASES=cases)
        return self.results

    def report(self, name):
        return self.results_of()[name]

    def test_a_passing_run_exits_zero_and_shows_only_its_pass_and_count_lines(self):
        # Mutation: the pass line's block discard removed, so the passing case's own lines are shown. Fails here.
        # Mutation: the budget regex broken, so the run line's budget is not read. Fails here.
        r = self.report("pass")
        self.assertEqual(0, r["ExitCode"])
        self.assertEqual([], r["Problems"])
        self.assertEqual([RUN, PASS_A, done(1, 1, 0)], r["Shown"])
        self.assertEqual(60, r["Budget"])
        self.assertTrue(r["Done"])

    def test_a_failed_case_shows_every_line_it_logged(self):
        # Mutation: the block lines not added to Shown on a fail line. Fails here.
        r = self.report("fail")
        self.assertEqual(1, r["ExitCode"])
        self.assertEqual([RUN, CASE_F, STEP_OK, STEP_BAD, DETAIL, FAIL_F, done(1, 0, 1)], r["Shown"])
        self.assertEqual(["exlib/sample-fails"], r["FailedNames"])
        self.assertEqual([FAIL_F, "1 case(s) failed"], r["Problems"])

    def test_an_error_line_inside_a_passing_case_does_not_fail_the_run(self):
        # Mutation: the [Error] check moved above the case capture, so it counts inside a case. Fails here.
        r = self.report("errorInsidePassingCase")
        self.assertEqual(0, r["ExitCode"])
        self.assertEqual([], r["Problems"])

    def test_a_missing_count_line_fails_the_run(self):
        # Mutation: the 'no count line' problem removed. Fails here.
        r = self.report("noCountLine")
        self.assertEqual(1, r["ExitCode"])
        self.assertEqual(["no count line"], r["Problems"])
        self.assertFalse(r["Done"])

    def test_a_count_line_reporting_zero_cases_fails_the_run(self):
        # Mutation: the '0 cases run' problem removed. Fails here.
        self.assertEqual(["0 cases run"], self.report("zeroCases")["Problems"])

    def test_a_run_with_no_case_selected_fails_on_its_error_line_and_its_zero_count(self):
        # Mutation: the '0 cases run' problem removed. Fails here too, on the second reason.
        self.assertEqual([ERR_RUN, "0 cases run"], self.report("noCaseSelected")["Problems"])

    def test_a_failed_count_without_a_fail_line_fails_the_run(self):
        # Mutation: the failed-count problem removed. Fails here.
        self.assertEqual(["1 case(s) failed"], self.report("failedCountOnly")["Problems"])

    def test_a_fail_line_fails_the_run_when_the_count_says_none_failed(self):
        # Mutation: the shared fail/error problem line removed. Fails here, and in the error test below.
        self.assertEqual([FAIL_F], self.report("failLineOnly")["Problems"])

    def test_an_error_line_fails_the_run(self):
        # Mutation: the shared fail/error problem line removed. Fails here.
        r = self.report("errorLineOnly")
        self.assertEqual([ERR_CASE], r["Problems"])
        self.assertEqual(["exlib/sample"], r["FailedNames"])

    def test_an_error_outside_a_case_fails_the_run(self):
        # Mutation: the 'error outside a case' problem removed. Fails here.
        self.assertEqual([f"error outside a case: {ERR_OUT}"], self.report("errorOutsideCase")["Problems"])

    def test_a_case_the_count_line_closes_over_is_reported_unfinished_and_its_lines_shown(self):
        # Mutation: the unfinished problem removed where the count line closes an open case. Fails here.
        r = self.report("unfinishedCase")
        self.assertEqual(["case exlib/sample did not finish"], r["Problems"])
        self.assertEqual([RUN, CASE_A, STEP_OK, done(1, 0, 0)], r["Shown"])

    def test_a_case_open_at_the_end_of_the_log_is_pending(self):
        # Mutation: Pending set to an empty list. Fails here.
        r = self.report("openAtEnd")
        self.assertEqual([CASE_A, STEP_OK], r["Pending"])
        self.assertEqual([RUN], r["Shown"])
        self.assertEqual(["case exlib/sample did not finish", "no count line"], r["Problems"])

    def test_a_case_that_starts_while_another_is_open_closes_the_first_as_unfinished(self):
        # Mutation: the case line dropped from the close regex, so the first case's lines are lost. Fails here.
        r = self.report("twoCasesInARow")
        self.assertEqual([CASE_A, STEP_OK, CASE_F, STEP_BAD, FAIL_F, done(2, 0, 1)], r["Shown"])
        self.assertEqual(["case exlib/sample did not finish", FAIL_F, "1 case(s) failed"], r["Problems"])


if __name__ == "__main__":
    unittest.main()
