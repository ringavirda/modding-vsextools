using System.Linq;
using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class PiecesTests {
  private static readonly Pieces Check = new();

  private static CheckInput Two(
    string[] elements,
    string order = "alt",
    int count = 2
  ) =>
    StageFixtures.Input(
      StageFixtures.Sand(elements),
      StageFixtures.Record(
        "floor",
        [
          .. Enumerable
            .Range(0, count)
            .Select(i => PourFixtures.At(13 + 8 * i, 10, -3)),
        ],
        [PourFixtures.Runner1],
        order: order
      )
    );

  private static CheckInput Good() =>
    Two([
      StageFixtures.Slab(),
      StageFixtures.Piece(1, 6, 12),
      StageFixtures.Piece(2, 20, 26),
    ]);

  [Fact]
  public void Two_pieces_in_the_pours_order_pass() {
    CheckResult r = Check.Run(Good());
    Assert.Equal(0, r.ExitCode);
    Assert.Contains("count: 2 pieces, 2 wanted", r.Lines);
    Assert.Contains(r.Lines, l => l.StartsWith("order alt: Piece1(9,"));
    Assert.Empty(StageFixtures.Rejecting(Good()));
  }

  [Fact]
  public void Pieces_level_children_are_flush() {
    CheckInput input = Two([
      StageFixtures.Slab(),
      StageFixtures.Piece(1, 6, 12, 13),
      StageFixtures.Piece(2, 20, 26),
    ]);
    CheckResult r = Check.Run(input);
    Assert.Contains(
      "Piece1: Level top at y 13, the sand's top is 14  FAIL",
      r.Lines
    );
    Assert.Equal(["pieces"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_missing_piece_group_fails_the_count() {
    CheckInput input = Two([
      StageFixtures.Slab(),
      StageFixtures.Piece(1, 6, 12),
    ]);
    Assert.Contains("count: 1 pieces, 2 wanted  FAIL", Check.Run(input).Lines);
    Assert.Equal(["pieces"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_one_piece_pour_has_no_piece_group() {
    CheckInput input = Two(
      [StageFixtures.Slab(), StageFixtures.Piece(1, 6, 12)],
      count: 1
    );
    Assert.Contains("count: 1 pieces, 0 wanted  FAIL", Check.Run(input).Lines);
    Assert.Equal(["pieces"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void Piece_numbers_run_from_one_without_gaps() {
    CheckInput input = Two([
      StageFixtures.Slab(),
      StageFixtures.Piece(1, 6, 12),
      StageFixtures.Piece(3, 20, 26),
    ]);
    Assert.Contains("names: Piece1,3  FAIL", Check.Run(input).Lines);
    Assert.Equal(["pieces"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void A_piece_holds_exactly_bedded_and_level() {
    string extra = StageFixtures.Group(
      "Piece2",
      StageFixtures.Group(
        "Bedded",
        StageFixtures.Box("Cavity", 20, 2, -3, 26, 10, 3)
      ),
      StageFixtures.Group(
        "Level",
        StageFixtures.Box("Cover", 20, 10, -3, 26, 14, 3)
      ),
      StageFixtures.Group("Spare")
    );
    CheckInput input = Two([
      StageFixtures.Slab(),
      StageFixtures.Piece(1, 6, 12),
      extra,
    ]);
    Assert.Contains(
      "holds: each Piece holds Bedded and Level  FAIL",
      Check.Run(input).Lines
    );
    Assert.Equal(["pieces"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void Bedded_and_level_share_one_foot() {
    CheckInput input = Two([
      StageFixtures.Slab(),
      StageFixtures.Piece(1, 6, 12, 14, 14),
      StageFixtures.Piece(2, 20, 26),
    ]);
    Assert.Contains(
      "Piece1: Bedded and Level bounds differ  FAIL",
      Check.Run(input).Lines
    );
    Assert.Equal(["pieces"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void Two_levels_do_not_share_a_plan_cell() {
    CheckInput input = Two([
      StageFixtures.Slab(),
      StageFixtures.Piece(1, 6, 12),
      StageFixtures.Piece(2, 11, 29),
    ]);
    Assert.Contains(
      "Piece1 and Piece2: 96 plan cells of Level in both  FAIL",
      Check.Run(input).Lines
    );
    Assert.Equal(["pieces"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void The_pieces_and_sand_cover_the_sand_box() {
    CheckInput input = Two([
      StageFixtures.Slab(10),
      StageFixtures.Piece(1, 6, 12),
      StageFixtures.Piece(2, 20, 26),
    ]);
    Assert.Contains(
      "cover: 1792 of 12544 plan cells of the sand box have no sand  FAIL",
      Check.Run(input).Lines
    );
    Assert.Equal(["pieces"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void The_pieces_run_in_the_pours_order() {
    CheckInput input = Two([
      StageFixtures.Slab(),
      StageFixtures.Piece(1, 20, 26),
      StageFixtures.Piece(2, 6, 12),
    ]);
    Assert.Contains(
      "order alt: Piece1(23,0) Piece2(9,0)  FAIL",
      Check.Run(input).Lines
    );
    Assert.Equal(["pieces"], StageFixtures.Rejecting(input));
  }

  private static CheckInput Ordered(
    string order,
    params (double X0, double X1, double Z0, double Z1)[] feet
  ) =>
    Two(
      [
        StageFixtures.Slab(),
        .. feet.Select(
          (f, i) => StageFixtures.Piece(i + 1, f.X0, f.X1, z0: f.Z0, z1: f.Z1)
        ),
      ],
      order,
      feet.Length
    );

  private static readonly (double, double, double, double) West =
      (6, 12, -3, 3),
    East = (20, 26, -3, 3),
    Centre = (13, 19, -3, 3);

  [Theory]
  [InlineData("south")]
  [InlineData("west")]
  [InlineData("pairs2")]
  [InlineData("rows2")]
  [InlineData("alt")]
  public void An_order_the_pieces_follow_passes(string order) {
    (double, double, double, double)[] feet = order switch {
      "south" => [(6, 12, 5, 11), (6, 12, -11, -5)],
      "west" => [East, West],
      "rows2" => [Centre, West],
      _ => [West, East],
    };
    CheckResult r = Check.Run(Ordered(order, feet));
    Assert.Equal(0, r.ExitCode);
    Assert.Contains(r.Lines, l => l.StartsWith("order " + order + ": "));
  }

  [Theory]
  [InlineData("south")]
  [InlineData("west")]
  [InlineData("pairs2")]
  [InlineData("rows2")]
  [InlineData("alt")]
  public void Pieces_against_the_order_fail_it(string order) {
    (double, double, double, double)[] feet = order switch {
      "south" => [(6, 12, -11, -5), (6, 12, 5, 11)],
      "west" => [West, East],
      "rows2" => [Centre, East],
      _ => [East, West],
    };
    CheckInput input = Ordered(order, feet);
    Assert.Contains(
      Check.Run(input).Lines,
      l => l.StartsWith("order " + order + ": ") && l.EndsWith("  FAIL")
    );
    Assert.Equal(["pieces"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void An_alt_pour_runs_each_pair_nearer_the_south() {
    (double, double, double, double)[] ok =
    [
      (6, 12, 5, 11),
      (20, 26, 5, 11),
      (6, 12, -11, -5),
      (20, 26, -11, -5),
    ];
    Assert.Equal(0, Check.Run(Ordered("alt", ok)).ExitCode);
    (double, double, double, double)[] bad =
    [
      (6, 12, -11, -5),
      (20, 26, -11, -5),
      (6, 12, 5, 11),
      (20, 26, 5, 11),
    ];
    CheckInput input = Ordered("alt", bad);
    Assert.Contains(
      Check.Run(input).Lines,
      l => l.StartsWith("order alt: ") && l.EndsWith("  FAIL")
    );
    Assert.Equal(["pieces"], StageFixtures.Rejecting(input));
  }

  [Fact]
  public void An_unknown_order_is_a_usage_error() {
    Assert.Throws<CheckUsageException>(() =>
      Check.Run(Ordered("spiral", West, East))
    );
  }

  [Fact]
  public void A_bedded_group_outside_any_piece_fails_the_count() {
    CheckInput input = Two(
      [
        StageFixtures.Slab(),
        StageFixtures.Group(
          "Bedded",
          StageFixtures.Box("Cavity", 6, 2, -3, 12, 10, 3)
        ),
      ],
      count: 1
    );
    Assert.Contains("count: 0 pieces, 0 wanted  FAIL", Check.Run(input).Lines);
    Assert.Equal(["pieces"], StageFixtures.Rejecting(input));
  }

  private static string Piece2(string bedded, string level) =>
    StageFixtures.Group(
      "Piece2",
      StageFixtures.Group("Bedded", bedded),
      StageFixtures.Group("Level", level)
    );

  [Fact]
  public void A_piece_with_an_empty_bedded_group_has_no_foot() {
    CheckInput input = Two([
      StageFixtures.Slab(),
      StageFixtures.Piece(1, 6, 12),
      Piece2("", StageFixtures.Box("Cover", 20, 10, -3, 26, 14, 3)),
    ]);
    Assert.Contains(
      "Piece2: Bedded and Level bounds differ  FAIL",
      Check.Run(input).Lines
    );
  }

  [Fact]
  public void A_piece_with_an_empty_level_group_has_no_foot() {
    CheckInput input = Two([
      StageFixtures.Slab(),
      StageFixtures.Piece(1, 6, 12),
      Piece2(StageFixtures.Box("Cavity", 20, 2, -3, 26, 10, 3), ""),
    ]);
    Assert.Contains(
      "Piece2: Bedded and Level bounds differ  FAIL",
      Check.Run(input).Lines
    );
  }

  [Fact]
  public void Pieces_with_no_sand_beside_them_fail_the_flush() {
    CheckInput input = Two([
      StageFixtures.Piece(1, 6, 12),
      StageFixtures.Piece(2, 20, 26),
    ]);
    Assert.Contains(
      "flush: no sand outside the Pieces  FAIL",
      Check.Run(input).Lines
    );
  }

  [Fact]
  public void A_turned_sand_leaf_covers_the_cells_inside_it_not_its_bounds() {
    CheckInput input = Two([
      StageFixtures.Box("SlabW", 2, 2, -14, 12, 14, 14),
      StageFixtures.Box("SlabE", 20, 2, -14, 30, 14, 14),
      PourFixtures.Turned("Fill", 12, 2, -4, 20, 14, 4),
      StageFixtures.Piece(1, 6, 12),
      StageFixtures.Piece(2, 20, 26),
    ]);
    CheckResult r = Check.Run(input);
    double reach = 4 * System.Math.Sqrt(2),
      expected = (8 * 28 - (4 * reach * 4 - 32)) / 0.0625;
    Assert.InRange(
      PourFixtures.Number(r.Lines, "cover: "),
      expected * 0.985,
      expected * 1.015
    );
  }
}
