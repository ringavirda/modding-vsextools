"""Tests for the server port reservation (Lock-ServerPorts, Test-ServerPortFree), Wait-ScratchServer's in-use line
and Save-ScenarioLast's swap, in exmod/run.ps1. Run through pwsh ($PWSH, PATH, or the nearest .dotnet/tools above
the checkout); skipped when no pwsh is found or off POSIX."""

import fcntl
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import PWSH, exmod_env, exmod_script, touch  # noqa: E402

NOW_MS = "[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()"


def free_port():
    """A TCP and UDP port with the next two above it also free, found by binding port 0."""
    for _ in range(50):
        with socket.socket() as s:
            s.bind(("0.0.0.0", 0))
            port = s.getsockname()[1]
        if port > 65000:
            continue
        try:
            for p in range(port, port + 3):
                with socket.socket() as t, socket.socket(type=socket.SOCK_DGRAM) as u:
                    t.bind(("0.0.0.0", p))
                    u.bind(("0.0.0.0", p))
            return port
        except OSError:
            continue
    raise RuntimeError("no free port run found")


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "the reservation is flock(2)")
class PortTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.repo = os.path.join(self.tmp, "ws", "repo")
        touch(os.path.join(self.tmp, "ws", "exmod.workspace.json"), "{}")
        touch(os.path.join(self.repo, "exmod.json"), "{}")
        self.ports = os.path.join(self.tmp, "ports")

    def start(self, body):
        env = exmod_env(os.path.join(self.tmp, "local"), TEST_REPO=self.repo, EXMOD_PORTS_DIR=self.ports)
        return subprocess.Popen([PWSH, "-NoProfile", "-NonInteractive", "-Command", exmod_script(body)],
                                env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

    def run_body(self, body, timeout=90):
        out, _ = self.start(body).communicate(timeout=timeout)
        return out.strip()

    def pick(self, first, count=3):
        out = self.run_body(f"$r = Lock-ServerPorts @({first}..{first + count - 1}) 1; $r.Ports[0]")
        return int(out.splitlines()[-1])

    def test_a_reserved_port_is_skipped(self):
        # Fails if the reservation file is not locked (the pick stays on the first port).
        first = free_port()
        os.makedirs(self.ports)
        fd = os.open(os.path.join(self.ports, f"{first}.lock"), os.O_CREAT | os.O_RDWR)
        self.addCleanup(os.close, fd)
        fcntl.flock(fd, fcntl.LOCK_EX)
        self.assertEqual(first + 1, self.pick(first))

    def test_smoke_candidates_start_at_42499_and_a_reserved_first_moves_to_the_next_free(self):
        # Fails if the reservation file is not locked (the second pick equals the first).
        out = self.run_body("$r = Lock-ServerPorts (Get-SmokePorts) 1; $r.Ports[0]; (Get-SmokePorts).Count")
        lines = out.splitlines()
        self.assertGreaterEqual(int(lines[-2]), 42499)
        self.assertEqual("64", lines[-1], out)
        first = int(lines[-2])
        fd = os.open(os.path.join(self.ports, f"{first}.lock"), os.O_CREAT | os.O_RDWR)
        self.addCleanup(os.close, fd)
        fcntl.flock(fd, fcntl.LOCK_EX)
        out = self.run_body("$r = Lock-ServerPorts (Get-SmokePorts) 1; $r.Ports[0]")
        self.assertGreater(int(out.splitlines()[-1]), first, out)

    def test_a_port_bound_over_tcp_is_skipped(self):
        # Fails if the bind probe is gone (the pick stays on the bound port).
        first = free_port()
        with socket.socket() as s:
            s.bind(("0.0.0.0", first))
            s.listen()
            self.assertEqual(first + 1, self.pick(first))

    def test_a_port_bound_over_udp_is_skipped(self):
        # Fails if the probe binds TCP only (the pick stays on the bound port).
        first = free_port()
        with socket.socket(type=socket.SOCK_DGRAM) as s:
            s.bind(("0.0.0.0", first))
            self.assertEqual(first + 1, self.pick(first))

    def test_candidates_that_run_out_fail_naming_the_count_and_the_first(self):
        # Fails if the throw is gone (the call returns with fewer ports than asked).
        first = free_port()
        with socket.socket() as s:
            s.bind(("0.0.0.0", first))
            s.listen()
            out = self.run_body(f"try {{ Lock-ServerPorts @({first}) 1 }} catch {{ $_.Exception.Message }}")
        self.assertEqual(f"no free port among the 1 from {first}", out.splitlines()[-1], out)

    def test_a_second_run_while_the_first_holds_takes_another_port(self):
        # Fails if the reservation is released right after the pick (both take the first port).
        first = free_port()
        body = (f"$r = Lock-ServerPorts @({first}..{first + 2}) 1; Write-Output \"port $($r.Ports[0])\"; "
                "Start-Sleep 5; Unlock-ServerPorts $r")
        procs = [self.start(body)]
        time.sleep(1.5)
        procs.append(self.start(body))
        got = []
        for p in procs:
            out, _ = p.communicate(timeout=90)
            got.append([l for l in out.splitlines() if l.startswith("port ")][-1])
        self.assertEqual(2, len(set(got)), got)

    def wait_failure(self, port, exited):
        out = self.run_body(
            f"$s = [pscustomobject]@{{ Log = '{self.tmp}/none.log'; Port = {port}; "
            f"Proc = [pscustomobject]@{{ HasExited = ${str(exited).lower()} }} }}; "
            "try { Wait-ScratchServer $s 1 } catch { $_.Exception.Message }")
        return out

    def test_an_exited_server_whose_port_is_bound_says_the_port_is_in_use(self):
        # Fails if the fresh probe is gone.
        port = free_port()
        with socket.socket() as s:
            s.bind(("0.0.0.0", port))
            s.listen()
            self.assertIn(f"port {port} is in use now", self.wait_failure(port, True))

    def test_an_exited_server_on_a_free_port_does_not(self):
        # Fails if the line is added without the probe.
        port = free_port()
        out = self.wait_failure(port, True)
        self.assertIn("did not report ready", out)
        self.assertNotIn("in use now", out)

    def test_a_running_server_holding_its_port_does_not(self):
        # Fails if the line is added while the server has not exited.
        port = free_port()
        with socket.socket() as s:
            s.bind(("0.0.0.0", port))
            s.listen()
            out = self.wait_failure(port, False)
        self.assertIn("did not report ready", out)
        self.assertNotIn("in use now", out)

    def test_the_last_run_swap_waits_for_the_lock_and_leaves_no_sibling(self):
        # Fails if the swap is made without the lock (it finishes before the lock is released) or leaves
        # a .scenario-last.* sibling.
        game = os.path.join(self.tmp, "game")
        data = os.path.join(self.tmp, "data")
        touch(os.path.join(data, "Scenarios", "recordings", "r.json"), "{}")
        os.makedirs(game)
        fd = os.open(os.path.join(game, ".scenario-lock"), os.O_CREAT | os.O_RDWR)
        fcntl.flock(fd, fcntl.LOCK_EX)
        released = []

        def release():
            time.sleep(2)
            released.append(time.time())
            fcntl.flock(fd, fcntl.LOCK_UN)
            os.close(fd)

        t = threading.Thread(target=release)
        t.start()
        self.addCleanup(t.join)
        out = self.run_body(f"$n = Save-ScenarioLast @('{data}') @() '{game}/.scenario-last'; "
                            f"Write-Output \"done $n $({NOW_MS})\"")
        done = [l for l in out.splitlines() if l.startswith("done ")][-1].split()
        self.assertEqual("1", done[1], out)
        self.assertGreaterEqual(int(done[2]) / 1000, released[0] - 0.05, out)
        self.assertEqual([".scenario-lock", ".scenario-last"], sorted(os.listdir(game), reverse=True))
        self.assertTrue(os.path.exists(os.path.join(game, ".scenario-last", "recordings", "r.json")))


if __name__ == "__main__":
    unittest.main()
