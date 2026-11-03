using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class DrawTests {
  private static readonly Draw Check = new();

  public static TheoryData<string> Names => [.. Fixtures.Parity("draw").Keys];

  private static CheckInput Pattern(Shape shape) => new(shape, Parting: 8);

  private static Shape CopeGap(double low, double high) =>
    Fixtures.Edited(
      "pattern-ok.json",
      raw => {
        JObject body = CastFixtures.Find(raw, "CopeBody");
        JObject upper = (JObject)body.DeepClone();
        upper["name"] = "CopeHigh";
        CastFixtures.Move(body, "to", 1, low);
        CastFixtures.Move(upper, "from", 1, high);
        ((JArray)raw["elements"]![1]!["children"]!).Add(upper);
      }
    );

  [Theory]
  [MemberData(nameof(Names))]
  public void Draw_parity(string name) {
    Recorded want = Fixtures.Parity("draw")[name];
    CheckResult got = Check.Run(Pattern(Fixtures.Shape(name)));
    Assert.Equal(want.Exit, got.ExitCode);
    Assert.True(got.Enumerated > 0);
    Assert.Equal(
      want.Faulted,
      got.Lines.Where(l => l.EndsWith("  FAIL")).Select(l => l[..4])
    );
  }

  [Fact]
  public void Draw_fails_a_broken_column() {
    Shape gap = CopeGap(8.53125, 8.59375);
    CheckResult r = Check.Run(Pattern(gap));
    Assert.Equal(1, r.ExitCode);
    Assert.Contains(
      "Cope: 9 columns occupied, 9 not unbroken from the parting  FAIL",
      r.Lines
    );
    Assert.Contains("  Cope x 6..7 z 4..5: first gap at y 8.53125", r.Lines);
    Assert.Contains(
      "Drag: 9 columns occupied, 0 not unbroken from the parting",
      r.Lines
    );
  }

  [Fact]
  public void A_column_with_a_gap_is_rejected_by_this_gate_alone() {
    var input = new CheckInput(
      CopeGap(8.53125, 8.59375),
      Record: CastFixtures.Record("\"kinds\": [\"pattern\"]"),
      Settings: CastFixtures.Settings(),
      Parting: 8
    );
    Assert.Equal(["draw"], CastFixtures.Rejecting(input));
  }

  [Fact]
  public void Leaves_meeting_at_a_sample_leave_no_gap() {
    Assert.Equal(0, Check.Run(Pattern(CopeGap(8.53125, 8.53125))).ExitCode);
  }

  [Fact]
  public void A_drag_gap_names_the_sample_under_the_parting_side() {
    CheckResult r = Check.Run(Pattern(Fixtures.Shape("pattern-gap.json")));
    Assert.Contains("  Drag x 6..10 z 4..8: first gap at y 5.46875", r.Lines);
  }

  [Fact]
  public void A_pattern_floating_off_the_parting_is_broken() {
    Shape lifted = Fixtures.Edited(
      "pattern-ok.json",
      raw => {
        JObject body = CastFixtures.Find(raw, "CopeBody");
        CastFixtures.Move(body, "from", 1, 9);
      }
    );
    Assert.Equal(1, Check.Run(Pattern(lifted)).ExitCode);
  }

  [Theory]
  [InlineData(8.03125, 0)]
  [InlineData(8.0625, 1)]
  public void A_column_may_start_within_the_drawing_tolerance_of_the_parting(
    double from,
    int exit
  ) {
    Shape stagger = Fixtures.Edited(
      "pattern-ok.json",
      raw =>
        CastFixtures.Move(CastFixtures.Find(raw, "CopeBody"), "from", 1, from)
    );

    Assert.Equal(exit, Check.Run(Pattern(stagger)).ExitCode);
  }

  [Fact]
  public void A_step_in_the_drag_is_not_a_gap() {
    CheckResult r = Check.Run(Pattern(Fixtures.Shape("pattern-step.json")));
    Assert.Equal(0, r.ExitCode);
    Assert.Contains(
      "Drag: 2 columns occupied, 0 not unbroken from the parting",
      r.Lines
    );
  }

  [Fact]
  public void The_parting_is_the_input_s() {
    Assert.Equal(
      1,
      Check
        .Run(new CheckInput(Fixtures.Shape("pattern-ok.json"), Parting: 12))
        .ExitCode
    );
  }

  [Fact]
  public void Empty_input_fails_and_the_parting_is_required() {
    Assert.Equal(Verdict.Fail, Check.Run(new CheckInput(null)).Verdict);
    Assert.Throws<CheckUsageException>(() =>
      Check.Run(new CheckInput(Fixtures.Shape("pattern-ok.json")))
    );
    Shape nothing = Fixtures.Edited(
      "pattern-ok.json",
      raw => {
        raw["elements"] = new JArray();
      }
    );
    Assert.Equal(1, Check.Run(Pattern(nothing)).ExitCode);
  }

  [Fact]
  public void A_turned_leaf_occupies_the_columns_inside_it_not_its_bounds() {
    Shape turned = Fixtures.Edited(
      "pattern-ok.json",
      raw => {
        JObject body = CastFixtures.Find(raw, "CopeBody");
        body["rotationY"] = 45;
        body["rotationOrigin"] = new JArray(8, 9.5, 6);
      }
    );
    CheckResult r = Check.Run(Pattern(turned));
    Assert.Equal(0, r.ExitCode);
    Assert.InRange(
      PourFixtures.Number(r.Lines, "Cope: "),
      16 * 1024 * 0.985,
      16 * 1024 * 1.015
    );
  }
}
