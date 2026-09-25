"""Tests for `exmod debug-adapter` (exmod/debug.ps1), `exmod provision vsdbg` (exmod/provision.ps1) and
the empty stdin the Windows helpers in exmod.ps1 run with. The adapter runs through pwsh ($PWSH,
PATH, or the checkout's .dotnet/tools) with exmod_harness's prelude, the Windows lookups stubbed and
a fake vsdbg that records what reaches it; one test runs the real launcher scripts/exmod.sh. Skipped
when no pwsh is found. Each test names the mutation it fails under."""

import json
import os
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exmod_harness import PRELUDE, PWSH, ROOT, exmod_env, exmod_script, touch  # noqa: E402

WIN_STORE = r"C:\Users\A B\AppData\Local\exmod"

# Interop on unless $env:TEST_INTEROP is 0; Windows' store WIN_STORE unless $env:TEST_STORE is 0; a
# Windows path C:\... maps to $env:TEST_C/... and a Linux path to \\wsl.localhost\test<path>;
# pwsh.exe and powershell.exe are found unless $env:TEST_PWSH or $env:TEST_POWERSHELL is 0.
STUBS = r"""
function Test-WslInterop { $env:TEST_INTEROP -ne '0' }
function Get-WindowsUserStore { if ($env:TEST_STORE -ne '0') { 'C:\Users\A B\AppData\Local\exmod' } }
function Convert-WslPath([string]$Path, [switch]$ToWindows) {
  if ($ToWindows) { return '\\wsl.localhost\test' + $Path.Replace('/', '\') }
  return $env:TEST_C + $Path.Substring(2).Replace('\', '/')
}
function Get-WindowsProgram([string]$Name) {
  if ($Name -eq 'pwsh.exe' -and $env:TEST_PWSH -ne '0') { return 'C:\Program Files\PowerShell\7\pwsh.exe' }
  if ($Name -eq 'powershell.exe' -and $env:TEST_POWERSHELL -ne '0') { return 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' }
  return $null
}
"""

# The debug-adapter command as the dispatcher runs it, with the arguments the C# extension appends.
ADAPTER = "$a = @('vsdbg', '--interpreter=vscode'); & (Resolve-ExmodCommand 'debug-adapter').Action $a"

# What the fake adapter writes to stdout as soon as it starts: a frame, then bytes no text decoding
# keeps.
CANNED = b'Content-Length: 15\r\n\r\n{"seq":1,"x":1}\xff\x00\r\nraw'


def fake_vsdbg(path):
    """Writes at `path` an executable fake vsdbg that records its arguments in $FAKE_RECORD.argv, one
    per line, writes CANNED to stdout, then copies stdin to $FAKE_RECORD until it ends."""
    touch(path, f"#!{sys.executable}\n"
                "import os, sys\n"
                "rec = os.environ['FAKE_RECORD']\n"
                "open(rec + '.argv', 'w').write('\\n'.join(sys.argv[1:]))\n"
                f"os.write(1, {CANNED!r})\n"
                "with open(rec, 'wb') as f:\n"
                "    while True:\n"
                "        b = os.read(0, 4096)\n"
                "        if not b: break\n"
                "        f.write(b); f.flush()\n")
    os.chmod(path, os.stat(path).st_mode | stat.S_IEXEC)


def slurp(path, mode="r"):
    with open(path, mode) as f:
        return f.read()


def frame(obj):
    body = json.dumps(obj).encode()
    return b"Content-Length: %d\r\n\r\n" % len(body) + body


def frames(data):
    """The (header, body) pairs of a Content-Length framed byte stream."""
    out = []
    while data:
        end = data.index(b"\r\n\r\n")
        header = data[:end].decode()
        length = int(header.split(":")[1])
        out.append((header, data[end + 4:end + 4 + length]))
        data = data[end + 4 + length:]
    return out


