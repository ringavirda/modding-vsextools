using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>A round run of staves about a world axis, read as the solid it turns.</summary>
/// <param name="Members">The staves, all leaves.</param>
/// <param name="Axis">The world axis the run turns about, 0 x, 1 y or 2 z.</param>
/// <param name="Lo">Where the run starts on <paramref name="Axis"/>, the lowest of its staves, in voxels.</param>
/// <param name="Hi">Where it ends, the highest of its staves.</param>
/// <param name="Polygons">Convex outlines in the other two axes, <c>(Axis + 1) % 3</c> as U and
/// <c>(Axis + 2) % 3</c> as V: each stave, or the hull of two neighbours, whose union is the run's section with its
/// bore left open.</param>
internal sealed record StaveRun(
  List<Node> Members,
  int Axis,
  double Lo,
  double Hi,
  List<List<(double U, double V)>> Polygons
);

/// <summary>
/// The round runs a pattern is drawn with, read as solids of revolution: the staves of one turned run stand for the
/// polygon their outer faces span about the axis over the run's full axial extent, so a drawing's stagger of the
/// staves along the axis and the wedge gaps between them break no column and take no stave loose.
/// </summary>
internal static class StaveRuns {
  private const double Eps = 1e-9;
  private const double OnWorldAxis = 1e-6;

  private const int MinRun = 6;
  private const double CrossTol = 0.02;

  /// <summary>Every round run of <paramref name="tree"/>'s leaves about a world axis: six or more boxes of one
  /// cross section, each with a side along that axis, whose centres circle one line and turn with their place on it.
  /// The leaves need not be siblings. A leaf stands in one run.</summary>
  public static List<StaveRun> Find(Tree tree, Frames fr) {
    var used = new HashSet<string>();
    var found = new List<StaveRun>();
    for (int axis = 0; axis < 3; axis++) {
      var cross = new List<(Node Leaf, double A, double B)>();
      foreach (Node n in tree.Leaves.Where(n => n.Solid)) {
        M3 rot = fr.World(n).L;
        int along = Enumerable
          .Range(0, 3)
          .FirstOrDefault(
            j => Math.Abs(rot.Col(j)[axis]) > 1 - OnWorldAxis,
            -1
          );
        if (along < 0)
          continue;
        double[] rest =
        [
          .. Enumerable
            .Range(0, 3)
            .Where(j => j != along)
            .Select(j => n.Size[j])
            .Order(),
        ];
        cross.Add((n, rest[0], rest[1]));
      }
      while (cross.Count > 0) {
        var first = cross[0];
        List<(Node Leaf, double A, double B)> same =
        [
          .. cross.Where(c =>
            Math.Abs(c.A - first.A) <= CrossTol
            && Math.Abs(c.B - first.B) <= CrossTol
          ),
        ];
        cross.RemoveAll(c => same.Contains(c));
        if (same.Count < MinRun)
          continue;
        double link = 3 * first.B;
        List<Node> left = [.. same.Select(c => c.Leaf)];
        while (left.Count > 0) {
          var part = new List<Node> { left[0] };
          left.RemoveAt(0);
          for (int i = 0; i < part.Count; i++)
            foreach (
              Node n in left.Where(n =>
                  V.Norm(fr.Centre(n) - fr.Centre(part[i])) <= link
                )
                .ToList()
            ) {
              part.Add(n);
              left.Remove(n);
            }
          if (
            part.Count >= MinRun
            && part.All(n => !used.Contains(n.Path))
            && RoundRuns.Circle(part, fr) is { } circle
            && Math.Abs(circle.Axis[axis]) > 1 - OnWorldAxis
          ) {
            found.Add(Of(part, circle.Point, axis, fr));
            foreach (Node n in part)
              used.Add(n.Path);
          }
        }
      }
    }
    return found;
  }

  private static StaveRun Of(
    List<Node> members,
    Vec3 point,
    int axis,
    Frames fr
  ) {
    int u = (axis + 1) % 3,
      v = (axis + 2) % 3;
    double lo = double.MaxValue,
      hi = double.MinValue;
    var sections = new List<(double Angle, List<(double U, double V)> Hull)>();
    foreach (Node m in members) {
      Vec3[] corners = Engine.Corners(fr.Mat(m), m.Size);
      lo = Math.Min(lo, corners.Min(c => c[axis]));
      hi = Math.Max(hi, corners.Max(c => c[axis]));
      List<(double U, double V)> hull = Hull([
        .. corners.Select(c => (c[u], c[v])),
      ]);
      double cu = hull.Average(p => p.U) - point[u],
        cv = hull.Average(p => p.V) - point[v];
      sections.Add((Math.Atan2(cv, cu), hull));
    }
    sections.Sort((a, b) => a.Angle.CompareTo(b.Angle));
    int n = sections.Count;
    double[] gaps =
    [
      .. Enumerable
        .Range(0, n)
        .Select(i =>
          V.Mod(
            (sections[(i + 1) % n].Angle - sections[i].Angle) * 180 / Math.PI,
            360.0
          )
        ),
    ];
    double step = V.Median([.. gaps]);
    var polygons = new List<List<(double U, double V)>>();
    var paired = new bool[n];
    for (int i = 0; i < n; i++) {
      if (gaps[i] > 1.5 * step)
        continue;
      int j = (i + 1) % n;
      paired[i] = paired[j] = true;
      polygons.Add(Hull([.. sections[i].Hull, .. sections[j].Hull]));
    }
    for (int i = 0; i < n; i++)
      if (!paired[i])
        polygons.Add(sections[i].Hull);
    return new StaveRun(members, axis, lo, hi, polygons);
  }

