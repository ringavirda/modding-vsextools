using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>What octcheck measures of a plate ring.</summary>
/// <param name="Flats">Across the flats.</param>
/// <param name="Diagonals">Across the diagonals.</param>
/// <param name="Bore">The bore across the flats.</param>
/// <param name="Worst">The worst deviation of either reach.</param>
/// <param name="Regular">Whether every plate is the same plate.</param>
internal sealed record RingMeasure(
  double Flats,
  double Diagonals,
  double Bore,
  double Worst,
  bool Regular
);

/// <summary>Plate-ring helpers of octcheck: the ring's frame, its pieces, and the section walked out from its axis.</summary>
internal static class PlateRingMeasure {
  private const double Edge = 0.03;
  private const double Tan = 0.4142;
  private const double RingTol = 0.03;
  public const int Gon = 16;

  /// <summary>Whether every plate of a ring is the same plate, its reach from the axis and its width within a
  /// hair (0.03) of the others'.</summary>
  public static bool IsRegular(List<Node> kids, int ax, Vec3 pivot) {
    (int a, int b) = Rings.Others(ax);
    var seen = new List<double[]>();
    foreach (Node k in kids) {
      Vec3 lo = k.Frm - pivot,
        hi = k.To - pivot;
      (int r, int t) =
        Math.Abs(lo[a] + hi[a]) > Math.Abs(lo[b] + hi[b]) ? (a, b) : (b, a);
      seen.Add([
        Math.Min(Math.Abs(lo[r]), Math.Abs(hi[r])),
        Math.Max(Math.Abs(lo[r]), Math.Abs(hi[r])),
        Math.Abs(hi[t] - lo[t]),
      ]);
    }
    return seen.All(s =>
      Enumerable
        .Range(0, 3)
        .All(i =>
          Math.Abs(s[i] - seen[0][i]) <= Edge + 1e-5 * Math.Abs(seen[0][i])
        )
    );
  }

  /// <summary>The plates of a ring and their pieces: each plate's children that span the plate's own box on two of
  /// its three axes (the remainder of a plate cut to the 16-unit limit along the third).</summary>
  public static List<Node> Pieces(List<Node> kids) {
    List<Node> outp = [.. kids];
    foreach (Node k in kids)
      outp.AddRange(
        k.Children.Where(c =>
          Enumerable
            .Range(0, 3)
            .Count(i =>
              Math.Abs(c.Frm[i]) < 1e-3
              && Math.Abs(c.Size[i] - k.Size[i]) < 1e-3
            ) >= 2
        )
      );
    return outp;
  }

  /// <summary>The path of every zero-size group of 16 sized plates, all its children, turned about one and the same
  /// axis: a 16-gon, which neither octcheck nor the plate ring reads.</summary>
  public static List<string> SixteenGons(Tree tree) =>
    [
      .. tree
        .Nodes.Where(g =>
          !g.AnySizeOver(1e-6)
          && g.Children.Count == Gon
          && g.Children.All(k => k.Solid)
          && g.Children.SelectMany(k =>
              Enumerable.Range(0, 3).Where(i => Math.Abs(k.Rot[i]) > 1e-9)
            )
            .Distinct()
            .Count() == 1
        )
        .Select(g => g.Path),
    ];

  /// <summary>The world axis nearest the group's axis <paramref name="ax"/>.</summary>
  public static int WorldAxis(Frames fr, Node g, int ax) {
    Vec3 c = fr.World(g).L.Col(ax);
    return Enumerable.Range(0, 3).MaxBy(i => Math.Abs(c[i]));
  }

  private static (double H, double KIn, double Lo, double Hi) Frame(
    Frames fr,
    Node g,
    int ax,
    List<Node> kids,
    Vec3 pivot
  ) {
    (int a, int b) = Rings.Others(ax);
    List<double> outs = [],
      ins = [];
    double lo = double.NegativeInfinity,
      hi = double.PositiveInfinity;
    foreach (Node k in kids) {
      Vec3[] p = [.. Sweep.CornersIn(fr, g, k).Select(c => c - pivot)];
      lo = Math.Max(lo, p.Min(c => c[ax]));
      hi = Math.Min(hi, p.Max(c => c[ax]));
      if (Math.Round(V.Mod(k.Rot[ax], 90), 4) != 0)
        continue;
      Vec3 mn = new(p.Min(c => c.X), p.Min(c => c.Y), p.Min(c => c.Z));
      Vec3 mx = new(p.Max(c => c.X), p.Max(c => c.Y), p.Max(c => c.Z));
      Vec3 ctr = (mn + mx) * 0.5;
      int r = Math.Abs(ctr[a]) > Math.Abs(ctr[b]) ? a : b;
      outs.Add(p.Max(c => Math.Abs(c[r])));
      ins.Add(p.Min(c => Math.Abs(c[r])));
    }
    return (V.Median(outs), V.Median(ins), lo, hi);
  }

