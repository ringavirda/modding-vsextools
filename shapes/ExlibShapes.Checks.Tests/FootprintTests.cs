using System.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class FootprintTests {
  public static TheoryData<string> Names =>
    [.. Fixtures.Parity("footprint").Keys];

  private static readonly (int, int, int)[] Cube =
  [
    (0, 0, 0),
    (0, 0, 1),
    (0, 1, 0),
    (0, 1, 1),
    (1, 0, 0),
    (1, 0, 1),
    (1, 1, 0),
    (1, 1, 1),
  ];

  [Theory]
  [MemberData(nameof(Names))]
  public void Footprint_parity(string name) {
    Recorded want = Fixtures.Parity("footprint")[name];
    var (findings, enumerated) = name.StartsWith("mppump")
      ? FootprintLint.Check(Fixtures.Shape(name), Cube, ["Suction", "Delivery"])
      : FootprintLint.Check(Fixtures.Shape(name), [(0, 0, 0)]);
    Assert.Equal(want.Exit, findings.Count > 0 ? 1 : 0);
    Assert.Equal(want.Enumerated, enumerated);
    Assert.Equal(want.Faulted, findings.Select(f => f.Message).Order());
  }

  [Fact]
  public void No_declared_cells_check_nothing() {
    var (findings, enumerated) = FootprintLint.Check(
      Fixtures.Shape("foot.json"),
      []
    );
    Assert.Empty(findings);
    Assert.Equal(0, enumerated);
  }
}
