"""Tests for EXMOD_BUILD_LOCK: Invoke-WithBuildLock in exmod.ps1, the dispatcher's hold, and the holds
Invoke-Check, Invoke-Test and the provisioning commands take around their building parts. Run through
pwsh ($PWSH, PATH, or the checkout's .dotnet/tools); skipped when no pwsh is found or off POSIX."""

import fcntl
import json
import os
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import threading
import time
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import PWSH, exmod_env, exmod_script, touch  # noqa: E402

NOW_MS = "[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()"
PROBE = r"""
function Get-LockState { & flock -n -s $env:EXMOD_BUILD_LOCK true; if ($LASTEXITCODE -eq 0) { 'free' } else { 'held' } }
"""


def executable(path, text):
    touch(path, text)
    os.chmod(path, os.stat(path).st_mode | stat.S_IEXEC)


class LockCase(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.ws = os.path.join(self.tmp, "ws")
        self.repo = os.path.join(self.ws, "repo")
        touch(os.path.join(self.ws, "exmod.workspace.json"), "{}")
        touch(os.path.join(self.repo, "exmod.json"), "{}")
        self.lock = os.path.join(self.tmp, "build.lock")
        self.stamps = os.path.join(self.tmp, "stamps.txt")

    def env(self, lock=True, wait=None, **extra):
        env = exmod_env(os.path.join(self.tmp, "local"), TEST_REPO=self.repo, TEST_STAMPS=self.stamps,
                        TEST_LOCK=self.lock, **extra)
        for name in ("EXMOD_BUILD_LOCK", "EXMOD_BUILD_LOCK_WAIT", "EXMOD_BUILD_LOCK_HELD", "EXLIB_WRITE_GOLDENS"):
            env.pop(name, None)
        if lock:
            env["EXMOD_BUILD_LOCK"] = self.lock
        if wait is not None:
            env["EXMOD_BUILD_LOCK_WAIT"] = str(wait)
        return env

    def start(self, body, **kw):
        assert PWSH
        return subprocess.Popen([PWSH, "-NoProfile", "-NonInteractive", "-Command", exmod_script(PROBE + body)],
                                env=self.env(**kw), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                start_new_session=True)

    def run_body(self, body, timeout=60, **kw):
        proc = self.start(body, **kw)
        try:
            out, _ = proc.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            os.killpg(proc.pid, signal.SIGKILL)
            raise
        return proc.returncode, out

    def stamp_lines(self):
        with open(self.stamps) as f:
            return f.read().split()

    def hold_lock(self, seconds):
        """Holds an exclusive flock on the lock file for `seconds`; returns the time it let go."""
        fd = os.open(self.lock, os.O_CREAT | os.O_RDWR)
        fcntl.flock(fd, fcntl.LOCK_EX)
        released = []

        def release():
            time.sleep(seconds)
            released.append(time.time())
            fcntl.flock(fd, fcntl.LOCK_UN)
            os.close(fd)

        t = threading.Thread(target=release)
        t.start()
        self.addCleanup(t.join)
        return released


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "the lock is flock(2)")
class BuildLockTests(LockCase):
    def test_a_held_lock_delays_the_body_until_it_is_released(self):
        # Fails if the take is skipped.
        released = self.hold_lock(2)
        code, out = self.run_body(f"Invoke-WithBuildLock {{ Add-Content $env:TEST_STAMPS ({NOW_MS}) }}")
        self.assertEqual(0, code, out)
        entered = int(self.stamp_lines()[0]) / 1000
        self.assertGreaterEqual(entered, released[0] - 0.05, out)

    def test_the_lock_is_exclusive_while_the_body_runs(self):
        # Fails if the helper takes a shared lock.
        code, out = self.run_body("Invoke-WithBuildLock { Add-Content $env:TEST_STAMPS (Get-LockState) }")
        self.assertEqual(0, code, out)
        self.assertEqual(["held"], self.stamp_lines(), out)
        self.assertIn("build lock: taken " + self.lock, out)
        self.assertIn("build lock: released " + self.lock, out)

    def test_two_bodies_started_together_do_not_overlap(self):
        # Fails if the helper takes a shared lock.
        body = (f"Invoke-WithBuildLock {{ Add-Content $env:TEST_STAMPS \"s $({NOW_MS})\"; Start-Sleep 1; "
                f"Add-Content $env:TEST_STAMPS \"e $({NOW_MS})\" }}")
        procs = [self.start(body), self.start(body)]
        for p in procs:
            out, _ = p.communicate(timeout=90)
            self.assertEqual(0, p.returncode, out)
        with open(self.stamps) as f:
            rows = sorted((int(ms), kind) for kind, ms in (l.split() for l in f.read().splitlines() if l.strip()))
        self.assertEqual(["s", "e", "s", "e"], [kind for _, kind in rows])

    def test_a_wait_that_runs_out_exits_non_zero_with_the_not_taken_message(self):
        # Fails if EXMOD_BUILD_LOCK_WAIT is ignored.
        self.hold_lock(30)
        started = time.time()
        code, out = self.run_body("Invoke-WithBuildLock { Add-Content $env:TEST_STAMPS ran }", wait=1, timeout=20)
        self.assertNotEqual(0, code, out)
        self.assertIn("build lock " + self.lock + " not taken in 1 s", out)
        self.assertLess(time.time() - started, 20)
        self.assertFalse(os.path.exists(self.stamps), out)

    def test_a_child_exmod_command_passes_through_its_parents_hold(self):
        # Fails if EXMOD_BUILD_LOCK_HELD is not set.
        script = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "exmod.ps1")
        body = (f"Invoke-WithBuildLock {{ & '{PWSH}' -NoProfile -File '{script}' -RepoRoot $env:TEST_REPO clean; "
                "Add-Content $env:TEST_STAMPS \"child $LASTEXITCODE\" }")
        code, out = self.run_body(body, wait=2, timeout=60)
        self.assertEqual(0, code, out)
        self.assertEqual(["child", "0"], self.stamp_lines(), out)
        self.assertNotIn("not taken", out)

    def test_the_variable_unset_takes_no_lock_at_the_workspace_default_path(self):
        # Fails if an unset variable falls back to the workspace's .exmods-build.lock, which is held here.
        fd = os.open(os.path.join(self.ws, ".exmods-build.lock"), os.O_CREAT | os.O_RDWR)
        self.addCleanup(os.close, fd)
        fcntl.flock(fd, fcntl.LOCK_EX)
        code, out = self.run_body("Invoke-WithBuildLock { Add-Content $env:TEST_STAMPS ran }", lock=False, timeout=30)
        self.assertEqual(0, code, out)
        self.assertEqual(["ran"], self.stamp_lines(), out)
        self.assertNotIn("build lock:", out)

    def test_every_command_but_the_split_and_exempt_ones_runs_held(self):
        # Fails if the dispatcher wraps nothing (probe free), or wraps render (render held).
        body = r"""
Add-ExmodCommand -Group source -Name probe -Summary 's' -Detail 'd' -Action { Add-Content $env:TEST_STAMPS ('probe ' + (Get-LockState)) }
Add-ExmodCommand -Group source -Name render -Summary 's' -Detail 'd' -Action { Add-Content $env:TEST_STAMPS ('render ' + (Get-LockState)) }
Invoke-ExmodCommand (Resolve-ExmodCommand 'probe') @()
Invoke-ExmodCommand (Resolve-ExmodCommand 'render') @()
"""
        code, out = self.run_body(body)
        self.assertEqual(0, code, out)
        self.assertEqual(["probe", "held", "render", "free"], self.stamp_lines(), out)

    def test_smoke_and_scenario_are_not_held_whole_by_the_dispatcher(self):
        # Fails if smoke or scenario is left off $NoBuildLockCommands (held).
        body = r"""
foreach ($name in 'smoke', 'scenario') {
  Invoke-ExmodCommand ([pscustomobject]@{ Name = $name; Action = { Add-Content $env:TEST_STAMPS ('cmd ' + (Get-LockState)) } }) @()
}
"""
        code, out = self.run_body(body)
        self.assertEqual(0, code, out)
        self.assertEqual(["cmd", "free", "cmd", "free"], self.stamp_lines(), out)

    def test_waiters_taking_turns_do_not_starve_the_body(self):
        # Fails if the take polls with a short non-blocking attempt.
        loop = f"while true; do flock -w 30 -o '{self.lock}' sleep 1; done"
        loops = [subprocess.Popen(["bash", "-c", loop], start_new_session=True) for _ in range(2)]
        for p in loops:
            self.addCleanup(lambda p=p: (os.killpg(p.pid, signal.SIGKILL), p.wait()))
        time.sleep(1)
        started = time.time()
        code, out = self.run_body("Invoke-WithBuildLock { Add-Content $env:TEST_STAMPS ran }", timeout=60)
        self.assertEqual(0, code, out)
        self.assertLess(time.time() - started, 10 + 4, out)
        self.assertEqual(["ran"], self.stamp_lines(), out)

    @staticmethod
    def kill_group(proc):
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass

    def test_a_killed_body_frees_the_lock(self):
        # Fails if the helper outlives its stdin.
        marker = os.path.join(self.tmp, "inside")
        proc = self.start(f"Invoke-WithBuildLock {{ Set-Content '{marker}' $PID; Start-Sleep 60 }}")
        self.addCleanup(self.kill_group, proc)
        deadline = time.time() + 30
        while not os.path.exists(marker) and time.time() < deadline:
            time.sleep(0.1)
        self.assertTrue(os.path.exists(marker))
        time.sleep(0.2)
        with open(marker) as f:
            pid = int(f.read())
        os.kill(pid, signal.SIGKILL)
        proc.wait()
        deadline = time.time() + 2
        free = False
        while time.time() < deadline and not free:
            free = subprocess.run(["flock", "-n", self.lock, "true"]).returncode == 0
            time.sleep(0.05)
        self.assertTrue(free)


