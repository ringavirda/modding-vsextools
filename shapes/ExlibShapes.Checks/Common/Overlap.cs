using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>Coplanar-face overlap shared by flushcheck and the z-fight lint.</summary>
internal static class Overlap {
  public const double Grid = 0.05;
  public const double Parallel = 0.99999;
  public const double Apart = 0.001;

  /// <summary>The determinant of a matrix's linear part.</summary>
  public static double Det(Mat4d m) =>
    V.Dot(
      m.MulLinear(new Vec3(1, 0, 0)),
      V.Cross(m.MulLinear(new Vec3(0, 1, 0)), m.MulLinear(new Vec3(0, 0, 1)))
    );

  /// <summary>A test of world points against the boxes of <paramref name="leaves"/> at <paramref name="frames"/>:
  /// true where no leaf holds the point, a hair (1e-6) inside its faces counting as held.</summary>
  public static Func<Vec3, bool> Uncovered(
    IReadOnlyList<Node> leaves,
    Frames frames
  ) {
    var held = leaves
      .Select(n =>
        (
          Inv: frames.Inverse(n),
          n.Size,
          Box: WorldBox.Of(frames.Mat(n), n.Size)
        )
      )
      .ToList();
    return p => {
      foreach (var h in held) {
        bool near = true;
        for (int a = 0; a < 3; a++)
          near &= h.Box.Lo[a] <= p[a] + 1e-6 && h.Box.Hi[a] >= p[a] - 1e-6;
        if (!near)
          continue;
        Vec3 l = h.Inv.Apply(p);
        bool inside = true;
        for (int a = 0; a < 3; a++)
          inside &= l[a] > -1e-6 && l[a] < h.Size[a] + 1e-6;
        if (inside)
          return false;
      }
      return true;
    };
  }

  private static bool Inside(double x, double y, (double X, double Y)[] quad) {
    double area = 0;
    for (int i = 0; i < 4; i++) {
      (double X, double Y) p = quad[i],
        q = quad[(i + 1) % 4];
      area += p.X * q.Y - q.X * p.Y;
    }
    double s = area > 0 ? 1 : -1;
    for (int i = 0; i < 4; i++) {
      (double X, double Y) p = quad[i],
        q = quad[(i + 1) % 4];
      if (!(s * ((q.X - p.X) * (y - p.Y) - (q.Y - p.Y) * (x - p.X)) > 1e-6))
        return false;
    }
    return true;
  }

  /// <summary>Square voxels of the overlap of near-parallel quads <paramref name="w1"/> and <paramref name="w2"/>
  /// (world corners; <paramref name="n1"/>, <paramref name="d1"/> and <paramref name="n2"/>,
  /// <paramref name="d2"/> their planes), counted where the second lies within 0.001 of the first along
  /// <paramref name="n1"/> and <paramref name="shows"/> finds the point 0.01 out along <paramref name="n1"/>
  /// uncovered, on a 0.05 grid; 0 when the overlap within 0.001 is 0.01 or less.</summary>
  public static double Shown(
    Vec3[] w1,
    Vec3 n1,
    double d1,
    Vec3[] w2,
    Vec3 n2,
    double d2,
    Func<Vec3, bool> shows
  ) {
    Vec3 u = V.Unit(w1[1] - w1[0]);
    Vec3 v = V.Cross(n1, u);
    var q1 = w1.Select(p => (V.Dot(p, u), V.Dot(p, v))).ToArray();
    var q2 = w2.Select(p => (V.Dot(p, u), V.Dot(p, v))).ToArray();
    double lox = Math.Max(q1.Min(p => p.Item1), q2.Min(p => p.Item1));
    double loy = Math.Max(q1.Min(p => p.Item2), q2.Min(p => p.Item2));
    double hix = Math.Min(q1.Max(p => p.Item1), q2.Max(p => p.Item1));
    double hiy = Math.Min(q1.Max(p => p.Item2), q2.Max(p => p.Item2));
    if (hix - lox <= 0 || hiy - loy <= 0)
      return 0;
    int nx = (int)Math.Ceiling((hix - (lox + Grid / 2)) / Grid);
    int ny = (int)Math.Ceiling((hiy - (loy + Grid / 2)) / Grid);
    var kept = new List<(double X, double Y)>();
    for (int iy = 0; iy < ny; iy++)
      for (int ix = 0; ix < nx; ix++) {
        double x = lox + Grid / 2 + ix * Grid;
        double y = loy + Grid / 2 + iy * Grid;
        if (!Inside(x, y, q1) || !Inside(x, y, q2))
          continue;
        Vec3 on1 = u * x + v * y + n1 * d1;
        if (Math.Abs((d2 - V.Dot(on1, n2)) / V.Dot(n1, n2)) <= Apart)
          kept.Add((x, y));
      }
    if (kept.Count * Grid * Grid <= 0.01)
      return 0;
    int shown = kept.Count(k => shows(u * k.X + v * k.Y + n1 * (d1 + 0.01)));
    return shown * Grid * Grid;
  }
}
