"""What every test that loads exmod.ps1 shares: the checkout root, the pwsh to run it with ($PWSH,
PATH, or the checkout's .dotnet/tools; None when there is none), the environment and the prelude
that keep a test off the Windows side of the machine it runs on, the network and the real store."""

import os
import shutil

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PWSH = (os.environ.get("PWSH") or shutil.which("pwsh")
        or next((p for p in [os.path.join(ROOT, ".dotnet", "tools", "pwsh")] if os.access(p, os.X_OK)), None))

# Defined after exmod.ps1 loads and before a test's body: Test-WslInterop is $false, and every
# Windows program exmod starts by name (cmd.exe through Invoke-WindowsCmd, reg, reg.exe, wsl.exe),
# Start-Process, Invoke-WebRequest and Invoke-RestMethod throw. A test body that turns interop on,
# installs or downloads must stub what it reaches.
PRELUDE = r"""
function Test-WslInterop { $false }
function Invoke-WindowsCmd { throw "cmd.exe reached in a test: $args" }
function reg { throw "reg reached in a test: $args" }
function reg.exe { throw "reg.exe reached in a test: $args" }
function wsl.exe { throw "wsl.exe reached in a test: $args" }
function Start-Process { throw "Start-Process reached in a test: $args" }
function Invoke-WebRequest { throw "download reached in a test: $args" }
function Invoke-RestMethod { throw "download reached in a test: $args" }
"""

# Register-ClientGpuPreference as a test sees it: one line, "gpu-stub: <exe>", " dry-run" appended
# under -DryRun; the registry is never read or written.
GPU_STUB = r"""
function Register-ClientGpuPreference([string]$ExePath, [switch]$DryRun) {
  Write-Host "gpu-stub: $ExePath$(if ($DryRun) { ' dry-run' })"
}
"""


def touch(path, text=""):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        f.write(text)


def exmod_env(local, **env):
    """The environment for a pwsh that runs exmod_script: this process's, with EXTOOLS_ROOT set to
    ROOT and LOCALAPPDATA to `local`, then `env` over both. `local` is a folder under the test's
    temporary directory, so on Windows the store (%LOCALAPPDATA%\\exmod) is never the user's."""
    return {**os.environ, "EXTOOLS_ROOT": ROOT, "LOCALAPPDATA": local, **env}


def exmod_script(body, stub_gpu=True):
    """The pwsh -Command text that dot-sources $env:EXTOOLS_ROOT/exmod.ps1 against $env:TEST_REPO
    with its load output discarded, runs PRELUDE and, unless stub_gpu is False, GPU_STUB, then
    `body`. The caller runs it in exmod_env with TEST_REPO set to a folder holding exmod.json."""
    return (". (Join-Path $env:EXTOOLS_ROOT 'exmod.ps1') -RepoRoot $env:TEST_REPO 6>$null; "
            + PRELUDE + (GPU_STUB if stub_gpu else "") + body)
