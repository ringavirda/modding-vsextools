using System;
using System.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class UvwrapTests {
  private static readonly Uvwrap Check = new();

  public static TheoryData<string> Names => [.. Fixtures.Parity("uvwrap").Keys];

  private static string[] FailedParents(CheckResult r) =>
    [
      .. r
        .Lines.Where(l => l.Contains("  FAIL"))
        .Select(l => l[..l.IndexOf("  ", StringComparison.Ordinal)])
        .Order(StringComparer.Ordinal),
    ];

  [Theory]
  [MemberData(nameof(Names))]
  public void Uvwrap_parity(string name) {
    Recorded want = Fixtures.Parity("uvwrap")[name];
    CheckResult got = Check.Run(new CheckInput(Fixtures.Shape(name)));
    Assert.Equal(want.Exit, got.ExitCode);
    Assert.Equal(want.Enumerated, got.Enumerated);
    Assert.Equal(want.Faulted, FailedParents(got));
  }

  [Fact]
  public void Uvwrap_passes_an_unrolled_ring_and_octagon() {
    CheckResult ring = Check.Run(
      new CheckInput(Fixtures.Shape("uvwrap-ring.json"))
    );
    Assert.Contains("/  8 members  #iron  PASS", ring.Lines);
    Assert.Contains("1 round runs; 0 fail", ring.Lines);
    CheckResult oct = Check.Run(
      new CheckInput(Fixtures.Shape("uvwrap-octagon.json"))
    );
    Assert.Contains("/  6 members  #iron  PASS", oct.Lines);
  }

  [Fact]
  public void Uvwrap_faults_random_rects() {
    CheckResult r = Check.Run(
      new CheckInput(Fixtures.Shape("uvwrap-ring-random-rects.json"))
    );
    Assert.Equal(1, r.ExitCode);
    Assert.Contains(
      r.Lines,
      l =>
        l.StartsWith("/  8 members  #iron  FAIL  Bar", StringComparison.Ordinal)
        && l.Contains(": ends at ")
    );
  }

  [Fact]
  public void Uvwrap_faults_one_rect_repeated() {
    var shape = Fixtures.Edited(
      "uvwrap-ring.json",
      raw => {
        foreach (var e in raw["elements"]!)
          e["faces"]!["up"]!["uv"] = new Newtonsoft.Json.Linq.JArray(
            2.0,
            0.0,
            8.0,
            2.0
          );
      }
    );
    CheckResult r = Check.Run(new CheckInput(shape));
    Assert.Equal(1, r.ExitCode);
    Assert.Contains(
      r.Lines,
      l => l.EndsWith("all 8 faces carry one rect", StringComparison.Ordinal)
    );
  }

  [Fact]
  public void Uvwrap_reads_a_ring_with_no_outward_face_as_a_failure() {
    CheckResult r = Check.Run(
      new CheckInput(Fixtures.Shape("ring-small.json"))
    );
    Assert.Contains("Ring  8 members  -  FAIL  no outward face", r.Lines);
  }

  [Fact]
  public void Uvwrap_directed_judges_wood_grain_along_the_axis() {
    var turned = Fixtures.Edited(
      "uvwrap-ring.json",
      raw => {
        foreach (var e in raw["elements"]!)
          e["faces"]!["up"]!["rotation"] = 90;
      }
    );
    var wood = new System.Collections.Generic.HashSet<string> { "iron" };
    CheckResult plain = Check.Run(new CheckInput(turned, Directed: true));
    Assert.DoesNotContain(
      plain.Lines,
      l => l.Contains("the grain runs round the axis")
    );
    CheckResult grain = Check.Run(
      new CheckInput(turned, WoodKeys: wood, Directed: true)
    );
    Assert.Equal(1, grain.ExitCode);
    Assert.Contains(
      grain.Lines,
      l =>
        l.EndsWith(
          "up: the grain runs round the axis",
          StringComparison.Ordinal
        )
    );
  }

  [Fact]
  public void Empty_input_fails() {
    CheckResult r = Check.Run(new CheckInput(null));
    Assert.Equal(Verdict.Fail, r.Verdict);
    Assert.Equal(0, r.Enumerated);
    var bare = Fixtures.Edited(
      "uvwrap-ring.json",
      raw => raw["elements"] = new Newtonsoft.Json.Linq.JArray()
    );
    Assert.Equal(1, Check.Run(new CheckInput(bare)).ExitCode);
  }
}