class Session:
    """One adapter run: `script` under pwsh with `env` (or `command`), fed `chunks` in order once the
    fake adapter has started, each after the previous with a pause long enough for the relay to read
    it alone; with `at_once`, all of them as soon as the process starts. Keeps the exit code, stdout
    and stderr, the fake's recorded stdin and its arguments."""

    def __init__(self, script, env, record, chunks, command=None, at_once=False):
        assert PWSH
        cmd = command or [PWSH, "-NoProfile", "-NonInteractive", "-Command", script]
        p = subprocess.Popen(cmd, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                             cwd=ROOT)
        assert p.stdin is not None
        deadline = time.time() + 60
        while chunks and not at_once and not os.path.exists(record + ".argv") and time.time() < deadline and p.poll() is None:
            time.sleep(0.05)
        for chunk in chunks:
            if not at_once:
                time.sleep(0.5)
            p.stdin.write(chunk)
            p.stdin.flush()
        self.out, err = p.communicate(timeout=60)
        self.err = err.decode(errors="replace")
        self.code = p.returncode
        self.record = slurp(record, "rb") if os.path.exists(record) else None
        self.argv = slurp(record + ".argv").splitlines() if os.path.exists(record + ".argv") else None


def launch_request(store, repo):
    return {
        "seq": 2, "type": "request", "command": "launch",
        "arguments": {
            "name": "demo (latest)", "type": "coreclr", "request": "launch",
            "program": f"{store}/game/1.22/Vintagestory.dll",
            "args": ["--tracelog", "--dataPath", f"{store}/data/default",
                     "--logPath", f"{store}/data/default/Logs/repo", "--addModPath", f"{repo}/bin/Mods"],
            "cwd": repo,
            "env": {"WAYLAND_DISPLAY": "none", "KEEP": "1"},
            "stopAtEntry": False,
        },
    }


