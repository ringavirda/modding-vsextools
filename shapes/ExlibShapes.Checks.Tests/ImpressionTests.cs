using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class ImpressionTests {
  private static readonly Impression Check = new();

  public static TheoryData<string> Names =>
    [.. Fixtures.Parity("impression").Keys];

  [Theory]
  [MemberData(nameof(Names))]
  public void Impression_parity(string name) {
    Recorded want = Fixtures.Parity("impression")[name];
    CheckResult got = Check.Run(
      PourFixtures.Input(Fixtures.Shape(name + ".json"), PourFixtures.One())
    );
    Assert.Equal(want.Exit, got.ExitCode);
    Assert.True(got.Enumerated > 0);
    Assert.Equal(
      want.Faulted,
      got.Lines.Where(l => l.EndsWith("  FAIL"))
        .Select(l => l[..l.IndexOf(':')])
        .Order()
    );
  }

  [Fact]
  public void Sand_in_the_metal_names_its_volume_and_cell() {
    CheckResult r = Check.Run(
      PourFixtures.Input(Fixtures.Shape("fill-in.json"), PourFixtures.One())
    );
    Assert.Contains(
      "in: 8 vx3 of sand in the metal, worst 0.5 plan cell x 18 z -1: 1 vx3, band 0  FAIL",
      r.Lines
    );
  }

  [Fact]
  public void A_cavity_outside_the_metal_is_rejected_by_this_gate_alone() {
    Assert.Equal(
      ["impression"],
      PourFixtures.Rejecting(
        PourFixtures.Input(Fixtures.Shape("fill-open.json"), PourFixtures.One())
      )
    );
  }

  [Fact]
  public void Sand_in_the_metal_is_rejected_by_this_gate_alone() {
    Assert.Equal(
      ["impression"],
      PourFixtures.Rejecting(
        PourFixtures.Input(Fixtures.Shape("fill-in.json"), PourFixtures.One())
      )
    );
  }

  [Fact]
  public void A_matching_filling_passes_every_filling_gate() {
    Assert.Empty(
      PourFixtures.Rejecting(
        PourFixtures.Input(Fixtures.Shape("fill-ok.json"), PourFixtures.One())
      )
    );
  }

  [Fact]
  public void A_turned_sand_leaf_in_the_metal_counts_its_own_volume() {
    Shape fill = PourFixtures.WithTurned(
      "fill-ok.json",
      PourFixtures.Turned("Cube", 15, 11, -1, 17, 13, 1)
    );
    CheckResult r = Check.Run(PourFixtures.Input(fill, PourFixtures.One()));
    Assert.Equal(1, r.ExitCode);
    Assert.InRange(PourFixtures.Number(r.Lines, "in: "), 7.9, 8.1);
  }

  private static CheckResult RunWithTurnedCast(string filling, int leaves) {
    Shape cast = Fixtures.Edited(
      "fill-cast.json",
      raw => {
        for (int i = 0; i < leaves; i++)
          ((JArray)raw["elements"]!).Add(
            JObject.Parse(PourFixtures.Turned($"T{i}", 1, 0, 1, 5, 4, 5))
          );
      }
    );
    return Check.Run(
      PourFixtures.Input(
        Fixtures.Shape(filling),
        PourFixtures.One(),
        cast: cast
      )
    );
  }

  [Fact]
  public void Sand_in_the_metal_over_the_turned_band_fails() {
    CheckResult r = RunWithTurnedCast("fill-in.json", 1);

    Assert.Equal(1, r.ExitCode);
    Assert.Contains(
      r.Lines,
      l => l.StartsWith("in: 8 vx3") && l.Contains("band 4")
    );
  }

  [Fact]
  public void Sand_in_the_metal_within_the_turned_band_passes() {
    CheckResult r = RunWithTurnedCast("fill-in.json", 3);

    Assert.Equal(0, r.ExitCode);
    Assert.Contains("band 12 vx3", r.Lines[^1]);
  }

  [Fact]
  public void A_cavity_over_the_turned_band_fails() {
    CheckResult r = RunWithTurnedCast("fill-open.json", 1);

    Assert.Equal(1, r.ExitCode);
    Assert.Contains(
      r.Lines,
      l => l.StartsWith("open: 24 vx3") && l.Contains("band 4")
    );
    Assert.DoesNotContain(r.Lines, l => l.StartsWith("in:"));
  }

  [Fact]
  public void A_cavity_within_the_turned_band_passes() {
    CheckResult r = RunWithTurnedCast("fill-open.json", 7);

    Assert.Equal(0, r.ExitCode);
    Assert.Contains("band 28 vx3", r.Lines[^1]);
  }

  [Fact]
  public void Sand_in_axis_aligned_metal_fails_at_any_volume() {
    Shape fill = Fixtures.Edited(
      "fill-ok.json",
      raw =>
        ((JArray)raw["elements"]!).Add(
          JObject.Parse(
            """{ "name": "Sliver", "from": [18.875, 12, 0], "to": [19, 12.125, 0.125], "faces": { "up": { "texture": "#andesite" } } }"""
          )
        )
    );

    CheckResult r = Check.Run(PourFixtures.Input(fill, PourFixtures.One()));

    Assert.Equal(1, r.ExitCode);
    Assert.Contains("band 0 vx3", r.Lines[^1]);
    Assert.InRange(PourFixtures.Number(r.Lines, "in: "), 0.0019, 0.002);
  }
}
