"""What every test that loads exmod.ps1 shares: the checkout root, the pwsh to run it with ($PWSH,
PATH, or the checkout's .dotnet/tools; None when there is none), and the prelude that keeps a test
off the Windows side of the machine it runs on."""

import os
import shutil

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PWSH = (os.environ.get("PWSH") or shutil.which("pwsh")
        or next((p for p in [os.path.join(ROOT, ".dotnet", "tools", "pwsh")] if os.access(p, os.X_OK)), None))

# Runs after exmod.ps1 is loaded and before a test's own body: WSL interop reads as off, and every
# Windows program exmod starts by name (cmd.exe through Invoke-WindowsCmd, reg, reg.exe, wsl.exe)
# throws instead of running. A test that exercises interop stubs the Windows programs itself.
PRELUDE = r"""
function Test-WslInterop { $false }
function Invoke-WindowsCmd { throw "cmd.exe reached in a test: $args" }
function reg { throw "reg reached in a test: $args" }
function reg.exe { throw "reg.exe reached in a test: $args" }
function wsl.exe { throw "wsl.exe reached in a test: $args" }
"""

# Replaces the GPU registration with one line naming the exe it was given, " dry-run" appended under
# -DryRun, so no test reads or writes the registry of a Windows machine it runs on.
GPU_STUB = r"""
function Register-ClientGpuPreference([string]$ExePath, [switch]$DryRun) {
  Write-Host "gpu-stub: $ExePath$(if ($DryRun) { ' dry-run' })"
}
"""


def touch(path, text=""):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        f.write(text)


def exmod_script(body, stub_gpu=True):
    """The pwsh -Command text that dot-sources $env:EXTOOLS_ROOT/exmod.ps1 against $env:TEST_REPO
    with its load output discarded, runs PRELUDE and, unless stub_gpu is False, GPU_STUB, then
    `body`. The caller sets EXTOOLS_ROOT to ROOT and TEST_REPO to a folder holding exmod.json."""
    return (". (Join-Path $env:EXTOOLS_ROOT 'exmod.ps1') -RepoRoot $env:TEST_REPO 6>$null; "
            + PRELUDE + (GPU_STUB if stub_gpu else "") + body)
