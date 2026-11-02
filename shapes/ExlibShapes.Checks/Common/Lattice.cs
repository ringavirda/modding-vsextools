using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// The sampling lattice a gate reads a turned leaf on: sample points at <c>(m + Phase) * Step</c> on every axis, each
/// standing for the cell of one step centred on it. A leaf with its axes on the world axes is its exact box.
/// </summary>
/// <param name="Step">The lattice spacing in voxels.</param>
/// <param name="Phase">The offset of the sample points from the multiples of <paramref name="Step"/>, in steps.</param>
internal readonly record struct Lattice(double Step, double Phase) {
  private const double Eps = 1e-9;

  /// <summary>1/32 voxel, samples at the cell centres.</summary>
  public static Lattice Fine => new(1.0 / 32, 0.5);

  /// <summary>1/16 voxel, samples 1/64 off the planes.</summary>
  public static Lattice Flood => new(1.0 / 16, 0.25);

  /// <summary>Whether each local axis of <paramref name="world"/> lies on one world axis.</summary>
  public static bool IsAligned(Mat4d world) {
    for (int a = 0; a < 3; a++) {
      var unit = new double[3];
      unit[a] = 1;
      Vec3 d = world.MulLinear(new Vec3(unit[0], unit[1], unit[2]));
      if (Enumerable.Range(0, 3).Count(i => Math.Abs(d[i]) > Eps) != 1)
        return false;
    }
    return true;
  }

  /// <summary>
  /// The solid of a box of <paramref name="size"/> placed by <paramref name="world"/>: its exact bounds when aligned,
  /// else the boxes of the lattice cells whose sample lies inside it, a run along x per row of y and z, equal runs of
  /// adjacent y rows joined.
  /// </summary>
  public List<Box> Solid(Mat4d world, Vec3 size) {
    Box bounds = WorldBox.Of(world, size);
    if (IsAligned(world))
      return [bounds];
    double[,] inv = Inverse(world);
    var o = new double[3];
    for (int r = 0; r < 3; r++)
      o[r] = -(
        inv[r, 0] * world[0, 3]
        + inv[r, 1] * world[1, 3]
        + inv[r, 2] * world[2, 3]
      );
    double[] extent = [size.X, size.Y, size.Z];
    int[] from = new int[3],
      to = new int[3];
    for (int a = 0; a < 3; a++) {
      from[a] = (int)Math.Ceiling(bounds.Lo[a] / Step - Phase - Eps);
      to[a] = (int)Math.Floor(bounds.Hi[a] / Step - Phase + Eps);
    }
    var cells = new List<Box>();
    for (int k = from[2]; k <= to[2]; k++) {
      double z = (k + Phase) * Step;
      (int, int)? open = null;
      int start = 0;
      for (int j = from[1]; j <= to[1] + 1; j++) {
        (int, int)? run =
          j <= to[1] ? Run(inv, o, extent, from[0], to[0], j, z) : null;
        if (open != null && open != run) {
          cells.Add(Cell(open.Value, start, j - 1, k));
          open = null;
        }
        if (run != null && open == null) {
          open = run;
          start = j;
        }
      }
    }
    return cells;
  }

  private (int, int)? Run(
    double[,] inv,
    double[] o,
    double[] extent,
    int i0,
    int i1,
    int j,
    double z
  ) {
    double y = (j + Phase) * Step,
      lo = (i0 + Phase) * Step,
      hi = (i1 + Phase) * Step;
    for (int r = 0; r < 3; r++) {
      double b = inv[r, 0],
        a = o[r] + inv[r, 1] * y + inv[r, 2] * z;
      if (Math.Abs(b) < 1e-12) {
        if (a < -Eps || a > extent[r] + Eps)
          return null;
        continue;
      }
      double x0 = (-Eps - a) / b,
        x1 = (extent[r] + Eps - a) / b;
      lo = Math.Max(lo, Math.Min(x0, x1));
      hi = Math.Min(hi, Math.Max(x0, x1));
    }
    int m0 = (int)Math.Ceiling(lo / Step - Phase - Eps),
      m1 = (int)Math.Floor(hi / Step - Phase + Eps);
    return m0 <= m1 ? (m0, m1) : null;
  }

  private Box Cell((int, int) run, int j0, int j1, int k) =>
    new(
      new Vec3(
        (run.Item1 + Phase - 0.5) * Step,
        (j0 + Phase - 0.5) * Step,
        (k + Phase - 0.5) * Step
      ),
      new Vec3(
        (run.Item2 + Phase + 0.5) * Step,
        (j1 + Phase + 0.5) * Step,
        (k + Phase + 0.5) * Step
      )
    );

  private static double[,] Inverse(Mat4d m) {
    double a = m[0, 0],
      b = m[0, 1],
      c = m[0, 2],
      d = m[1, 0],
      e = m[1, 1],
      f = m[1, 2],
      g = m[2, 0],
      h = m[2, 1],
      i = m[2, 2];
    double det =
      a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
    if (Math.Abs(det) < 1e-12)
      throw new CheckUsageException("a leaf is flattened by its placement");
    return new[,]
    {
      { (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det },
      { (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det },
      { (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det },
    };
  }
}
