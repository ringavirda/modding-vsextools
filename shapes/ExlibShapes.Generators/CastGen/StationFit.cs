using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Generators;

/// <summary>One station's room against a casting.</summary>
/// <param name="Station">The station's name in the settings.</param>
/// <param name="Room">The x, y and z a piece may take: the sand less the margin to the walls and floor, and to the
/// roof of a station with a parting.</param>
/// <param name="Over">By how much the pieces' joint extent passes <paramref name="Room"/> on each axis; 0 where it
/// fits.</param>
public sealed record StationRow(
  string Station,
  IReadOnlyList<double> Room,
  IReadOnlyList<double> Over
) {
  /// <summary>Whether every piece fits the station's room.</summary>
  public bool Fits => Over.All(o => o <= 0);
}

/// <summary>The fit of a laid casting to every station.</summary>
/// <param name="Item">The casting's item name.</param>
/// <param name="Rows">One row per station, in the name order.</param>
/// <param name="Table">The rows as lines: station, room, extent, margin, fits or the overflow.</param>
/// <param name="Failures">One line per fault: a piece short of the margin at the named station, or the casting
/// fitting no station with its numbers, the nearest station and the split lines.</param>
public sealed record FitReport(
  string Item,
  IReadOnlyList<StationRow> Rows,
  IReadOnlyList<string> Table,
  IReadOnlyList<string> Failures
);

/// <summary>Step 8: the laid casting fitted to the stations of the settings.</summary>
public static class StationFit {
  private const double Eps = 1e-9;

  /// <summary>Fits a casting to every station. Each piece (a group entry's leaves) is read in the flask frame; a
  /// station holds the casting when the extent of all the pieces together is within the room on every axis. Placed at
  /// <paramref name="station"/>, each piece also keeps the station's margin from the sand walls, the floor and the
  /// roof of a station with a parting, and each leaf the margin from the leaves of every other piece. A casting no station holds fails with its overflow, the
  /// nearest station (the least total overflow, the first by name on a tie) and the planes that would cut it to fit.</summary>
  /// <param name="settings">The settings whose stations are read.</param>
  /// <param name="item">The casting's item name.</param>
  /// <param name="pieces">The laid leaves of each piece.</param>
  /// <param name="station">The station the casting is poured at, null for the table alone.</param>
  /// <exception cref="CastGenException">There is no piece or <paramref name="station"/> is not in the settings.</exception>
  public static FitReport Run(
    Settings settings,
    string item,
    IReadOnlyList<IReadOnlyList<Leaf>> pieces,
    string? station = null
  ) {
    List<Box> bounds =
    [
      .. pieces.Select(p =>
        BlankResult.Union(p.Select(l => l.Box))
        ?? throw new CastGenException($"{item}: a piece has no leaf")
      ),
    ];
    if (bounds.Count == 0)
      throw new CastGenException($"{item}: no piece to fit");
    if (station != null && !settings.Stations.ContainsKey(station))
      throw new CastGenException(
        $"{item}: station \"{station}\" is not in the settings"
      );
    var extent = new double[3];
    var lo = new double[3];
    for (int a = 0; a < 3; a++) {
      lo[a] = bounds.Min(b => b.Lo[a]);
      extent[a] = bounds.Max(b => b.Hi[a]) - lo[a];
    }

    var rows = new List<StationRow>();
    var table = new List<string>();
    foreach (
      KeyValuePair<string, Station> kv in settings.Stations.OrderBy(
        s => s.Key,
        StringComparer.Ordinal
      )
    ) {
      double[] room = Room(kv.Value);
      double[] over =
      [
        .. Enumerable
          .Range(0, 3)
          .Select(a => Math.Max(0, Round(extent[a] - room[a]))),
      ];
      var row = new StationRow(kv.Key, room, over);
      rows.Add(row);
      table.Add(
        row.Fits
          ? $"{kv.Key}: fits, room {Dims(room)}, extent {Dims(extent)}, margin {Fmt.G(kv.Value.Margin)}"
          : $"{kv.Key}: over {OverText(over)}, room {Dims(room)}, extent {Dims(extent)}, margin {Fmt.G(kv.Value.Margin)}"
      );
    }

    var failures = new List<string>();
    if (rows.All(r => !r.Fits)) {
      StationRow near = rows.OrderBy(r => r.Over.Sum()).First();
      failures.Add(
        $"{item}: fits no station; nearest {near.Station}, over {OverText(near.Over)} (extent {Dims(extent)}, room {Dims(near.Room)}){SplitLines(near, extent, lo)}"
      );
    }
    if (station != null)
      failures.AddRange(
        Placed(item, settings.Stations[station], station, bounds, pieces)
      );
    return new FitReport(item, rows, table, failures);
  }

