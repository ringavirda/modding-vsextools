using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class FlushcheckTests {
  private static readonly Flushcheck Check = new();

  public static TheoryData<string> Names =>
    [.. Fixtures.Parity("flushcheck").Keys];

  private static string[] PairNames(CheckResult r) =>
    [
      .. r
        .Lines.Where(l => l.StartsWith("flush  "))
        .Select(l =>
          l[7..l.IndexOf("  area ", System.StringComparison.Ordinal)]
        )
        .Order(System.StringComparer.Ordinal),
    ];

  [Theory]
  [MemberData(nameof(Names))]
  public void Flushcheck_parity(string name) {
    Recorded want = Fixtures.Parity("flushcheck")[name];
    bool noHide = name.EndsWith("(no hide)");
    string file = noHide ? name[..name.IndexOf(' ')] : name;
    CheckResult got = Check.Run(
      new CheckInput(Fixtures.Shape(file), Hide: noHide ? [] : null)
    );
    Assert.Equal(want.Exit, got.ExitCode);
    Assert.Equal(want.Enumerated, got.Enumerated);
    Assert.Equal(want.Faulted, PairNames(got));
    Shape shape = Fixtures.Shape(file);
    var (_, found) = Flushcheck.Pairs(
      shape,
      null,
      noHide ? [] : Flushcheck.FillingHides(shape)
    );
    double[] areas =
    [
      .. found
        .OrderBy(
          p => $"{p.Path1} {p.Face1} | {p.Path2} {p.Face2}",
          StringComparer.Ordinal
        )
        .Select(p => p.Area),
    ];
    Assert.Equal(want.Areas.Length, areas.Length);
    for (int i = 0; i < areas.Length; i++)
      Assert.True(
        Math.Abs(want.Areas[i] - areas[i]) < 1e-4,
        $"{want.Faulted[i]}: want {want.Areas[i]}, got {areas[i]}"
      );
  }

  [Fact]
  public void Flushcheck_derives_the_hide_from_a_filling_structure() {
    Shape filling = Fixtures.Shape("filling-small.json");
    Assert.Equal(["Piece1/Level"], Flushcheck.FillingHides(filling));
    Assert.Empty(Flushcheck.FillingHides(Fixtures.Shape("flush.json")));
    Assert.Equal(0, Check.Run(new CheckInput(filling)).ExitCode);
  }

  [Fact]
  public void Hide_leaves_a_subtree_out_and_names_a_missing_one_a_usage_error() {
    CheckResult r = Check.Run(
      new CheckInput(Fixtures.Shape("flush.json"), Hide: ["B"])
    );
    Assert.Equal(0, r.ExitCode);
    Assert.Contains("6 faces; 0 flush pairs", r.Lines);
    CheckUsageException e = Assert.Throws<CheckUsageException>(() =>
      Check.Run(new CheckInput(Fixtures.Shape("flush.json"), Hide: ["Nope"]))
    );
    Assert.Contains("Nope", e.Message);
  }

  private static CheckResult Moved(double dy, double dz) {
    Shape s = Fixtures.Edited(
      "flush.json",
      r => {
        JObject b = Fixtures.Element(r, "B");
        b["from"] = new JArray(2, dy, dz);
        b["to"] = new JArray(6, 4 + dy, 4 + dz);
      }
    );
    return Check.Run(new CheckInput(s));
  }

  [Fact]
  public void Flushcheck_near_planes_are_not_flush() {
    CheckResult near = Moved(0.01, 0.01);
    Assert.Equal(0, near.ExitCode);
    Assert.Contains("12 faces; 0 flush pairs", near.Lines);
    CheckResult within = Moved(0.0005, 0.0005);
    Assert.Equal(1, within.ExitCode);
    Assert.Contains("flush  A north | B north  area 8.00", within.Lines);
  }

  private static CheckResult Tilted(
    double deg,
    double pivotX,
    double dy = 0,
    double dz = 0
  ) =>
    Check.Run(
      new CheckInput(
        Fixtures.Edited(
          "flush.json",
          r => {
            foreach (string name in new[] { "A", "B" }) {
              JObject e = Fixtures.Element(r, name);
              e["from"]![0] = (double)e["from"]![0]! + 260;
              e["to"]![0] = (double)e["to"]![0]! + 260;
            }
            JObject b = Fixtures.Element(r, "B");
            foreach (string end in new[] { "from", "to" }) {
              b[end]![1] = (double)b[end]![1]! + dy;
              b[end]![2] = (double)b[end]![2]! + dz;
            }
            b["rotationOrigin"] = new JArray(pivotX, 0, 0);
            b["rotationY"] = deg;
          }
        )
      )
    );

  [Fact]
  public void Flushcheck_finds_a_tilted_pair_far_from_the_origin() {
    CheckResult r = Tilted(0.0172, 263);
    Assert.Equal(1, r.ExitCode);
    Assert.Contains("flush  A north | B north  area 8.00", r.Lines);
    Assert.Contains("12 faces; 4 flush pairs", r.Lines);
  }

  [Fact]
  public void Flushcheck_leaves_a_separated_tilted_pair_and_measures_each_sample() {
    CheckResult apart = Tilted(0.2521, 263, 0.01, 0.01);
    Assert.Equal(0, apart.ExitCode);
    Assert.Contains("12 faces; 0 flush pairs", apart.Lines);
    CheckResult samples = Tilted(0.0229, 263, 0.01, 0.0015);
    Assert.Equal(0, samples.ExitCode);
    Assert.Contains("12 faces; 0 flush pairs", samples.Lines);
  }

  [Fact]
  public void Flushcheck_counts_the_part_of_a_tilted_pair_within_the_tolerance() {
    CheckResult r = Tilted(0.0859, 262, 0.01);
    Assert.Equal(1, r.ExitCode);
    Assert.Contains("flush  A north | B north  area 2.60", r.Lines);
  }

  [Fact]
  public void Flushcheck_leaves_out_a_part_stretched_to_nothing() {
    Shape s = Fixtures.Shape("flush.json");
    Assert.NotEmpty(Flushcheck.Pairs(s, null, []).Found);
    var gone = new Dictionary<string, Pose> {
      ["B"] = new Pose { Stretch = Vec3.Zero },
    };
    (int faces, var found) = Flushcheck.Pairs(s, gone, []);
    Assert.Equal(6, faces);
    Assert.Empty(found);
  }

  [Fact]
  public void Empty_input_fails() {
    CheckResult r = Check.Run(new CheckInput(null));
    Assert.Equal(Verdict.Fail, r.Verdict);
    Assert.Equal(0, r.Enumerated);
    Shape bare = Reader.Read("""{"textures": {}, "elements": []}""");
    Assert.Equal(1, Check.Run(new CheckInput(bare)).ExitCode);
  }

  [Fact]
  public void References_match_a_subtree_one_element_or_every_named_one() {
    Assert.True(Refs.Matches("Root/A/B", "A"));
    Assert.True(Refs.Matches("A/B", "A/B"));
    Assert.False(Refs.Matches("A/BC", "A/B"));
    Assert.True(Refs.Matches("A/B", "A/B="));
    Assert.False(Refs.Matches("A/B/C", "A/B="));
    Assert.True(Refs.Matches("X/Level/C", "*Level"));
    Assert.True(Refs.Matches("X/Level", "*Level"));
    Assert.False(Refs.Matches("X/Level", "*Level/"));
    Assert.True(Refs.Matches("X/Level/C", "*Level/"));
  }
}
