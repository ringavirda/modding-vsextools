using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class StageParityTests {
  public static TheoryData<string> Pieces =>
    [.. Fixtures.Parity("pieces").Keys];
  public static TheoryData<string> Feeding =>
    [.. Fixtures.Parity("feeding").Keys];
  public static TheoryData<string> Pit => [.. Fixtures.Parity("pit").Keys];
  public static TheoryData<string> Parting =>
    [.. Fixtures.Parity("parting").Keys];
  public static TheoryData<string> Fit => [.. Fixtures.Parity("fit").Keys];

  private static (string Category, string Pattern) Row(
    string category,
    string pattern
  ) => (category, pattern);

  private static void Matches(
    string gate,
    string name,
    CheckResult got,
    Func<string, bool> isFault,
    params (string Category, string Pattern)[] rows
  ) {
    Recorded want = Fixtures.Parity(gate)[name];
    string[] kept = [.. want.Faulted.Except(StageParity.Omits(gate, name))];
    Assert.Equal(kept.Length > 0 ? 1 : 0, got.ExitCode);
    Assert.Equal(want.Enumerated, got.Enumerated);
    string[] faulted =
    [
      .. got
        .Lines.Where(isFault)
        .Select(l => rows.First(r => Regex.IsMatch(l, r.Pattern)).Category)
        .Distinct()
        .Order(StringComparer.Ordinal),
    ];
    Assert.Equal(kept, faulted);
  }

  private static bool NotSummary(string line) =>
    !Regex.IsMatch(line, @"^\w+: \d+ (rules read|clearances read)$");

  [Theory]
  [MemberData(nameof(Pieces))]
  public void Pieces_parity(string name) =>
    Matches(
      "pieces",
      name,
      new Pieces().Run(StageParity.Input("pieces", name)),
      l => l.EndsWith("  FAIL"),
      Row("count", "^count:"),
      Row("names", "^names:"),
      Row("holds", "^holds:"),
      Row("foot", "bounds differ"),
      Row("apart", "plan cells of Level in both"),
      Row("flush", "Level top at y|^flush:"),
      Row("cover", "^cover:"),
      Row("order", "^order ")
    );

  [Theory]
  [MemberData(nameof(Feeding))]
  public void Feeding_parity(string name) =>
    Matches(
      "feeding",
      name,
      new Feeding().Run(StageParity.Input("feeding", name)),
      NotSummary,
      Row("head", "^head:"),
      Row("seal", "^seal:"),
      Row("layout", "^layout:")
    );

  [Theory]
  [MemberData(nameof(Pit))]
  public void Pit_parity(string name) =>
    Matches(
      "pit",
      name,
      new Pit().Run(StageParity.Input("pit", name)),
      NotSummary,
      Row("band", "is not inside the band above"),
      Row("floor", "of sand under its foot"),
      Row("core", "rests on no sand"),
      Row("order", "from the drop|not on Piece|of the runner")
    );

  [Theory]
  [MemberData(nameof(Parting))]
  public void Parting_parity(string name) =>
    Matches(
      "parting",
      name,
      new Parting().Run(StageParity.Input("parting", name)),
      NotSummary,
      Row("over", "^drag: sand reaches"),
      Row("under", "^cope: sand reaches"),
      Row("shared", "stand in both halves"),
      Row("span", "does not span the parting"),
      Row("drag", "pattern Drag element"),
      Row("cope", "pattern Cope element"),
      Row("lie", "placement is no rotation")
    );

  [Theory]
  [MemberData(nameof(Fit))]
  public void Fit_parity(string name) =>
    Matches(
      "fit",
      name,
      new Fit().Run(StageParity.Input("fit", name)),
      NotSummary,
      Row("floor", "from the floor"),
      Row("roof", "from the roof"),
      Row("wall", "from the \\w+ wall"),
      Row("runner", "^Runner\\d+ and Piece\\d+:")
    );
}
