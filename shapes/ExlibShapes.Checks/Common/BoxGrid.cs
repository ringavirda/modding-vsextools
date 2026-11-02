using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// The grid cut by every plane of a set of boxes, with a break at every half voxel on x and z over a plan, so each
/// cell lies in one half-voxel plan cell. Volumes of unions and intersections of axis-aligned boxes are sums over
/// its cells, exact.
/// </summary>
internal sealed class BoxGrid {
  public double[] X { get; }
  public double[] Y { get; }
  public double[] Z { get; }

  public BoxGrid(IEnumerable<Box> boxes, Box? plan = null) {
    List<Box> all = [.. boxes];
    if (plan is Box p)
      all.Add(p);
    X = Breaks(all, 0, plan);
    Y = Breaks(all, 1, null);
    Z = Breaks(all, 2, plan);
  }

  public int Count => (X.Length - 1) * (Y.Length - 1) * (Z.Length - 1);

  private static double[] Breaks(List<Box> boxes, int axis, Box? plan) {
    var set = new SortedSet<double>();
    foreach (Box b in boxes) {
      set.Add(Math.Round(b.Lo[axis], 9));
      set.Add(Math.Round(b.Hi[axis], 9));
    }
    if (plan is Box p)
      for (
        double v = Math.Ceiling(p.Lo[axis] * 2) / 2;
        v < p.Hi[axis] - 1e-9;
        v += 0.5
      )
        set.Add(Math.Round(v, 9));
    return [.. set];
  }

  public int Index(int i, int j, int k) =>
    (i * (Y.Length - 1) + j) * (Z.Length - 1) + k;

  public double Volume(int i, int j, int k) =>
    (X[i + 1] - X[i]) * (Y[j + 1] - Y[j]) * (Z[k + 1] - Z[k]);

  /// <summary>The cells inside any of <paramref name="boxes"/>.</summary>
  public bool[] Mark(IEnumerable<Box> boxes) {
    var mask = new bool[Count];
    foreach (Box b in boxes) {
      (int i0, int i1) = Span(X, b.Lo.X, b.Hi.X);
      (int j0, int j1) = Span(Y, b.Lo.Y, b.Hi.Y);
      (int k0, int k1) = Span(Z, b.Lo.Z, b.Hi.Z);
      for (int i = i0; i < i1; i++)
        for (int j = j0; j < j1; j++)
          for (int k = k0; k < k1; k++)
            mask[Index(i, j, k)] = true;
    }
    return mask;
  }

  private static (int, int) Span(double[] breaks, double lo, double hi) {
    int a = Array.BinarySearch(breaks, Math.Round(lo, 9));
    int b = Array.BinarySearch(breaks, Math.Round(hi, 9));
    return (a < 0 ? ~a : a, b < 0 ? ~b : b);
  }

  /// <summary>The volume of the cells <paramref name="mask"/> holds, in vx3.</summary>
  public double Total(bool[] mask) {
    double v = 0;
    Each(mask, (i, j, k) => v += Volume(i, j, k));
    return v;
  }

  public void Each(bool[] mask, Action<int, int, int> visit) {
    for (int i = 0; i < X.Length - 1; i++)
      for (int j = 0; j < Y.Length - 1; j++)
        for (int k = 0; k < Z.Length - 1; k++)
          if (mask[Index(i, j, k)])
            visit(i, j, k);
  }

  /// <summary>The half-voxel plan cell holding the most of <paramref name="mask"/>'s volume, as
  /// <c>x A z B: V vx3</c> with A, B its low corner.</summary>
  public string Worst(bool[] mask) {
    var cells = new Dictionary<(double, double), double>();
    Each(
      mask,
      (i, j, k) => {
        var key = (
          Math.Floor((X[i] + X[i + 1]) / 2 * 2) / 2,
          Math.Floor((Z[k] + Z[k + 1]) / 2 * 2) / 2
        );
        cells[key] = cells.GetValueOrDefault(key) + Volume(i, j, k);
      }
    );
    ((double x, double z), double v) = cells.MaxBy(c => c.Value);
    return $"x {Fmt.G(x)} z {Fmt.G(z)}: {Fmt.G(v)} vx3";
  }
}
