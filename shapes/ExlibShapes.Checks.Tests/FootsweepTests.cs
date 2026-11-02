using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class FootsweepTests {
  private static readonly Footsweep Check = new();

  public static TheoryData<string> Names =>
    [.. Fixtures.Parity("footsweep").Keys];

  private static readonly Regex Group = new(
    @"^cell .*? reach\s+(\S+) \((.+?)\) (\S+)$"
  );

  private static FootprintSpec SpecOf(string name) =>
    name switch {
      "foot.json+golden" => new FootprintSpec(
        Footsweep.GoldenCells(Fixtures.Text("golden.json"))
      ),
      "foot.json+extra" => new FootprintSpec(Extra: [new FootCell(0, 0, 1)]),
      _ => new FootprintSpec(),
    };

  [Theory]
  [MemberData(nameof(Names))]
  public void Footsweep_parity(string name) {
    Recorded want = Fixtures.Parity("footsweep")[name];
    CheckResult got = Check.Run(
      new CheckInput(
        Fixtures.Shape(name.Split('+')[0]),
        Footprint: SpecOf(name)
      )
    );
    Assert.Equal(want.Exit, got.ExitCode);
    Assert.Equal(want.Enumerated, got.Enumerated);
    Assert.Equal(
      want.Faulted,
      got.Lines.Select(l => Group.Match(l))
        .Where(m => m.Success && double.Parse(m.Groups[1].Value) > 4 + 1e-6)
        .Select(m => m.Groups[3].Value)
        .Order(System.StringComparer.Ordinal)
    );
  }

  [Fact]
  public void Footsweep_sweeps_every_clip_frame_and_names_the_pose() {
    CheckResult r = Check.Run(new CheckInput(Fixtures.Shape("foot-clip.json")));
    Assert.Contains(r.Lines, l => l.Contains("reach  6.00 (reach f3) Body"));
    Assert.Contains("worst reach 6.00 past the footprint; limit 4", r.Lines);
  }

  [Fact]
  public void Footsweep_golden_box_limits_the_reach() {
    CheckResult r = Check.Run(
      new CheckInput(
        Fixtures.Shape("foot.json"),
        Footprint: SpecOf("foot.json+golden")
      )
    );
    Assert.Contains("worst reach 1.00 past the footprint; limit 4", r.Lines);
  }

  [Fact]
  public void Footsweep_attributes_boundary_samples_to_their_element() {
    CheckResult r = Check.Run(new CheckInput(Fixtures.Shape("foot.json")));
    string[] cells =
    [
      .. r
        .Lines.Where(l => l.StartsWith("cell"))
        .Select(l => l[..(l.IndexOf(')') + 1)]),
    ];
    Assert.Equal(["cell (0, 0, 1)"], cells);
  }

  private static CheckResult Reaching(params ReachAllow[] reach) {
    const string frame = """
      {"elements": [{"name": "Frame", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
        {"name": "Body", "from": [0, 0, 0], "to": [16, 16, 21], "faces": {"up": {"texture": "#iron"}}},
        {"name": "Arm", "from": [0, 0, 16], "to": [2, 2, 19], "faces": {"up": {"texture": "#iron"}}}]}],
       "animations": []}
      """;
    return Check.Run(
      new CheckInput(
        Reader.Read(frame),
        Footprint: new FootprintSpec(Reach: reach, Depth: 1)
      )
    );
  }

  private static ReachAllow Allow(
    string el,
    int[] cell,
    double max,
    string why
  ) => new(el, cell, max, why);

  [Fact]
  public void A_listed_reach_within_its_max_passes_with_its_reason() {
    CheckResult r = Reaching(Allow("Frame/Body", [0, 0, 1], 5, "ruled"));
    Assert.Equal(0, r.ExitCode);
    Assert.Contains(
      r.Lines,
      l => l.EndsWith("reach  5.00 (rest) Frame/Body  allowed to 5: ruled")
    );
    Assert.Contains(r.Lines, l => l.EndsWith("reach  3.00 (rest) Frame/Arm"));
    Assert.Contains("worst reach 3.00 past the footprint; limit 4", r.Lines);
  }

  [Fact]
  public void A_reach_past_its_entrys_max_fails() {
    CheckResult r = Reaching(Allow("Frame/Body", [0, 0, 1], 4.5, "ruled"));
    Assert.Equal(1, r.ExitCode);
    Assert.Contains(
      r.Lines,
      l => l.EndsWith("reach  5.00 (rest) Frame/Body  past its allowed 4.5")
    );
  }

  [Fact]
  public void An_entry_holds_to_its_cell_and_unused_entries_print() {
    CheckResult r = Reaching(
      Allow("Frame/Body", [0, 0, -1], 5, "x"),
      Allow("Frame/Arm", [0, 0, 1], 5, "y")
    );
    Assert.Equal(1, r.ExitCode);
    Assert.Contains("worst reach 5.00 past the footprint; limit 4", r.Lines);
    Assert.Contains(
      "reach: Frame/Body [0, 0, -1] unused, the reach passes as drawn",
      r.Lines
    );
    Assert.Contains(
      "reach: Frame/Arm [0, 0, 1] unused, the reach passes as drawn",
      r.Lines
    );
  }

  [Fact]
  public void An_entry_naming_a_group_covers_its_subtree() {
    CheckResult r = Reaching(Allow("Frame", [0, 0, 1], 5, "ruled"));
    Assert.Equal(0, r.ExitCode);
    Assert.Contains("worst reach 3.00 past the footprint; limit 4", r.Lines);
    Assert.DoesNotContain(r.Lines, l => l.Contains("unused"));
  }

  [Fact]
  public void Of_two_entries_for_one_cell_the_nearest_holds() {
    CheckResult r = Reaching(
      Allow("Frame", [0, 0, 1], 5, "group"),
      Allow("Frame/Body", [0, 0, 1], 4.5, "body")
    );
    Assert.Equal(1, r.ExitCode);
    Assert.Contains(
      r.Lines,
      l => l.EndsWith("reach  5.00 (rest) Frame/Body  past its allowed 4.5")
    );
  }

  [Fact]
  public void An_entry_naming_a_missing_element_is_a_usage_error() {
    CheckUsageException e = Assert.Throws<CheckUsageException>(() =>
      Reaching(Allow("Frame/Nope", [0, 0, 1], 5, "x"))
    );
    Assert.Contains("Frame/Nope", e.Message);
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
  public void A_run_is_rest_a_clip_or_a_held_frame_and_refuses_the_rest() {
    Shape s = Fixtures.Shape("foot-clip.json");
    Assert.Equal(["rest"], PoseRuns.Run(s, "rest").Select(r => r.Label));
    Assert.Equal(
      ["reach f0", "reach f1", "reach f2", "reach f3"],
      PoseRuns.Run(s, "reach").Select(r => r.Label)
    );
    Assert.Equal(["reach f3"], PoseRuns.Run(s, "reach@3").Select(r => r.Label));
    Assert.Throws<CheckUsageException>(() => PoseRuns.Run(s, "nope"));
    Assert.Contains(
      "a run is rest, CLIP or CLIP@F",
      Assert
        .Throws<CheckUsageException>(() => PoseRuns.Run(s, "reach+reach"))
        .Message
    );
    Shape v1 = Fixtures.Edited(
      "foot-clip.json",
      r => r["animations"]![0]!["version"] = 1
    );
    Assert.Throws<CheckUsageException>(() => PoseRuns.Run(v1, "reach"));
  }

  [Fact]
  public void Golden_cells_read_collision_boxes_whole_cells_and_attribute_keys() {
    const string golden = """
      {"attributes": {"fillerOffsets": [{"x": 0, "y": 0, "z": 1}]},
       "attributesByType": {"*-ns": {"fillerOffsets": [
         {"x": 1, "y": 0, "z": 0, "collisionBoxes": [
           {"x1": 0, "y1": 0, "z1": 0, "x2": 1, "y2": 1, "z2": 0.5},
           {"x1": 0, "y1": 0, "z1": 0.5, "x2": 0.5, "y2": 1, "z2": 1}]}]}}}
      """;
    Assert.Equal(
      new[] { new FootCell(0, 0, 1) },
      Footsweep.GoldenCells(golden)
    );
    IReadOnlyList<FootCell> keyed = Footsweep.GoldenCells(golden, "*-ns");
    Assert.Equal(2, keyed.Count);
    Assert.Equal(new double[] { 0, 0, 0, 1, 1, 0.5 }, keyed[0].Box);
    Assert.Equal(new double[] { 0, 0, 0.5, 0.5, 1, 1 }, keyed[1].Box);
    Assert.Throws<CheckUsageException>(() =>
      Footsweep.GoldenCells(golden, "*-ew")
    );
  }
}
