using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Whether the metal reaches the whole cast from where it enters. The cavity is the lattice of 1/16 voxel, set 1/64
/// off the planes, over the box of the metal and the seed grown one step, less sand, runner box and cores, and bounded by the station's sand box. The flood
/// starts at the cavity samples in the station's first inlet cut, else its drop (a closed pour's cope), and spreads
/// through the six neighbours. It passes when every cast sample of every piece is reached; a seed with no cavity
/// fails. One line per piece with unreached samples, in vx3 with the worst half-voxel plan cell, and a summary line.
/// Reads axis-aligned leaves only; needs the part record's pour, the settings and the cast.
/// </summary>
public sealed class Flood : ICheck {
  private const double Step = 1.0 / 16;
  private const double Offset = 1.0 / 64;

  /// <inheritdoc />
  public string Id => "flood";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    Pouring p = Pouring.Of(input, Id);
    List<Box> reach = [.. p.Metal, p.Seed];
    var lo = new double[3];
    var n = new int[3];
    for (int a = 0; a < 3; a++) {
      double from = reach.Min(b => b.Lo[a]) - Step,
        to = reach.Max(b => b.Hi[a]) + Step;
      lo[a] = Math.Floor(from / Step) * Step + Offset;
      n[a] = (int)Math.Ceiling((to - lo[a]) / Step);
    }
    long total = (long)n[0] * n[1] * n[2];
    if (total > int.MaxValue)
      throw new CheckUsageException(
        $"flood: the metal's box is {total} samples, over the lattice limit"
      );
    var solid = new bool[total];
    for (int i = 0; i < n[0]; i++)
      for (int j = 0; j < n[1]; j++)
        for (int k = 0; k < n[2]; k++)
          if (
            !Within(p.Plan, 0, lo[0] + i * Step)
            || !Within(p.Plan, 1, lo[1] + j * Step)
            || !Within(p.Plan, 2, lo[2] + k * Step)
          )
            solid[(i * n[1] + j) * n[2] + k] = true;
    foreach (Box b in p.Sand.Concat(p.Runner).Concat(p.Cores))
      Fill(b, lo, n, (idx) => solid[idx] = true);
    var seeded = new List<int>();
    Fill(
      p.Seed,
      lo,
      n,
      idx => {
        if (!solid[idx])
          seeded.Add(idx);
      }
    );
    var reached = new bool[total];
    var queue = new Queue<int>();
    foreach (int s in seeded) {
      reached[s] = true;
      queue.Enqueue(s);
    }
    int sy = n[2],
      sx = n[1] * n[2];
    while (queue.Count > 0) {
      int c = queue.Dequeue();
      int i = c / sx,
        j = c / sy % n[1],
        k = c % sy;
      if (i > 0)
        Visit(c - sx);
      if (i < n[0] - 1)
        Visit(c + sx);
      if (j > 0)
        Visit(c - sy);
      if (j < n[1] - 1)
        Visit(c + sy);
      if (k > 0)
        Visit(c - 1);
      if (k < n[2] - 1)
        Visit(c + 1);
    }
    void Visit(int idx) {
      if (solid[idx] || reached[idx])
        return;
      reached[idx] = true;
      queue.Enqueue(idx);
    }

    var lines = new List<string>();
    if (seeded.Count == 0)
      lines.Add(
        "no cavity at the seed: the sand fills the metal's entry  FAIL"
      );
    var counted = new bool[total];
    double cube = Step * Step * Step;
    long samples = 0;
    bool faults = seeded.Count == 0;
    for (int piece = 0; piece < p.Cast.Count; piece++) {
      var cells = new Dictionary<(double, double), long>();
      long count = 0,
        missed = 0;
      Fill(
        p.Cast[piece],
        lo,
        n,
        idx => {
          if (solid[idx] || counted[idx])
            return;
          counted[idx] = true;
          count++;
          if (reached[idx])
            return;
          missed++;
          int i = idx / sx,
            k = idx % sy;
          var key = (
            Math.Floor((lo[0] + i * Step) * 2) / 2,
            Math.Floor((lo[2] + k * Step) * 2) / 2
          );
          cells[key] = cells.GetValueOrDefault(key) + 1;
        }
      );
      samples += count;
      if (missed > 0) {
        faults = true;
        ((double x, double z), long m) = cells.MaxBy(c => c.Value);
        lines.Add(
          $"Piece{piece + 1}: {Fmt.G(missed * cube)} of {Fmt.G(count * cube)} vx3 of cast not reached, worst 0.5 plan cell x {Fmt.G(x)} z {Fmt.G(z)}: {Fmt.G(m * cube)} vx3  FAIL"
        );
      }
    }
    lines.Add(
      $"flood: {samples} cast samples, seeded {seeded.Count}, reached {Fmt.G(reached.LongCount(r => r) * cube)} vx3 of cavity"
    );
    return CheckResult.Of(
      Id,
      (int)Math.Min(samples, int.MaxValue),
      faults,
      lines
    );
  }

  private static bool Within(Box plan, int axis, double at) =>
    at >= plan.Lo[axis] && at <= plan.Hi[axis];

  private static void Fill(
    IEnumerable<Box> boxes,
    double[] lo,
    int[] n,
    Action<int> visit
  ) {
    foreach (Box b in boxes)
      Fill(b, lo, n, visit);
  }

  private static void Fill(Box b, double[] lo, int[] n, Action<int> visit) {
    var from = new int[3];
    var to = new int[3];
    for (int a = 0; a < 3; a++) {
      from[a] = Math.Max(0, (int)Math.Ceiling((b.Lo[a] - lo[a]) / Step - 1e-9));
      to[a] = Math.Min(
        n[a],
        (int)Math.Ceiling((b.Hi[a] - lo[a]) / Step - 1e-9)
      );
    }
    for (int i = from[0]; i < to[0]; i++)
      for (int j = from[1]; j < to[1]; j++)
        for (int k = from[2]; k < to[2]; k++)
          visit((i * n[1] + j) * n[2] + k);
  }
}
