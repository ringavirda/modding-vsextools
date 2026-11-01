using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class ParityLiveTests {
  [SkippableFact]
  public void Recorded_parity_matches_a_live_vsshape_run() {
    string? vs = Environment.GetEnvironmentVariable("VSSHAPE");
    Skip.If(
      string.IsNullOrEmpty(vs),
      "VSSHAPE does not name a vsshape checkout"
    );
    string python = Path.Combine(vs!, ".venv", "bin", "python");
    Skip.IfNot(File.Exists(python), $"no {python}");
    string live = Path.Combine(
      Path.GetTempPath(),
      "parity-" + Guid.NewGuid().ToString("N")
    );
    Directory.CreateDirectory(Path.Combine(live, "parity"));
    try {
      foreach (
        string rec in new[]
        {
          "record_st7.py",
          "record_st8.py",
          "record_st9.py",
        }
      ) {
        var start = new ProcessStartInfo(python) {
          RedirectStandardOutput = true,
          RedirectStandardError = true,
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(
          Path.Combine(AppContext.BaseDirectory, "recorders", rec)
        );
        start.Environment["VSSHAPE"] = vs;
        start.Environment["PARITY_OUT"] = live;
        using Process p = Process.Start(start)!;
        string err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{rec}: {err}");
      }
      string[] files = Directory.GetFiles(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "parity"),
        "*.json"
      );
      Assert.NotEmpty(files);
      foreach (string recorded in files) {
        string name = Path.GetFileName(recorded);
        JToken want = JToken.Parse(File.ReadAllText(recorded));
        JToken got = JToken.Parse(
          File.ReadAllText(Path.Combine(live, "parity", name))
        );
        Assert.True(
          JToken.DeepEquals(want, got),
          $"{name} drifted from vsshape:\nrecorded {want}\nlive {got}"
        );
      }
    } finally {
      Directory.Delete(live, true);
    }
  }
}
