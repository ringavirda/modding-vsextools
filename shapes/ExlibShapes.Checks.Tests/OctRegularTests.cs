using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class OctRegularTests {
  private static readonly OctRegular Check = new();

  [Fact]
  public void A_chamfered_bar_the_glob_names_is_held_regular() {
    CheckResult r = Check.Run(
      new CheckInput(Fixtures.Shape("octagon-small.json"), Regular: ["*"])
    );
    Assert.Equal(1, r.ExitCode);
    Assert.Equal(1, r.Enumerated);
    Assert.Contains(
      "  FAULT Bar: arms 2.0000 wide on 4.0000 across, not 1.6568 (regular)",
      r.Lines
    );
    Assert.Contains(
      "  FAULT Bar: 4.2426 across the diagonals against 4.0000 on the axes",
      r.Lines
    );
    Assert.Contains("1 regular octagons held; faults", r.Lines);
  }

  [Fact]
  public void A_regular_bar_passes() {
    CheckResult r = Check.Run(
      new CheckInput(Fixtures.Shape("shaft-small.json"), Regular: ["*"])
    );
    Assert.Equal(0, r.ExitCode);
    Assert.Contains("1 regular octagons held; no fault", r.Lines);
  }

  [Fact]
  public void Octagons_no_glob_names_are_not_read_and_nothing_read_fails() {
    CheckResult r = Check.Run(
      new CheckInput(Fixtures.Shape("octagon-small.json"), Regular: ["Bolt*"])
    );
    Assert.Equal(1, r.ExitCode);
    Assert.Equal(0, r.Enumerated);
    CheckResult none = Check.Run(
      new CheckInput(Fixtures.Shape("shaft-small.json"))
    );
    Assert.Equal(0, none.Enumerated);
  }

  [Fact]
  public void Plate_rings_and_gear_hubs_are_not_read() {
    CheckResult r = Check.Run(
      new CheckInput(Fixtures.Shape("ring-regular-small.json"), Regular: ["*"])
    );
    Assert.Equal(0, r.Enumerated);
  }

  [Fact]
  public void Octregular_is_a_period_check() {
    Assert.Equal(Profile.ExpandedPeriod, Check.Profile);
  }
}