CHECK = r"""
$Manifest = [pscustomobject]@{ series = @('1.22') }
$CurrentGameVersion = '1.22'
function Rec([string]$Name) { Add-Content $env:TEST_STAMPS "$Name $(Get-LockState)" }
function Invoke-Build { Rec build }
function Invoke-Verify { Rec verify }
function Invoke-Smoke { Invoke-WithBuildLock { Rec smokestage }; Rec smokeboot }
function Invoke-ExmodParallelBuild { Rec testbuild; return @() }
function Resolve-DotnetHost { $env:TEST_STUB }
function Get-ExmodTestProjects {
  [ordered]@{ a = [pscustomobject]@{ Project = 'A.Tests'; Proj = $env:TEST_PROJ; Series = $Manifest.series } }
}
try { Invoke-Check @('-NoFormat') } catch { Write-Host "check threw: $_" }
"""

CHECK_STUB = """#!/bin/sh
[ "$1" = test ] || exit 0
if flock -n -s "$EXMOD_BUILD_LOCK" true; then echo "testrun free" >> "$TEST_STAMPS"; else echo "testrun held" >> "$TEST_STAMPS"; fi
echo "Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1"
"""


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "the lock is flock(2)")
class StepHoldTests(LockCase):
    def setUp(self):
        super().setUp()
        self.stub = os.path.join(self.tmp, "dotnet")
        executable(self.stub, CHECK_STUB)
        self.proj = os.path.join(self.repo, "tests", "A.Tests", "A.Tests.csproj")

    def test_check_holds_build_verify_test_build_and_smoke_and_runs_the_tests_free(self):
        # Fails if the test run sits inside the hold (testrun held), a step is run without one, or Invoke-Smoke
        # is called inside a hold of check's own (smokeboot held).
        code, out = self.run_body(CHECK, TEST_STUB=self.stub, TEST_PROJ=self.proj)
        self.assertEqual(0, code, out)
        self.assertEqual(["build held", "verify held", "testbuild held", "testrun free", "smokestage held",
                          "smokeboot free"],
                         [" ".join(self.stamp_lines()[i:i + 2]) for i in range(0, 12, 2)], out)
        self.assertEqual(3, out.count("build lock: taken"), out)
        self.assertIn("test phase:", out)

    def test_two_test_phases_in_one_checkout_take_turns(self):
        # Fails if the per-checkout test-phase lock is gone.
        slow = os.path.join(self.tmp, "slow")
        executable(slow, '#!/bin/sh\n[ "$1" = test ] || exit 0\necho "start" >> "$TEST_STAMPS"\nsleep 1\n'
                         'echo "end" >> "$TEST_STAMPS"\n'
                         'echo "Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1"\n')
        dll = os.path.join(self.repo, "tests", "A.Tests", "bin", "Debug", "net10.0", "A.Tests.dll")
        touch(dll)
        body = ("$Manifest = [pscustomobject]@{ series = @('1.22') }; $CurrentGameVersion = '1.22'; "
                "function Resolve-DotnetHost { $env:TEST_STUB }; "
                "function Get-ExmodTestProjects { [ordered]@{ a = [pscustomobject]@{ Project = 'A.Tests'; "
                "Proj = $env:TEST_PROJ; Series = $Manifest.series } } }; Invoke-Test @('all', '-NoBuild')")
        procs = [self.start(body, TEST_STUB=slow, TEST_PROJ=self.proj) for _ in range(2)]
        for p in procs:
            out, _ = p.communicate(timeout=90)
            self.assertEqual(0, p.returncode, out)
        self.assertEqual(["start", "end", "start", "end"], self.stamp_lines())

    def test_the_coverage_run_holds_the_lock_for_its_dotnet_test(self):
        # Fails if the coverage collect runs outside the hold.
        tools = os.path.join(self.tmp, "dn", "tools")
        executable(os.path.join(tools, "dotnet-coverage"),
                   '#!/bin/sh\nif flock -n -s "$EXMOD_BUILD_LOCK" true; then echo cov free >> "$TEST_STAMPS"; '
                   'else echo cov held >> "$TEST_STAMPS"; fi\nexit 1\n')
        body = ("function Get-ExmodDotnetDir { $env:TEST_DOTNET }; function Resolve-DotnetHost { 'stub-dotnet' }; "
                "function stub-dotnet { }; function Get-ExmodTestProjects { [ordered]@{} }; "
                "function Get-ExmodSolution { 'demo.sln' }; try { Invoke-Test @('-Coverage') } catch { }")
        _, out = self.run_body(body, TEST_DOTNET=os.path.join(self.tmp, "dn"))
        self.assertEqual(["cov", "held"], self.stamp_lines(), out)