  /// <summary>The boxes of the lattice cells of the run's section whose sample lies inside it, each the run's full
  /// extent along its axis; a run of cells along U is one box.</summary>
  public static List<Box> Solid(StaveRun run, Lattice lattice) {
    int a = run.Axis,
      u = (a + 1) % 3,
      v = (a + 2) % 3;
    double s = lattice.Step;
    double minU = run.Polygons.Min(p => p.Min(q => q.U)),
      maxU = run.Polygons.Max(p => p.Max(q => q.U)),
      minV = run.Polygons.Min(p => p.Min(q => q.V)),
      maxV = run.Polygons.Max(p => p.Max(q => q.V));
    var cells = new HashSet<(int, int)>();
    foreach (List<(double U, double V)> poly in run.Polygons) {
      int u0 = (int)Math.Ceiling(poly.Min(p => p.U) / s - lattice.Phase - Eps),
        u1 = (int)Math.Floor(poly.Max(p => p.U) / s - lattice.Phase + Eps),
        v0 = (int)Math.Ceiling(poly.Min(p => p.V) / s - lattice.Phase - Eps),
        v1 = (int)Math.Floor(poly.Max(p => p.V) / s - lattice.Phase + Eps);
      for (int j = v0; j <= v1; j++)
        for (int i = u0; i <= u1; i++)
          if (Inside(poly, (i + lattice.Phase) * s, (j + lattice.Phase) * s))
            cells.Add((i, j));
    }
    var boxes = new List<Box>();
    foreach (var row in cells.GroupBy(c => c.Item2).OrderBy(g => g.Key)) {
      int[] us = [.. row.Select(c => c.Item1).Order()];
      int from = us[0];
      for (int k = 1; k <= us.Length; k++) {
        if (k < us.Length && us[k] == us[k - 1] + 1)
          continue;
        var lo = new double[3];
        var hi = new double[3];
        lo[a] = run.Lo;
        hi[a] = run.Hi;
        lo[u] = Math.Max((from + lattice.Phase - 0.5) * s, minU);
        hi[u] = Math.Min((us[k - 1] + lattice.Phase + 0.5) * s, maxU);
        lo[v] = Math.Max((row.Key + lattice.Phase - 0.5) * s, minV);
        hi[v] = Math.Min((row.Key + lattice.Phase + 0.5) * s, maxV);
        boxes.Add(
          new Box(new Vec3(lo[0], lo[1], lo[2]), new Vec3(hi[0], hi[1], hi[2]))
        );
        if (k < us.Length)
          from = us[k];
      }
    }
    return boxes;
  }

  private static bool Inside(
    List<(double U, double V)> poly,
    double u,
    double v
  ) {
    int sign = 0;
    for (int i = 0; i < poly.Count; i++) {
      (double au, double av) = poly[i];
      (double bu, double bv) = poly[(i + 1) % poly.Count];
      double cross = (bu - au) * (v - av) - (bv - av) * (u - au);
      if (Math.Abs(cross) <= Eps)
        continue;
      int c = Math.Sign(cross);
      if (sign == 0)
        sign = c;
      else if (c != sign)
        return false;
    }
    return true;
  }

  /// <summary>The convex hull of <paramref name="pts"/>, counter-clockwise; the points themselves when fewer than 3
  /// are distinct.</summary>
  public static List<(double U, double V)> Hull(List<(double U, double V)> pts) {
    List<(double U, double V)> s =
    [
      .. pts.Distinct().OrderBy(p => p.U).ThenBy(p => p.V),
    ];
    if (s.Count < 3)
      return s;
    static double Cross(
      (double U, double V) o,
      (double U, double V) a,
      (double U, double V) b
    ) => (a.U - o.U) * (b.V - o.V) - (a.V - o.V) * (b.U - o.U);
    var h = new List<(double U, double V)>();
    foreach (var p in s) {
      while (h.Count >= 2 && Cross(h[^2], h[^1], p) <= Eps)
        h.RemoveAt(h.Count - 1);
      h.Add(p);
    }
    int lower = h.Count + 1;
    for (int i = s.Count - 2; i >= 0; i--) {
      while (h.Count >= lower && Cross(h[^2], h[^1], s[i]) <= Eps)
        h.RemoveAt(h.Count - 1);
      h.Add(s[i]);
    }
    h.RemoveAt(h.Count - 1);
    return h;
  }
}
