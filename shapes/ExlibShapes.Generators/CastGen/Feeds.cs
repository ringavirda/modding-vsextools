using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Generators;

/// <summary>A cut in the sand the metal runs through.</summary>
/// <param name="Name">The feed's name in the record.</param>
/// <param name="Kind"><c>runner</c>, <c>gate</c>, <c>head</c>, <c>sprue</c> or <c>riser</c>.</param>
/// <param name="Boxes">The feed's boxes in the filling's space; empty for a feed the pattern carries.</param>
/// <param name="Pattern">The globs of the pattern elements a head is, placed by the piece it enters.</param>
/// <param name="Into">The piece, from 1, the feed enters; null for a runner or sprue.</param>
public sealed record FeedLaid(
  string Name,
  string Kind,
  IReadOnlyList<Box> Boxes,
  IReadOnlyList<string> Pattern,
  int? Into
);

internal readonly record struct Rect(double X0, double Z0, double X1, double Z1) {
  public double CentreX => (X0 + X1) / 2;
  public double CentreZ => (Z0 + Z1) / 2;

  public static Rect Of(IEnumerable<Box> boxes) =>
    new(
      boxes.Min(b => b.Lo.X),
      boxes.Min(b => b.Lo.Z),
      boxes.Max(b => b.Hi.X),
      boxes.Max(b => b.Hi.Z)
    );

  public bool Overlaps(Rect o) =>
    X1 > o.X0 && o.X1 > X0 && Z1 > o.Z0 && o.Z1 > Z0;
}

/// <summary>Step 11: the runner, gates, heads, sprue and risers of a pour.</summary>
internal static class Feeds {
  private const double Eps = 1e-9;
  private const double Past = 0.5;

