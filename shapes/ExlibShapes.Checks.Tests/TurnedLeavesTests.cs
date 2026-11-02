using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class TurnedLeavesTests {
  private static Shape Turned() => Fixtures.Shape("pattern-turned.json");

  [Fact]
  public void Draw_reads_a_pattern_of_turned_leaves() {
    CheckResult r = new Draw().Run(new CheckInput(Turned(), Parting: 8));
    Assert.InRange(r.ExitCode, 0, 1);
    Assert.True(r.Enumerated > 0);
  }

  [Fact]
  public void Impression_reads_a_shape_of_turned_leaves() {
    CheckResult r = new Impression().Run(
      PourFixtures.Input(Turned(), PourFixtures.One())
    );
    Assert.InRange(r.ExitCode, 0, 1);
  }
}
