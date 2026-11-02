using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Xunit;
using static ExpandedLib.Shapes.Generators.Tests.FlushFixtures;

namespace ExpandedLib.Shapes.Generators.Tests;

public class StationFitTests {
  private static IReadOnlyList<Leaf> Piece(
    string from,
    string to,
    string name = "Cast"
  ) => Leaves(Bx(name, from, to));

  private static FitReport Run(
    string? station,
    params IReadOnlyList<Leaf>[] pieces
  ) => StationFit.Run(Fx.Settings, "castthing", pieces, station);

  [Fact]
  public void A_slab_over_every_flask_names_the_nearest_and_its_overflow() {
    FitReport r = Run("long", Piece("[0, 3, -30]", "[72, 7, 18]"));

    string line = Assert.Single(r.Failures, f => f.Contains("fits no station"));
    Assert.Contains("nearest long", line);
    Assert.Contains("over x by 46", line);
    Assert.Contains("(extent 72 x 4 x 48, room 26 x 10 x 58)", line);
    Assert.Contains("split x at 24, 48", line);
    Assert.Equal(
      ["floor", "long", "pit", "small"],
      r.Rows.Select(x => x.Station)
    );
    Assert.All(r.Rows, x => Assert.False(x.Fits));
  }

  [Fact]
  public void A_casting_in_the_room_fits_with_no_failure() {
    FitReport r = Run("floor", Piece("[5, 3, -10]", "[15, 7, 0]"));

    Assert.Empty(r.Failures);
    Assert.Contains(
      "floor: fits, room 26 x 11 x 26, extent 10 x 4 x 10, margin 1",
      r.Table
    );
    Assert.Contains(
      r.Rows,
      x => x.Station == "small" && x.Over.SequenceEqual([0, 0, 0])
    );
  }

  [Fact]
  public void A_casting_that_fits_one_station_does_not_fail_the_others() {
    FitReport r = Run(null, Piece("[0, 3, 0]", "[20, 7, 20]"));

    Assert.Empty(r.Failures);
    Assert.Equal(
      ["floor", "long", "pit"],
      r.Rows.Where(x => x.Fits).Select(x => x.Station)
    );
  }

  [Fact]
  public void A_piece_short_of_the_wall_margin_names_the_wall() {
    FitReport r = Run("floor", Piece("[2.5, 3, -10]", "[12, 7, 0]"));

    Assert.Equal(
      "castthing: Piece1 0.5 from the west wall of floor, margin 1",
      Assert.Single(r.Failures)
    );
  }

  [Fact]
  public void A_piece_short_of_the_floor_margin_is_named() {
    FitReport r = Run("floor", Piece("[5, 2.5, -10]", "[15, 7, 0]"));

    Assert.Equal(
      "castthing: Piece1 0.5 from the floor of floor, margin 1",
      Assert.Single(r.Failures)
    );
  }

  [Fact]
  public void The_roof_margin_applies_only_where_the_station_has_a_parting() {
    FitReport open = Run("floor", Piece("[5, 3, -10]", "[15, 13.5, 0]"));
    FitReport closed = Run("long", Piece("[5, 3, -10]", "[15, 13.5, 0]"));

    Assert.Empty(open.Failures);
    Assert.Equal(
      "castthing: Piece1 0.5 from the roof of long, margin 1",
      Assert.Single(closed.Failures)
    );
  }

  [Fact]
  public void Pieces_keep_the_margin_between_them() {
    FitReport r = Run(
      "floor",
      Piece("[5, 3, -10]", "[10, 7, 0]", "A"),
      Piece("[10.5, 3, -10]", "[15, 7, 0]", "B")
    );

    Assert.Equal(
      "castthing: Piece1 and Piece2 0.5 apart at floor, margin 1",
      Assert.Single(r.Failures)
    );
  }

  [Fact]
  public void A_piece_nested_in_another_pieces_corner_keeps_the_margin_by_leaf() {
    IReadOnlyList<Leaf> ell = Leaves(
      Bx("Flat", "[5, 3, -10]", "[15, 4, 0]"),
      Bx("Upright", "[5, 4, -10]", "[6, 10, 0]")
    );

    FitReport r = Run("floor", ell, Piece("[8, 5, -8]", "[10, 7, -2]", "B"));

    Assert.Empty(r.Failures);
    FitReport close = Run(
      "floor",
      ell,
      Piece("[8, 4.5, -8]", "[10, 7, -2]", "B")
    );
    Assert.Equal(
      "castthing: Piece1 and Piece2 0.5 apart at floor, margin 1",
      Assert.Single(close.Failures)
    );
  }

  [Fact]
  public void Pieces_that_fit_apart_but_not_together_fit_no_flask_row() {
    FitReport r = Run(
      null,
      Piece("[0, 3, -10]", "[20, 7, 0]", "A"),
      Piece("[22, 3, -10]", "[42, 7, 0]", "B")
    );

    StationRow floor = Assert.Single(r.Rows, x => x.Station == "floor");
    Assert.False(floor.Fits);
    Assert.Equal(16, floor.Over[0]);
  }

  [Fact]
  public void A_station_missing_from_the_settings_is_refused() {
    Assert.Throws<CastGenException>(() =>
      Run("dock", Piece("[5, 3, -10]", "[15, 7, 0]"))
    );
  }
}
