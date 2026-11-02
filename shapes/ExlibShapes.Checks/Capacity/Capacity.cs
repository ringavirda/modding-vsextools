using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Whether the record's units are what the metal weighs: <c>units.perPiece</c> is the station's ladle units per vx3
/// times the cast's volume and <c>units.share</c> the same of the head, riser and sprue feeds, each rounded and held to
/// within 1 unit. Volumes are exact sums over the box grid, a turned leaf counted on the 1/32 lattice; runner and gate cuts are not counted. One line per figure;
/// needs the part record's pour with its units, the settings and the cast.
/// </summary>
public sealed class Capacity : ICheck {
  private static readonly string[] Shared = ["head", "riser", "sprue"];

  /// <inheritdoc />
  public string Id => "capacity";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    Pouring p = Pouring.Of(input, Id);
    double perPiece =
      p.Pour.PerPiece
      ?? throw new CheckUsageException(
        "capacity reads units.perPiece, none given"
      );
    double share =
      p.Pour.Share
      ?? throw new CheckUsageException(
        "capacity reads units.share, none given"
      );
    var lines = new List<string>();
    bool faults = false;
    int figures = 0;
    for (int k = 0; k < p.Cast.Count; k++) {
      figures++;
      faults |= Compare(
        lines,
        $"Piece{k + 1} perPiece",
        perPiece,
        p.Station.LadleUnits * Volume(p.Cast[k])
      );
    }
    figures++;
    faults |= Compare(
      lines,
      "share",
      share,
      p.Station.LadleUnits
        * Volume(
          p.Feeds.Where(f => Shared.Contains(f.Feed.Kind))
            .SelectMany(f => f.Boxes)
        )
    );
    return CheckResult.Of(
      Id,
      figures,
      faults,
      [.. lines.OrderBy(l => !l.EndsWith("FAIL", StringComparison.Ordinal))]
    );
  }

  private static double Volume(IEnumerable<Box> boxes) {
    List<Box> list = [.. boxes];
    if (list.Count == 0)
      return 0;
    var grid = new BoxGrid(list);
    return grid.Total(grid.Mark(list));
  }

  private static bool Compare(
    List<string> lines,
    string what,
    double recorded,
    double computed
  ) {
    double rounded = Math.Round(computed, MidpointRounding.AwayFromZero);
    bool bad = Math.Abs(recorded - rounded) > 1;
    lines.Add(
      $"{what}: recorded {Fmt.G(recorded)}, computed {Fmt.G(computed)} (round {Fmt.G(rounded)})"
        + (bad ? "  FAIL" : "")
    );
    return bad;
  }
}
