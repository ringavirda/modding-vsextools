"""Tests for `exmod scenario` in exmod/run.ps1 and the scratch server it shares with `exmod smoke`: the
option parse, the command line and names, the staging layout, the server config, the schematic copy, and the
report over sample log text with the exit rules. Run through pwsh ($PWSH, PATH, or the nearest
.dotnet/tools above the checkout) with exmod_harness's prelude, one pwsh per class; skipped when no pwsh is found. Each test
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
CENSUS = "[scenario] census exlib: 3 blocktype(s), 2 covered"
COVERAGE = "[scenario] coverage 41 hook(s), 38 reached"
UNCOVERED = "[scenario] uncovered exlib:boiler OnTick"


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
Try-Case 'verbose' { $o = Get-ScenarioOptions @('-Verbose','exlib/sample'); [ordered]@{ Names = $o.Names; Verbose = $o.Verbose; Off = (Get-ScenarioOptions @('exlib/sample')).Verbose } }
Try-Case 'hostVerbose' { $VerbosePreference = 'Continue'; (Get-ScenarioOptions @('exlib/sample')).Verbose }
Try-Case 'unknown' { Get-ScenarioOptions @('-Keep','exlib/sample') | Out-Null; 'no error' }
Try-Case 'series' { Get-ScenarioOptions @('-Version','1.21') | Out-Null; 'no error' }
Try-Case 'shardsMods' { $o = Get-ScenarioOptions @('-Shards','3','-Mods','a,b','-KeepData','exlib/sample'); [ordered]@{ Shards = $o.Shards; Mods = @($o.Mods); KeepData = $o.KeepData } }
Try-Case 'shardDefaults' { $o = Get-ScenarioOptions @('exlib/sample'); [ordered]@{ Shards = $o.Shards; Mods = @($o.Mods); KeepData = $o.KeepData } }
Try-Case 'shardsZero' { Get-ScenarioOptions @('-Shards','0') | Out-Null; 'no error' }
Try-Case 'modDirs' { Get-ScenarioModDirs @('default-a') 'live-dir' @(Join-Path $RepoRoot 'build/mod-a') }
Try-Case 'tooManyShards' { Invoke-Scenario @('-Shards','3','exlib/sample') | Out-Null; 'no error' }
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

    def test_verbose_is_a_switch_and_not_a_name(self):
        # Mutation: '-Verbose' removed from the flag options, so it is refused as unknown. Fails here.
        got = self.results_of()["verbose"]
        self.assertEqual(["exlib/sample"], as_list(got["Names"]))
        self.assertTrue(got["Verbose"])
        self.assertFalse(got["Off"])

    def test_verbose_is_read_from_the_preference_the_host_binds(self):
        # Mutation: the $VerbosePreference test removed from Verbose in Get-ScenarioOptions. Fails here.
        self.assertTrue(self.results_of()["hostVerbose"])

    def test_an_unknown_option_is_refused(self):
        # Mutation: the unknown-option throw removed, so -Keep is passed on as a scenario name. Fails here.
        self.assertIn("Unknown option -Keep", self.results_of()["unknown"].get("Error", "no error"))

    def test_another_game_series_is_refused(self):
        # Mutation: the series throw removed, so -Version 1.21 is accepted. Fails here.
        self.assertIn("runs on 1.22 only", self.results_of()["series"].get("Error", "no error"))

    def test_shards_mods_and_keep_data_are_read(self):
        # Mutation: '-Shards' removed from the value options, so 3 is refused as an unknown option. Fails here.
        # Mutation: KeepData set to $false in Get-ScenarioOptions, so -KeepData is ignored. Fails here.
        got = self.results_of()["shardsMods"]
        self.assertEqual(3, got["Shards"])
        self.assertEqual(["a", "b"], as_list(got["Mods"]))
        self.assertTrue(got["KeepData"])

    def test_one_shard_no_extra_mods_and_no_keep_by_default(self):
        # Mutation: the default shard count set to 2. Fails here.
        got = self.results_of()["shardDefaults"]
        self.assertEqual(1, got["Shards"])
        self.assertEqual([], got["Mods"])
        self.assertFalse(got["KeepData"])

    def test_zero_shards_is_refused(self):
        # Mutation: the -Shards minimum check removed, so 0 shards boots nothing and passes. Fails here.
        self.assertIn("-Shards must be 1 or more", self.results_of()["shardsZero"].get("Error", "no error"))

    def test_extra_mod_folders_are_staged_after_the_live_mod(self):
        # Mutation: the -Mods folders dropped from Get-ScenarioModDirs. Fails here.
        got = as_list(self.results_of()["modDirs"])
        self.assertEqual(3, len(got))
        self.assertEqual(["default-a", "live-dir"], got[:2])
        self.assertTrue(got[2].endswith(os.path.join("build", "mod-a")))

    def test_more_shards_than_cases_is_refused_before_anything_is_built(self):
        # Mutation: the shard count check removed, so the run goes on to the live build and fails elsewhere. Fails here.
        self.assertIn("-Shards 3 is more than the 1 case(s) to run.", self.results_of()["tooManyShards"].get("Error", "no error"))


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

    def test_a_bare_stem_is_filed_under_the_checkouts_modid_and_a_slashed_name_is_kept(self):
        # Mutation: Expand-ScenarioNames returns its names unchanged. Fails here.
        got = run_cases(self.repo, r"""
