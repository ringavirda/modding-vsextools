using System.Linq;
using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class FacecheckTests {
  private static readonly Facecheck Check = new();

  public static TheoryData<string> Names =>
    [.. Fixtures.Parity("facecheck").Keys];

  private static CheckResult RunFixture(string name) =>
    name == "head-small-origin.json"
      ? Check.Run(
        new CheckInput(
          Fixtures.Shape(name),
          Fixtures.Shape("head-small.json"),
          Fixtures.Table()
        )
      )
      : Check.Run(new CheckInput(Fixtures.Shape(name)));

  [Theory]
  [MemberData(nameof(Names))]
  public void Facecheck_parity(string name) {
    Recorded want = Fixtures.Parity("facecheck")[name];
    CheckResult got = RunFixture(name);
    Assert.Equal(want.Exit, got.ExitCode);
    Assert.Equal(want.Enumerated, got.Enumerated);
    Assert.Equal(
      want.Faulted,
      Fixtures.FaultedNames(got, "uv against", "changes")
    );
  }

  [Fact]
  public void Facecheck_parity_planted_origin() {
    CheckResult got = RunFixture("head-small-origin.json");
    Assert.Equal(1, got.ExitCode);
    Assert.Contains(
      "Lug up: created, origin 0,0 on random texture 'iron'",
      got.Lines
    );
    Assert.Contains(
      "uv against shipped: 6 faces created, 0 resized, 54 kept; 1 at fault",
      got.Lines
    );
    Assert.Equal(0, RunFixture("head-small.json").ExitCode);
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
  public void Visible_untextured_face_fails_and_hidden_one_is_skipped() {
    Shape seen = Fixtures.Edited(
      "flush.json",
      r => Fixtures.Element(r, "A")["faces"]!["west"]!["texture"] = "#none"
    );
    CheckResult r = Check.Run(new CheckInput(seen));
    Assert.Equal(1, r.ExitCode);
    Assert.Contains(r.Lines, l => l.StartsWith("A west: texture '#none'"));
    Shape hidden = Fixtures.Edited(
      "flush.json",
      r => Fixtures.Element(r, "B")["faces"]!["west"]!["texture"] = "#none"
    );
    r = Check.Run(new CheckInput(hidden));
    Assert.Equal(0, r.ExitCode);
    Assert.Contains(r.Lines, l => l.EndsWith("1 at fault, 0 of them visible"));
  }

  [Fact]
  public void Shipped_without_table_is_a_usage_error() {
    Assert.Throws<CheckUsageException>(() =>
      Check.Run(
        new CheckInput(
          Fixtures.Shape("flush.json"),
          Fixtures.Shape("flush.json")
        )
      )
    );
  }

  [Fact]
  public void Boss_ring_is_a_round_run() {
    Shape s = Fixtures.Shape("head-small.json");
    var runs = RoundRuns.Find(new Tree(s), new Frames(s));
    Assert.Single(runs);
    Assert.Equal(8, runs[0].Members.Count);
  }
}
