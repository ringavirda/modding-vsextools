using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class PitTests {
  private static readonly Pit Check = new();

  private static string Feed(string name, string into, string boxes) =>
    $$"""{ "name": "{{name}}", "kind": "gate", "into": {{into}}, "boxes": [{{boxes}}] }""";

  private static CheckInput Upright(Shape cast) =>
    StageFixtures.Input(
      Fixtures.Shape("fill-ok.json"),
      StageFixtures.Record(
        "pit",
        [PourFixtures.At(13, 0, -3)],
        [PourFixtures.Runner1]
      ),
      cast
    );

  private static Shape Stepped(bool wideBelow) =>
    StageFixtures.CastOf(
      wideBelow
        ? StageFixtures.Box("Foot", 0, 0, 0, 6, 2, 6, "cast-iron1")
        : StageFixtures.Box("Foot", 1, 0, 1, 5, 2, 5, "cast-iron1"),
      wideBelow
        ? StageFixtures.Box("Neck", 1, 2, 1, 5, 6, 5, "cast-iron1")
        : StageFixtures.Box("Neck", 0, 2, 0, 6, 6, 6, "cast-iron1")
    );

  private static CheckInput Cored(double soilTop, double foot) =>
    StageFixtures.Input(
      StageFixtures.Sand(
        StageFixtures.Box("Under", 2, -14, -14, 30, soilTop, 14),
        StageFixtures.Group(
          "Core1",
          StageFixtures.Box("Stem", 10, foot, -2, 12, foot + 8, 0)
        )
      ),
      StageFixtures.Record(
        "pit",
        [PourFixtures.At(13, 0, -3)],
        [PourFixtures.Runner1]
      )
    );

  private static CheckInput Gated(string order, params string[] feeds) =>
    StageFixtures.Input(
      Fixtures.Shape("fill-ok.json"),
      StageFixtures.Record(
        "pit",
        [PourFixtures.At(13, 0, -3)],
        [PourFixtures.Runner1, .. feeds],
        order: order
      )
    );

  private static CheckInput GatedTwo(params string[] feeds) =>
    StageFixtures.Input(
      StageFixtures.Sand(
        StageFixtures.Slab(),
        StageFixtures.Piece(1, 6, 12),
        StageFixtures.Piece(2, 20, 26)
      ),
      StageFixtures.Record(
        "pit",
        [PourFixtures.At(13, 0, -3), PourFixtures.At(21, 0, -3)],
        [PourFixtures.Runner1, .. feeds],
        order: "one"
      )
    );

  [Fact]
  public void A_piece_narrowing_upward_passes() {
    CheckInput input = Upright(Stepped(false));
    Assert.Equal(0, Check.Run(input).ExitCode);
    Assert.Empty(StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_foot_wider_than_the_band_above_cannot_lift_out() {
    CheckInput input = Upright(Stepped(true));
    Assert.Contains(
      "Piece1: the band y 0 to 2 is not inside the band above",
      Check.Run(input).Lines
    );
    Assert.Equal(["pit"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_core_on_sand_with_margin_under_it_passes() {
    CheckInput input = Cored(-5, -5);
    Assert.Equal(0, Check.Run(input).ExitCode);
    Assert.Empty(StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_core_resting_on_no_sand_names_its_foot() {
    CheckInput input = Cored(-6, -5);
    Assert.Contains(
      "Core1: its foot at y -5 rests on no sand",
      Check.Run(input).Lines
    );
    Assert.Equal(["pit"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_core_too_near_the_flask_floor_names_the_sand_under_it() {
    CheckInput input = Cored(-13.5, -13.5);
    Assert.Contains(
      "Core1: 0.5 of sand under its foot, 1 wanted",
      Check.Run(input).Lines
    );
    Assert.Equal(["pit"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void Gates_farther_from_the_drop_with_each_piece_pass() {
    CheckInput input = GatedTwo(
      Feed("Gate1", "1", "[14, 13.5, 0, 16, 14, 1]"),
      Feed("Gate2", "2", "[22, 13.5, -1, 24, 14, 0]")
    );
    Assert.Equal(0, Check.Run(input).ExitCode);
    Assert.Empty(StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_later_piece_gated_nearer_the_drop_names_the_gate() {
    CheckInput input = GatedTwo(
      Feed("Gate1", "1", "[14, 13.5, -1, 16, 14, 0]"),
      Feed("Gate2", "2", "[22, 13.5, 0, 24, 14, 1]")
    );
    Assert.Contains(
      "Gate2: 9.5 from the drop, not past the gate before",
      Check.Run(input).Lines
    );
    Assert.Equal(["pit"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_gate_off_its_pieces_z_names_the_piece() {
    CheckInput input = Gated(
      "one",
      Feed("Gate1", "1", "[14, 13.5, 5, 16, 14, 6]")
    );
    Assert.Contains("Gate1: not on Piece1's z", Check.Run(input).Lines);
    Assert.Equal(["pit"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_gate_south_of_its_piece_names_the_piece() {
    CheckInput input = Gated(
      "one",
      Feed("Gate1", "1", "[14, 13.5, -8, 16, 14, -7]")
    );
    Assert.Contains("Gate1: not on Piece1's z", Check.Run(input).Lines);
    Assert.Equal(["pit"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void An_alt_pour_gates_odd_pieces_west_of_the_runner() {
    CheckInput input = Gated(
      "alt",
      Feed("Gate1", "1", "[24, 13.5, 0, 26, 14, 1]")
    );
    Assert.Contains(
      "Gate1: on the east of the runner, Piece1 takes the west",
      Check.Run(input).Lines
    );
    Assert.Equal(["pit"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_gate_into_a_piece_the_pour_lacks_is_named() {
    CheckInput input = Gated(
      "one",
      Feed("Gate3", "3", "[14, 13.5, 0, 16, 14, 1]")
    );
    Assert.Contains(
      "Gate3: enters piece 3, the pour has 1",
      Check.Run(input).Lines
    );
    Assert.Equal(["pit"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_station_without_a_head_height_is_no_pit() {
    CheckInput input = StageFixtures.Input(
      Fixtures.Shape("fill-ok.json"),
      StageFixtures.Record(
        "floor",
        [PourFixtures.At(13, 10, -3)],
        [PourFixtures.Runner1]
      )
    );
    Assert.Throws<CheckUsageException>(() => Check.Run(input));
    Assert.False(Selection.Applies(Check, input));
  }
}
