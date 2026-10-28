"""Tests for the memory budget (Get-ExmodMemoryBudget, Lock-ExmodMemory, Unlock-ExmodMemory) in exmod.ps1. Run
through pwsh ($PWSH, PATH, or the nearest .dotnet/tools above the checkout); skipped when no pwsh is found or off
POSIX."""

import fcntl
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import time
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import PWSH, exmod_env, exmod_script, touch  # noqa: E402

LOAD = 40


class Run:
    """A pwsh child whose output lines are collected as they arrive."""

    def __init__(self, body, env):
        self.proc = subprocess.Popen([PWSH, "-NoProfile", "-NonInteractive", "-Command", exmod_script(body)],
                                     env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                     start_new_session=True)
        self.lines = []
        self.lock = threading.Lock()
        threading.Thread(target=self.pump, daemon=True).start()

    def pump(self):
        for line in self.proc.stdout:
            with self.lock:
                self.lines.append(line.rstrip("\n"))

    def text(self):
        with self.lock:
            return "\n".join(self.lines)

    def has(self, needle):
        return needle in self.text()

    def wait_for(self, needle, timeout=LOAD):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.has(needle):
                return
            time.sleep(0.05)
        raise AssertionError(f"no {needle!r} in {timeout} s:\n{self.text()}")

    def never(self, needle, seconds):
        deadline = time.time() + seconds
        while time.time() < deadline:
            if self.has(needle):
                raise AssertionError(f"unexpected {needle!r}:\n{self.text()}")
            time.sleep(0.05)

    def finish(self, timeout=LOAD):
        self.proc.wait(timeout=timeout)
        return self.text()


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "the budget is flock(2)")
class MemoryTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.runs = []
        self.fds = []
        self.addCleanup(self.cleanup)
        self.repo = os.path.join(self.tmp, "ws", "repo")
        touch(os.path.join(self.tmp, "ws", "exmod.workspace.json"), "{}")
        touch(os.path.join(self.repo, "exmod.json"), "{}")
        self.mem = os.path.join(self.tmp, "memory")
        self.meminfo = os.path.join(self.tmp, "meminfo")
        self.set_available(64)

    def cleanup(self):
        for r in self.runs:
            try:
                os.killpg(r.proc.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            r.proc.wait()
        for fd in self.fds:
            os.close(fd)
        shutil.rmtree(self.tmp)

    def set_available(self, gib):
        touch(self.meminfo, f"MemTotal:       67108864 kB\nMemAvailable:   {gib * 1048576} kB\n")

    def start(self, body, budget=4, **env):
        env = {"EXMOD_MEMORY_DIR": self.mem, "EXMOD_MEMORY_GB": str(budget), "EXMOD_MEMINFO": self.meminfo, **env}
        env = exmod_env(os.path.join(self.tmp, "local"), TEST_REPO=self.repo, **env)
        run = Run(body, env)
        self.runs.append(run)
        return run

    def hold(self, name):
        """Takes flock on a file of the memory dir from this process; returns its descriptor."""
        os.makedirs(self.mem, exist_ok=True)
        fd = os.open(os.path.join(self.mem, name), os.O_CREAT | os.O_RDWR)
        fcntl.flock(fd, fcntl.LOCK_EX)
        self.fds.append(fd)
        return fd

    def release(self, fd):
        os.close(fd)
        self.fds.remove(fd)

    def ask(self, gb, label, hold_s=0, **kw):
        return self.start(f"$r = Lock-ExmodMemory {gb} '{label}'; Start-Sleep -Milliseconds {int(hold_s * 1000)}; "
                          "Unlock-ExmodMemory $r", **kw)

    def rows(self):
        with open(os.path.join(self.mem, "waits.tsv")) as f:
            return [ln.split("\t") for ln in f.read().splitlines()]

    def test_a_share_waits_while_the_files_it_needs_are_held(self):
        # Fails if a grant does not count the free files (the second share is granted beside the holder of 3 of 4).
        fds = [self.hold(f"{i}.lock") for i in range(3)]
        w = self.ask(2, "w")
        w.wait_for("waits for 2 GiB")
        self.assertIn("1 of 4 GiB free", w.text())
        w.never("took", 3)
        for fd in fds:
            self.release(fd)
        w.wait_for("took 2 GiB of 4", 5)
        self.assertIn("released 2 GiB", w.finish())

    def test_two_runspaces_of_one_process_exclude_each_other(self):
        # Fails if the files are opened with FileShare.Read (both runspaces of the process hold their share at once).
        body = ("$lock = ${function:Lock-ExmodMemory}.ToString(); $unlock = ${function:Unlock-ExmodMemory}.ToString(); "
                "1..2 | ForEach-Object -Parallel { ${function:Lock-ExmodMemory} = $using:lock; "
                "${function:Unlock-ExmodMemory} = $using:unlock; $r = Lock-ExmodMemory 2 \"u$_\" 6>$null; "
                "Write-Output \"in $_ $([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())\"; Start-Sleep -Seconds 2; "
                "Write-Output \"out $_ $([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())\"; "
                "Unlock-ExmodMemory $r 6>$null } -ThrottleLimit 2")
        out = self.start(body, budget=3).finish()
        stamps = {}
        for ln in out.splitlines():
            parts = ln.split()
            if len(parts) == 3 and parts[0] in ("in", "out"):
                stamps[(parts[0], parts[1])] = int(parts[2])
        self.assertEqual(4, len(stamps), out)
        first, second = sorted("12", key=lambda u: stamps[("in", u)])
        self.assertGreaterEqual(stamps[("in", second)], stamps[("out", first)], out)

    def test_a_killed_holder_frees_its_share(self):
        # Fails if the files are held by a child that outlives the process (a setsid flock): the waiter stays waiting.
        h = self.ask(4, "holder", hold_s=60)
        h.wait_for("took 4 GiB of 4")
        w = self.ask(1, "w")
        w.wait_for("waits for 1 GiB")
        os.killpg(h.proc.pid, signal.SIGKILL)
        start = time.time()
        w.wait_for("took 1 GiB of 4", 3)
        self.assertLess(time.time() - start, 3.5)

    def test_a_share_waits_for_memavailable_and_goes_on_when_it_rises(self):
        # Fails if MemAvailable is not checked (the share is granted at once beside the holder of 1).
        self.set_available(1)
        fd = self.hold("0.lock")
        w = self.ask(2, "w", budget=8)
        w.wait_for("waits for 2 GiB")
        self.assertIn("MemAvailable 1.0 GiB", w.text())
        w.never("took", 3)
        self.set_available(8)
        start = time.time()
        w.wait_for("took 2 GiB of 8", 3)
        self.assertLess(time.time() - start, 2.5)
        self.release(fd)

    def test_a_share_is_granted_at_once_when_nothing_else_is_held(self):
        # Fails if the nothing-else-held exception is gone (the share waits for MemAvailable it never gets).
        self.set_available(1)
        w = self.ask(2, "w", budget=8)
        w.wait_for("took 2 GiB of 8", LOAD)
        self.assertNotIn("waits for", w.finish())

    def test_a_large_share_is_not_overtaken_by_a_small_one_that_arrives_after_it(self):
        # Fails if the turn and the files kept between probes are given up (the small share is granted at once).
        fd = self.hold("0.lock")
        big = self.ask(4, "big", hold_s=2)
        big.wait_for("waits for 4 GiB")
        small = self.ask(1, "small")
        small.never("took", 5)
        self.release(fd)
        big.wait_for("took 4 GiB of 4", 5)
        small.wait_for("took 1 GiB of 4", 15)
        big.finish()
        small.finish()
        self.assertEqual(["big", "small"], [r[7] for r in self.rows()])

    def test_a_share_above_the_budget_is_cut_to_it(self):
        # Fails if the share is not cut (it asks for 9 of 4 files and is never granted).
        out = self.ask(9, "w").finish()
        self.assertIn("memory: w asks 9 GiB, over the 4 GiB budget; taking 4", out)
        self.assertIn("took 4 GiB of 4", out)

    def test_a_grant_appends_one_row_of_nine_fields(self):
        # Fails if no row is written.
        self.ask(2, "lab", budget=5).finish()
        rows = self.rows()
        self.assertEqual(1, len(rows))
        self.assertEqual(9, len(rows[0]))
        _, waited, k, budget, free, avail, prio, label, cwd = rows[0]
        self.assertEqual(("2", "5", "5", "64.0", "0", "lab"), (k, budget, free, avail, prio, label))
        self.assertGreaterEqual(float(waited), 0)
        self.assertTrue(os.path.isabs(cwd))

    def test_without_a_meminfo_file_there_is_no_budget_and_no_line(self):
        # Fails if a missing meminfo file throws or prints (the call returns a reservation or an error).
        w = self.start("$r = Lock-ExmodMemory 2 'w'; \"null=$($null -eq $r)\"; Unlock-ExmodMemory $r; "
                       "\"budget=$($null -eq (Get-ExmodMemoryBudget))\"", EXMOD_MEMINFO=os.path.join(self.tmp, "none"))
        self.assertEqual(["null=True", "budget=True"], w.finish().splitlines()[-2:])
        self.assertNotIn("memory:", w.text())

    def test_the_budget_is_memtotal_less_ten_gib_when_not_set(self):
        # Fails if the 10 GiB or the floor is wrong (54 GiB total gives 44).
        touch(self.meminfo, "MemTotal:       56800000 kB\nMemAvailable:   1000 kB\n")
        env = exmod_env(os.path.join(self.tmp, "local"), TEST_REPO=self.repo, EXMOD_MEMINFO=self.meminfo)
        env.pop("EXMOD_MEMORY_GB", None)
        r = Run("Get-ExmodMemoryBudget", env)
        self.assertEqual("44", r.finish().splitlines()[-1])

    def test_a_priority_waiter_is_granted_before_a_plain_one_that_arrived_first(self):
        # Fails if a waiter without priority does not look at the priority files (the plain one is granted first).
        fds = [self.hold(f"{i}.lock") for i in range(2)]
        plain = self.ask(1, "plain", budget=2)
        plain.wait_for("waits for 1 GiB")
        prio = self.ask(1, "prio", budget=2, EXMOD_MEMORY_PRIORITY="1")
        time.sleep(6)
        for fd in fds:
            self.release(fd)
        prio.wait_for("took 1 GiB of 2")
        plain.wait_for("took 1 GiB of 2")
        prio.finish()
        plain.finish()
        self.assertEqual(["prio", "plain"], [r[7] for r in self.rows()])

    def test_a_priority_waiter_waits_for_a_priority_file_when_all_sixteen_are_held(self):
        # Fails if the waiter goes on without a priority file (it is granted while all sixteen are held).
        fds = [self.hold(f"priority-{i}.lock") for i in range(16)]
        w = self.ask(1, "w", EXMOD_MEMORY_PRIORITY="1")
        w.never("took", 8)
        self.release(fds[5])
        w.wait_for("took 1 GiB of 4", 4)

    def test_asking_while_the_build_lock_is_held_throws(self):
        # Fails if the build-lock check is gone (the call returns a reservation).
        out = self.start("try { Lock-ExmodMemory 1 'w' | Out-Null; 'no throw' } catch { \"threw: $($_.Exception.Message)\" }",
                         EXMOD_BUILD_LOCK_HELD="1").finish()
        self.assertIn("threw: memory share asked for by w while the build lock is held", out)

    def test_a_throw_inside_the_call_disposes_what_it_held_and_a_later_call_is_granted(self):
        # Fails if the streams already held are not disposed (the later call of 3 waits on the file the first kept).
        os.makedirs(os.path.join(self.mem, "1.lock"))
        body = (f"[void][GC]::TryStartNoGCRegion(100MB); try {{ Lock-ExmodMemory 3 'a' | Out-Null; 'no throw' }} catch {{ 'threw' }}; "
                f"Remove-Item -LiteralPath '{self.mem}/1.lock' -Recurse; $r = Lock-ExmodMemory 3 'b'; 'granted'")
        out = self.start(body, budget=3).finish(30)
        self.assertIn("threw", out.splitlines())
        self.assertIn("memory: b took 3 GiB of 3", out)
        self.assertEqual("granted", out.splitlines()[-1])

    def test_a_waiter_behind_the_turn_says_so_once_and_calls_the_callback(self):
        # Fails if the turn wait prints no waits line or skips the callback (mutant: the `& $stall 'the turn'` call removed).
        fd = self.hold("turn.lock")
        w = self.start("$r = Lock-ExmodMemory 1 'w' -OnWait { Write-Host 'on-wait' }; Unlock-ExmodMemory $r")
        w.wait_for("memory: w waits for the turn at")
        time.sleep(3)
        self.release(fd)
        out = w.finish()
        self.assertEqual(1, out.count("waits for the turn"), out)
        self.assertEqual(1, out.count("on-wait"), out)

    def test_a_waiter_without_a_priority_file_says_so(self):
        # Fails if the priority-file wait prints no waits line (mutant: the `& $stall 'a priority file'` call removed).
        fds = [self.hold(f"priority-{i}.lock") for i in range(16)]
        w = self.ask(1, "w", EXMOD_MEMORY_PRIORITY="1")
        w.wait_for("memory: w waits for a priority file at")
        self.release(fds[0])
        w.finish()

    def test_a_slot_file_that_cannot_be_created_throws_instead_of_waiting(self):
        # Fails if a missing directory counts as a held file (mutant: the FileNotFoundException, DirectoryNotFoundException catch removed).
        os.makedirs(self.mem)
        os.symlink(os.path.join(self.tmp, "none", "0.lock"), os.path.join(self.mem, "0.lock"))
        out = self.start("try { Lock-ExmodMemory 1 'w' | Out-Null; 'no throw' } catch { 'threw' }").finish(15)
        self.assertEqual("threw", out.splitlines()[-1], out)

    def test_a_throw_inside_the_call_frees_the_priority_file(self):
        # Fails if the priority file is not disposed on a throw (mutant: `$priorityFile.Dispose()` removed from the finally).
        os.makedirs(os.path.join(self.mem, "1.lock"))
        body = ("[void][GC]::TryStartNoGCRegion(100MB); try { Lock-ExmodMemory 3 'a' | Out-Null } catch { }; "
                f"try {{ [IO.File]::Open('{self.mem}/priority-0.lock', 'OpenOrCreate', 'ReadWrite', 'None').Dispose(); 'free' }} "
                "catch { 'held' }")
        out = self.start(body, budget=3, EXMOD_MEMORY_PRIORITY="1").finish(30)
        self.assertEqual("free", out.splitlines()[-1], out)

    def test_a_released_share_can_be_taken_again(self):
        # Fails if Unlock does not close the streams (the second call waits on the first's files).
        out = self.start("[void][GC]::TryStartNoGCRegion(100MB); $r = Lock-ExmodMemory 2 'a'; Unlock-ExmodMemory $r; Unlock-ExmodMemory $r; Unlock-ExmodMemory $null; "
                         "$r = Lock-ExmodMemory 2 'b'; 'again'", budget=2).finish(30)
        self.assertEqual(1, out.count("memory: a released 2 GiB"), out)
        self.assertEqual("again", out.splitlines()[-1])


@unittest.skipUnless(PWSH, "pwsh not found")
class HarnessEnvTests(unittest.TestCase):
    def test_the_callers_memory_settings_are_not_passed_on(self):
        # Fails if exmod_env passes this process's budget, heavy or priority setting (mutant: os.environ used whole).
        saved = {k: os.environ.get(k) for k in ("EXMOD_MEMORY_GB", "EXMOD_HEAVY", "EXMOD_MEMORY_PRIORITY")}
        self.addCleanup(lambda: [os.environ.pop(k, None) if v is None else os.environ.__setitem__(k, v)
                                 for k, v in saved.items()])
        os.environ.update(EXMOD_MEMORY_GB="20", EXMOD_HEAVY="1", EXMOD_MEMORY_PRIORITY="1")
        with tempfile.TemporaryDirectory() as d:
            env = exmod_env(d)
            self.assertFalse({"EXMOD_MEMORY_GB", "EXMOD_HEAVY", "EXMOD_MEMORY_PRIORITY"} & set(env))
            self.assertEqual("7", exmod_env(d, EXMOD_MEMORY_GB="7")["EXMOD_MEMORY_GB"])


if __name__ == "__main__":
    unittest.main()