  /// <summary>Lays the feeds of a pour by one rule at every station. An open pour's runner crosses the station's
  /// <c>RunnerX</c> from the drop's far z edge toward piece 1 at the sand's top, ending <c>Past</c> inside it, and
  /// is none when the drop lies over piece 1; each later piece gets a gate <c>GateAcross</c> wide, <c>chain</c> from
  /// the piece before and <c>pairs</c> or <c>rows</c> from the main runner, <c>Past</c> into each end, one box or two
  /// at a right angle where no straight one stays the margin clear of the other pieces. A closed pour's sprue stands
  /// over the station's sprue rect from the parting to the sand's top, its runner and gates lie in the drag under the
  /// parting, and a riser stands through the cope over each asked point. A station with a head height gets a head
  /// feed over each piece, the pattern's <c>Head</c>. A piece of bodies that do not touch (a kit laid apart) gets a gate
  /// <c>Gate&lt;k&gt;.&lt;n&gt;</c> from its largest body to each other body, <c>GateAcross</c> wide.</summary>
  /// <param name="item">The filling's name, for messages.</param>
  /// <param name="station">The station row.</param>
  /// <param name="closed">Whether the pour is closed.</param>
  /// <param name="fill">The fill order, <c>chain</c>, <c>pairs</c> or <c>rows</c>.</param>
  /// <param name="pieces">The metal of each piece in the filling's space, its head included.</param>
  /// <param name="risers">The kind and the point on the cast's top of each riser, in the filling's space.</param>
  /// <param name="solids">The occupancy of each piece's metal, head included; a runner or gate leg that ends at a
  /// piece ends <c>Past</c> inside the first of its metal the leg's own line meets. Null ends it <c>Past</c> inside
  /// the plan rectangle of the piece's boxes.</param>
  /// <exception cref="CastGenException">The station lacks a runner depth, a gate width, a drop, a sprue, a parting
  /// or a riser's section; a piece has
  /// no metal in the runner's depth; no route keeps the margin.</exception>
  public static List<FeedLaid> Lay(
    string item,
    Station station,
    bool closed,
    string fill,
    IReadOnlyList<IReadOnlyList<Box>> pieces,
    IReadOnlyList<(string Kind, Vec3 Top)> risers,
    IReadOnlyList<Occupancy>? solids = null
  ) {
    double top = station.SandTo[1];
    double depth =
      station.RunnerDepth
      ?? throw new CastGenException($"{item}: the station has no runnerDepth");
    double across =
      station.GateAcross
      ?? throw new CastGenException($"{item}: the station has no gateAcross");
    double margin = station.Margin;
    var feeds = new List<FeedLaid>();
    double y1 = top;
    Rect source;
    Rect over;
    double pastSource;
    if (closed) {
      double[] sp =
        station.Sprue
        ?? throw new CastGenException($"{item}: the station has no sprue");
      double parting =
        station.Parting
        ?? throw new CastGenException($"{item}: a closed pour needs a parting");
      feeds.Add(
        new FeedLaid(
          "Sprue",
          "sprue",
          [
            new Box(
              new Vec3(sp[0], parting, sp[1]),
              new Vec3(sp[2], top, sp[3])
            ),
          ],
          [],
          null
        )
      );
      y1 = parting;
      source = new Rect(sp[0], sp[1], sp[2], sp[3]);
      over = source;
      pastSource = Past;
    } else {
      double[] drop =
        station.Drop
        ?? throw new CastGenException($"{item}: the station has no drop");
      over = new Rect(drop[0], drop[2], drop[3], drop[5]);
      double[] span = station.RunnerX is { Length: 2 } rx
        ? rx
        : [drop[0], drop[3]];
      source = new Rect(span[0], over.Z0, span[1], over.Z1);
      pastSource = 0;
    }
    double y0 = y1 - depth;

    List<List<List<Box>>> bodies = [.. pieces.Select(Bodies)];
    List<Box> Band(IEnumerable<Box> boxes) =>
      [.. boxes.Where(b => b.Hi.Y > y0 + Eps && b.Lo.Y < y1 - Eps)];
    CastGenException NoMetal(int piece) =>
      new(
        $"{item}: Piece{piece + 1} has no metal between y {Fmt.G(y0)} and {Fmt.G(y1)}, where the runner lies"
      );

    Rect[] feet = new Rect[pieces.Count];
    var primaries = new List<Box>[pieces.Count];
    for (int k = 0; k < pieces.Count; k++) {
      List<Box> primary =
        bodies[k]
          .Select(body => Band(body))
          .FirstOrDefault(band => band.Count > 0)
        ?? throw NoMetal(k);
      feet[k] = Rect.Of(primary);
      primaries[k] = primary;
      bodies[k] =
      [
        .. bodies[k].Where(body => !body.Any(b => primary.Contains(b))),
      ];
    }

    List<Box> Reach(
      string what,
      int piece,
      Rect a,
      double pastA,
      Rect b,
      double w,
      Func<Box, bool> clear,
      Occupancy? solid = null
    ) =>
      Route(a, pastA, b, w, y0, y1, clear, solid)
      ?? throw new CastGenException(
        $"{item}: no {what} reaches Piece{piece} and keeps the margin {Fmt.G(margin)} from the others"
      );

    bool Clear(Box b, params int[] enters) =>
      Enumerable
        .Range(0, pieces.Count)
        .Where(k => !enters.Contains(k))
        .All(k => pieces[k].All(p => Gap(b, p) >= margin - Eps));

    Rect main = source;
    if (feet[0].Overlaps(over)) {
      main = feet[0];
    } else {
      if (!closed) {
        double far = feet[0].CentreZ <= over.Z0 + Eps ? over.Z1 : over.Z0;
        source = new Rect(source.X0, far, source.X1, far);
      }
      List<Box> run = Reach(
        "runner",
        1,
        source,
        pastSource,
        feet[0],
        source.X1 - source.X0,
        b => Clear(b, 0),
        solids?[0]
      );
      feeds.Add(new FeedLaid("Runner1", "runner", run, [], null));
      main = Rect.Of(run);
    }
    for (int k = 1; k < pieces.Count; k++) {
      int from = fill == "chain" ? k - 1 : -1;
      Rect a = from >= 0 ? feet[from] : main;
      List<Box> gate = Reach(
        "gate",
        k + 1,
        a,
        Past,
        feet[k],
        across,
        b => Clear(b, from >= 0 ? [k, from] : [k]),
        solids?[k]
      );
      if (gate.Count > 0)
        feeds.Add(new FeedLaid($"Gate{k + 1}", "gate", gate, [], k + 1));
    }
    for (int k = 0; k < pieces.Count; k++)
      foreach (
        (List<Box> band, int n) in bodies[k]
          .Select(body => Band(body))
          .Where(band => band.Count > 0)
          .Select((band, n) => (band, n + 1))
      ) {
        (Box from, Box to) = Nearest(primaries[k], band);
        List<Box> gate = Reach(
          "gate",
          k + 1,
          Rect.Of([from]),
          Past,
          Rect.Of([to]),
          across,
          b => Clear(b, k)
        );
        if (gate.Count > 0)
          feeds.Add(new FeedLaid($"Gate{k + 1}.{n}", "gate", gate, [], k + 1));
      }
    if (station.HeadHeight != null)
      for (int k = 0; k < pieces.Count; k++)
        feeds.Add(new FeedLaid($"Head{k + 1}", "head", [], ["Head"], k + 1));
    if (closed)
      for (int i = 0; i < risers.Count; i++) {
        (string kind, Vec3 at) = risers[i];
        double[] section = station.Risers.TryGetValue(kind, out double[]? s)
          ? s
          : throw new CastGenException(
            $"{item}: the station has no riser section \"{kind}\""
          );
        feeds.Add(
          new FeedLaid(
            $"Riser{i + 1}",
            "riser",
            [
              new Box(
                new Vec3(at.X - section[0] / 2, at.Y, at.Z - section[1] / 2),
                new Vec3(at.X + section[0] / 2, top, at.Z + section[1] / 2)
              ),
            ],
            [],
            null
          )
        );
      }
    return feeds;
  }

