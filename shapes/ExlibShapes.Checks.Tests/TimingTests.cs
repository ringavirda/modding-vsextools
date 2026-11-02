using System.Collections.Generic;
using System.IO;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class TimingTests {
  [Fact]
  public void Timing_appends_one_line_per_command() {
    using var dir = new TempDir();
    string log = Path.Combine(dir.Path, "state", "runs.jsonl");
    Timing.Append(
      log,
      "check",
      ["a", "b"],
      120,
      new Dictionary<string, int> { ["sizes"] = 4, ["facecheck"] = 9 },
      0
    );
    Timing.Append(log, "gen", ["c"], 7, new Dictionary<string, int>(), 3);

    string[] lines = File.ReadAllLines(log);
    Assert.Equal(2, lines.Length);
    JObject first = JObject.Parse(lines[0]);
    Assert.Equal("check", (string?)first["command"]);
    Assert.Equal(Provenance.Sha256Of("a\0b"), (string?)first["args"]);
    Assert.Equal(120, (long?)first["wallMs"]);
    Assert.Equal(9, (int?)first["enumerated"]!["facecheck"]);
    Assert.Equal(0, (int?)first["exit"]);
    Assert.Equal(3, (int?)JObject.Parse(lines[1])["exit"]);
  }

  [Fact]
  public void Timing_log_lives_under_xdg_state_home() {
    Assert.Equal(
      Path.Combine("/state", "exlib-shapes", "runs.jsonl"),
      Timing.DefaultPath(k => k == "XDG_STATE_HOME" ? "/state" : null)
    );
    Assert.EndsWith(
      Path.Combine(".local", "state", "exlib-shapes", "runs.jsonl"),
      Timing.DefaultPath(_ => "")
    );
  }
}
