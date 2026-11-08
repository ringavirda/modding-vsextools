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

    def put_ask(self, name, gb, priority=0, age_s=0, owner=None, charge=None):
        """A waiting ask of gb GiB made age_s seconds ago, owned by this (live) process unless owner is given."""
        touch(os.path.join(self.mem, f"ask-{name}.json"), json.dumps({
            "owner": os.getpid() if owner is None else owner, "gb": gb, "charge": gb if charge is None else charge,
            "priority": priority, "asked": int((time.time() - age_s) * 1000), "label": name}))

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

    def test_a_share_is_refused_while_the_running_hosts_still_have_room_to_grow(self):
        # Fails if the remaining growth is left out of the headroom (14 - 10 = 4 admits the share at once).
        self.full_budget()
        self.fake_proc(100, 0.5)
        self.book("a", 4, [100])
        w = self.ask_real(2, 14)
        w.never("took", 5)
        self.fake_proc(100, 4)
        w.wait_for("(real use: 4 GiB available)", 8)
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

    def test_a_host_with_no_pids_counts_its_whole_share(self):
        # Fails if an unknown size counts as nothing still to grow: 15 - 10 - 4 = 1 refuses a share of 2.
        self.full_budget()
        self.book("a", 4, [])
        w = self.ask_real(2, 15)
        w.never("took", 4)

    def test_a_host_whose_pid_is_gone_counts_its_whole_share(self):
        # Fails if a PID missing from the process table counts as nothing still to grow.
        self.full_budget()
        self.book("b", 4, [999])
        w = self.ask_real(2, 15)
        w.never("took", 4)

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

    def test_the_real_use_waiter_holds_no_lock_and_leaves_an_ask(self):
        # Fails if the waiter keeps turn.lock or a booking file while it waits (the lock probes time out).
        self.fake_proc(100, 0)
        self.book("a", 8, [100])
        self.set_available(10)
        w = self.start("$r = Lock-ExmodMemory 8 'big' -OnWait { Write-Host 'on-wait' }; Unlock-ExmodMemory $r",
                       budget=40, EXMOD_PROC_DIR=self.proc, EXMOD_MEMORY_FLOOR_GB="0")
        w.wait_for("waits for 8 GiB")
        self.assertIn("2 of 40 GiB free", w.text())
        asks = self.files(r"ask-.*\.json")
        self.assertEqual(1, len(asks))
        with open(os.path.join(self.mem, asks[0])) as f:
            rec = json.load(f)
        self.assertEqual((8, 8, 0, "big"), (rec["gb"], rec["charge"], rec["priority"], rec["label"]))
        self.assertTrue(self.lockable("turn.lock"))
        self.assertTrue(self.lockable("0.lock"))
        self.assertEqual([], self.files(r"booked-(?!a\.).*\.json"))
        self.fake_proc(100, 8)
        w.wait_for("took 8 GiB of 40", 5)
        self.assertEqual([], self.files(r"ask-.*\.json"))
        self.assertEqual(1, w.finish().count("on-wait"), w.text())
        self.assertEqual("2", self.rows()[0][4])

    def test_a_small_ask_is_granted_while_a_big_ask_waits(self):
        # Fails if small asks take the big-order rule (the small ask queues behind the big one).
        self.fake_proc(100, 0)
        self.book("a", 8, [100])
        big = self.ask_free(8, "big", 10)
        big.wait_for("waits for 8 GiB")
        small = self.ask_free(1, "small", 10)
        small.wait_for("took 1 GiB of 40", 8)
        self.assertNotIn("took", big.text())

    def test_big_asks_are_granted_in_order_priority_first(self):
        # Fails if the first-among-big check is dropped (the plain ask is granted behind a priority one) or priority is ignored in the order.
        self.fake_proc(100, 1)
        self.book("a", 1, [100])
        self.put_ask("older", 8, priority=0, age_s=100)
        first = self.ask_free(8, "prio", 40, EXMOD_MEMORY_PRIORITY="1")
        first.wait_for("took 8 GiB of 40", 8)
        first.finish()
        os.remove(os.path.join(self.mem, "ask-older.json"))
        self.put_ask("prio", 8, priority=1, age_s=0)
        plain = self.ask_free(8, "plain", 40)
        plain.never("took", 4)
        os.remove(os.path.join(self.mem, "ask-prio.json"))
        plain.wait_for("took 8 GiB of 40", 5)

    def test_big_asks_of_equal_priority_are_granted_oldest_first(self):
        # Fails if arrival time is dropped from the big-ask order (the younger ask sorts first by name and is granted).
        self.fake_proc(100, 1)
        self.book("a", 1, [100])
        self.put_ask("older", 8, age_s=100)
        w = self.ask_free(8, "w", 40)
        w.never("took", 4)
        os.remove(os.path.join(self.mem, "ask-older.json"))
        w.wait_for("took 8 GiB of 40", 5)

    def test_a_big_ask_is_granted_at_its_charge_not_its_share(self):
        # Fails if the big ask is compared at its take (room 2 is below 8 and the ask waits; its charge is 1.5).
        log = os.path.join(self.tmp, "peaks.jsonl")
        touch(log, "".join(json.dumps({"label": "w", "shareGb": 8, "filtered": False, "peakGb": 0.5}) + "\n" for _ in range(5)))
        self.fake_proc(100, 0)
        self.book("a", 8, [100])
        w = self.ask_free(8, "w", 10, EXMOD_MEMORY_LOG=log)
        w.wait_for("took 8 GiB of 40", LOAD)

    def test_a_small_ask_leaves_room_for_a_big_ask_past_the_big_wait(self):
        # Fails if the aged rule is dropped (the small ask is granted at room 4 while the big ask of 8 has waited 700 s).
        self.fake_proc(100, 0)
        self.book("a", 8, [100])
        self.put_ask("big", 8, age_s=700)
        small = self.ask_free(2, "small", 12)
        small.wait_for("waits for 2 GiB")
        small.never("took", 3)
        self.put_ask("big", 8, age_s=10)
        small.wait_for("took 2 GiB of 40", 5)

    def test_a_small_ask_leaves_the_charge_of_an_aged_big_ask(self):
        # Fails if the aged rule adds the big ask's share instead of its charge (need 2 + 8 = 10 exceeds room 4; charge 2 needs 4).
        self.fake_proc(100, 0)
        self.book("a", 8, [100])
        self.put_ask("big", 8, age_s=700, charge=2)
        small = self.ask_free(2, "small", 12)
        small.wait_for("took 2 GiB of 40", LOAD)

    def test_a_small_ask_leaves_room_for_the_oldest_big_ask_not_the_first_in_order(self):
        # Fails if the aged rule reads the first big ask in grant order (the younger priority ask is not aged and the small ask is granted at room 4).
        self.fake_proc(100, 0)
        self.book("a", 8, [100])
        self.put_ask("old", 8, priority=0, age_s=700)
        self.put_ask("young", 8, priority=1, age_s=10)
        small = self.ask_free(2, "small", 12)
        small.wait_for("waits for 2 GiB")
        small.never("took", 3)
        os.remove(os.path.join(self.mem, "ask-old.json"))
        small.wait_for("took 2 GiB of 40", 5)

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

    def test_charge_comes_from_recorded_peaks_and_falls_back_to_the_take(self):
        # Fails if charge is always the take (the ask of 4 never fits room 2) or ignores the 5-row minimum (4 rows admit it).
        log = os.path.join(self.tmp, "peaks.jsonl")

        def row(label, share, peak):
            return json.dumps({"label": label, "shareGb": share, "peakGb": peak}) + "\n"

        noise = row("w", 4, None) + row("other", 4, 30) + row("w", 8, 30)
        touch(log, noise + row("w", 4, 0.5) * 4)
        self.fake_proc(100, 0)
        self.book("a", 8, [100])
        few = self.ask_free(4, "w", 10, EXMOD_MEMORY_LOG=log)
        few.wait_for("waits for 4 GiB")
        few.never("took", 3)
        with open(os.path.join(self.mem, self.files(r"ask-.*\.json")[0])) as f:
            self.assertEqual(4, json.load(f)["charge"])
        os.killpg(few.proc.pid, signal.SIGKILL)
        os.remove(os.path.join(self.mem, self.files(r"ask-.*\.json")[0]))
        touch(log, noise + row("w", 4, 0.5) * 5)
        many = self.ask_free(4, "w", 10, hold_s=4, EXMOD_MEMORY_LOG=log)
        many.wait_for("took 4 GiB of 40", 5)
        booked = [f for f in self.files(r"booked-.*\.json") if f != "booked-a.json"]
        with open(os.path.join(self.mem, booked[0])) as f:
            self.assertEqual(1.5, json.load(f)["charge"])

    def test_a_running_reservation_is_charged_its_charge_not_its_share(self):
        # Fails if headroom reads gb (room 6 - 8 is below zero and the ask of 3 waits).
        self.fake_proc(100, 0)
        self.book("a", 8, [100], charge=2)
        w = self.ask_free(3, "w", 6)
        w.wait_for("(real use: 4 GiB available)")

    def test_a_grant_is_visible_to_the_next_decision(self):
        # Fails if the booking is written after turn.lock is released (the ask file is gone while turn.lock is free and no booking exists).
        os.makedirs(self.mem)
        fd = os.open(os.path.join(self.mem, "turn.lock"), os.O_CREAT | os.O_RDWR)
        self.fds.append(fd)
        self.ask_free(2, "w", 40, hold_s=4)
        seen = False
        deadline = time.time() + LOAD
        while time.time() < deadline:
            try:
                fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                continue
            try:
                if self.files(r"ask-.*\.json"):
                    seen = True
                elif seen:
                    self.assertEqual(1, len(self.files(r"booked-.*\.json")), "the ask is gone and the booking is not written")
                    return
            finally:
                fcntl.flock(fd, fcntl.LOCK_UN)
            time.sleep(0.0005)
        self.fail("the ask was never seen and granted")

    def test_no_live_reservation_grants_a_whole_budget_ask(self):
        # Fails if the no-reservation branch is dropped (40 GiB asked of 1 GiB available waits).
        self.set_available(1)
        w = self.ask(40, "w", budget=40, EXMOD_PROC_DIR=self.proc)
        w.wait_for("took 40 GiB of 40")

    def test_a_dead_owners_ask_is_ignored_and_deleted(self):
        # Fails if the liveness check on asks is dropped (the dead priority ask stays first among the big asks).
        self.fake_proc(100, 1)
        self.book("a", 1, [100])
        self.put_ask("dead", 8, priority=1, owner=self.dead_pid())
        w = self.ask_free(8, "w", 40)
        w.wait_for("took 8 GiB of 40")
        self.assertFalse(os.path.exists(os.path.join(self.mem, "ask-dead.json")))

    def test_a_dead_owners_record_is_ignored_and_deleted(self):
        # Fails if the liveness check on bookings is dropped (the dead 40 GiB booking leaves no room and counts as live).
        self.book("dead", 40, [], owner=self.dead_pid())
        w = self.ask_free(4, "w", 12)
        w.wait_for("took 4 GiB of 40")
        self.assertFalse(os.path.exists(os.path.join(self.mem, "booked-dead.json")))

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
