using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Whether a pattern draws from its flask: along y, every column the pattern occupies runs unbroken from the
/// parting, the drag's below it and the cope's above it. The plan is cut at every leaf edge into cells, each a
/// column; y is sampled every 1/32 voxel, the drag's from the sample under the parting down and the cope's from the
/// parting up, a leaf holding a sample on [lo, hi). No gap is bridged: a gap the sand step leaves counts as a
/// failure. One summary line per half (<c>Drag: N columns occupied, M not unbroken from the parting</c>, ending
/// <c>  FAIL</c> when M is not 0) and one <c>  Drag x A..B z C..D: first gap at y Y</c> per broken column. A turned leaf
/// is read as the 1/32 lattice cells whose centre lies inside it; needs <see cref="CheckInput.Parting"/>.
/// </summary>
public sealed class Draw : ICheck {
  internal const double Step = 1.0 / 32;
  private const double Eps = 1e-9;

  /// <inheritdoc />
  public string Id => "draw";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    double parting =
      input.Parting
      ?? throw new CheckUsageException(
        "draw reads the parting height, none given"
      );
    var tree = new Tree(input.Shape);
    var frames = new Frames(input.Shape);
    var boxes = new List<Box>();
    foreach (Node leaf in tree.Leaves)
      boxes.AddRange(Lattice.Fine.Solid(frames.Mat(leaf), leaf.Size));
    if (boxes.Count == 0)
      return CheckResult.Of(Id, 0, false, []);
    double[] xs = Breaks(boxes, 0),
      zs = Breaks(boxes, 2);
    var columns = new List<Box>[xs.Length - 1, zs.Length - 1];
    foreach (Box b in boxes) {
      (int i0, int i1) = Span(xs, b.Lo.X, b.Hi.X);
      (int k0, int k1) = Span(zs, b.Lo.Z, b.Hi.Z);
      for (int i = i0; i < i1; i++)
        for (int k = k0; k < k1; k++)
          (columns[i, k] ??= []).Add(b);
    }
    var lines = new List<string>();
    int enumerated = 0;
    bool faults = false;
    foreach ((string half, int dir) in new[] { ("Drag", -1), ("Cope", 1) }) {
      int occupied = 0;
      var broken = new List<string>();
      for (int i = 0; i + 1 < xs.Length; i++)
        for (int k = 0; k + 1 < zs.Length; k++) {
          if (columns[i, k] is not { } over)
            continue;
          (int total, int run) = Column(over, parting, dir);
          if (total == 0)
            continue;
          occupied++;
          if (total != run)
            broken.Add(
              $"  {half} x {Fmt.G(xs[i])}..{Fmt.G(xs[i + 1])} z {Fmt.G(zs[k])}..{Fmt.G(zs[k + 1])}: first gap at y {Fmt.G(parting + dir * (run + (dir < 0 ? 1 : 0)) * Step)}"
            );
        }
      enumerated += occupied;
      faults |= broken.Count > 0;
      lines.Add(
        $"{half}: {occupied} columns occupied, {broken.Count} not unbroken from the parting"
          + (broken.Count > 0 ? "  FAIL" : "")
      );
      lines.AddRange(broken);
    }
    return CheckResult.Of(Id, enumerated, faults, lines);
  }

  internal static (int, int) Span(double[] breaks, double lo, double hi) =>
    (
      Array.BinarySearch(breaks, Math.Round(lo, 9)),
      Array.BinarySearch(breaks, Math.Round(hi, 9))
    );

  internal static double[] Breaks(List<Box> boxes, int axis) =>
    [
      .. boxes
        .SelectMany(b => new[] { b.Lo[axis], b.Hi[axis] })
        .Select(v => Math.Round(v, 9))
        .Distinct()
        .Order(),
    ];

  /// <summary>The occupied samples of one column on one side of the parting, and how many of them run unbroken
  /// from it. The drag's samples are at parting - n/32 for n of 1 and on, the cope's at parting + n/32 for n of 0
  /// and on.</summary>
  internal static (int Total, int Run) Column(
    List<Box> over,
    double parting,
    int dir
  ) {
    int first = dir < 0 ? 1 : 0;
    var spans = new List<(int Lo, int Hi)>();
    foreach (Box b in over) {
      bool Hit(int n) {
        double y = parting + dir * n * Step;
        return b.Lo.Y - Eps <= y && y < b.Hi.Y - Eps;
      }

      double n1 = (b.Lo.Y - parting) / (dir * Step),
        n2 = (b.Hi.Y - parting) / (dir * Step);
      int lo = (int)Math.Floor(Math.Min(n1, n2)) - 1,
        hi = (int)Math.Ceiling(Math.Max(n1, n2)) + 1;
      while (lo <= hi && !Hit(lo))
        lo++;
      while (hi >= lo && !Hit(hi))
        hi--;
      lo = Math.Max(lo, first);
      if (lo <= hi)
        spans.Add((lo, hi));
    }
    spans.Sort();
    int total = 0,
      run = 0,
      end = first - 1;
    bool alive = true;
    foreach ((int lo, int hi) in spans) {
      if (hi <= end)
        continue;
      int from = Math.Max(lo, end + 1);
      total += hi - from + 1;
      if (alive && lo <= end + 1)
        run += hi - from + 1;
      else
        alive = false;
      end = hi;
    }
    return (total, run);
  }
}