  /// <summary>Measures a plate ring about its group's axis and lists what is wrong with it.</summary>
  public static (RingMeasure Measure, List<string> Faults) Measure(
    Frames fr,
    PlateRing ring
  ) {
    Node g = ring.Group;
    int ax = ring.Axis;
    Vec3 pivot = ring.Pivot;
    (int a, int b) = Rings.Others(ax);
    (double h, double kIn, double lo, double hi) = Frame(
      fr,
      g,
      ax,
      ring.Plates,
      pivot
    );
    bool regular = IsRegular(ring.Plates, ax, pivot);
    double s = regular ? Tan : 0.5;
    double diag = regular ? 1.0 : 1.5 / Math.Sqrt(2);
    double[] rr = Sweep.Radii(1.3 * diag * h + 0.5);
    List<Node> pieces = Pieces(ring.Plates);
    int nb = Sweep.Th.Length;
    var hit = new bool[nb][];
    for (int t = 0; t < nb; t++) {
      double cos = Math.Cos(Sweep.Th[t]),
        sin = Math.Sin(Sweep.Th[t]);
      hit[t] = new bool[rr.Length];
      for (int i = 0; i < rr.Length; i++) {
        double[] loc = new double[3];
        loc[ax] = pivot[ax] + (lo + hi) / 2;
        loc[a] = pivot[a] + rr[i] * cos;
        loc[b] = pivot[b] + rr[i] * sin;
        var p = new Vec3(loc[0], loc[1], loc[2]);
        hit[t][i] = pieces.Any(k => Sweep.Inside(fr, g, p, k, 1e-6));
      }
    }
    List<string> faults = [];
    bool[] some = [.. hit.Select(row => row.Any(x => x))];
    if (!some.All(x => x)) {
      int i = Array.IndexOf(some, false);
      faults.Add(
        $"{g.Path}: open on {some.Count(x => !x)} bearings, from {Sweep.F(Sweep.Deg(i), 2)} deg"
      );
    }
    var devIn = new double[nb];
    var devOut = new double[nb];
    double[] reachIn = Sweep.OctagonReach(kIn, kIn, s * kIn, s * kIn);
    double[] reachOut = Sweep.OctagonReach(h, h, s * h, s * h);
    for (int t = 0; t < nb; t++) {
      if (!some[t])
        continue;
      int first = Array.IndexOf(hit[t], true);
      int past = Array.FindIndex(hit[t], first, x => !x);
      devIn[t] = rr[first] - reachIn[t];
      devOut[t] = (past >= 0 ? rr[past] : rr[^1]) - reachOut[t];
    }
    (double[] Dev, int Sign, string What)[] rules =
    [
      (devIn, -1, "a plate {0} into the bore"),
      (devIn, 1, "a hole {0} deep in the bore"),
      (devOut, -1, "a hole {0} deep"),
      (devOut, 1, "a bump {0} proud"),
    ];
    foreach ((double[] dev, int sign, string what) in rules) {
      int i = 0;
      for (int t = 1; t < nb; t++)
        if (sign * dev[t] > sign * dev[i])
          i = t;
      if (sign * dev[i] > RingTol)
        faults.Add(
          $"{g.Path}: {string.Format(what, Sweep.F(Math.Abs(dev[i]), 3))} at {Sweep.F(Sweep.Deg(i), 2)} deg"
        );
    }
    double worst = Math.Max(devIn.Max(Math.Abs), devOut.Max(Math.Abs));
    return (
      new RingMeasure(2 * h, 2 * diag * h, 2 * kIn, worst, regular),
      faults
    );
  }
}
