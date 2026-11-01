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
/// <c>  FAIL</c> when M is not 0) and one <c>  Drag x A..B z C..D: first gap at y Y</c> per broken column. Reads
/// axis-aligned leaves only; needs <see cref="CheckInput.Parting"/>.
/// </summary>
public sealed class Draw : ICheck {
  private const double Step = 1.0 / 32;
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
    foreach (Node leaf in tree.Leaves) {
      Mat4d m = frames.Mat(leaf);
      for (int a = 0; a < 3; a++) {
        var unit = new double[3];
        unit[a] = 1;
        Vec3 d = m.MulLinear(new Vec3(unit[0], unit[1], unit[2]));
        if (Enumerable.Range(0, 3).Count(i => Math.Abs(d[i]) > 1e-9) != 1)
          throw new CheckUsageException(
            $"{leaf.Path}: draw reads axis-aligned leaves, this one is turned"
          );
      }
      boxes.Add(WorldBox.Of(m, leaf.Size));
    }
    if (boxes.Count == 0)
      return CheckResult.Of(Id, 0, false, []);
    double[] xs = Breaks(boxes, 0),
      zs = Breaks(boxes, 2);
    double top = boxes.Max(b => b.Hi.Y),
      bottom = boxes.Min(b => b.Lo.Y);
    var lines = new List<string>();
    int enumerated = 0;
    bool faults = false;
    foreach ((string half, int dir) in new[] { ("Drag", -1), ("Cope", 1) }) {
      int occupied = 0;
      var broken = new List<string>();
      for (int i = 0; i + 1 < xs.Length; i++)
        for (int k = 0; k + 1 < zs.Length; k++) {
          double cx = (xs[i] + xs[i + 1]) / 2,
            cz = (zs[k] + zs[k + 1]) / 2;
          List<Box> over =
          [
            .. boxes.Where(b =>
            b.Lo.X <= cx && cx <= b.Hi.X && b.Lo.Z <= cz && cz <= b.Hi.Z
          ),
        ];
          if (over.Count == 0)
            continue;
          (int total, int run) = Column(over, parting, dir, top, bottom);
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

  private static double[] Breaks(List<Box> boxes, int axis) =>
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
  private static (int Total, int Run) Column(
    List<Box> over,
    double parting,
    int dir,
    double top,
    double bottom
  ) {
    int first = dir < 0 ? 1 : 0;
    int total = 0,
      run = 0;
    bool alive = true;
    for (int n = first; ; n++) {
      double y = parting + dir * n * Step;
      if (dir < 0 ? y < bottom - Step : y > top + Step)
        break;
      bool hit = over.Any(b => b.Lo.Y - Eps <= y && y < b.Hi.Y - Eps);
      if (hit) {
        total++;
        if (alive)
          run++;
      } else
        alive = false;
    }
    return (total, run);
  }
}
