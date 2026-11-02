using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Whether the metal is fed and the sand holds round it. A <c>head</c> feed starts at the top of the piece it enters
/// (<c>into</c>) and rises over it by the station's head height, else 1. A closed pour (its cope read beside its
/// drag through <c>Pair</c>) keeps its cast's top 1 under the box floor's top, the sand's top; its flood from the
/// cast leaves the flask only by the <c>sprue</c> and <c>riser</c> feeds' mouths at that top and by no wall or the
/// floor (the flood is a 1/8 voxel lattice set 1/64 off the planes, so a gap narrower than a step is no leak); and
/// the groups of the cast item (the elements the cast fans out into) lie the station's margin apart, leaf bounds
/// against leaf bounds. One line per fault; reads a turned leaf on the 1/32 lattice; needs the part record's pour, the settings
/// and the cast.
/// </summary>
public sealed class Feeding : ICheck {
  private const double Eps = 1e-9;
  private const double DefaultHead = 1.0;

  /// <inheritdoc />
  public string Id => "feeding";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    Pouring p = Pouring.Of(input, Id);
    var lines = new List<string>();
    int counted = Head(p, lines);
    if (p.Pour.Closed) {
      counted += ClosedHead(p, lines);
      counted += Seal.Run(p, input, lines);
      counted += Layout(p, input, lines);
    }
    bool faults = lines.Count > 0;
    lines.Add($"feeding: {counted} rules read");
    return CheckResult.Of(Id, counted, faults, lines);
  }

  private static int Head(Pouring p, List<string> lines) {
    int counted = 0;
    foreach (
      (Feed feed, List<Box> boxes) in p.Feeds.Where(f => f.Feed.Kind == "head")
    ) {
      counted++;
      if (
        feed.Into is not int k
        || k < 1
        || k > p.Cast.Count
        || boxes.Count == 0
      ) {
        lines.Add($"{feed.Name}: a head enters a piece and holds a box");
        continue;
      }
      double pieceTop = p.Cast[k - 1].Max(b => b.Hi.Y),
        start = boxes.Min(b => b.Lo.Y),
        rise = boxes.Max(b => b.Hi.Y) - pieceTop,
        need = p.Station.HeadHeight ?? DefaultHead;
      if (start > pieceTop + Eps)
        lines.Add(
          $"{feed.Name}: starts {Fmt.G(start - pieceTop)} over Piece{k}'s top"
        );
      if (rise < need - Eps)
        lines.Add(
          $"{feed.Name}: rises {Fmt.G(rise)} over Piece{k}, {Fmt.G(need)} wanted"
        );
    }
    return counted;
  }

  private static int ClosedHead(Pouring p, List<string> lines) {
    double castTop = p.Cast.SelectMany(c => c).Max(b => b.Hi.Y),
      over = p.Top - castTop;
    if (over < DefaultHead - Eps)
      lines.Add(
        $"head: the cast's top is {Fmt.G(over)} under the box floor's top at y {Fmt.G(p.Top)}, {Fmt.G(DefaultHead)} wanted"
      );
    return 1;
  }

  private static int Layout(Pouring p, CheckInput input, List<string> lines) {
    Shape cast =
      input.Cast
      ?? throw new CheckUsageException("feeding reads the cast, none given");
    var tree = new Tree(cast);
    var frames = new Frames(cast);
    List<Node> groups = tree.Roots;
    while (groups.Count == 1 && groups[0].Children.Count > 0)
      groups = groups[0].Children;
    if (groups.Count < 2)
      return 0;
    List<List<Box>> bounds =
    [
      .. groups.Select(g =>
        Tree.Subtree(g)
          .Where(n => n.IsLeaf)
          .SelectMany(n => Lattice.Fine.Solid(frames.Mat(n), n.Size))
          .ToList()
      ),
    ];
    int counted = 0;
    for (int a = 0; a < groups.Count; a++)
      for (int b = a + 1; b < groups.Count; b++) {
        if (bounds[a].Count == 0 || bounds[b].Count == 0)
          continue;
        counted++;
        double gap = bounds[a].Min(x => bounds[b].Min(y => Gap(x, y)));
        if (gap < p.Station.Margin - Eps)
          lines.Add(
            $"layout: {groups[a].Name} and {groups[b].Name} are {Fmt.G(gap)} apart, {Fmt.G(p.Station.Margin)} wanted"
          );
      }
    return counted;
  }

  private static double Gap(Box a, Box b) =>
    Enumerable
      .Range(0, 3)
      .Max(i => Math.Max(a.Lo[i] - b.Hi[i], b.Lo[i] - a.Hi[i]));
}
