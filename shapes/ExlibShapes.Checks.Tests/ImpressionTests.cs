using System.Linq;
using ExpandedLib.Shapes.Core;
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
      "in: 8 vx3 of sand in the metal, worst 0.5 plan cell x 18 z -1: 1 vx3  FAIL",
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
}
