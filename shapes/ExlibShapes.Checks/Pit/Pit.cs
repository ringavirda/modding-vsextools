using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// The rules of a pour whose pieces stand on end in a deep flask (a station with a head height). Orient: the plan of
/// each y band of a piece lies inside the plan of the band above it, so the pattern lifts out upward. Seat: each
/// <c>Core&lt;i&gt;</c> group stands its foot on the top of sand that lies under it, with at least the station's margin
/// of sand between that foot and the flask floor. Order: the <c>gate</c> feeds, by the piece they enter, lie
/// strictly farther from the drop's far edge on z with each piece, each gate overlaps its piece on z, and with
/// the pour's order <c>alt</c> the gate of an odd piece lies west of the runner's centre and an even piece's east.
/// One line per fault; reads axis-aligned leaves; needs the part record's pour, the settings and the cast.
/// </summary>
public sealed class Pit : ICheck {
  private const double Eps = 1e-9;
  private const double Seated = 0.01;

  /// <inheritdoc />
  public string Id => "pit";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    Pouring p = Pouring.Of(input, Id);
    if (p.Station.HeadHeight == null)
      throw new CheckUsageException(
        $"pit: station '{p.Pour.Station}' has no head height, it is no pit"
      );
    var lines = new List<string>();
    int counted = Orient(p, lines) + Seat(p, input, lines) + Order(p, lines);
    bool faults = lines.Count > 0;
    lines.Add($"pit: {counted} rules read");
    return CheckResult.Of(Id, counted, faults, lines);
  }

  private static int Orient(Pouring p, List<string> lines) {
    int counted = 0;
    for (int k = 0; k < p.Cast.Count; k++) {
      List<Box> boxes = p.Cast[k];
      double[] ys =
      [
        .. boxes.SelectMany(b => new[] { b.Lo.Y, b.Hi.Y }).Distinct().Order(),
      ];
      double[] xs =
      [
        .. boxes.SelectMany(b => new[] { b.Lo.X, b.Hi.X }).Distinct().Order(),
      ];
      double[] zs =
      [
        .. boxes.SelectMany(b => new[] { b.Lo.Z, b.Hi.Z }).Distinct().Order(),
      ];
      bool[,] Plan(double y0, double y1) {
        var cells = new bool[xs.Length - 1, zs.Length - 1];
        foreach (
          Box b in boxes.Where(b => b.Lo.Y <= y0 + Eps && b.Hi.Y >= y1 - Eps)
        )
          for (int i = 0; i < xs.Length - 1; i++)
            for (int j = 0; j < zs.Length - 1; j++)
              if (
                xs[i] >= b.Lo.X - Eps
                && xs[i + 1] <= b.Hi.X + Eps
                && zs[j] >= b.Lo.Z - Eps
                && zs[j + 1] <= b.Hi.Z + Eps
              )
                cells[i, j] = true;
        return cells;
      }

      for (int b = 0; b + 2 < ys.Length; b++) {
        counted++;
        bool[,] below = Plan(ys[b], ys[b + 1]),
          above = Plan(ys[b + 1], ys[b + 2]);
        if (
          Enumerable
            .Range(0, xs.Length - 1)
            .Any(i =>
              Enumerable
                .Range(0, zs.Length - 1)
                .Any(j => below[i, j] && !above[i, j])
            )
        )
          lines.Add(
            $"Piece{k + 1}: the band y {Fmt.G(ys[b])} to {Fmt.G(ys[b + 1])} is not inside the band above"
          );
      }
    }
    return counted;
  }

  private static int Seat(Pouring p, CheckInput input, List<string> lines) {
    List<FillingLeaf> leaves = Pouring.Leaves(
      input.Shape!,
      input,
      input.Settings!,
      "pit"
    );
    int counted = 0;
    foreach (
      IGrouping<string, FillingLeaf> core in leaves
        .Where(l => l.CoreGroup != null)
        .GroupBy(l => l.CoreGroup!)
    ) {
      counted++;
      double foot = core.Min(l => l.Box.Lo.Y);
      double minX = core.Min(l => l.Box.Lo.X),
        maxX = core.Max(l => l.Box.Hi.X),
        minZ = core.Min(l => l.Box.Lo.Z),
        maxZ = core.Max(l => l.Box.Hi.Z);
      double deep = foot - p.Station.Floor;
      if (deep < p.Station.Margin - Eps)
        lines.Add(
          $"{core.Key}: {Fmt.G(deep)} of sand under its foot, {Fmt.G(p.Station.Margin)} wanted"
        );
      bool seated = leaves.Any(l =>
        l.Role == "sand"
        && !l.Level
        && l.CoreGroup == null
        && Math.Abs(l.Box.Hi.Y - foot) <= Seated
        && l.Box.Lo.X < maxX
        && l.Box.Hi.X > minX
        && l.Box.Lo.Z < maxZ
        && l.Box.Hi.Z > minZ
      );
      if (!seated)
        lines.Add($"{core.Key}: its foot at y {Fmt.G(foot)} rests on no sand");
    }
    return counted;
  }

  private static int Order(Pouring p, List<string> lines) {
    List<(Feed Feed, List<Box> Boxes)> gates =
    [
      .. p
        .Feeds.Where(f => f.Feed.Kind == "gate" && f.Feed.Into is not null)
        .OrderBy(f => f.Feed.Into),
    ];
    if (gates.Count == 0)
      return 0;
    double dropFar =
      p.Station.Drop?[5]
      ?? throw new CheckUsageException("pit: the station has no drop");
    double runnerX = p.Station.RunnerX is { Length: 2 } r
      ? (r[0] + r[1]) / 2
      : throw new CheckUsageException("pit: the station has no runnerX");
    double last = double.NegativeInfinity;
    foreach ((Feed gate, List<Box> boxes) in gates) {
      int k = gate.Into!.Value;
      double z = (boxes.Min(b => b.Lo.Z) + boxes.Max(b => b.Hi.Z)) / 2,
        x = (boxes.Min(b => b.Lo.X) + boxes.Max(b => b.Hi.X)) / 2,
        dist = dropFar - z;
      if (dist <= last + Eps)
        lines.Add(
          $"{gate.Name}: {Fmt.G(dist)} from the drop, not past the gate before"
        );
      last = dist;
      if (k < 1 || k > p.Cast.Count) {
        lines.Add(
          $"{gate.Name}: enters piece {k}, the pour has {p.Cast.Count}"
        );
        continue;
      }
      if (
        p.Cast[k - 1].Min(b => b.Lo.Z) > boxes.Max(b => b.Hi.Z) + Eps
        || p.Cast[k - 1].Max(b => b.Hi.Z) < boxes.Min(b => b.Lo.Z) - Eps
      )
        lines.Add($"{gate.Name}: not on Piece{k}'s z");
      if (p.Pour.Order == "alt" && (x < runnerX) != (k % 2 == 1))
        lines.Add(
          $"{gate.Name}: on the {(x < runnerX ? "west" : "east")} of the runner, Piece{k} takes the {(k % 2 == 1 ? "west" : "east")}"
        );
    }
    return gates.Count;
  }
}
