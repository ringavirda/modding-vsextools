using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>Coplanar, same-facing, overlapping axis-aligned faces of different leaves: z-fighting in game.</summary>
/// <remarks>Only faces whose world normal lies within 0.01 of a coordinate axis are read. Two faces turned in
/// their own plane are held to the area their exact quads share. Only the part of a pair's overlap that shows
/// counts, by flushcheck's test: a buried overlap, such as the plates of a round cast elbow's neighbouring pieces
/// meeting inside the next piece, is not a fight.</remarks>
public static class ZfightLint {
  /// <summary>One fighting pair.</summary>
  /// <param name="Path1">The first element's path.</param>
  /// <param name="Path2">The second element's path.</param>
  /// <param name="Face">The first element's face name.</param>
  /// <param name="Area">The shown overlap in square voxels.</param>
  public sealed record Pair(
    string Path1,
    string Path2,
    string Face,
    double Area
  );

  private sealed record Face(string Path, string Name, Vec3[] Pts, Vec3 Normal);

  private static (int Axis, int Sign)? AxisAligned(Vec3 n) {
    int idx = 0;
    for (int a = 1; a < 3; a++)
      if (Math.Abs(n[a]) > Math.Abs(n[idx]))
        idx = a;
    for (int a = 0; a < 3; a++)
      if (a != idx && Math.Abs(n[a]) >= 0.01)
        return null;
    return Math.Abs(n[idx]) > 0.999 ? (idx, n[idx] > 0 ? 1 : -1) : null;
  }

  private static (double X, double Y)[] Ring(Vec3[] pts, int[] plane) {
    var p = pts.Select(v => (X: v[plane[0]], Y: v[plane[1]])).ToArray();
    double cx = p.Average(v => v.X),
      cy = p.Average(v => v.Y);
    return [.. p.OrderBy(v => Math.Atan2(v.Y - cy, v.X - cx))];
  }

  private static double Shared(
    (double X, double Y)[] a,
    (double X, double Y)[] b
  ) {
    List<(double X, double Y)> outp = [.. a];
    for (int i = 0; i < b.Length; i++) {
      var p = b[i];
      var q = b[(i + 1) % b.Length];
      (double X, double Y) edge = (q.X - p.X, q.Y - p.Y);
      bool[] inside =
      [
        .. outp.Select(v => edge.X * (v.Y - p.Y) - edge.Y * (v.X - p.X) >= 0),
      ];
      List<(double X, double Y)> next = [];
      for (int k = 0; k < outp.Count; k++) {
        var v = outp[k];
        var w = outp[(k + 1) % outp.Count];
        if (inside[k])
          next.Add(v);
        if (inside[k] != inside[(k + 1) % outp.Count]) {
          (double X, double Y) d = (w.X - v.X, w.Y - v.Y);
          double den = edge.X * d.Y - edge.Y * d.X;
          double t =
            den != 0 ? (edge.X * (p.Y - v.Y) - edge.Y * (p.X - v.X)) / den : 0;
          next.Add((v.X + t * d.X, v.Y + t * d.Y));
        }
      }
      outp = next;
      if (outp.Count == 0)
        return 0;
    }
    double s = 0;
    for (int i = 0; i < outp.Count; i++) {
      var v = outp[i];
      var w = outp[(i + 1) % outp.Count];
      s += v.X * w.Y - v.Y * w.X;
    }
    return Math.Abs(s) / 2;
  }

  /// <summary>The fighting pairs of the shape at rest and the number of axis-aligned faces read.</summary>
  public static (List<Pair> Pairs, int Faces) Pairs(Shape shape) {
    var tree = new Tree(shape);
    var frames = new Frames(shape);
    List<Node> leaves = [.. tree.Leaves];
    var byDir = new Dictionary<(int, int), List<Face>>();
    int faces = 0;
    foreach (Node n in leaves)
      foreach (Quad q in Engine.FaceQuads(n.Element, n.Path, frames.Mat(n))) {
        if (AxisAligned(q.Normal) is not { } dir)
          continue;
        if (!byDir.TryGetValue(dir, out List<Face>? list))
          byDir[dir] = list = [];
        list.Add(new Face(n.Path, q.Face, q.Points, q.Normal));
        faces++;
      }
    Func<Vec3, bool> shows = Overlap.Uncovered(leaves, frames);
    var seen = new HashSet<(string, string)>();
    var found = new List<Pair>();
    foreach (var (dir, group) in byDir) {
      int axis = dir.Item1;
      int[] plane = [.. Enumerable.Range(0, 3).Where(a => a != axis)];
      for (int i = 0; i < group.Count; i++) {
        Face f1 = group[i];
        double coord1 = f1.Pts.Average(p => p[axis]);
        double[] lo1 = [.. plane.Select(a => f1.Pts.Min(p => p[a]))];
        double[] hi1 = [.. plane.Select(a => f1.Pts.Max(p => p[a]))];
        for (int j = i + 1; j < group.Count; j++) {
          Face f2 = group[j];
          if (f1.Path == f2.Path)
            continue;
          if (Math.Abs(coord1 - f2.Pts.Average(p => p[axis])) > Overlap.Apart)
            continue;
          double[] lo2 = [.. plane.Select(a => f2.Pts.Min(p => p[a]))];
          double[] hi2 = [.. plane.Select(a => f2.Pts.Max(p => p[a]))];
          double ox = Math.Max(
            0,
            Math.Min(hi1[0], hi2[0]) - Math.Max(lo1[0], lo2[0])
          );
          double oy = Math.Max(
            0,
            Math.Min(hi1[1], hi2[1]) - Math.Max(lo1[1], lo2[1])
          );
          double area = ox * oy;
          if (area <= 0.01)
            continue;
          var r1 = Ring(f1.Pts, plane);
          var r2 = Ring(f2.Pts, plane);
          bool square =
            Math.Abs(Shared(r1, r1) - (hi1[0] - lo1[0]) * (hi1[1] - lo1[1]))
              <= 1e-6
            && Math.Abs(Shared(r2, r2) - (hi2[0] - lo2[0]) * (hi2[1] - lo2[1]))
              <= 1e-6;
          if (!square && Shared(r1, r2) <= 0.01)
            continue;
          var pair =
            string.CompareOrdinal(f1.Path, f2.Path) < 0
              ? (f1.Path, f2.Path)
              : (f2.Path, f1.Path);
          if (seen.Contains(pair))
            continue;
          area = Overlap.Shown(
            f1.Pts,
            f1.Normal,
            V.Dot(f1.Normal, f1.Pts[0]),
            f2.Pts,
            f2.Normal,
            V.Dot(f2.Normal, f2.Pts[0]),
            shows
          );
          if (area <= 0.01)
            continue;
          seen.Add(pair);
          found.Add(new Pair(f1.Path, f2.Path, f1.Name, area));
        }
      }
    }
    return (found, faces);
  }

  /// <summary>Lints one shape.</summary>
  /// <param name="shape">The shape.</param>
  /// <returns>One warning per fighting pair, and the number of axis-aligned faces read.</returns>
  public static (IReadOnlyList<Finding> Findings, int Enumerated) Check(
    Shape shape
  ) {
    (List<Pair> pairs, int faces) = Pairs(shape);
    return (
      [
        .. pairs.Select(p => new Finding(
          "warn",
          "zfight",
          "flush",
          p.Path1,
          $"{p.Path1} and {p.Path2} share a coplanar, overlapping {p.Face} face"
        )),
      ],
      faces
    );
  }
}