  private static List<Box>? Route(
    Rect a,
    double pastA,
    Rect b,
    double w,
    double y0,
    double y1,
    Func<Box, bool> clear,
    Occupancy? solid
  ) {
    if (a.Overlaps(b))
      return [];
    var tries = new List<List<Box>>();
    double ox0 = Math.Max(a.X0, b.X0),
      ox1 = Math.Min(a.X1, b.X1),
      oz0 = Math.Max(a.Z0, b.Z0),
      oz1 = Math.Min(a.Z1, b.Z1);
    if (ox1 - ox0 >= w - Eps)
      tries.Add([
        MeetZ(
          LegZ((ox0 + ox1) / 2, w, a, pastA, b, b.Z1 <= a.Z0 + Eps, y0, y1),
          b.Z1 <= a.Z0 + Eps,
          b,
          solid
        ),
      ]);
    if (oz1 - oz0 >= w - Eps)
      tries.Add([
        MeetX(
          LegX((oz0 + oz1) / 2, w, a, pastA, b, b.X1 <= a.X0 + Eps, y0, y1),
          b.X1 <= a.X0 + Eps,
          b,
          solid
        ),
      ]);

    double cx = a.CentreX,
      cz = a.CentreZ;
    bool south = b.CentreZ <= a.Z0 + Eps;
    bool west = b.CentreX <= a.X0 + Eps;
    Box zFirst = LegZTo(cx, w, a, pastA, b.CentreZ, south, y0, y1);
    Box xAfter = MeetX(
      LegXTo(b.CentreZ, w, cx, b, b.X1 <= cx + Eps, y0, y1),
      b.X1 <= cx + Eps,
      b,
      solid
    );
    tries.Add([zFirst, xAfter]);
    Box xFirst = LegXTo2(cz, w, a, pastA, b.CentreX, west, y0, y1);
    Box zAfter = MeetZ(
      LegZFrom(b.CentreX, w, cz, b, b.Z1 <= cz + Eps, y0, y1),
      b.Z1 <= cz + Eps,
      b,
      solid
    );
    tries.Add([xFirst, zAfter]);
    return tries.FirstOrDefault(t => t.All(clear));
  }

  private const double Step = 1.0 / 16;

  private static Box MeetX(Box leg, bool towardLow, Rect b, Occupancy? solid) {
    if (solid == null)
      return leg;
    double z = (leg.Lo.Z + leg.Hi.Z) / 2;
    double? hit = Meet(
      solid,
      leg.Lo.Y,
      leg.Hi.Y,
      towardLow ? leg.Hi.X : leg.Lo.X,
      towardLow ? b.X0 : b.X1,
      x => new Vec3(x, 0, z)
    );
    if (hit == null)
      return leg;
    return towardLow
      ? new Box(new Vec3(hit.Value - Past, leg.Lo.Y, leg.Lo.Z), leg.Hi)
      : new Box(leg.Lo, new Vec3(hit.Value + Past, leg.Hi.Y, leg.Hi.Z));
  }

  private static Box MeetZ(Box leg, bool towardLow, Rect b, Occupancy? solid) {
    if (solid == null)
      return leg;
    double x = (leg.Lo.X + leg.Hi.X) / 2;
    double? hit = Meet(
      solid,
      leg.Lo.Y,
      leg.Hi.Y,
      towardLow ? leg.Hi.Z : leg.Lo.Z,
      towardLow ? b.Z0 : b.Z1,
      z => new Vec3(x, 0, z)
    );
    if (hit == null)
      return leg;
    return towardLow
      ? new Box(new Vec3(leg.Lo.X, leg.Lo.Y, hit.Value - Past), leg.Hi)
      : new Box(leg.Lo, new Vec3(leg.Hi.X, leg.Hi.Y, hit.Value + Past));
  }

  /// <summary>Where, scanning from <paramref name="from"/> to <paramref name="to"/>, the line first holds metal at
  /// one of three heights in the band; null when it never does.</summary>
  private static double? Meet(
    Occupancy solid,
    double y0,
    double y1,
    double from,
    double to,
    Func<double, Vec3> at
  ) {
    int n = (int)Math.Ceiling(Math.Abs(to - from) / Step);
    for (int i = 0; i <= n; i++) {
      double t = from + (to - from) * i / Math.Max(n, 1);
      foreach (double f in new[] { 0.25, 0.5, 0.75 }) {
        Vec3 p = at(t) with { Y = y0 + (y1 - y0) * f };
        if (
          solid.Boxes.Any(b => Inside(b, p))
          || solid.Prisms.Any(pr => pr.Contains(p))
        )
          return t;
      }
    }
    return null;
  }

