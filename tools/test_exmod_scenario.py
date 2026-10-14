"""Tests for `exmod scenario` in exmod/run.ps1 and the scratch server it shares with `exmod smoke`: the
option parse, the command line and names, the staging layout, the server config, the schematic copy, the
last-run folder, and the report over sample log text with the exit rules. Run through pwsh ($PWSH, PATH, or the nearest
.dotnet/tools above the checkout) with exmod_harness's prelude, one pwsh per class; skipped when no pwsh is found."""

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
    """A temporary checkout with an exmod.json naming a live mod, a built mod folder named build/mod-a whose modinfo names
    exliblive, a mod folder build/mod-b whose modinfo does not parse, and tests/live with one scenario
    and one schematic."""

    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.repo = os.path.join(self.tmp, "checkout")
        touch(os.path.join(self.repo, "exmod.json"), '{"live": {"path": "src/Live"}}')
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
Try-Case 'unknown' { Get-ScenarioOptions @('-Bogus','exlib/sample') | Out-Null; 'no error' }
Try-Case 'series' { Get-ScenarioOptions @('-Version','1.21') | Out-Null; 'no error' }
Try-Case 'shardsMods' { $o = Get-ScenarioOptions @('-Shards','3','-Mods','a,b','-KeepData','exlib/sample'); [ordered]@{ Shards = $o.Shards; Mods = @($o.Mods); KeepData = $o.KeepData } }
Try-Case 'shardDefaults' { $o = Get-ScenarioOptions @('exlib/sample'); [ordered]@{ Shards = $o.Shards; Mods = @($o.Mods); KeepData = $o.KeepData } }
Try-Case 'shardsZero' { Get-ScenarioOptions @('-Shards','0') | Out-Null; 'no error' }
Try-Case 'modDirs' { Get-ScenarioModDirs @('default-a') 'live-dir' @(Join-Path $RepoRoot 'build/mod-a') }
Try-Case 'tooManyShards' { Invoke-Scenario @('-Shards','3','exlib/sample') | Out-Null; 'no error' }
Try-Case 'shardsAsked' { [ordered]@{ Asked = (Get-ScenarioOptions @('-Shards','1','exlib/sample')).ShardsAsked; NotAsked = (Get-ScenarioOptions @('exlib/sample')).ShardsAsked } }
Try-Case 'noFiles' { Get-ChildItem (Join-Path $RepoRoot 'tests/live') -Filter '*.json' -File | Remove-Item; Invoke-Scenario @() | Out-Null; 'no error' }
Try-Case 'noFilesShards' { Invoke-Scenario @('-Shards','2') | Out-Null; 'no error' }
Try-Case 'shardPorts' { [ordered]@{ Clear = @(Get-ScenarioPorts 42600 2); Skips = @(Get-ScenarioPorts 42498 3); From = @(Get-ScenarioPorts 42499 2) } }
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
        self.assertIn("Unknown option -Bogus", self.results_of()["unknown"].get("Error", "no error"))

    def test_another_game_series_is_refused(self):
        # Mutation: the series throw removed, so -Version 1.21 is accepted. Fails here.
        self.assertIn("runs on 1.22 only", self.results_of()["series"].get("Error", "no error"))

    def test_shards_mods_and_keep_data_are_read(self):
        got = self.results_of()["shardsMods"]
        self.assertEqual(3, got["Shards"])
        self.assertEqual(["a", "b"], as_list(got["Mods"]))
        self.assertTrue(got["KeepData"])

    def test_one_shard_no_extra_mods_and_no_keep_by_default(self):
        got = self.results_of()["shardDefaults"]
        self.assertEqual(1, got["Shards"])
        self.assertEqual([], got["Mods"])
        self.assertFalse(got["KeepData"])

    def test_zero_shards_is_refused(self):
        self.assertIn("-Shards must be 1 or more", self.results_of()["shardsZero"].get("Error", "no error"))

    def test_extra_mod_folders_are_staged_after_the_live_mod(self):
        got = as_list(self.results_of()["modDirs"])
        self.assertEqual(3, len(got))
        self.assertEqual(["default-a", "live-dir"], got[:2])
        self.assertTrue(got[2].endswith(os.path.join("build", "mod-a")))

    def test_more_shards_than_cases_is_refused_before_anything_is_built(self):
        self.assertIn("-Shards 3 is more than the 1 scenario name(s) to run.", self.results_of()["tooManyShards"].get("Error", "no error"))

    def test_shards_asked_is_true_only_when_the_option_was_given(self):
        got = self.results_of()["shardsAsked"]
        self.assertTrue(got["Asked"])
        self.assertFalse(got["NotAsked"])

    def test_a_run_with_no_scenario_file_says_so_with_or_without_shards(self):
        for key in ("noFiles", "noFilesShards"):
            self.assertIn("No scenario file under tests/live", self.results_of()[key].get("Error", "no error"))

    def test_shard_ports_run_upward_from_the_first_and_skip_smokes(self):
        got = self.results_of()["shardPorts"]
        self.assertEqual([42600, 42601], got["Clear"])
        self.assertEqual([42498, 42500, 42501], got["Skips"])
        self.assertEqual([42500, 42501], got["From"])


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
Try-Case 'names' { Get-ScenarioNames @([pscustomobject]@{ ModId = 'exlib'; Dir = Join-Path $RepoRoot 'tests/live' }) }
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
        self.assertNotIn("Error", got)
        self.assertIn("mod-b", got["Mods"])

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
        self.assertEqual(done(2, 1, 1), self.results_of()["line"])

    def test_no_count_line_from_any_shard_gives_no_line(self):
        self.assertIsNone(self.results_of()["lineNone"])

    def test_one_shard_reasons_are_not_prefixed(self):
        self.assertEqual([FAIL_F, "1 case(s) failed"], as_list(self.results_of()["problemsOne"]))

    def test_each_reason_names_its_shard_when_there_are_several(self):
        self.assertEqual([f"shard 2: {FAIL_F}", "shard 2: 1 case(s) failed"], self.results_of()["problemsTwo"])

    def test_passing_shards_give_no_reasons(self):
        self.assertEqual(0, self.results_of()["problemsPass"])

    def test_a_shard_without_a_count_line_is_a_reason(self):
        self.assertEqual(["shard 2: no count line"], as_list(self.results_of()["problemsNoCount"]))

    def test_progress_prints_every_line_but_the_count_line(self):
        self.assertEqual([RUN, PASS_A], self.results_of()["progressOut"])

    def test_a_schematic_is_found_in_any_shards_data_path(self):
        self.assertEqual(["scenario-exlib-sample-fails.json"], as_list(self.results_of()["twoDirs"]))


