"""Tests for the memory budget (Get-ExmodMemoryBudget, Lock-ExmodMemory, Unlock-ExmodMemory) in exmod.ps1. Run
through pwsh ($PWSH, PATH, or the nearest .dotnet/tools above the checkout); skipped when no pwsh is found or off
POSIX."""

import fcntl
import json
import os
import re
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
        self.proc = os.path.join(self.tmp, "proc")
        os.makedirs(self.proc)
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


    def fake_proc(self, pid, rss_gb, ppid=1):
        """A process table entry under the fake EXMOD_PROC_DIR: parent ppid, resident size rss_gb GiB."""
        touch(os.path.join(self.proc, str(pid), "stat"), f"{pid} (dotnet) S {ppid} 0 0 0\n")
        touch(os.path.join(self.proc, str(pid), "statm"), f"0 {int(rss_gb * 262144)} 0 0 0 0 0\n")

    def book(self, name, gb, pids, charge=None, owner=None):
        """A running reservation of gb GiB whose host is the processes pids, owned by this (live) process unless owner is given."""
        rec = {"owner": os.getpid() if owner is None else owner, "gb": gb, "pids": pids}
        if charge is not None:
            rec["charge"] = charge
        touch(os.path.join(self.mem, f"booked-{name}.json"), json.dumps(rec))

    def dead_pid(self):
        p = subprocess.Popen(["true"])
        p.wait()
        return p.pid

    def files(self, pattern):
        return [f for f in os.listdir(self.mem) if re.fullmatch(pattern, f)] if os.path.isdir(self.mem) else []

    def lockable(self, name):
        """True when this process can take flock on a file of the memory dir within 2 s."""
        fd = os.open(os.path.join(self.mem, name), os.O_CREAT | os.O_RDWR)
        try:
            deadline = time.time() + 2
            while time.time() < deadline:
                try:
                    fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    return True
                except BlockingIOError:
                    time.sleep(0.02)
            return False
        finally:
            os.close(fd)

    def ask_free(self, gb, label, avail, hold_s=0, budget=40, **env):
        """Asks with avail GiB of MemAvailable, no floor, the fake process table and a budget of 40 GiB."""
        self.set_available(avail)
        return self.ask(gb, label, hold_s=hold_s, budget=budget, EXMOD_PROC_DIR=self.proc, EXMOD_MEMORY_FLOOR_GB="0", **env)

    def full_budget(self, n=4):
        return [self.hold(f"{i}.lock") for i in range(n)]

    def ask_real(self, gb, avail, **env):
        """Asks for gb GiB of a fully booked 4 GiB budget with avail GiB of MemAvailable and the fake process table."""
        self.set_available(avail)
        return self.ask(gb, "w", EXMOD_PROC_DIR=self.proc, **env)

    def test_a_share_is_admitted_on_real_use_when_the_bookings_are_full_but_memory_is_available(self):
        # Fails if the real-use rule is gone (the share waits for a file): 40 - 10 floor - 0 growth = 30 GiB.
        self.full_budget()
        self.fake_proc(100, 4)
        self.book("a", 4, [100])
        w = self.ask_real(2, 40)
        w.wait_for("took 2 GiB of 4", 8)
        self.assertIn("(real use: 30 GiB available)", w.text())
        w.finish()

    def test_the_floor_is_kept(self):
        # Fails if the floor is not subtracted: 11 GiB available admits the share of 2.
        self.full_budget()
        self.fake_proc(100, 4)
        self.book("a", 4, [100])
        w = self.ask_real(2, 11)
        w.never("took", 5)

    def test_the_floor_setting_moves_the_floor(self):
        # Fails if EXMOD_MEMORY_FLOOR_GB is ignored: 11 - 8 = 3 GiB is not seen and the share of 2 waits.
        self.full_budget()
        self.fake_proc(100, 4)
        self.book("a", 4, [100])
        w = self.ask_real(2, 11, EXMOD_MEMORY_FLOOR_GB="8")
        w.wait_for("(real use: 3 GiB available)", 8)
        w.finish()

    def test_without_a_process_table_only_the_booked_rule_admits(self):
        # Fails if the real-use rule runs with no process table (it then throws reading the missing table).
        self.full_budget()
        w = self.ask(2, "w")
        w.never("took", 5)
        self.assertEqual([], [ln for ln in w.text().splitlines() if not ln.startswith("memory: w waits for")], w.text())

    def test_a_reservation_leaves_a_record_of_its_share_and_removes_it_on_release(self):
        # Fails if the record is not written or Unlock-ExmodMemory leaves it behind.
        body = (f"$r = Lock-ExmodMemory 2 'a'; [System.IO.File]::ReadAllText($r.Record); Unlock-ExmodMemory $r; "
                f"\"left=$((Get-ChildItem '{self.mem}' -Filter 'booked-*.json').Count)\"")
        out = self.start(body, EXMOD_PROC_DIR=self.proc).finish(30)
        self.assertRegex(out, r'\{"owner":\d+,"gb":2,"charge":2(\.0)?,"pids":\[\]\}')
        self.assertIn("left=0", out)

    def peaks_log(self, label, share, peak, n=5):
        log = os.path.join(self.tmp, f"peaks-{label}.jsonl")
        touch(log, "".join(json.dumps({"label": label, "shareGb": share, "filtered": False, "peakGb": peak}) + "\n" for _ in range(n)))
        return log

    def test_the_real_use_waiter_holds_no_lock_and_no_file_while_it_waits(self):
        # Fails if the waiter keeps turn.lock, a slot file or a booking of its own while it waits (the lock probes time out).
        self.fake_proc(100, 0)
        self.book("a", 8, [100])
        self.set_available(6)
        w = self.start("$r = Lock-ExmodMemory 8 'big' -OnWait { Write-Host 'on-wait' }; Unlock-ExmodMemory $r",
                       budget=40, EXMOD_PROC_DIR=self.proc, EXMOD_MEMORY_FLOOR_GB="0")
        w.wait_for("waits for 8 GiB")
        self.assertIn("6 of 40 GiB free", w.text())
        self.assertTrue(self.lockable("turn.lock"))
        self.assertTrue(self.lockable("0.lock"))
        self.assertEqual([], self.files(r"ask-.*\.json"))
        self.assertEqual([], self.files(r"booked-(?!a\.).*\.json"))
        self.set_available(10)
        w.wait_for("took 8 GiB of 40", 5)
        self.assertEqual(1, w.finish().count("on-wait"), w.text())
        self.assertEqual("6", self.rows()[0][4])

    def test_a_run_whose_charge_fits_is_granted_while_a_large_host_runs(self):
        # Fails if what the running host may still grow to is charged again (the host of 20 GiB at 1 GiB resident leaves 5 - 19 < 2).
        self.fake_proc(100, 1)
        self.book("a", 20, [100])
        w = self.ask_free(2, "w", 5)
        w.wait_for("took 2 GiB of 40", 8)
        self.assertIn("(real use: 5 GiB available)", w.text())
        w.finish()

    def test_a_small_and_a_large_run_are_each_granted_when_their_own_charge_fits(self):
        # Fails if order is restored: a large run leaves the charge of a waiting priority run (8) free, so with 4 GiB room the
        # large run of charge 2 and the small run are held for it.
        self.fake_proc(100, 1)
        self.book("a", 4, [100])
        prio = self.ask_free(8, "prio", 4, EXMOD_MEMORY_PRIORITY="1")
        prio.wait_for("waits for 8 GiB")
        large = self.ask_free(8, "large", 4, EXMOD_MEMORY_LOG=self.peaks_log("large", 8, 1.0))
        large.wait_for("took 8 GiB of 40", 8)
        small = self.ask_free(1, "small", 4)
        small.wait_for("took 1 GiB of 40", 8)
        self.assertNotIn("took", prio.text())
        self.set_available(12)
        prio.wait_for("took 8 GiB of 40", 8)

    def test_a_run_waits_while_room_is_under_its_charge_and_is_granted_when_it_rises(self):
        # Fails if the room is not compared to the charge (always granted): 3 GiB room, charge 4, then 5.
        self.fake_proc(100, 1)
        self.book("a", 4, [100])
        w = self.ask_free(4, "w", 3)
        w.wait_for("waits for 4 GiB")
        w.never("took", 3)
        self.set_available(5)
        w.wait_for("(real use: 5 GiB available)", 8)
        w.finish()

    def test_the_priority_setting_changes_nothing(self):
        # Fails if a priority run is granted ahead of its room (at 1 GiB) or a plain run is held for it.
        self.fake_proc(100, 1)
        self.book("a", 4, [100])
        prio = self.ask_free(2, "prio", 1, EXMOD_MEMORY_PRIORITY="1")
        prio.wait_for("waits for 2 GiB")
        prio.never("took", 3)
        self.set_available(3)
        plain = self.ask_free(1, "plain", 3)
        plain.wait_for("took 1 GiB of 40", 8)
        prio.wait_for("took 2 GiB of 40", 8)

    def test_two_asks_at_once_with_room_for_one_are_not_both_granted(self):
        # Fails if the booking is written after turn.lock is released (both see no running host and both are granted).
        self.set_available(1)
        a = self.ask_free(2, "a", 1, hold_s=20)
        b = self.ask_free(2, "b", 1, hold_s=20)
        deadline = time.time() + LOAD
        while time.time() < deadline and not (a.has("took") or b.has("took")):
            time.sleep(0.05)
        time.sleep(4)
        self.assertEqual(1, (a.text() + b.text()).count("took 2 GiB"), a.text() + b.text())

    def test_no_live_booking_grants_a_run_over_the_room(self):
        # Fails if a booking of a dead owner counts as a running host (1 GiB room refuses the run of 4).
        self.book("dead", 40, [], owner=self.dead_pid())
        w = self.ask_free(4, "w", 1)
        w.wait_for("took 4 GiB of 40")
        self.assertFalse(os.path.exists(os.path.join(self.mem, "booked-dead.json")))

    def test_the_charge_decides_not_the_take(self):
        # Fails if the take is compared instead of the charge (room 4 is under 8; the recorded peaks make the charge 1.5), or if
        # fewer than five rows count (4 rows leave the charge at 8).
        self.fake_proc(100, 1)
        self.book("a", 8, [100])
        few = self.ask_free(8, "w", 4, EXMOD_MEMORY_LOG=self.peaks_log("w", 8, 0.5, n=4))
        few.wait_for("waits for 8 GiB")
        few.never("took", 3)
        os.killpg(few.proc.pid, signal.SIGKILL)
        many = self.ask_free(8, "w", 4, hold_s=4, EXMOD_MEMORY_LOG=self.peaks_log("w", 8, 0.5, n=5))
        many.wait_for("took 8 GiB of 40", 8)
        booked = [f for f in self.files(r"booked-.*\.json") if f != "booked-a.json"]
        with open(os.path.join(self.mem, booked[0])) as f:
            self.assertEqual(1.5, json.load(f)["charge"])

    def charge_of(self, log, gb, filtered=False):
        """The charge booked by an ask of gb GiB labelled w against the peak log, filtered or not."""
        flag = " -Filtered" if filtered else ""
        run = self.start(f"$r = Lock-ExmodMemory {gb} 'w'{flag}; Start-Sleep -Seconds 4; Unlock-ExmodMemory $r",
                         budget=40, EXMOD_PROC_DIR=self.proc, EXMOD_MEMORY_LOG=log)
        run.wait_for(f"took {gb} GiB of 40")
        with open(os.path.join(self.mem, self.files(r"booked-.*\.json")[0])) as f:
            return json.load(f)["charge"]

    def test_charge_is_the_p95_of_the_last_fifty_rows_plus_one(self):
        # Fails if the p95 is the minimum (1.05) or the window is the whole log (the 30 GiB rows lift it to the take, 8).
        log = os.path.join(self.tmp, "peaks.jsonl")
        peaks = [30.0] * 10 + [round(0.05 * i, 2) for i in range(1, 51)]
        touch(log, "".join(json.dumps({"label": "w", "shareGb": 8, "filtered": False, "peakGb": p}) + "\n" for p in peaks))
        self.assertAlmostEqual(3.4, self.charge_of(log, 8))

    def test_charge_reads_only_rows_of_the_same_filtered_flag(self):
        # Fails if the filtered flag is not matched (the filtered ask reads the 30 GiB whole-suite rows, the whole-suite ask the 0.5 GiB filtered ones).
        log = os.path.join(self.tmp, "peaks.jsonl")
        rows = [{"label": "w", "shareGb": 8, "filtered": True, "peakGb": 0.5}] * 5 + \
               [{"label": "w", "shareGb": 8, "filtered": False, "peakGb": 30.0}] * 5
        touch(log, "".join(json.dumps(r) + "\n" for r in rows))
        self.assertAlmostEqual(1.5, self.charge_of(log, 8, filtered=True))
        shutil.rmtree(self.mem)
        self.assertAlmostEqual(8, self.charge_of(log, 8))

    def test_no_live_reservation_grants_a_whole_budget_ask(self):
        # Fails if the no-reservation branch is dropped (40 GiB asked of 1 GiB available waits).
        self.set_available(1)
        w = self.ask(40, "w", budget=40, EXMOD_PROC_DIR=self.proc)
        w.wait_for("took 40 GiB of 40")

    def test_a_reservation_with_no_streams_is_released_and_its_record_removed(self):
        # Fails if Unlock-ExmodMemory returns early on empty Streams (no released line, the record is left).
        body = ("$r = Lock-ExmodMemory 2 'a'; \"streams=$($r.Streams.Count) record=$(Test-Path $r.Record)\"; "
                "Unlock-ExmodMemory $r; Unlock-ExmodMemory $r; "
                f"\"left=$((Get-ChildItem '{self.mem}' -Filter '*.json').Count)\"")
        out = self.start(body, EXMOD_PROC_DIR=self.proc).finish(30)
        self.assertIn("streams=0 record=True", out)
        self.assertEqual(1, out.count("memory: a released 2 GiB"), out)
        self.assertIn("left=0", out)

    def test_the_sampler_writes_the_host_pids_into_the_reservation_record(self):
        # Fails if the sampler never writes the record (pids stay empty) or reads PIDs outside the host's tree.
        if not os.path.isdir("/proc/self"):
            self.skipTest("no /proc")
        body = ("$r = Lock-ExmodMemory 1 'a'; $s = Start-ExmodMemorySampler 'NEEDLE123' $r.Record; "
                "$i = [System.Diagnostics.ProcessStartInfo]::new('bash'); foreach ($a in '-c', 'sleep 4; :', 'NEEDLE123') { $i.ArgumentList.Add($a) }; $p = [System.Diagnostics.Process]::Start($i); Start-Sleep -Milliseconds 1500; "
                "\"pids=$(([System.IO.File]::ReadAllText($r.Record) | ConvertFrom-Json).pids -join ',') host=$($p.Id)\"; "
                "Stop-ExmodMemorySampler $s | Out-Null; $p.Kill(); Unlock-ExmodMemory $r")
        out = self.start(body, EXMOD_PROC_DIR="/proc", EXMOD_MEMORY_SAMPLE_MS="200").finish(30)
        m = re.search(r"pids=([\d,]+) host=(\d+)", out)
        self.assertTrue(m, out)
        self.assertIn(m.group(2), m.group(1).split(","))

    def test_the_release_line_ends_with_the_peak_only_when_one_is_given(self):
        # Fails if Unlock-ExmodMemory leaves PeakGb off the line (the first line has no peak) or prints one when none is given.
        out = self.start("$r = Lock-ExmodMemory 2 'a'; Unlock-ExmodMemory $r -PeakGb 10.44; "
                         "$r = Lock-ExmodMemory 2 'b'; Unlock-ExmodMemory $r").finish(30)
        self.assertRegex(out, r"memory: a released 2 GiB at \d\d:\d\d:\d\d, held \d+ s, peak 10\.4 GiB\n")
        self.assertRegex(out, r"memory: b released 2 GiB at \d\d:\d\d:\d\d, held \d+ s$")


    def share(self, filter_text, memory=None, peaks=(), filtered=True, heavy=False):
        """Get-ExmodTestShare for A.Tests as `ShareGb|LimitGb|RetryShareGb|ShareSource`, with `memory` as the manifest's
        A.Tests testMemory entry (none when None) and `peaks` as rows of the memory log."""
        manifest = {"testMemory": {"A.Tests": memory}} if memory else {}
        touch(os.path.join(self.repo, "exmod.json"), json.dumps(manifest))
        rows = [{"label": "1.22/A.Tests", "filtered": True, "filter": "F", "peakGb": None, **p} for p in peaks]
        touch(os.path.join(self.tmp, "local", "memory-peaks.jsonl"), "".join(json.dumps(r) + "\n" for r in rows))
        flag = "$true" if filtered else "$false"
        body = (f"$s = Get-ExmodTestShare 'A.Tests' $null {flag} '{filter_text}'; "
                "\"$($s.ShareGb)|$($s.LimitGb)|$($s.RetryShareGb)|$($s.ShareSource)\"")
        env = {"EXMOD_HEAVY": "1"} if heavy else {}
        return self.start(body, **env).finish(30).strip().splitlines()[-1].split("|")

    ENTRY = {"heapGb": 20, "filterHeapGb": 15, "filterStartGb": 4}

    def test_a_filtered_run_takes_the_largest_recorded_peak_of_its_filter_plus_headroom(self):
        # Fails if the ledger peak is ignored (the heavy filtered run takes 21), if the headroom is dropped (8 instead of 9),
        # or if a row of another filter, an unfiltered row or a lower peak decides (30, 40 or 3.1).
        peaks = [{"peakGb": 3.1}, {"peakGb": 7.2}, {"peakGb": 5.0},
                 {"peakGb": 30, "filter": "G"}, {"peakGb": 40, "filtered": False}, {"peakGb": 50, "label": "1.22/B.Tests"}]
        share, limit, _, source = self.share("F", self.ENTRY, peaks, heavy=True)
        self.assertEqual(("9", "20"), (share, limit))
        self.assertEqual("recorded peak 7.2 GiB + 1", source)

    def test_a_filtered_run_with_no_recorded_peak_takes_the_heap_it_starts_under_plus_headroom(self):
        # Fails if the unfiltered size is taken for the filtered run (EXMOD_HEAVY=1 on a filter no part owns: 21, not 5).
        share, limit, _, source = self.share("F", self.ENTRY, [{"peakGb": None}])
        self.assertEqual(("5", "4", "heap 4 GiB + 1"), (share, limit, source))
        share, limit, _, source = self.share("DisplayName~x", self.ENTRY, heavy=True)
        self.assertEqual(("21", "20", "heap 20 GiB + 1"), (share, limit, source))
        share, _, _, source = self.share("F", self.ENTRY, filtered=False)
        self.assertEqual(("21", "configured heap"), (share, source))

    def test_a_filtered_run_of_an_assembly_with_no_entry_takes_the_small_default(self):
        # Fails if the unlimited unfiltered share (4) is taken for the filtered run, or if the default ignores a recorded peak.
        share, limit, _, source = self.share("F")
        self.assertEqual(("2", "", "default for a filtered run with no testMemory entry"), (share, limit, source))
        self.assertEqual("4", self.share("F", filtered=False)[0])
        self.assertEqual("4", self.share("F", peaks=[{"peakGb": 2.6}])[0])

    def test_the_share_of_the_rerun_after_an_out_of_memory_grows_with_its_heap(self):
        # Fails if the rerun share is the first share (5), drops the headroom (8), or falls below a larger recorded peak.
        entry = {"heapGb": 12, "filterHeapGb": 8, "filterStartGb": 3}
        share, _, retry, _ = self.share("F", entry)
        self.assertEqual(("4", "9"), (share, retry))
        share, _, retry, _ = self.share("F", entry, [{"peakGb": 10.5}])
        self.assertEqual(("12", "12"), (share, retry))

    def test_the_took_line_says_where_the_share_came_from(self):
        # Fails if Lock-ExmodMemory leaves -Source off the line, or prints a share part when none is given.
        out = self.start("$r = Lock-ExmodMemory 2 'a' -Source 'recorded peak 1.0 GiB + 1'; Unlock-ExmodMemory $r; "
                         "$r = Lock-ExmodMemory 2 'b'; Unlock-ExmodMemory $r").finish(30)
        self.assertRegex(out, r"memory: a took 2 GiB of 4 at \d\d:\d\d:\d\d after \d+ s \(share: recorded peak 1\.0 GiB \+ 1\)\n")
        self.assertRegex(out, r"memory: b took 2 GiB of 4 at \d\d:\d\d:\d\d after \d+ s\n")


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
