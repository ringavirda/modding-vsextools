"""Tests for Get-ExmodTestFailures in exmod/src.ps1, run through pwsh ($PWSH, PATH, or the
checkout's .dotnet/tools); skipped when no pwsh is found."""

import json
import os
import shutil
import subprocess
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PWSH = (os.environ.get("PWSH") or shutil.which("pwsh")
        or next((p for p in [os.path.join(ROOT, ".dotnet", "tools", "pwsh")] if os.access(p, os.X_OK)), None))


def failures(lines):
    """[(name, message)] Get-ExmodTestFailures pulls from the console log lines."""
    script = ("function Add-ExmodCommand { }; . (Join-Path $env:EXTOOLS_ROOT 'exmod/src.ps1'); "
              "$lines = @(Get-Content -LiteralPath $env:FAILURE_LOG); "
              "ConvertTo-Json -Compress -InputObject @(Get-ExmodTestFailures $lines)")
    with tempfile.TemporaryDirectory() as d:
        log = os.path.join(d, "console.log")
        with open(log, "w") as f:
            f.write("\n".join(lines) + "\n")
        out = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", script],
                             env=dict(os.environ, EXTOOLS_ROOT=ROOT, FAILURE_LOG=log),
                             capture_output=True, text=True, check=True).stdout
    return [(f["Name"], f["Message"]) for f in json.loads(out)]


@unittest.skipUnless(PWSH, "pwsh not found")
class FailureLineTests(unittest.TestCase):
    def test_a_plain_test_and_a_theory_row_with_spaces_are_named_with_their_messages(self):
        found = failures([
            "  Failed Mod.Tests.Boiler.Holds_pressure [12 ms]",
            "  Error Message:",
            "   Assert.Equal() Failure",
            '  Failed Mod.Tests.Pipe.Bursts(tier: "cast iron", atm: [1, 2]) [3 ms]',
            "  Error Message:",
            "   Assert.True() Failure",
        ])
        self.assertEqual([
            ("Mod.Tests.Boiler.Holds_pressure", "Assert.Equal() Failure"),
            ('Mod.Tests.Pipe.Bursts(tier: "cast iron", atm: [1, 2])', "Assert.True() Failure"),
        ], found)

    def test_a_logged_failed_line_is_not_a_failure_and_does_not_end_the_message_search(self):
        found = failures([
            "  Failed Mod.Tests.Loader.Reads_assets [40 ms]",
            "  Standard Output Messages:",
            "     Failed to load asset [game:x]",
            "  Error Message:",
            "   Assert.NotNull() Failure",
            "Failed!  - Failed:     1, Passed:    12, Skipped:     0, Total:    13",
        ])
        self.assertEqual([("Mod.Tests.Loader.Reads_assets", "Assert.NotNull() Failure")], found)


if __name__ == "__main__":
    unittest.main()
