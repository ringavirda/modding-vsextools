using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class SplitTests {
  private static readonly Split Check = new();

  private static Shape Wide(double x1) =>
    Fixtures.Edited(
      "cast-ok.json",
      raw => CastFixtures.Move(CastFixtures.Find(raw, "Seat"), "to", 0, x1)
    );

  [Fact]
  public void Sixteen_holds_and_sixteen_and_a_half_is_named() {
    Assert.Equal(0, Check.Run(new CheckInput(Wide(20))).ExitCode);
    CheckResult r = Check.Run(new CheckInput(Wide(20.5)));
    Assert.Equal(1, r.ExitCode);
    Assert.Equal(3, r.Enumerated);
    Assert.Contains("Bed/Seat: x is 16.5 voxels, over 16", r.Lines);
  }

  [Theory]
  [InlineData(1, "y")]
  [InlineData(2, "z")]
  public void Every_axis_is_read(int axis, string name) {
    Shape tall = Fixtures.Edited(
      "cast-ok.json",
      raw => CastFixtures.Move(CastFixtures.Find(raw, "Seat"), "to", axis, 18.5)
    );
    Assert.Contains(
      $"Bed/Seat: {name} is {(axis == 1 ? 16.5 : 18.5)} voxels, over 16",
      Check.Run(new CheckInput(tall)).Lines
    );
  }

  [Fact]
  public void A_16_5_element_is_rejected_by_this_gate_alone() {
    Shape item = Wide(20.5);
    Shape source = Fixtures.Edited(
      "cast-source.json",
      raw => CastFixtures.Move(CastFixtures.Find(raw, "Seat"), "to", 0, 20.5)
    );
    var input = new CheckInput(
      item,
      Record: CastFixtures.Record(CastFixtures.CastRecord),
      Settings: CastFixtures.Settings(),
      Source: source,
      SourceRecord: CastFixtures.Record(
        CastFixtures.CastRecord + "," + CastFixtures.PlaneWay
      )
    );
    Assert.Equal(["split"], CastFixtures.Rejecting(input));
  }

  [Fact]
  public void Empty_input_fails() {
    Assert.Equal(Verdict.Fail, Check.Run(new CheckInput(null)).Verdict);
  }
}