INITIALIZE = frame({"seq": 1, "type": "request", "command": "initialize", "arguments": {"adapterID": "coreclr"}})
TWO = (frame({"seq": 3, "type": "request", "command": "configurationDone", "arguments": {}})
       + frame({"seq": 4, "type": "request", "command": "disconnect", "arguments": {"terminateDebuggee": True}}))


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "the fake adapter and the Linux store are POSIX")
class RelayTests(unittest.TestCase):
    """One session with interop on and one with it off, both fed the initialize request, the launch
    request split across two reads, then two requests in one read."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = os.path.realpath(tempfile.mkdtemp())
        cls.home = os.path.join(cls.tmp, "home")
        cls.store = os.path.join(cls.home, ".local", "share", "exmod")
        cls.repo = os.path.join(cls.tmp, "repo")
        cls.c = os.path.join(cls.tmp, "c")
        touch(os.path.join(cls.repo, "exmod.json"), "{}")
        fake_vsdbg(os.path.join(cls.tmp, "fake-vsdbg"))
        launch = frame(launch_request(cls.store, cls.repo))
        cls.sent = INITIALIZE + launch + TWO
        chunks = [INITIALIZE + launch[:40], launch[40:], TWO]
        cls.sessions = {}
        for name, interop in (("on", "1"), ("off", "0")):
            record = os.path.join(cls.tmp, f"record-{name}")
            env = exmod_env(os.path.join(cls.tmp, "local"), TEST_REPO=cls.repo, HOME=cls.home, TEST_C=cls.c,
                            TEST_INTEROP=interop, EXMOD_VSDBG=os.path.join(cls.tmp, "fake-vsdbg"),
                            FAKE_RECORD=record)
            cls.sessions[name] = Session(exmod_script(STUBS + ADAPTER), env, record, chunks)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp)

    def rewritten(self):
        s = self.sessions["on"]
        got = frames(s.record)
        self.assertEqual(4, len(got), s.record)
        header, body = got[1]
        self.assertEqual(f"Content-Length: {len(body)}", header)
        return json.loads(body)

    def test_the_launch_runs_the_windows_client_from_windows_store(self):
        # Fails if the program keeps its Linux path, stays Vintagestory.dll, or goes to the Windows
        # store without the series folder.
        self.assertEqual(WIN_STORE + r"\game\1.22\Vintagestory.exe", self.rewritten()["arguments"]["program"])

    def test_store_arguments_move_to_windows_store_and_workspace_paths_become_unc(self):
        # Fails if an argument under the Linux store is not the same path under the Windows store, a
        # workspace path is left as a Linux path, or a non-path argument changes.
        unc = "\\\\wsl.localhost\\test" + self.repo.replace("/", "\\")
        self.assertEqual(["--tracelog", "--dataPath", WIN_STORE + r"\data\default",
                          "--logPath", WIN_STORE + r"\data\default\Logs\repo",
                          "--addModPath", unc + r"\bin\Mods"], self.rewritten()["arguments"]["args"])

    def test_the_client_runs_from_its_folder_wayland_leaves_and_the_rest_is_kept(self):
        # Fails if cwd is left as the Linux workspace path or becomes its UNC path instead of the
        # client's folder, WAYLAND_DISPLAY is kept or the rest of env dropped, or any other launch
        # field or the message's own fields change.
        launch = self.rewritten()
        want = launch_request(self.store, self.repo)
        self.assertEqual(WIN_STORE + r"\game\1.22", launch["arguments"].pop("cwd"))
        self.assertEqual({"KEEP": "1"}, launch["arguments"].pop("env"))
        for key in ("cwd", "env", "program", "args"):
            want["arguments"].pop(key)
            launch["arguments"].pop(key, None)
        self.assertEqual(want, launch)

    def test_split_and_batched_messages_reach_the_adapter_whole_and_in_order(self):
        # Fails if a read that ends mid-message is sent on before the rest arrives, or only the first
        # message of a read is taken.
        s = self.sessions["on"]
        self.assertNotIn(b"Vintagestory.dll", s.record)
        self.assertTrue(s.record.startswith(INITIALIZE), s.record)
        self.assertTrue(s.record.endswith(TWO), s.record)
        self.assertEqual(["initialize", "launch", "configurationDone", "disconnect"],
                         [json.loads(b)["command"] for _, b in frames(s.record)])

    def test_the_gpu_preference_goes_to_stderr_and_settings_are_seeded_in_windows_store(self):
        # Fails if the GPU preference is not registered for the rewritten exe, its output reaches
        # stdout, or the --dataPath folder's settings are not seeded through its Linux view.
        s = self.sessions["on"]
        self.assertIn("gpu-stub: " + WIN_STORE + r"\game\1.22\Vintagestory.exe", s.err)
        self.assertNotIn(b"gpu-stub", s.out)
        settings = os.path.join(self.c, "Users", "A B", "AppData", "Local", "exmod", "data", "default",
                                "clientsettings.json")
        self.assertTrue(os.path.isfile(settings))

    def test_adapter_bytes_reach_stdout_unchanged_and_alone(self):
        # Fails if the adapter's output is decoded as text, buffered past the end, or anything else
        # is written to stdout, in either mode.
        for name, s in self.sessions.items():
            self.assertEqual(CANNED, s.out, (name, s.err))
            self.assertEqual(0, s.code, (name, s.err))

    def test_trailing_arguments_are_not_passed_to_the_adapter(self):
        # Fails if the command's arguments reach vsdbg.
        for name, s in self.sessions.items():
            self.assertEqual(["--interpreter=vscode"], s.argv, name)

    def test_without_interop_every_byte_goes_through_unchanged(self):
        # Fails if the launch is rewritten, or the GPU preference registered, without interop.
        s = self.sessions["off"]
        self.assertEqual(self.sent, s.record)
        self.assertNotIn("gpu-stub", s.err)


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "the fake adapter and the Linux store are POSIX")
class AdapterLookupTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.home = os.path.join(self.tmp, "home")
        self.repo = os.path.join(self.tmp, "repo")
        self.c = os.path.join(self.tmp, "c")
        self.record = os.path.join(self.tmp, "record")
        touch(os.path.join(self.repo, "exmod.json"), "{}")

    def session(self, chunks=(), **env):
        e = exmod_env(os.path.join(self.tmp, "local"), TEST_REPO=self.repo, HOME=self.home, TEST_C=self.c,
                      FAKE_RECORD=self.record, **env)
        if "EXMOD_VSDBG" not in env:
            e.pop("EXMOD_VSDBG", None)
        return Session(exmod_script(STUBS + ADAPTER), e, self.record, list(chunks))

    def test_with_interop_windows_vsdbg_starts_from_windows_store(self):
        # Fails if the interop branch looks for vsdbg anywhere but <Windows store>\vsdbg\vsdbg.exe.
        fake_vsdbg(os.path.join(self.c, "Users", "A B", "AppData", "Local", "exmod", "vsdbg", "vsdbg.exe"))
        s = self.session([INITIALIZE])
        self.assertEqual((0, CANNED, INITIALIZE), (s.code, s.out, s.record), s.err)

    def test_without_interop_linux_vsdbg_starts_from_linux_store(self):
        # Fails if the Linux branch looks for vsdbg anywhere but <Linux store>/vsdbg/vsdbg.
        fake_vsdbg(os.path.join(self.home, ".local", "share", "exmod", "vsdbg", "vsdbg"))
        s = self.session([INITIALIZE], TEST_INTEROP="0")
        self.assertEqual((0, CANNED, INITIALIZE), (s.code, s.out, s.record), s.err)

    def test_a_missing_vsdbg_names_the_provision_command_on_stderr(self):
        # Fails if a missing vsdbg is started anyway, the message goes to stdout, or it does not name
        # `exmod provision vsdbg`, in either mode.
        for interop, where in (("1", WIN_STORE + r"\vsdbg\vsdbg.exe"),
                               ("0", os.path.join(self.home, ".local", "share", "exmod", "vsdbg", "vsdbg"))):
            s = self.session(TEST_INTEROP=interop)
            self.assertEqual((1, b""), (s.code, s.out), s.err)
            self.assertEqual(f"exmod debug-adapter: no vsdbg at {where}; install it with: exmod provision vsdbg",
                             s.err.strip())

    def test_an_adapter_that_ends_first_ends_the_relay_with_its_exit_code(self):
        # Fails if the relay waits on stdin after the adapter has gone, or exits 0 for it. Stdin
        # stays open throughout.
        assert PWSH
        path = os.path.join(self.tmp, "quits")
        touch(path, "#!/bin/sh\nprintf done\nexit 5\n")
        os.chmod(path, 0o755)
        e = exmod_env(os.path.join(self.tmp, "local"), TEST_REPO=self.repo, HOME=self.home, TEST_C=self.c,
                      EXMOD_VSDBG=path)
        p = subprocess.Popen([PWSH, "-NoProfile", "-NonInteractive", "-Command", exmod_script(STUBS + ADAPTER)],
                             env=e, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        assert p.stdin is not None and p.stdout is not None
        try:
            code = p.wait(timeout=30)
            out = p.stdout.read()
        finally:
            p.kill()
            p.stdin.close()
            p.stdout.close()
        self.assertEqual((5, b"done"), (code, out))

    def test_an_adapter_that_ignores_the_end_of_input_is_killed(self):
        # Fails if the relay waits for the adapter without a bound after stdin ends.
        path = os.path.join(self.tmp, "stays")
        touch(path, "#!/bin/sh\nexec sleep 300 < /dev/null\n")
        os.chmod(path, 0o755)
        started = time.time()
        s = self.session(EXMOD_VSDBG=path)
        self.assertEqual((0, b""), (s.code, s.out), s.err)
        self.assertLess(time.time() - started, 30)

    def test_an_unreadable_windows_store_stops_on_stderr(self):
        # Fails if the adapter goes on without Windows' store.
        s = self.session(TEST_STORE="0")
        self.assertEqual((1, b""), (s.code, s.out), s.err)
        self.assertEqual("exmod debug-adapter: Windows %LOCALAPPDATA% could not be read through cmd.exe.",
                         s.err.strip())


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "the launcher under test is the POSIX one")
class LauncherStdoutTests(unittest.TestCase):
    def test_the_launcher_writes_nothing_but_the_adapters_bytes(self):
        # Fails if the launcher, the dispatcher or anything the adapter runs before the relay prints
        # to stdout, or a Windows program started on the way (cmd.exe under interop) takes the
        # client's first message from stdin; the message is written before the adapter starts. No
        # launch request is sent, so nothing is registered.
        with tempfile.TemporaryDirectory() as d:
            fake = os.path.join(d, "fake-vsdbg")
            fake_vsdbg(fake)
            record = os.path.join(d, "record")
            env = dict(os.environ, EXMOD_VSDBG=fake, FAKE_RECORD=record, PWSH=PWSH)
            s = Session(None, env, record, [INITIALIZE],
                        command=["bash", os.path.join(ROOT, "scripts", "exmod.sh"), "debug-adapter", "vsdbg",
                                 "--interpreter=vscode"], at_once=True)
        self.assertEqual((0, CANNED, INITIALIZE), (s.code, s.out, s.record), s.err)


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "the fake Windows programs are shell scripts")
class WindowsHelperStdinTests(unittest.TestCase):
    """The real Invoke-WindowsCmd and Register-ClientGpuPreference, with PATH holding only fake
    cmd.exe and reg.exe scripts that record what they read from stdin, and the system folders."""

    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.repo = os.path.join(self.tmp, "repo")
        touch(os.path.join(self.repo, "exmod.json"), "{}")
        self.bin = os.path.join(self.tmp, "bin")
        for name, code in (("cmd.exe", 0), ("reg.exe", 1)):
            touch(os.path.join(self.bin, name),
                  f'#!/bin/sh\ncat >> "{self.tmp}/{name}.stdin"\necho C:\\\\fake\nexit {code}\n')
            os.chmod(os.path.join(self.bin, name), 0o755)

    def run_left(self, call):
        """What is left on stdin ("keep-me" fed) after `call`, as the number of bytes read after it."""
        assert PWSH
        script = (". (Join-Path $env:EXTOOLS_ROOT 'exmod.ps1') -RepoRoot $env:TEST_REPO 6>$null; "
                  "$windowsCmd = ${function:Invoke-WindowsCmd}; " + PRELUDE
                  + "Set-Item function:Invoke-WindowsCmd $windowsCmd; Remove-Item function:reg.exe; $OnWindows = $false; "
                  + call + " *> $null; $ms = [IO.MemoryStream]::new(); [Console]::OpenStandardInput().CopyTo($ms); "
                  "Write-Host \"left $($ms.Length)\"")
        env = exmod_env(os.path.join(self.tmp, "local"), TEST_REPO=self.repo, PATH=f"{self.bin}:/usr/bin:/bin")
        out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", script], env=env, input=b"keep-me",
                             capture_output=True)
        return out.stdout.decode().strip(), out.stderr.decode()

    def test_cmd_exe_gets_an_empty_stdin(self):
        # Fails if Invoke-WindowsCmd starts cmd.exe on this process's stdin.
        left, err = self.run_left("Invoke-WindowsCmd 'echo %LOCALAPPDATA%'")
        self.assertEqual("left 7", left, err)
        self.assertEqual("", slurp(os.path.join(self.tmp, "cmd.exe.stdin")))

    def test_reg_exe_gets_an_empty_stdin_for_the_query_and_the_add(self):
        # Fails if either reg.exe call in Register-ClientGpuPreference runs on this process's stdin.
        left, err = self.run_left(r"Register-ClientGpuPreference 'C:\x\Vintagestory.exe'")
        self.assertEqual("left 7", left, err)
        self.assertEqual("", slurp(os.path.join(self.tmp, "reg.exe.stdin")))


@unittest.skipUnless(PWSH, "pwsh not found")
@unittest.skipIf(sys.platform == "win32", "the fake Windows shells and curl are shell scripts")
class ProvisionVsdbgTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.home = os.path.join(self.tmp, "home")
        self.repo = os.path.join(self.tmp, "repo")
        self.c = os.path.join(self.tmp, "c")
        touch(os.path.join(self.repo, "exmod.json"), "{}")
        self.win_dir = os.path.join(self.c, "Users", "A B", "AppData", "Local", "exmod", "vsdbg")
        self.linux_dir = os.path.join(self.home, ".local", "share", "exmod", "vsdbg")
        self.calls = os.path.join(self.tmp, "calls")

    def provision(self, args, path=None, body_prefix="", **env):
        assert PWSH
        argv = ", ".join(f"'{a}'" for a in args)
        e = exmod_env(os.path.join(self.tmp, "local"), TEST_REPO=self.repo, HOME=self.home, TEST_C=self.c, **env)
        if path:
            e["PATH"] = path
        out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command",
                              exmod_script(STUBS + body_prefix + f"Invoke-ProvisionVsdbg @({argv})")],
                             env=e, capture_output=True, text=True)
        return out.returncode, out.stdout.splitlines()

    def windows_shell(self, *parts):
        """An executable at the Linux view of Windows path C:\\<parts> that records each argument on
        its own line and puts a vsdbg.exe in Windows' store."""
        path = os.path.join(self.c, *parts)
        touch(path, f'#!/bin/sh\nprintf "%s\\n" "$@" >> "{self.calls}"\n'
                    f'mkdir -p "{self.win_dir}" && touch "{self.win_dir}/vsdbg.exe"\n')
        os.chmod(path, 0o755)

    def test_with_interop_windows_pwsh_runs_getvsdbg_into_windows_store(self):
        # Fails if the interop branch installs the Linux vsdbg, names another folder or runtime, or
        # does not report the vsdbg.exe it left.
        self.windows_shell("Program Files", "PowerShell", "7", "pwsh.exe")
        code, lines = self.provision([])
        self.assertEqual(0, code, lines)
        self.assertEqual("vsdbg installed at " + WIN_STORE + r"\vsdbg\vsdbg.exe", lines[-1])
        calls = slurp(self.calls).split("\n")
        self.assertEqual(["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command"], calls[:4])
        command = calls[4]
        self.assertIn("Invoke-WebRequest -Uri 'https://aka.ms/getvsdbgps1'", command)
        self.assertIn("-Version latest -RuntimeID win7-x64 -InstallPath '" + WIN_STORE + r"\vsdbg'", command)

    def test_the_windows_command_parses_in_powershell_with_a_quote_in_the_store(self):
        # Fails if the command handed to Windows' PowerShell has a syntax error, such as the quote in
        # a user name like O'Neil left unescaped in the install path.
        assert PWSH
        code, lines = self.provision(["-DryRun"], body_prefix=(
            "function Get-WindowsUserStore { 'C:\\Users\\O''Neil\\AppData\\Local\\exmod' }; "))
        self.assertEqual(0, code, lines)
        command = lines[-1].split(" -Command ", 1)[1]
        self.assertIn("-InstallPath 'C:\\Users\\O''Neil\\AppData\\Local\\exmod\\vsdbg'", command)
        out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command",
                              "$e = $null; $null = [System.Management.Automation.Language.Parser]::ParseInput("
                              "$env:TEST_COMMAND, [ref]$null, [ref]$e); Write-Host \"errors $($e.Count)\""],
                             env=dict(os.environ, TEST_COMMAND=command), capture_output=True, text=True)
        self.assertEqual("errors 0", out.stdout.strip(), command)

    def test_with_interop_a_dry_run_prints_the_folder_and_the_command_and_installs_nothing(self):
        # Fails if a dry run runs the installer, or prints another folder or shell.
        code, lines = self.provision(["-DryRun"])
        self.assertEqual(0, code, lines)
        self.assertEqual("dest: " + WIN_STORE + r"\vsdbg", lines[0])
        self.assertTrue(lines[1].startswith(r"run: C:\Program Files\PowerShell\7\pwsh.exe -NoProfile -ExecutionPolicy "
                                            r"Bypass -Command "), lines)
        self.assertFalse(os.path.exists(self.calls))

    def test_without_pwsh_windows_powershell_runs_it(self):
        # Fails if the powershell.exe fallback is dropped.
        code, lines = self.provision(["-DryRun"], TEST_PWSH="0")
        self.assertTrue(lines[1].startswith(r"run: C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile "),
                        lines)

    def test_without_either_powershell_it_stops_naming_powershell(self):
        # Fails if the missing-shell check is dropped.
        code, lines = self.provision([], TEST_PWSH="0", TEST_POWERSHELL="0")
        self.assertEqual(1, code)
        self.assertEqual(["exmod: Windows has neither pwsh.exe nor powershell.exe on its PATH; install PowerShell 7 "
                          "for Windows (winget install Microsoft.PowerShell) to provision vsdbg."], lines)

    def test_an_installed_windows_vsdbg_is_skipped(self):
        # Fails if the presence check is dropped or looks outside Windows' store.
        self.windows_shell("Program Files", "PowerShell", "7", "pwsh.exe")
        touch(os.path.join(self.win_dir, "vsdbg.exe"))
        code, lines = self.provision([])
        self.assertEqual((0, ["vsdbg already at " + WIN_STORE + r"\vsdbg\vsdbg.exe"]), (code, lines))
        self.assertFalse(os.path.exists(self.calls))

    def test_an_unreadable_windows_store_stops_pointing_at_linux(self):
        # Fails if the store check is dropped.
        code, lines = self.provision([], TEST_STORE="0")
        self.assertEqual((1, ["exmod: Windows %LOCALAPPDATA% could not be read through cmd.exe; pass -Linux to "
                              "provision the Linux vsdbg."]), (code, lines))

    def test_linux_flag_and_no_interop_each_take_the_linux_store(self):
        # Fails if -Linux is ignored under interop, or the interop check is dropped.
        want = ["dest: " + self.linux_dir,
                f"run: curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l '{self.linux_dir}'"]
        self.assertEqual((0, want), self.provision(["-Linux", "-DryRun"]))
        self.assertEqual((0, want), self.provision(["-DryRun"], TEST_INTEROP="0"))

    def test_on_linux_curl_pipes_getvsdbg_into_bash_with_the_store_folder(self):
        # Fails if the script is not fetched from aka.ms/getvsdbgsh, is run without -v latest -l
        # <store>/vsdbg, or the install is not checked for the vsdbg it leaves.
        bin_dir = os.path.join(self.tmp, "bin")
        touch(os.path.join(bin_dir, "curl"),
              f'#!/bin/sh\necho "curl $*" >> "{self.calls}"\n'
              f"printf '%s\\n' 'echo \"getvsdbg $*\" >> \"{self.calls}\"; mkdir -p \"$4\" && touch \"$4/vsdbg\"'\n")
        os.chmod(os.path.join(bin_dir, "curl"), 0o755)
        code, lines = self.provision([], path=f"{bin_dir}:/usr/bin:/bin", TEST_INTEROP="0")
        self.assertEqual(0, code, lines)
        self.assertEqual(["curl -sSL https://aka.ms/getvsdbgsh", f"getvsdbg -v latest -l {self.linux_dir}"],
                         slurp(self.calls).splitlines())
        self.assertEqual("vsdbg installed at " + os.path.join(self.linux_dir, "vsdbg"), lines[-1])

    def test_a_failed_or_empty_windows_install_stops(self):
        # Fails if the installer's exit code is ignored, or the store is not checked for vsdbg.exe
        # after it.
        shell = os.path.join(self.c, "Program Files", "PowerShell", "7", "pwsh.exe")
        touch(shell, "#!/bin/sh\nexit 3\n")
        os.chmod(shell, 0o755)
        self.assertEqual(3, self.provision([])[0])
        touch(shell, "#!/bin/sh\nexit 0\n")
        code, lines = self.provision([])
        self.assertEqual((1, "exmod: GetVsDbg completed but left no vsdbg.exe in " + WIN_STORE + r"\vsdbg."),
                         (code, lines[-1]))

    def test_a_failed_or_empty_linux_install_stops(self):
        # Fails if the pipeline's exit code is ignored (pipefail dropped, so curl's failure is lost),
        # or the store is not checked for vsdbg after it.
        bin_dir = os.path.join(self.tmp, "bin")
        touch(os.path.join(bin_dir, "curl"), "#!/bin/sh\necho true\nexit 6\n")
        os.chmod(os.path.join(bin_dir, "curl"), 0o755)
        self.assertEqual(6, self.provision([], path=f"{bin_dir}:/usr/bin:/bin", TEST_INTEROP="0")[0])
        touch(os.path.join(bin_dir, "curl"), "#!/bin/sh\necho true\n")
        code, lines = self.provision([], path=f"{bin_dir}:/usr/bin:/bin", TEST_INTEROP="0")
        self.assertEqual((1, f"exmod: GetVsDbg completed but left no vsdbg in {self.linux_dir}."), (code, lines[-1]))

    def test_on_windows_and_macos_it_says_the_extension_debugger_is_used(self):
        # Fails if the Windows or macOS early return is dropped (a dry run then prints a store).
        code, lines = self.provision(["-DryRun"], body_prefix="$OnWindows = $true; ")
        self.assertEqual((0, ["F5 on Windows uses the C# extension's own debugger; nothing to provision."]),
                         (code, lines))


@unittest.skipUnless(PWSH, "pwsh not found")
class HelpTests(unittest.TestCase):
    def test_debug_adapter_is_left_out_of_the_list_but_has_help(self):
        # Fails if the command list shows hidden commands, or a hidden command loses its help.
        assert PWSH
        with tempfile.TemporaryDirectory() as d:
            touch(os.path.join(d, "exmod.json"), "{}")
            env = exmod_env(os.path.join(d, "local"), TEST_REPO=d)
            listed = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", exmod_script("Show-ExmodHelp")],
                                    env=env, capture_output=True, text=True).stdout
            detail = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command",
                                     exmod_script("Show-ExmodHelp 'debug-adapter'")],
                                    env=env, capture_output=True, text=True).stdout
        self.assertIn("  client", listed)
        self.assertNotIn("debug-adapter", listed)
        self.assertIn("exmod debug-adapter [anything ...]", detail)


if __name__ == "__main__":
    unittest.main()
