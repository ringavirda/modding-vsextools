using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Generators;

/// <summary>Step 11: the sand of a station, its volume less what the occupancy holds.</summary>
public static class Sands {
  /// <summary>The longest a sand box may be on any axis, in voxels.</summary>
  public const double Limit = 16;

  /// <summary>The grid step turned leaves are read on, in voxels.</summary>
  public const double Step = 0.125;

  private const double Eps = 1e-9;

  /// <summary>The boxes of <paramref name="region"/> that <paramref name="occupied"/> does not hold: the free cells
  /// of the grid the occupancy's edges make, joined greedily into maximal boxes, none over
  /// <see cref="Limit"/> on any axis. A cell is held when its centre lies in a held box or prism; the edges of a
  /// turned leaf are read on a grid of <paramref name="step"/>, so its sand follows the polygon to that step. The
  /// boxes come in z, y, x order of their first cell.</summary>
  /// <param name="region">The volume to fill.</param>
  /// <param name="occupied">What is not sand; anything outside the region is ignored.</param>
  /// <param name="step">The grid step on the plane of a turned leaf, in voxels; <see cref="Step"/> when null.</param>
  public static List<Box> Fill(
    Box region,
    Occupancy occupied,
    double? step = null
  ) {
    var grid = new Grid(region, occupied, step ?? Step);
    var used = new bool[grid.Cells.Length];
    var boxes = new List<Box>();
    for (int k = 0; k < grid.N[2]; k++)
      for (int j = 0; j < grid.N[1]; j++)
        for (int i = 0; i < grid.N[0]; i++) {
          int at = grid.At(i, j, k);
          if (grid.Cells[at] || used[at])
            continue;
          int i1 = i + 1;
          while (
            i1 < grid.N[0]
            && !grid.Cells[grid.At(i1, j, k)]
            && !used[grid.At(i1, j, k)]
            && grid.Lines[0][i1 + 1] - grid.Lines[0][i] <= Limit + Eps
          )
            i1++;
          int j1 = j + 1;
          while (
            j1 < grid.N[1]
            && grid.Lines[1][j1 + 1] - grid.Lines[1][j] <= Limit + Eps
            && Free(grid, used, i, i1, j1, j1 + 1, k, k + 1)
          )
            j1++;
          int k1 = k + 1;
          while (
            k1 < grid.N[2]
            && grid.Lines[2][k1 + 1] - grid.Lines[2][k] <= Limit + Eps
            && Free(grid, used, i, i1, j, j1, k1, k1 + 1)
          )
            k1++;
          for (int c = k; c < k1; c++)
            for (int b = j; b < j1; b++)
              for (int a = i; a < i1; a++)
                used[grid.At(a, b, c)] = true;
          boxes.Add(
            new Box(
              new Vec3(grid.Lines[0][i], grid.Lines[1][j], grid.Lines[2][k]),
              new Vec3(grid.Lines[0][i1], grid.Lines[1][j1], grid.Lines[2][k1])
            )
          );
        }
    return boxes;
  }

  /// <summary>The volume <paramref name="occupied"/> holds, each cell once, in cubic voxels; held cells are
  /// read as <see cref="Fill"/> reads them.</summary>
  /// <param name="occupied">The occupancy to measure.</param>
  /// <param name="step">The grid step on the plane of a turned leaf, in voxels.</param>
  public static double Volume(Occupancy occupied, double step) {
    if (occupied.Empty)
      return 0;
    var grid = new Grid(occupied.Bounds, occupied, step);
    double total = 0;
    for (int k = 0; k < grid.N[2]; k++)
      for (int j = 0; j < grid.N[1]; j++)
        for (int i = 0; i < grid.N[0]; i++)
          if (grid.Cells[grid.At(i, j, k)])
            total +=
              (grid.Lines[0][i + 1] - grid.Lines[0][i])
              * (grid.Lines[1][j + 1] - grid.Lines[1][j])
              * (grid.Lines[2][k + 1] - grid.Lines[2][k]);
    return total;
  }

  private static bool Free(
    Grid g,
    bool[] used,
    int i0,
    int i1,
    int j0,
    int j1,
    int k0,
    int k1
  ) {
    for (int k = k0; k < k1; k++)
      for (int j = j0; j < j1; j++)
        for (int i = i0; i < i1; i++)
          if (g.Cells[g.At(i, j, k)] || used[g.At(i, j, k)])
            return false;
    return true;
  }

  private sealed class Grid {
    public double[][] Lines { get; }
    public int[] N { get; }
    public bool[] Cells { get; }

    public Grid(Box region, Occupancy occ, double step) {
      var sets = new SortedSet<double>[3];
      for (int a = 0; a < 3; a++) {
        sets[a] = [Round(region.Lo[a]), Round(region.Hi[a])];
        for (
          double x = region.Lo[a] + Limit;
          x < region.Hi[a] - Eps;
          x += Limit
        )
          sets[a].Add(Round(x));
      }
      foreach (Box b in occ.Boxes.Concat(occ.Prisms.Select(p => p.Bounds)))
        for (int a = 0; a < 3; a++) {
          Edge(sets[a], region, a, b.Lo[a]);
          Edge(sets[a], region, a, b.Hi[a]);
        }
      foreach (Prism p in occ.Prisms)
        for (int s = 1; s <= 2; s++) {
          int a = (p.Axis + s) % 3;
          Box b = p.Bounds;
          for (
            double x = Math.Ceiling(b.Lo[a] / step) * step;
            x < b.Hi[a] - Eps;
            x += step
          )
            Edge(sets[a], region, a, x);
        }
      Lines = [.. sets.Select(s => s.ToArray())];
      N = [.. Lines.Select(l => l.Length - 1)];
      Cells = new bool[N[0] * N[1] * N[2]];
      foreach (Box b in occ.Boxes)
        Mark(b, null);
      foreach (Prism p in occ.Prisms)
        Mark(p.Bounds, p);
    }

    public int At(int i, int j, int k) => (k * N[1] + j) * N[0] + i;

    private static double Round(double v) => Math.Round(v, 9);

    private static void Edge(SortedSet<double> s, Box region, int a, double v) {
      if (v > region.Lo[a] + Eps && v < region.Hi[a] - Eps)
        s.Add(Round(v));
    }

    private void Mark(Box b, Prism? prism) {
      var from = new int[3];
      var to = new int[3];
      for (int a = 0; a < 3; a++) {
        from[a] = Array.FindLastIndex(Lines[a], l => l <= b.Lo[a] + Eps);
        to[a] = Array.FindIndex(Lines[a], l => l >= b.Hi[a] - Eps);
        if (from[a] < 0)
          from[a] = 0;
        if (to[a] < 0)
          to[a] = N[a];
        if (
          to[a] <= from[a]
          || b.Hi[a] <= Lines[a][0] + Eps
          || b.Lo[a] >= Lines[a][^1] - Eps
        )
          return;
      }
      for (int k = from[2]; k < to[2]; k++)
        for (int j = from[1]; j < to[1]; j++)
          for (int i = from[0]; i < to[0]; i++) {
            if (
              prism != null
              && !prism.Contains(
                new Vec3(
                  (Lines[0][i] + Lines[0][i + 1]) / 2,
                  (Lines[1][j] + Lines[1][j + 1]) / 2,
                  (Lines[2][k] + Lines[2][k + 1]) / 2
                )
              )
            )
              continue;
            Cells[At(i, j, k)] = true;
          }
    }
  }
}