Try-Case 'expand' { Expand-ScenarioNames @('sample','exlib/sample-2','other/x') }
""")["expand"]
        self.assertEqual(["exlib/sample", "exlib/sample-2", "other/x"], got)

    def test_the_schematic_name_replaces_slashes_as_the_runner_does(self):
        # Mutation: the -replace of '/' removed, so the file is looked for under a subfolder. Fails here.
        got = run_cases(self.repo, r"""
Try-Case 'schematic' { Get-ScenarioSchematicName 'exlib/sample-fails' }
""")["schematic"]
        self.assertEqual("scenario-exlib-sample-fails.json", got)

    def test_names_are_dealt_out_over_the_shards_in_turn(self):
        # Mutation: the stride set to 1 rather than the shard count, so each shard takes every name after its own. Fails here.
        # Mutation: the inner loop's bound removed, so a shard past the last name reads past the array. Fails here.
        got = run_cases(self.repo, r"""
Try-Case 'deal' { $g = Split-ScenarioShards @('a','b','c','d','e') 2; @($g | ForEach-Object { $_ -join ',' }) }
Try-Case 'one' { $g = Split-ScenarioShards @('a','b') 1; @($g | ForEach-Object { $_ -join ',' }) }
Try-Case 'past' { $g = Split-ScenarioShards @('a') 2; @($g | ForEach-Object { $_ -join ',' }) }
""")
        self.assertEqual(["a,c,e", "b,d"], got["deal"])
        self.assertEqual(["a,b"], as_list(got["one"]))
        self.assertEqual(["a", ""], got["past"])


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
    GameMode = $c.WorldConfig.WorldConfiguration.gameMode; PassTimeWhenEmpty = $c.PassTimeWhenEmpty; WhitelistMode = $c.WhitelistMode;
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
        self.assertEqual("Off", got["WhitelistMode"])

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
    "census": [RUN, CENSUS, CASE_A, STEP_OK, PASS_A, done(1, 1, 0), COVERAGE, UNCOVERED],
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
Try-Case 'passSteps' { Get-ScenarioReport @($cases.pass) $true }
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

    def test_verbose_shows_a_passing_cases_step_lines(self):
        # Mutation: the block lines not added on a pass line when Steps is set. Fails here.
        r = self.report("passSteps")
        self.assertEqual([RUN, CASE_A, STEP_OK, PASS_A, done(1, 1, 0)], r["Shown"])
        self.assertEqual(0, r["ExitCode"])

    def test_the_census_coverage_and_uncovered_lines_are_shown(self):
        # Mutation: the census/coverage/uncovered branch removed, so those lines are dropped. Fails here.
        r = self.report("census")
        self.assertEqual([RUN, CENSUS, PASS_A, done(1, 1, 0), COVERAGE, UNCOVERED], r["Shown"])
        self.assertEqual(0, r["ExitCode"])


SHARD_CASES = {
    "passA": [RUN, CASE_A, STEP_OK, PASS_A, done(1, 1, 0)],
    "failF": [RUN, CASE_F, STEP_BAD, FAIL_F, done(1, 0, 1)],
    "noCount": [RUN, CASE_A, PASS_A],
    "progress": [RUN, CASE_A, PASS_A, done(1, 1, 0)],
}


@unittest.skipUnless(PWSH, "pwsh not found")
class ShardTests(FixtureRepo):
    """The per-shard reports merged into one count line and one exit rule, the progress printer that
    leaves the count line to the merge, and the schematic copy over every shard's data path."""

    @classmethod
    def setUpClass(cls):
        cls.results = None

    def results_of(self):
        if self.results is None:
            cases = os.path.join(self.tmp, "cases.json")
            with open(cases, "w") as f:
                json.dump(SHARD_CASES, f)
            type(self).results = run_cases(self.repo, r"""
$cases = Get-Content -Raw -LiteralPath $env:CASES | ConvertFrom-Json
$passA = Get-ScenarioReport @($cases.passA)
$failF = Get-ScenarioReport @($cases.failF)
$noCount = Get-ScenarioReport @($cases.noCount)
Try-Case 'line' { Get-ScenarioCountLine @($passA, $failF) }
Try-Case 'lineOne' { Get-ScenarioCountLine @($passA) }
Try-Case 'lineNone' { Get-ScenarioCountLine @($noCount) }
Try-Case 'lineSkipsMissing' { Get-ScenarioCountLine @($noCount, $passA) }
Try-Case 'problemsOne' { Get-ScenarioExitProblems @($failF) }
Try-Case 'problemsTwo' { Get-ScenarioExitProblems @($passA, $failF) }
Try-Case 'problemsPass' { @(Get-ScenarioExitProblems @($passA, $passA)).Count }
Try-Case 'problemsNoCount' { Get-ScenarioExitProblems @($passA, $noCount) }
Try-Case 'progressOut' {
  $log = Join-Path $RepoRoot 'progress.log'
  Set-Content -Path $log -Value @($cases.progress)
  @(Write-ScenarioProgress ([pscustomobject]@{ Log = $log }) 0 $false 6>&1 |
    Where-Object { $_ -is [System.Management.Automation.InformationRecord] } | ForEach-Object { "$_" })
}
Try-Case 'twoDirs' {
  $a = Join-Path $RepoRoot 'dir-a'
  $b = Join-Path $RepoRoot 'dir-b'
  New-Item -ItemType Directory -Force -Path (Join-Path $a 'WorldEdit'), (Join-Path $b 'WorldEdit') | Out-Null
  Set-Content -Path (Join-Path $b 'WorldEdit/scenario-exlib-sample-fails.json') -Value '{}'
  $dest = Join-Path $RepoRoot 'last-two'
  Save-ScenarioSchematics @($a, $b) @('exlib/sample-fails') $dest
  @(Get-ChildItem $dest | ForEach-Object Name)
}
""", CASES=cases)
        return self.results

    def test_the_count_line_sums_the_shards(self):
        # Mutation: the sum replaced by the first shard's totals. Fails here.
        self.assertEqual(done(2, 1, 1), self.results_of()["line"])

    def test_no_count_line_from_any_shard_gives_no_line(self):
        # Mutation: the early return of $null removed, so a summed zero count line is printed. Fails here.
        self.assertIsNone(self.results_of()["lineNone"])

    def test_one_shard_reasons_are_not_prefixed(self):
        # Mutation: the prefix added for one shard too. Fails here.
        self.assertEqual([FAIL_F, "1 case(s) failed"], as_list(self.results_of()["problemsOne"]))

    def test_each_reason_names_its_shard_when_there_are_several(self):
        # Mutation: the prefix dropped. Fails here. Mutation: only the first report read. Fails here too.
        self.assertEqual([f"shard 2: {FAIL_F}", "shard 2: 1 case(s) failed"], self.results_of()["problemsTwo"])

    def test_passing_shards_give_no_reasons(self):
        # Mutation: an extra reason added per shard report. Fails here.
        self.assertEqual(0, self.results_of()["problemsPass"])

    def test_a_shard_without_a_count_line_is_a_reason(self):
        # Mutation: only the first report's reasons read. Fails here.
        self.assertEqual(["shard 2: no count line"], as_list(self.results_of()["problemsNoCount"]))

    def test_progress_prints_every_line_but_the_count_line(self):
        # Mutation: the count line filter removed in Write-ScenarioProgress, so each shard prints its own. Fails here.
        self.assertEqual([RUN, PASS_A], self.results_of()["progressOut"])

    def test_a_schematic_is_found_in_any_shards_data_path(self):
        # Mutation: only the first data path searched in Save-ScenarioSchematics. Fails here.
        self.assertEqual(["scenario-exlib-sample-fails.json"], as_list(self.results_of()["twoDirs"]))


if __name__ == "__main__":
    unittest.main()