PROVISION_DOTNET = r"""
function Get-ExmodDotnetDir { $env:TEST_DOTNET }
if ($env:TEST_GAP -ne '1') {
  function Get-MissingRuntimeMajors { Add-Content $env:TEST_STAMPS ("resolve " + (Get-LockState)); @('10') }
  Resolve-DotnetHost @('1.22') | Out-Null
} else {
  Invoke-ProvisionDotnet @('-Version', '1.22')
}
"""

INSTALLER = '#!/bin/sh\nif flock -n -s "$EXMOD_BUILD_LOCK" true; then echo "install free" >> "$TEST_STAMPS"; else echo "install held" >> "$TEST_STAMPS"; fi\n'


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "the lock is flock(2)")
class ProvisionHoldTests(LockCase):
    def provision(self, gap):
        dn = os.path.join(self.tmp, "dn")
        executable(os.path.join(dn, ".cache", "dotnet-install.sh"), INSTALLER)
        if not gap:
            os.makedirs(os.path.join(dn, "sdk", "10.0.1"))
            os.makedirs(os.path.join(dn, "shared", "Microsoft.NETCore.App", "10.0.1"))
        code, out = self.run_body(PROVISION_DOTNET, TEST_DOTNET=dn, TEST_GAP="1" if gap else "0")
        self.assertEqual(0, code, out)
        return self.stamp_lines() if os.path.exists(self.stamps) else []

    def test_installing_a_dotnet_takes_the_hold(self):
        # Fails if Install-Dotnet runs its installer outside the hold.
        lines = self.provision(gap=True)
        self.assertEqual(["install", "held"] * 2, lines)

    def test_resolving_with_nothing_to_install_does_not_take_the_hold(self):
        # Fails if the hold wraps the whole of Resolve-DotnetHost.
        lines = self.provision(gap=False)
        self.assertEqual(["resolve", "free"], lines)

    def test_the_game_extract_leaves_the_old_install_until_the_rename(self):
        # Fails if the archive is extracted over the old folder.
        dest = os.path.join(self.tmp, "game")
        touch(os.path.join(dest, "old.txt"), "old")
        cache = os.path.join(self.ws, ".game", ".cache")
        payload = os.path.join(self.tmp, "payload")
        touch(os.path.join(payload, "VintagestoryAPI.dll"), "api")
        os.makedirs(cache)
        subprocess.run(["tar", "-czf", os.path.join(cache, "vs_server_linux-x64_1.22.6.tar.gz"), "-C", payload, "."],
                       check=True)
        body = ("function Publicize-GameApi([string]$ApiDll) { Add-Content $env:TEST_STAMPS "
                "(\"old \" + (Test-Path (Join-Path $env:TEST_DEST 'old.txt'))) }; "
                "Invoke-ProvisionGame @('-Version', '1.22.6', '-Dest', $env:TEST_DEST)")
        code, out = self.run_body(body, TEST_DEST=dest, lock=False)
        self.assertEqual(0, code, out)
        self.assertEqual(["old", "True"], self.stamp_lines(), out)
        self.assertFalse(os.path.exists(os.path.join(dest, "old.txt")))
        self.assertTrue(os.path.exists(os.path.join(dest, "VintagestoryAPI.dll")))
        with open(os.path.join(dest, ".vsversion")) as f:
            self.assertEqual("1.22.6", f.read())
        self.assertFalse(os.path.exists(dest + ".part"))


