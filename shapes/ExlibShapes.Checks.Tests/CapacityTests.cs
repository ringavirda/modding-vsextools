using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class CapacityTests {
  private static readonly Capacity Check = new();

  public static TheoryData<string> Names =>
    [.. Fixtures.Parity("capacity").Keys];

  private static string Head(double volume) =>
    $$"""{ "name": "Head", "kind": "head", "boxes": [[20, 10, -3, {{20 + volume / 16}}, 14, 1]] }""";

  [Theory]
  [MemberData(nameof(Names))]
  public void Capacity_parity(string name) {
    Recorded want = Fixtures.Parity("capacity")[name];
    double vx3 = want.Areas[0],
      head = want.Areas[1],
      per = want.Areas[2],
      share = want.Areas[3];
    int pieces = (int)want.Areas[4];
    string[] at =
    [
      .. Enumerable
        .Range(0, pieces)
        .Select(i => PourFixtures.At(0, 0, i * 100)),
    ];
    string[] feeds = head > 0 ? [Head(head)] : [];
    var input = PourFixtures.Input(
      Fixtures.Shape("fill-ok.json"),
      PourFixtures.Record(at, feeds, per, share),
      PourFixtures.Cast(vx3 / 16, 4, 4)
    );
    CheckResult got = Check.Run(input);
    Assert.Equal(want.Exit, got.ExitCode);
    Assert.Equal(want.Enumerated, got.Enumerated);
    Assert.Empty(got.Lines.Where(l => l.EndsWith("  FAIL")));
  }

  [Fact]
  public void A_figure_one_unit_off_passes_and_two_fail_with_the_computed_value() {
    Shape fill = Fixtures.Shape("fill-ok.json");
    CheckResult off1 = Check.Run(
      PourFixtures.Input(fill, PourFixtures.One(361))
    );
    Assert.Equal(0, off1.ExitCode);
    CheckResult off2 = Check.Run(
      PourFixtures.Input(fill, PourFixtures.One(362))
    );
    Assert.Equal(1, off2.ExitCode);
    Assert.Equal(
      "Piece1 perPiece: recorded 362, computed 360 (round 360)  FAIL",
      off2.Lines[0]
    );
  }

  [Fact]
  public void The_share_counts_head_riser_and_sprue_feeds_and_not_runners() {
    string[] at = [PourFixtures.At(13, 10, -3)];
    CheckResult r = Check.Run(
      PourFixtures.Input(
        Fixtures.Shape("fill-ok.json"),
        PourFixtures.Record(at, [PourFixtures.Runner1, Head(16)], 360, 40)
      )
    );
    Assert.Equal(0, r.ExitCode);
    Assert.Contains("share: recorded 40, computed 40 (round 40)", r.Lines);
  }

  [Fact]
  public void A_wrong_unit_count_is_rejected_by_this_gate_alone() {
    Assert.Equal(
      ["capacity"],
      PourFixtures.Rejecting(
        PourFixtures.Input(
          Fixtures.Shape("fill-ok.json"),
          PourFixtures.One(400)
        )
      )
    );
  }

  [Fact]
  public void A_turned_cast_leaf_weighs_what_it_holds() {
    Shape cast = Fixtures.Edited(
      "fill-cast.json",
      raw => {
        JObject body = CastFixtures.Find(raw, "Body");
        body["rotationY"] = 45;
        body["rotationOrigin"] = new JArray(3, 2, 3);
      }
    );
    CheckResult r = Check.Run(
      PourFixtures.Input(
        Fixtures.Shape("fill-ok.json"),
        PourFixtures.One(),
        cast
      )
    );
    Assert.Equal(0, r.ExitCode);
    Assert.InRange(
      PourFixtures.Number(r.Lines, "Piece1 perPiece: recorded 360, computed "),
      357,
      363
    );
  }
}