EXMODS_JSON = {
    "mods": {"iiex": {"path": "mods/iiex"}, "siex": {"path": "mods/siex"}},
    "samples": {},
}


@unittest.skipUnless(PWSH, "pwsh not found")
class ModIdStagingTests(unittest.TestCase):
    """The scenario folders of an exmods-shaped checkout, the live mod found through ExlibRoot, the -Keep
    lines and the console relay."""

    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.repo = os.path.join(self.tmp, "exmods")
        touch(os.path.join(self.repo, "exmod.json"), json.dumps(EXMODS_JSON))
        for mod, modid in (("iiex", "iiex"), ("siex", "steelindustry")):
            touch(os.path.join(self.repo, "mods", mod, "src", f"{mod}.csproj"), "<Project />")
            touch(os.path.join(self.repo, "mods", mod, "src", "modinfo.json"), json.dumps({"modid": modid}))
        touch(os.path.join(self.repo, "mods", "iiex", "tests", "iiex.tests.csproj"), "<Project />")
        touch(os.path.join(self.repo, "mods", "iiex", "tests", "live", "sample.json"), "{}")
        touch(os.path.join(self.repo, "mods", "iiex", "tests", "live", "lines", "cold.json"), "{}")
        for other in ("generic.json", "coverage-allow.json", "schematics/s.json"):
            touch(os.path.join(self.repo, "mods", "iiex", "tests", "live", other), "{}")
        self.exlib = os.path.join(self.tmp, "exlib")
        touch(os.path.join(self.exlib, "src", "ExpandedLib.Testing.Live", "Live.csproj"), "<Project />")

    def test_each_mods_tests_live_is_staged_under_its_modid(self):
        got = run_cases(self.repo, r"""
Try-Case 'sources' { @(Get-ScenarioSources | ForEach-Object { "$($_.ModId)=$([bool]$_.Dir)" }) }
Try-Case 'staged' {
  $copies = @{}
  foreach ($source in @(Get-ScenarioSources | Where-Object { $_.Dir })) { $copies[$source.Dir] = "Scenarios/$($source.ModId)" }
  $s = New-ScratchServer 'scenario' @() -DataCopies $copies
  $data = @(Get-ChildItem -Recurse -File $s.Data | ForEach-Object { [IO.Path]::GetRelativePath($s.Data, $_.FullName).Replace('\', '/') } | Sort-Object)
  Remove-ScratchServer $s $false
  $data
}
""")
        self.assertEqual(["iiex=True", "steelindustry=False"], got["sources"])
        self.assertEqual(["Scenarios/iiex/coverage-allow.json", "Scenarios/iiex/generic.json", "Scenarios/iiex/lines/cold.json",
                          "Scenarios/iiex/sample.json", "Scenarios/iiex/schematics/s.json"], got["staged"])

    def test_no_name_runs_every_mods_scenario_files_and_a_bare_stem_is_the_first_mods(self):
        got = run_cases(self.repo, r"""
Try-Case 'names' { Get-ScenarioNames @(Get-ScenarioSources) }
Try-Case 'bare' { Expand-ScenarioNames @('sample','steelindustry/x') @(Get-ScenarioSources)[0].ModId }
""")
        self.assertEqual(["iiex/lines/cold", "iiex/sample"], as_list(got["names"]))
        self.assertEqual(["iiex/sample", "steelindustry/x"], got["bare"])

    def test_a_manifest_with_a_live_key_stages_its_own_tests_live_under_exlib(self):
        touch(os.path.join(self.repo, "exmod.json"), json.dumps({"mods": {}, "live": {"path": "x"}}))
        got = run_cases(self.repo, r"""
Try-Case 'sources' { @(Get-ScenarioSources | ForEach-Object { "$($_.ModId)=$($_.Dir.Substring($RepoRoot.Length).Replace('\', '/'))" }) }
""")
        self.assertEqual(["exlib=/tests/live"], as_list(got["sources"]))

    def test_the_live_project_is_found_through_exlibroot_then_the_sibling_and_a_missing_one_is_refused(self):
        body = r"""
Try-Case 'project' { Split-Path (Get-ExmodLiveProject) -Leaf }
"""
        shutil.copytree(self.exlib, os.path.join(self.tmp, "sibling-parent", "exlib"))
        moved = os.path.join(self.tmp, "sibling-parent", "exmods")
        shutil.copytree(self.repo, moved)
        self.assertEqual("Live.csproj", run_cases(moved, body, ExlibRoot="")["project"])
        lonely = os.path.join(self.tmp, "lonely", "exmods")
        shutil.copytree(self.repo, lonely)
        env_got = run_cases(lonely, body, ExlibRoot=self.exlib + "/")
        self.assertEqual("Live.csproj", env_got["project"])
        missing = run_cases(lonely, body, ExlibRoot="")
        self.assertIn("found none", missing["project"]["Error"])
        self.assertIn("ExlibRoot", missing["project"]["Error"])

    def test_the_exlib_root_is_the_workspace_exlib_found_by_walking_up_to_the_props(self):
        # Mutation: the walk-up removed, so a lane resolves ../exlib instead of the workspace's. Fails here.
        workspace = os.path.join(self.tmp, "ws")
        touch(os.path.join(workspace, "Directory.Build.props"), "<Project />")
        shutil.copytree(self.exlib, os.path.join(workspace, "exlib"))
        lane = os.path.join(workspace, ".worktrees", "exmods-lane")
        shutil.copytree(self.repo, lane)
        os.makedirs(os.path.join(workspace, ".worktrees", "exlib"))
        got = run_cases(lane, "Try-Case 'root' { Get-ScenarioExlibRoot }", ExlibRoot="")
        self.assertEqual(os.path.join(workspace, "exlib"), got["root"])

    def sibling_exlib(self, root, project):
        touch(os.path.join(root, "exmod.json"), json.dumps({"mods": {"exlib": {"path": "src/ExpandedLib"}}}))
        touch(os.path.join(root, "src", "ExpandedLib", project), "<Project />")

    def test_a_dependency_is_taken_from_the_exlibroot_checkout_before_the_parent_directory(self):
        # Mutation: the Get-ScenarioExlibRoot lookup removed from Get-ExmodDependencySiblingProject. Fails here.
        body = "Try-Case 'dep' { Split-Path (Get-ExmodDependencySiblingProject 'exlib') -Leaf }"
        self.sibling_exlib(os.path.join(self.tmp, "parent", "exlib"), "Beside.csproj")
        self.sibling_exlib(os.path.join(self.tmp, "elsewhere", "exlib"), "Built.csproj")
        lane = os.path.join(self.tmp, "parent", "exmods")
        shutil.copytree(self.repo, lane)
        got = run_cases(lane, body, ExlibRoot=os.path.join(self.tmp, "elsewhere", "exlib") + "/")
        self.assertEqual("Built.csproj", got["dep"])

    def test_a_dependency_is_taken_from_the_exlib_beside_the_props_before_the_parent_directory(self):
        # Mutation: the walk-up removed from Get-ScenarioExlibRoot, so the parent directory's exlib wins. Fails here.
        body = "Try-Case 'dep' { Split-Path (Get-ExmodDependencySiblingProject 'exlib') -Leaf }"
        workspace = os.path.join(self.tmp, "ws")
        touch(os.path.join(workspace, "Directory.Build.props"), "<Project />")
        self.sibling_exlib(os.path.join(workspace, "exlib"), "Built.csproj")
        self.sibling_exlib(os.path.join(workspace, ".worktrees", "exlib"), "Beside.csproj")
        lane = os.path.join(workspace, ".worktrees", "exmods-lane")
        shutil.copytree(self.repo, lane)
        self.assertEqual("Built.csproj", run_cases(lane, body, ExlibRoot="")["dep"])

    def test_a_dependency_the_exlibroot_checkout_does_not_list_is_found_beside_the_checkout(self):
        # Mutation: the parent-directory scan removed from Get-ExmodDependencySiblingProject. Fails here.
        body = "Try-Case 'dep' { Split-Path (Get-ExmodDependencySiblingProject 'other') -Leaf }"
        self.sibling_exlib(os.path.join(self.tmp, "elsewhere", "exlib"), "Built.csproj")
        touch(os.path.join(self.tmp, "parent", "other", "exmod.json"), json.dumps({"mods": {"other": {"path": "o"}}}))
        touch(os.path.join(self.tmp, "parent", "other", "o", "Other.csproj"), "<Project />")
        lane = os.path.join(self.tmp, "parent", "exmods")
        shutil.copytree(self.repo, lane)
        got = run_cases(lane, body, ExlibRoot=os.path.join(self.tmp, "elsewhere", "exlib"))
        self.assertEqual("Other.csproj", got["dep"])

    def test_the_scenario_default_mods_replace_the_resolved_exlib_with_the_given_one(self):
        # Mutation: the Id filter dropped, so the dependency resolution's exlib is staged beside the given one. Fails here.
        got = run_cases(self.repo, r"""
function Get-BuiltModDirs { @('/built/iiex') }
function Resolve-DependencyMods { @([pscustomobject]@{ Id = 'exlib'; Path = '/dep/exlib' }, [pscustomobject]@{ Id = 'other'; Path = '/dep/other' }) }
Try-Case 'dirs' { Get-ScenarioDefaultModDirs '/lane/exlib/mod' }
""")
        self.assertEqual(["/built/iiex", "/dep/other", "/lane/exlib/mod"], got["dirs"])

    def test_the_keep_server_config_makes_the_default_role_an_admin_the_config_defines(self):
        # Mutation: the Keep branch removed from Get-ScenarioServerConfig. Fails here.
        got = run_cases(self.repo, r"""
Try-Case 'keep' { $c = Get-ScenarioServerConfig $true; [ordered]@{ Default = $c.DefaultRoleCode; Codes = @($c.Roles | ForEach-Object { $_.Code }) } }
Try-Case 'plain' { (Get-ScenarioServerConfig $false).DefaultRoleCode }
""")
        self.assertEqual({"Default": "admin", "Codes": ["suplayer", "admin"]}, got["keep"])
        self.assertEqual("suplayer", got["plain"])

    def test_keep_is_a_flag_and_appends_keep_to_the_command(self):
        got = run_cases(self.repo, r"""
Try-Case 'flag' { $o = Get-ScenarioOptions @('-Keep','iiex/sample'); [ordered]@{ Keep = $o.Keep; Names = @($o.Names); Off = (Get-ScenarioOptions @('iiex/sample')).Keep } }
Try-Case 'on' { Get-ScenarioCommand @('iiex/sample') $true }
Try-Case 'off' { Get-ScenarioCommand @('iiex/sample') $false }
""")
        self.assertEqual({"Keep": True, "Names": ["iiex/sample"], "Off": False}, got["flag"])
        self.assertEqual("/exmod scenario run iiex/sample keep", got["on"])
        self.assertEqual("/exmod scenario run iiex/sample", got["off"])

    def test_the_keep_lines_give_the_port_the_data_path_and_how_to_join(self):
        got = run_cases(self.repo, r"""
Try-Case 'lines' { Get-ScenarioKeepLines ([pscustomobject]@{ Data = '/scratch/data' }) 42501 }
""")
        self.assertEqual(["Server kept up on port 42501, data at /scratch/data",
                          "Join: exmod client, Multiplayer, localhost:42501"], got["lines"])

    def test_the_relay_writes_each_line_to_every_live_server_and_ends_with_the_input(self):
        got = run_cases(self.repo, r"""
function Fake([bool]$Exited) {
  $ms = [System.IO.MemoryStream]::new()
  $w = [System.IO.StreamWriter]::new($ms)
  [pscustomobject]@{ Ms = $ms; Proc = [pscustomobject]@{ HasExited = $Exited; StandardInput = $w } }
}
Try-Case 'relay' {
  $live = Fake $false
  $dead = Fake $true
  Send-ScenarioRelay @($live, $dead) ([System.IO.StringReader]::new("/time set day`n/stop`n"))
  [ordered]@{
    Live = [System.Text.Encoding]::UTF8.GetString($live.Ms.ToArray()).Replace("`r", '')
    Dead = $dead.Ms.Length
  }
}
""")
        self.assertEqual({"Live": "/time set day\n/stop\n", "Dead": 0}, got["relay"])