  private static double[] Room(Station s) =>
    [
      Round(s.SandTo[0] - s.SandFrom[0] - 2 * s.Margin),
      Round(
        s.SandTo[1] - s.Floor - s.Margin - (s.Parting != null ? s.Margin : 0)
      ),
      Round(s.SandTo[2] - s.SandFrom[2] - 2 * s.Margin),
    ];

  private static List<string> Placed(
    string item,
    Station s,
    string name,
    List<Box> bounds,
    IReadOnlyList<IReadOnlyList<Leaf>> pieces
  ) {
    var lines = new List<string>();
    for (int k = 0; k < bounds.Count; k++) {
      Box b = bounds[k];
      foreach (
        (string wall, double gap) in new (string, double)[]
        {
          ("west wall", b.Lo.X - s.SandFrom[0]),
          ("east wall", s.SandTo[0] - b.Hi.X),
          ("north wall", b.Lo.Z - s.SandFrom[2]),
          ("south wall", s.SandTo[2] - b.Hi.Z),
          ("floor", b.Lo.Y - s.Floor),
        }.Concat(
          s.Parting != null
            ? [("roof", s.SandTo[1] - b.Hi.Y)]
            : Array.Empty<(string, double)>()
        )
      )
        if (gap < s.Margin - Eps)
          lines.Add(
            $"{item}: Piece{k + 1} {Fmt.G(Round(gap))} from the {wall} of {name}, margin {Fmt.G(s.Margin)}"
          );
    }
    for (int a = 0; a < bounds.Count; a++)
      for (int c = a + 1; c < bounds.Count; c++) {
        double gap = pieces[a]
          .SelectMany(x => pieces[c].Select(y => Gap(x.Box, y.Box)))
          .Min();
        if (gap < s.Margin - Eps)
          lines.Add(
            $"{item}: Piece{a + 1} and Piece{c + 1} {Fmt.G(Round(gap))} apart at {name}, margin {Fmt.G(s.Margin)}"
          );
      }
    return lines;
  }

  private static double Gap(Box a, Box b) =>
    Enumerable
      .Range(0, 3)
      .Max(i => Math.Max(a.Lo[i] - b.Hi[i], b.Lo[i] - a.Hi[i]));

  private static string SplitLines(
    StationRow near,
    double[] extent,
    double[] lo
  ) {
    var cuts = new List<string>();
    for (int a = 0; a < 3; a++) {
      if (near.Over[a] <= 0)
        continue;
      int parts = (int)Math.Ceiling(extent[a] / near.Room[a] - Eps);
      cuts.Add(
        $"{"xyz"[a]} at {string.Join(", ", Enumerable.Range(1, parts - 1).Select(i => Fmt.G(Round(lo[a] + extent[a] * i / parts))))}"
      );
    }
    return $"; split {string.Join(", ", cuts)}";
  }

  private static string OverText(IReadOnlyList<double> over) =>
    string.Join(
      ", ",
      Enumerable
        .Range(0, 3)
        .Where(a => over[a] > 0)
        .Select(a => $"{"xyz"[a]} by {Fmt.G(over[a])}")
    );

  private static string Dims(IReadOnlyList<double> v) =>
    string.Join(" x ", v.Select(x => Fmt.G(Round(x))));

  private static double Round(double v) => Math.Round(v, 4);
}
