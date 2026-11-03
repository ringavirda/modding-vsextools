using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class PartingTests {
  private static readonly Parting Check = new();

  private static CheckInput Placed(string at) =>
    StageFixtures.Input(
      Fixtures.Shape("fill-ok.json"),
      StageFixtures.Record("floor", [at], [PourFixtures.Runner1])
    );

  private static Shape Pattern(double dragTop, double copeBottom) =>
    StageFixtures.Roots(
      StageFixtures.Group(
        "Drag",
        StageFixtures.Box("DragBody", 0, 0, 0, 6, dragTop, 6, "cast-iron1")
      ),
      StageFixtures.Group(
        "Cope",
        StageFixtures.Box("CopeBody", 0, copeBottom, 0, 6, 4, 6, "cast-iron1")
      )
    );

  [Fact]
  public void A_rotated_placement_passes() {
    CheckInput input = Placed(PourFixtures.At(13, 10, -3));
    Assert.Equal(0, Check.Run(input).ExitCode);
    Assert.Empty(StageFixtures.Rejecting(input));
  }

  [Theory]
  [InlineData("[1, 0, 0, 13, 0, 1, 0, 10, 0, 0, -1, -3, 0, 0, 0, 1]", "-1")]
  [InlineData(
    "[1.25, 0, 0, 13, 0, 1, 0.6, 10, 0, 0, 0.8, -3, 0, 0, 0, 1]",
    "1"
  )]
  [InlineData(
    "[1, 0, 0.6, 13, 0, 1.25, 0, 10, 0, 0, 0.8, -3, 0, 0, 0, 1]",
    "1"
  )]
  [InlineData(
    "[1, 0.6, 0, 13, 0, 0.8, 0, 10, 0, 0, 1.25, -3, 0, 0, 0, 1]",
    "1"
  )]
  public void A_placement_that_is_no_rotation_names_its_determinant(
    string at,
    string det
  ) {
    CheckInput input = Placed(at);
    Assert.Contains(
      $"Piece1: its placement is no rotation, determinant {det}",
      Check.Run(input).Lines
    );
    Assert.Equal(["parting"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_mirrored_core_placement_is_named_by_its_item() {
    CheckInput bad = Placed(PourFixtures.At(13, 10, -3)) with {
      Record = CastFixtures.Record(
        """
        "kinds": ["filling"],
        "pour": { "station": "floor", "mould": "open", "cast": "fill-cast",
          "pieces": [{ "at": [1, 0, 0, 13, 0, 1, 0, 10, 0, 0, 1, -3, 0, 0, 0, 1] }],
          "cores": [{ "item": "Core1", "at": [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1] }],
          "feeds": [], "units": { "perPiece": 360, "share": 0 } }
        """
      ),
    };
    Assert.Contains(
      "Core1: its placement is no rotation, determinant -1",
      Check.Run(bad).Lines
    );
    Assert.Equal(["parting"], StageFixtures.Rejecting(bad));
  }

  [Fact]
  public void A_closed_pour_split_at_the_parting_passes() {
    CheckInput input = StageFixtures.Closed();
    Assert.Equal(0, Check.Run(input).ExitCode);
    Assert.Empty(StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_piece_wholly_in_one_half_names_its_span() {
    CheckInput input = StageFixtures.Closed(at: PourFixtures.At(13, 9, -3));
    Assert.Contains(
      "Piece1: y 9 to 13 does not span the parting at 8",
      Check.Run(input).Lines
    );
    Assert.Equal(["parting"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_flat_back_piece_touching_the_parting_passes() {
    foreach (double y in new[] { 4.0, 4.0 - 5e-7, 8.0, 8.0 + 5e-7 }) {
      CheckInput input = StageFixtures.Closed(at: PourFixtures.At(13, y, -3));
      Assert.DoesNotContain(
        Check.Run(input).Lines,
        l => l.Contains("does not span the parting")
      );
    }
  }

  [Fact]
  public void A_piece_wholly_under_the_parting_names_its_span() {
    CheckInput input = StageFixtures.Closed(at: PourFixtures.At(13, 1, -3));
    Assert.Contains(
      "Piece1: y 1 to 5 does not span the parting at 8",
      Check.Run(input).Lines
    );
    Assert.Contains("parting", StageFixtures.Rejecting(input));
  }

  [Fact]
  public void Drag_sand_over_the_parting_is_named() {
    CheckInput input = StageFixtures.Closed("closed-drag-over.json");
    Assert.Contains(
      "drag: sand reaches y 9, over the parting at 8",
      Check.Run(input).Lines
    );
    Assert.Equal(["parting"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void Cope_sand_under_the_parting_is_named() {
    CheckInput input = StageFixtures.Closed(cope: "closed-cope-under.json");
    Assert.Contains(
      "cope: sand reaches y 7, under the parting at 8",
      Check.Run(input).Lines
    );
    Assert.Equal(["parting"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_sand_box_in_both_halves_is_counted() {
    CheckInput input = StageFixtures.Closed(
      "closed-drag-over.json",
      "closed-cope-shared.json"
    );
    Assert.Contains(
      "1 sand boxes stand in both halves",
      Check.Run(input).Lines
    );
  }

  [Fact]
  public void Pattern_halves_on_their_own_side_pass() {
    CheckInput input = StageFixtures.Closed(pattern: Pattern(3, 3));
    CheckResult r = Check.Run(input);
    Assert.Equal(0, r.ExitCode);
    Assert.Contains("parting: 5 rules read", r.Lines);
  }

  [Fact]
  public void A_turned_piece_places_the_pattern_by_its_offset_alone() {
    CheckInput input = StageFixtures.Closed(
      at: "[0, -1, 0, 13, 1, 0, 0, 5, 0, 0, 1, -3, 0, 0, 0, 1]",
      pattern: Pattern(3, 3)
    );
    Assert.DoesNotContain(
      Check.Run(input).Lines,
      l => l.StartsWith("Piece1: pattern")
    );
  }

  [Fact]
  public void A_pattern_drag_reaching_over_the_parting_is_named() {
    CheckInput input = StageFixtures.Closed(pattern: Pattern(4, 3));
    Assert.Contains(
      "Piece1: pattern Drag element DragBody reaches y 9, past the parting at 8",
      Check.Run(input).Lines
    );
    Assert.Equal(["parting"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_pattern_cope_reaching_under_the_parting_is_named() {
    CheckInput input = StageFixtures.Closed(pattern: Pattern(3, 2));
    Assert.Contains(
      "Piece1: pattern Cope element CopeBody reaches y 7, past the parting at 8",
      Check.Run(input).Lines
    );
    Assert.Equal(["parting"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_turned_pattern_element_is_read_to_the_parting() {
    Shape Turned(double dragTop) =>
      StageFixtures.Roots(
        StageFixtures.Group(
          "Drag",
          PourFixtures.Turned("DragBody", 0, 0, 0, 6, dragTop, 6, "cast-iron1")
        ),
        StageFixtures.Group(
          "Cope",
          StageFixtures.Box("CopeBody", 0, 3, 0, 6, 4, 6, "cast-iron1")
        )
      );
    CheckResult fits = Check.Run(StageFixtures.Closed(pattern: Turned(3)));
    Assert.Equal(0, fits.ExitCode);
    Assert.Contains("parting: 5 rules read", fits.Lines);
    Assert.Contains(
      "Piece1: pattern Drag element DragBody reaches y 9, past the parting at 8",
      Check.Run(StageFixtures.Closed(pattern: Turned(4))).Lines
    );
  }

  [Fact]
  public void A_turned_drag_sand_leaf_over_the_parting_is_named() {
    CheckInput input = StageFixtures.Input(
      PourFixtures.WithTurned(
        "closed-drag.json",
        PourFixtures.Turned("Hump", 0, 6, -4, 4, 9, 0)
      ),
      StageFixtures.Record(
        "long",
        [PourFixtures.At(13, 5, -3)],
        [StageFixtures.Sprue, StageFixtures.Riser],
        "closed"
      ),
      pair: Fixtures.Shape("closed-cope.json")
    );
    Assert.Contains(
      "drag: sand reaches y 9, over the parting at 8",
      Check.Run(input).Lines
    );
  }
}