SMOKE_STAGE = r"""
function Rec([string]$Name) { Add-Content $env:TEST_STAMPS "$Name $(Get-LockState)" }
function Resolve-SmokeServer { Rec stage; return $env:TEST_REPO }
function Get-ScratchDefaultModDirs { return @($env:TEST_REPO) }
function Get-ScenarioSources { return @([pscustomobject]@{ Dir = $env:TEST_REPO; ModId = 'exlib' }) }
function Get-ScenarioNames { return @('exlib/sample') }
function Build-ScenarioExlibMod { Rec exlibbuild; return 'exlib-dir' }
function Build-ScenarioLiveMod { Rec livebuild; return 'live-dir' }
function Get-ScenarioDefaultModDirs { return @() }
function Get-ScenarioModDirs { return @() }
function New-ScratchServer { Rec newserver; return [pscustomobject]@{ Root = $env:TEST_REPO; Proc = $null; Port = $null } }
function Start-ScratchServer { Rec boot; throw 'stop here' }
function Remove-ScratchServer { }
"""


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "the lock is flock(2)")
class StageAndBootTests(LockCase):
    def stamps_of(self, call, count):
        code, out = self.run_body(SMOKE_STAGE + call, EXMOD_PORTS_DIR=os.path.join(self.tmp, "ports"))
        self.assertEqual(0, code, out)
        lines = self.stamp_lines()
        return [" ".join(lines[i:i + 2]) for i in range(0, 2 * count, 2)], out

    def test_smoke_stages_under_the_lock_and_boots_without_it(self):
        # Fails if Start-ScratchServer runs inside the hold (boot held).
        got, out = self.stamps_of("try { Invoke-Smoke @() } catch { }; exit 0", 3)
        self.assertEqual(["stage held", "newserver held", "boot free"], got, out)

    def test_scenario_builds_and_stages_under_the_lock_and_boots_without_it(self):
        # Fails if Start-ScratchServer runs inside the hold (boot held).
        got, out = self.stamps_of("try { Invoke-Scenario @() } catch { }; exit 0", 5)
        self.assertEqual(["exlibbuild held", "livebuild held", "stage held", "newserver held", "boot free"], got, out)

    def ports_held(self, call):
        stub = ("function Start-ScratchServer { Get-ChildItem $env:EXMOD_PORTS_DIR -Filter '*.lock' | ForEach-Object { "
                "& flock -n $_.FullName true; Add-Content $env:TEST_STAMPS $(if ($LASTEXITCODE -eq 0) { 'ports-free' } else { 'ports-held' }) }; "
                "throw 'stop here' }\n")
        code, out = self.run_body(SMOKE_STAGE + stub + call, EXMOD_PORTS_DIR=os.path.join(self.tmp, "ports"))
        self.assertEqual(0, code, out)
        return [l for l in self.stamp_lines() if l.startswith("ports-")], out

    def test_smoke_holds_its_port_reservation_through_the_boot(self):
        # Fails if Invoke-Smoke calls Unlock-ServerPorts before Start-ScratchServer.
        got, out = self.ports_held("try { Invoke-Smoke @() } catch { }; exit 0")
        self.assertIn("ports-held", got, out)

    def test_scenario_holds_its_port_reservation_through_the_boot(self):
        # Fails if Invoke-Scenario calls Unlock-ServerPorts before Start-ScratchServer.
        got, out = self.ports_held("try { Invoke-Scenario @() } catch { }; exit 0")
        self.assertIn("ports-held", got, out)


if __name__ == "__main__":
    unittest.main()
