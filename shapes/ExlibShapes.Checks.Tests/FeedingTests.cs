using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class FeedingTests {
  private static readonly Feeding Check = new();

  private static string Head(string boxes, int into = 1) =>
    $$"""{ "name": "Head1", "kind": "head", "into": {{into}}, "boxes": [{{boxes}}] }""";

  private static CheckInput Open(string head) =>
    StageFixtures.Input(
      Fixtures.Shape("fill-ok.json"),
      StageFixtures.Record(
        "floor",
        [PourFixtures.At(13, 10, -3)],
        [PourFixtures.Runner1, head]
      )
    );

  private static Shape TwoGroups(double rightFrom) =>
    StageFixtures.CastOf(
      StageFixtures.Group(
        "Left",
        StageFixtures.Box("LeftBody", 0, 0, 0, 2, 4, 6, "cast-iron1")
      ),
      StageFixtures.Group(
        "Right",
        StageFixtures.Box("RightBody", rightFrom, 0, 0, 6, 4, 6, "cast-iron1")
      )
    );

  [Fact]
  public void A_head_that_rises_the_height_over_its_piece_passes() {
    CheckInput input = Open(Head("[14, 13, -1, 16, 15, 1]"));
    Assert.Equal(0, Check.Run(input).ExitCode);
    Assert.Empty(StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_head_short_of_the_height_names_its_rise() {
    CheckInput input = Open(Head("[14, 13, -1, 16, 14.5, 1]"));
    Assert.Contains(
      "Head1: rises 0.5 over Piece1, 1 wanted",
      Check.Run(input).Lines
    );
    Assert.Equal(["feeding"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_head_that_starts_over_the_piece_names_the_gap() {
    CheckInput input = Open(Head("[14, 15, -1, 16, 17, 1]"));
    Assert.Contains(
      "Head1: starts 1 over Piece1's top",
      Check.Run(input).Lines
    );
    Assert.Equal(["feeding"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_head_that_enters_no_piece_is_a_fault() {
    CheckInput input = Open(Head("[14, 13, -1, 16, 15, 1]", 3));
    Assert.Contains(
      "Head1: a head enters a piece and holds a box",
      Check.Run(input).Lines
    );
    Assert.Equal(["feeding"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_sealed_closed_pour_passes() {
    CheckInput input = StageFixtures.Closed();
    CheckResult r = Check.Run(input);
    Assert.Equal(0, r.ExitCode);
    Assert.Empty(StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_cast_up_to_the_sands_top_fails_the_closed_head() {
    CheckInput input = StageFixtures.Closed(cast: PourFixtures.Cast(6, 9, 6));
    Assert.Contains(
      "head: the cast's top is 0 under the box floor's top at y 14, 1 wanted",
      Check.Run(input).Lines
    );
    Assert.Equal(["feeding"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_flood_reaching_the_flask_floor_is_a_leak() {
    CheckInput input = StageFixtures.Closed("closed-drag-leak.json");
    Assert.Contains(
      Check.Run(input).Lines,
      l =>
        l.StartsWith("seal: ")
        && l.EndsWith("samples at the flask's walls or floor are reached")
    );
    Assert.Equal(["feeding"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_flood_that_misses_a_mouth_names_it() {
    CheckInput input = StageFixtures.Closed(cope: "closed-cope-blocked.json");
    Assert.Contains(
      "seal: the flood does not reach Sprue's mouth",
      Check.Run(input).Lines
    );
    Assert.Equal(["feeding"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_vent_outside_the_mouths_is_a_leak_at_the_top() {
    CheckInput input = StageFixtures.Closed(cope: "closed-cope-vent.json");
    Assert.Contains(
      Check.Run(input).Lines,
      l =>
        l.StartsWith("seal: ")
        && l.EndsWith(
          "samples at the sand's top outside the mouths are reached"
        )
    );
    Assert.Equal(["feeding"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_closed_pour_with_no_sprue_or_riser_has_no_way_out() {
    CheckInput input = StageFixtures.Closed(feeds: []);
    Assert.Contains(
      "seal: the pour has no sprue or riser feed to leave by",
      Check.Run(input).Lines
    );
    Assert.Equal(["feeding"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void Groups_a_margin_apart_pass() {
    CheckInput input = StageFixtures.Closed(cast: TwoGroups(3));
    Assert.Equal(0, Check.Run(input).ExitCode);
  }

  [Fact]
  public void Groups_closer_than_the_margin_name_the_gap() {
    CheckInput input = StageFixtures.Closed(cast: TwoGroups(2.5));
    Assert.Contains(
      "layout: Left and Right are 0.5 apart, 1 wanted",
      Check.Run(input).Lines
    );
    Assert.Equal(["feeding"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_cope_with_no_sand_is_a_usage_error() {
    CheckInput input = StageFixtures.Closed();
    Shape empty = Reader.Read(
      """
      { "textures": { "burned": "block/clay/vessel/sides/burned" },
        "elements": [ { "name": "Wall", "from": [5, 9, 10], "to": [6, 11, 11], "faces": { "up": { "texture": "#burned" } } } ] }
      """
    );
    Assert.Throws<CheckUsageException>(() =>
      Check.Run(input with { Pair = empty })
    );
  }
}
