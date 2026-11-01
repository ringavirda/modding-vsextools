using System.Linq;
using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class FloodTests {
  private static readonly Flood Check = new();

  public static TheoryData<string> Names = [.. Fixtures.Parity("flood").Keys];

  private static CheckInput Chain(string name) =>
    PourFixtures.Input(
      Fixtures.Shape(name + ".json"),
      PourFixtures.Record(
        [PourFixtures.At(13, 10, -3), PourFixtures.At(13, 10, -13)],
        [PourFixtures.Runner1, PourFixtures.Gate2],
        720
      )
    );

  private static CheckInput For(string name) =>
    name.StartsWith("fill-chain")
      ? Chain(name)
      : PourFixtures.Input(Fixtures.Shape(name + ".json"), PourFixtures.One());

  [Theory]
  [MemberData(nameof(Names))]
  public void Flood_parity(string name) {
    Recorded want = Fixtures.Parity("flood")[name];
    CheckResult got = Check.Run(For(name));
    Assert.Equal(want.Exit, got.ExitCode);
    Assert.True(got.Enumerated > 0);
    Assert.Equal(
      want.Faulted,
      got.Lines.Where(l => l.EndsWith("  FAIL"))
        .Select(l => l.StartsWith("no cavity") ? "seed" : "unreached")
        .OrderBy(c => c != "seed")
        .Take(1)
    );
  }

  [Fact]
  public void Flood_reaches_every_cavity() {
    CheckResult r = Check.Run(For("fill-ok"));
    Assert.Equal(0, r.ExitCode);
    Assert.Contains(
      r.Lines,
      l => l.StartsWith("flood: ") && l.Contains("seeded 4096")
    );
  }

  [Fact]
  public void A_piece_with_no_feed_to_it_names_its_volume_and_cell() {
    var input = PourFixtures.Input(
      Fixtures.Shape("fill-island.json"),
      PourFixtures.Record(
        [PourFixtures.At(13, 10, -3)],
        [PourFixtures.Runner1],
        400
      ),
      Fixtures.Shape("fill-island-cast.json")
    );
    CheckResult r = Check.Run(input);
    Assert.Contains(
      "Piece1: 16 of 160 vx3 of cast not reached, worst 0.5 plan cell x 21 z -3: 1 vx3  FAIL",
      r.Lines
    );
  }

  [Fact]
  public void A_cast_the_metal_cannot_reach_is_rejected_by_this_gate_alone() {
    var input = PourFixtures.Input(
      Fixtures.Shape("fill-island.json"),
      PourFixtures.Record(
        [PourFixtures.At(13, 10, -3)],
        [PourFixtures.Runner1],
        400
      ),
      Fixtures.Shape("fill-island-cast.json")
    );
    Assert.Equal(["flood"], PourFixtures.Rejecting(input));
  }
}