@unittest.skipUnless(PWSH, "pwsh not found")
class LastRunTests(FixtureRepo):
    """The last-run folder: each shard's recordings land in its recordings folder, a run with none makes no folder,
    and the recordings survive the schematic step that empties the folder."""

    @classmethod
    def setUpClass(cls):
        cls.results = None

    def results_of(self):
        if self.results is None:
            type(self).results = run_cases(self.repo, r"""
$a = Join-Path $RepoRoot 'data-a'
$b = Join-Path $RepoRoot 'data-b'
$c = Join-Path $RepoRoot 'data-c'
New-Item -ItemType Directory -Force -Path (Join-Path $a 'Scenarios/recordings'), (Join-Path $b 'Scenarios/recordings'), (Join-Path $a 'WorldEdit'), $c | Out-Null
Set-Content -Path (Join-Path $a 'Scenarios/recordings/exlib-sample.json') -Value '{}'
Set-Content -Path (Join-Path $b 'Scenarios/recordings/exlib-sample-2.json') -Value '{}'
Set-Content -Path (Join-Path $a 'WorldEdit/scenario-exlib-sample-fails.json') -Value '{}'
$dest = Join-Path $RepoRoot 'last'
function Names($root) { @(Get-ChildItem -Recurse -File $root | ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/') } | Sort-Object) }
Try-Case 'both' {
  $count = Save-ScenarioLast @($a, $b) @() $dest
  [ordered]@{ Count = $count; Files = (Names $dest) }
}
Try-Case 'survive' {
  $count = Save-ScenarioLast @($a, $b) @('exlib/sample-fails') $dest
  [ordered]@{ Count = $count; Files = (Names $dest) }
}
Try-Case 'printed' {
  $lines = @(Save-ScenarioLast @($a, $b) @() $dest 6>&1 | ForEach-Object { "$_" })
  @($lines | Where-Object { $_ -like 'recordings:*' })
}
Try-Case 'none' {
  $none = Join-Path $RepoRoot 'last-none'
  $count = Save-ScenarioLast @($c) @() $none
  [ordered]@{ Count = $count; Folder = (Test-Path (Join-Path $none 'recordings')) }
}
""")
        return self.results

    def test_the_recordings_of_every_shard_land_in_the_folder(self):
        # Fails when only the first shard's data path is copied: exlib-sample-2.json is then missing.
        got = self.results_of()["both"]
        self.assertEqual(2, got["Count"])
        self.assertEqual(["recordings/exlib-sample-2.json", "recordings/exlib-sample.json"], sorted(as_list(got["Files"])))

    def test_the_recordings_survive_the_schematic_step(self):
        # Fails when the recordings are copied before the folder is emptied: the schematic step then removes them.
        got = self.results_of()["survive"]
        self.assertEqual(2, got["Count"])
        self.assertEqual(["recordings/exlib-sample-2.json", "recordings/exlib-sample.json",
                          "scenario-exlib-sample-fails.json"], sorted(as_list(got["Files"])))

    def test_the_run_prints_the_recording_count_and_folder(self):
        # Fails when the Write-Host line is removed from Save-ScenarioLast.
        self.assertEqual(["recordings: 2 in .game/.scenario-last/recordings"], as_list(self.results_of()["printed"]))

    def test_no_recording_makes_no_folder(self):
        # Fails when the recordings folder is made unconditionally.
        got = self.results_of()["none"]
        self.assertEqual(0, got["Count"])
        self.assertFalse(got["Folder"])


if __name__ == "__main__":
    unittest.main()
