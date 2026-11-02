using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>The flood of a closed pour from its cast through the open lattice, which may leave the flask only by the
/// sprue and riser mouths at the sand's top.</summary>
internal static class Seal {
  private const double Step = 1.0 / 8;
  private const double Offset = 1.0 / 64;

  /// <summary>Adds a line for each fault; returns the rules read.</summary>
  /// <exception cref="CheckUsageException">The cope is missing or the lattice is over its limit.</exception>
  public static int Run(Pouring p, CheckInput input, List<string> lines) {
    Shape pair =
      input.Pair
      ?? throw new CheckUsageException("feeding reads the cope, none given");
    double parting = p.Station.Parting!.Value;
    List<Box> copeSand =
    [
      .. Pouring
        .Leaves(pair, input, input.Settings!, "feeding")
        .Where(l => l.Role == "sand")
        .Select(l => l.Box),
    ];
    if (copeSand.Count == 0)
      throw new CheckUsageException("feeding: the cope holds no sand");
    double cx0 = copeSand.Min(b => b.Lo.X),
      cx1 = copeSand.Max(b => b.Hi.X),
      cz0 = copeSand.Min(b => b.Lo.Z),
      cz1 = copeSand.Max(b => b.Hi.Z);
    var lo = new double[3];
    var n = new int[3];
    for (int a = 0; a < 3; a++) {
      lo[a] = p.Plan.Lo[a] + Offset;
      n[a] = (int)Math.Ceiling((p.Plan.Hi[a] - lo[a]) / Step);
    }
    long total = (long)n[0] * n[1] * n[2];
    if (total > int.MaxValue)
      throw new CheckUsageException(
        $"feeding: the flask is {total} samples, over the lattice limit"
      );
    var solid = new bool[total];
    int sz = n[2],
      sx = n[1] * n[2];
    for (int i = 0; i < n[0]; i++)
      for (int j = 0; j < n[1]; j++)
        for (int k = 0; k < n[2]; k++) {
          double x = lo[0] + i * Step,
            y = lo[1] + j * Step,
            z = lo[2] + k * Step;
          if (y > parting && (x < cx0 || x > cx1 || z < cz0 || z > cz1))
            solid[i * sx + j * sz + k] = true;
        }
    foreach (Box b in p.Sand.Concat(p.Runner).Concat(p.Cores))
      Fill(b, lo, n, idx => solid[idx] = true);
    var reached = new bool[total];
    var queue = new Queue<int>();
    foreach (Box b in p.Cast.SelectMany(c => c))
      Fill(
        b,
        lo,
        n,
        idx => {
          if (!solid[idx] && !reached[idx]) {
            reached[idx] = true;
            queue.Enqueue(idx);
          }
        }
      );
    while (queue.Count > 0) {
      int c = queue.Dequeue();
      int i = c / sx,
        j = c / sz % n[1],
        k = c % sz;
      if (i > 0)
        Visit(c - sx);
      if (i < n[0] - 1)
        Visit(c + sx);
      if (j > 0)
        Visit(c - sz);
      if (j < n[1] - 1)
        Visit(c + sz);
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

    List<(string Name, List<Box> Boxes)> mouths =
    [
      .. p
        .Feeds.Where(f => f.Feed.Kind is "sprue" or "riser")
        .Select(f => (f.Feed.Name, f.Boxes)),
    ];
    if (mouths.Count == 0)
      lines.Add("seal: the pour has no sprue or riser feed to leave by");
    int top = n[1] - 1,
      leak = 0,
      edge = 0;
    var open = new bool[mouths.Count];
    for (int i = 0; i < n[0]; i++)
      for (int k = 0; k < n[2]; k++) {
        if (!reached[i * sx + top * sz + k])
          continue;
        double x = lo[0] + i * Step,
          z = lo[2] + k * Step;
        int m = mouths.FindIndex(mo =>
          mo.Boxes.Any(b =>
            x >= b.Lo.X && x <= b.Hi.X && z >= b.Lo.Z && z <= b.Hi.Z
          )
        );
        if (m >= 0)
          open[m] = true;
        else
          leak++;
      }
    for (int i = 0; i < n[0]; i++)
      for (int j = 0; j < n[1]; j++)
        for (int k = 0; k < n[2]; k++)
          if (
            (i == 0 || i == n[0] - 1 || k == 0 || k == n[2] - 1 || j == 0)
            && reached[i * sx + j * sz + k]
          )
            edge++;
    for (int m = 0; m < mouths.Count; m++)
      if (!open[m])
        lines.Add($"seal: the flood does not reach {mouths[m].Name}'s mouth");
    if (leak > 0)
      lines.Add(
        $"seal: {leak} samples at the sand's top outside the mouths are reached"
      );
    if (edge > 0)
      lines.Add(
        $"seal: {edge} samples at the flask's walls or floor are reached"
      );
    return mouths.Count + 1;
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