  private static bool Inside(Box b, Vec3 p) =>
    Enumerable
      .Range(0, 3)
      .All(i => p[i] >= b.Lo[i] - Eps && p[i] <= b.Hi[i] + Eps);

  private static Box LegZ(
    double cx,
    double w,
    Rect a,
    double pastA,
    Rect b,
    bool towardLow,
    double y0,
    double y1
  ) {
    double lo = towardLow ? b.Z1 - Past : a.Z1 - pastA,
      hi = towardLow ? a.Z0 + pastA : b.Z0 + Past;
    return new Box(new Vec3(cx - w / 2, y0, lo), new Vec3(cx + w / 2, y1, hi));
  }

  private static Box LegX(
    double cz,
    double w,
    Rect a,
    double pastA,
    Rect b,
    bool towardLow,
    double y0,
    double y1
  ) {
    double lo = towardLow ? b.X1 - Past : a.X1 - pastA,
      hi = towardLow ? a.X0 + pastA : b.X0 + Past;
    return new Box(new Vec3(lo, y0, cz - w / 2), new Vec3(hi, y1, cz + w / 2));
  }

  private static Box LegZTo(
    double cx,
    double w,
    Rect a,
    double pastA,
    double z,
    bool towardLow,
    double y0,
    double y1
  ) {
    double lo = towardLow ? z - w / 2 : a.Z1 - pastA,
      hi = towardLow ? a.Z0 + pastA : z + w / 2;
    return new Box(new Vec3(cx - w / 2, y0, lo), new Vec3(cx + w / 2, y1, hi));
  }

  private static Box LegXTo(
    double z,
    double w,
    double cx,
    Rect b,
    bool towardLow,
    double y0,
    double y1
  ) {
    double lo = towardLow ? b.X1 - Past : cx - w / 2,
      hi = towardLow ? cx + w / 2 : b.X0 + Past;
    return new Box(new Vec3(lo, y0, z - w / 2), new Vec3(hi, y1, z + w / 2));
  }

  private static Box LegXTo2(
    double cz,
    double w,
    Rect a,
    double pastA,
    double x,
    bool towardLow,
    double y0,
    double y1
  ) {
    double lo = towardLow ? x - w / 2 : a.X1 - pastA,
      hi = towardLow ? a.X0 + pastA : x + w / 2;
    return new Box(new Vec3(lo, y0, cz - w / 2), new Vec3(hi, y1, cz + w / 2));
  }

  private static Box LegZFrom(
    double x,
    double w,
    double cz,
    Rect b,
    bool towardLow,
    double y0,
    double y1
  ) {
    double lo = towardLow ? b.Z1 - Past : cz - w / 2,
      hi = towardLow ? cz + w / 2 : b.Z0 + Past;
    return new Box(new Vec3(x - w / 2, y0, lo), new Vec3(x + w / 2, y1, hi));
  }

  /// <summary>The bodies of one piece, boxes that touch or overlap joined, the largest volume first.</summary>
  private static List<List<Box>> Bodies(IReadOnlyList<Box> boxes) {
    var bodies = new List<List<Box>>();
    var left = boxes.ToList();
    while (left.Count > 0) {
      var body = new List<Box> { left[0] };
      left.RemoveAt(0);
      for (int i = 0; i < body.Count; i++)
        for (int j = left.Count - 1; j >= 0; j--)
          if (Gap(body[i], left[j]) <= Eps) {
            body.Add(left[j]);
            left.RemoveAt(j);
          }
      bodies.Add(body);
    }
    return
    [
      .. bodies.OrderByDescending(b =>
        b.Sum(x => (x.Hi.X - x.Lo.X) * (x.Hi.Y - x.Lo.Y) * (x.Hi.Z - x.Lo.Z))
      ),
    ];
  }

  private static (Box From, Box To) Nearest(
    IReadOnlyList<Box> from,
    IReadOnlyList<Box> to
  ) =>
    (
      from.SelectMany(a => to.Select(b => (From: a, To: b)))
        .MinBy(p =>
          Math.Max(
            Math.Max(p.From.Lo.X - p.To.Hi.X, p.To.Lo.X - p.From.Hi.X),
            Math.Max(p.From.Lo.Z - p.To.Hi.Z, p.To.Lo.Z - p.From.Hi.Z)
          )
        )
    );

  private static double Gap(Box a, Box b) =>
    Enumerable
      .Range(0, 3)
      .Max(i => Math.Max(a.Lo[i] - b.Hi[i], b.Lo[i] - a.Hi[i]));
}
