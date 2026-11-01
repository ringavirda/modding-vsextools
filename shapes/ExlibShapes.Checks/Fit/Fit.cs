using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Whether the sand left between the metal and the flask holds: each piece keeps the station's margin from its sand
/// box walls and floor (a closed pour's roof too), the pieces keep it apart, leaf from leaf, and each feed keeps it
/// from every piece it does not enter. A feed enters a piece it overlaps or touches. With more than one piece and a
/// station footprint, each piece's plan grown by the footprint stays inside its Bedded sand's plan. One line per
/// fault; needs the part record's pour, the settings and the cast.
/// </summary>
public sealed class Fit : ICheck {
  private const double Eps = 1e-9;

  /// <inheritdoc />
  public string Id => "fit";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    Pouring p = Pouring.Of(input, Id);
    double margin = p.Station.Margin;
    var lines = new List<string>();
    int counted = 0;
    for (int k = 0; k < p.Cast.Count; k++) {
      counted++;
      Box b = Bounds(p.Cast[k]);
      foreach (
        (string wall, double gap) in new (string, double)[]
        {
          ("west wall", b.Lo.X - p.Plan.Lo.X),
          ("east wall", p.Plan.Hi.X - b.Hi.X),
          ("north wall", b.Lo.Z - p.Plan.Lo.Z),
          ("south wall", p.Plan.Hi.Z - b.Hi.Z),
          ("floor", b.Lo.Y - p.Station.Floor),
        }.Concat(
          p.Pour.Closed
            ? [("roof", p.Plan.Hi.Y - b.Hi.Y)]
            : Array.Empty<(string, double)>()
        )
      )
        if (gap < margin - Eps)
          lines.Add(
            $"Piece{k + 1}: {Fmt.G(gap)} from the {wall}, margin {Fmt.G(margin)}"
          );
    }
    for (int a = 0; a < p.Cast.Count; a++)
      for (int b = a + 1; b < p.Cast.Count; b++)
        foreach (Box x in p.Cast[a])
          foreach (Box y in p.Cast[b]) {
            counted++;
            if (Gap(x, y) < margin - Eps)
              lines.Add(
                $"Piece{a + 1} and Piece{b + 1}: {Fmt.G(Gap(x, y))} apart, margin {Fmt.G(margin)}"
              );
          }
    foreach ((Feed feed, List<Box> boxes) in p.Feeds)
      for (int k = 0; k < p.Cast.Count; k++) {
        if (boxes.Any(f => p.Cast[k].Any(c => Gap(f, c) <= Eps)))
          continue;
        counted++;
        double gap = boxes.Min(f => p.Cast[k].Min(c => Gap(f, c)));
        if (gap < margin - Eps)
          lines.Add(
            $"{feed.Name} and Piece{k + 1}: {Fmt.G(gap)} apart, margin {Fmt.G(margin)}"
          );
      }
    if (p.Station.Footprint is double foot && p.Pour.Pieces.Count > 1)
      counted += Footprint(input, p, foot, lines);
    bool faults = lines.Count > 0;
    lines.Add($"fit: {counted} clearances read");
    return CheckResult.Of(Id, counted, faults, lines);
  }

  private static int Footprint(
    CheckInput input,
    Pouring p,
    double foot,
    List<string> lines
  ) {
    List<FillingLeaf> leaves = Pouring.Leaves(
      input.Shape!,
      input,
      input.Settings!,
      "fit"
    );
    int counted = 0;
    for (int k = 0; k < p.Cast.Count; k++) {
      counted++;
      List<Box> bedded =
      [
        .. leaves
          .Where(l =>
            l.Piece == k + 1
            && l.Role == "sand"
            && !l.Level
            && InGroup(l.Node, "Bedded")
          )
          .Select(l => l.Box),
      ];
      if (bedded.Count == 0) {
        lines.Add($"Piece{k + 1}: no Bedded sand to hold its footprint");
        continue;
      }
      Box plan = Bounds(p.Cast[k]),
        sand = Bounds(bedded);
      double short_ = new[]
      {
        plan.Lo.X - foot - sand.Lo.X,
        sand.Hi.X - plan.Hi.X - foot,
        plan.Lo.Z - foot - sand.Lo.Z,
        sand.Hi.Z - plan.Hi.Z - foot,
      }.Min();
      if (short_ < -Eps)
        lines.Add(
          $"Piece{k + 1}: its plan grown by {Fmt.G(foot)} leaves the Bedded sand by {Fmt.G(-short_)}"
        );
    }
    return counted;
  }

  private static bool InGroup(Node leaf, string name) {
    for (Node? n = leaf; n != null; n = n.Parent)
      if (n.Name == name)
        return true;
    return false;
  }

  private static Box Bounds(IEnumerable<Box> boxes) =>
    new(
      new Vec3(
        boxes.Min(b => b.Lo.X),
        boxes.Min(b => b.Lo.Y),
        boxes.Min(b => b.Lo.Z)
      ),
      new Vec3(
        boxes.Max(b => b.Hi.X),
        boxes.Max(b => b.Hi.Y),
        boxes.Max(b => b.Hi.Z)
      )
    );

  /// <summary>The largest separation of two boxes on one axis; at most 0 when they overlap or touch.</summary>
  private static double Gap(Box a, Box b) =>
    Enumerable
      .Range(0, 3)
      .Max(i => Math.Max(a.Lo[i] - b.Hi[i], b.Lo[i] - a.Hi[i]));
}
