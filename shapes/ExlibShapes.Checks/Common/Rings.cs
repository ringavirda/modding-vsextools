using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>A plate ring: a zero-size group whose plates turn by multiples of 45 degrees about one axis.</summary>
/// <param name="Group">The group.</param>
/// <param name="Axis">The turn axis (0, 1, 2) in the group's frame.</param>
/// <param name="Plates">The sized plates.</param>
/// <param name="Pivot">The pivot in the group's frame.</param>
internal sealed record PlateRing(
  Node Group,
  int Axis,
  List<Node> Plates,
  Vec3 Pivot
);

/// <summary>Octagon and plate-ring helpers the round-run detector reads.</summary>
internal static class Rings {
  /// <summary>Plates a ring may carry off the 45 degree steps while four or more stand on them.</summary>
  public const int Stray = 2;

  /// <summary>(axis, degrees) when <paramref name="el"/> is turned about exactly one axis, (null, 0)
  /// when unturned; null when turned about several.</summary>
  public static (int? Axis, double Deg)? Turned(Node el) {
    int[] axes =
    [
      .. Enumerable.Range(0, 3).Where(i => Math.Abs(el.Rot[i]) > 1e-9),
    ];
    if (axes.Length == 0)
      return (null, 0.0);
    return axes.Length == 1 ? (axes[0], el.Rot[axes[0]]) : null;
  }

  /// <summary>Whether <paramref name="k"/>'s pivot lies on an outer corner of <paramref name="head"/>'s
  /// cross section about <paramref name="ax"/>.</summary>
  public static bool OnCorner(Node head, Node k, int ax) {
    (int a, int b) = Others(ax);
    return new[] { 0.0, head.Size[a] }.Any(u =>
      new[] { 0.0, head.Size[b] }.Any(v =>
        Math.Abs(k.Origin[a] - u) < 1e-3 && Math.Abs(k.Origin[b] - v) < 1e-3
      )
    );
  }

  public static (int A, int B) Others(int ax) {
    int[] o = [.. Enumerable.Range(0, 3).Where(i => i != ax)];
    return (o[0], o[1]);
  }

  /// <summary>Every plate ring of the tree; up to <paramref name="stray"/> more plates may stand off the
  /// 45 degree steps, each on its own turn, while four or more stand on them.</summary>
  public static IEnumerable<PlateRing> PlateRings(Tree tree, int stray = 0) {
    foreach (Node g in tree.Nodes) {
      if (g.AnySizeOver(1e-6))
        continue;
      List<Node> kids = [.. g.Children.Where(k => k.Solid)];
      if (kids.Count < 4 || kids.Count != g.Children.Count)
        continue;
      var axes = new HashSet<int>(
        kids.SelectMany(k =>
          Enumerable.Range(0, 3).Where(i => Math.Abs(k.Rot[i]) > 1e-9)
        )
      );
      if (axes.Count != 1)
        continue;
      int ax = axes.Single();
      var turns = new HashSet<double>(
        kids.Select(k => Math.Round(V.Mod(k.Rot[ax], 360), 4))
      );
      var steps = new HashSet<double>(
        turns.Where(t => Enumerable.Range(0, 8).Any(j => t == 45.0 * j))
      );
      if (
        turns.Count != kids.Count
        || turns.Count(t => !steps.Contains(t)) > stray
        || steps.Count < 4
        || !steps.Select(t => t % 90).ToHashSet().SetEquals([0.0, 45.0])
      )
        continue;
      List<Vec3> pivots =
      [
        .. kids.Where(k => Math.Abs(k.Rot[ax]) > 1e-9).Select(k => k.Origin),
      ];
      (int a, int b) = Others(ax);
      if (
        pivots.Any(p =>
          Math.Abs(p[a] - pivots[0][a]) > 1e-3
          || Math.Abs(p[b] - pivots[0][b]) > 1e-3
        )
      )
        continue;
      if (
        kids.All(k =>
          Math.Abs((k.Frm[a] + k.To[a]) / 2 - pivots[0][a]) <= 1e-3
          && Math.Abs((k.Frm[b] + k.To[b]) / 2 - pivots[0][b]) <= 1e-3
        )
      )
        continue;
      yield return new PlateRing(g, ax, kids, pivots[0]);
    }
  }
}
