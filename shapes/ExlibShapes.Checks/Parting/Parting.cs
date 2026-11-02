using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// The parting and the lie of a pour. Lie: every piece's and core's <c>at</c> is a rotation and a translation, the
/// linear part orthonormal with determinant 1. Parting, for a closed pour: each placed piece spans the station's
/// parting; no sand box of the drag reaches over it by more than 0.001, none of the cope under it by more than
/// that, and no box stands in both halves; and where the pattern has roots named <c>Drag</c> and <c>Cope</c>,
/// placed by each piece's <c>at</c>, the drag's stay under the parting and the cope's over it. One line per fault;
/// reads a turned leaf on the 1/32 lattice; needs the part record's pour, and for a closed pour the settings, cast and cope.
/// </summary>
public sealed class Parting : ICheck {
  private const double Eps = 1e-6;
  private const double Tol = 0.001;

  /// <inheritdoc />
  public string Id => "parting";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    Pour pour = Pour.Read(input.Record);
    var lines = new List<string>();
    int counted = Lie(pour, lines);
    if (pour.Closed)
      counted += Split(input, lines);
    bool faults = lines.Count > 0;
    lines.Add($"parting: {counted} rules read");
    return CheckResult.Of(Id, counted, faults, lines);
  }

  private static int Lie(Pour pour, List<string> lines) {
    int counted = 0;
    foreach (
      (string name, Mat4d at) in pour
        .Pieces.Select((a, k) => ($"Piece{k + 1}", a))
        .Concat(pour.Cores.Select(c => (c.Item, c.At)))
    ) {
      counted++;
      Vec3 a = at.MulLinear(new Vec3(1, 0, 0)),
        b = at.MulLinear(new Vec3(0, 1, 0)),
        c = at.MulLinear(new Vec3(0, 0, 1));
      double det =
        a.X * (b.Y * c.Z - b.Z * c.Y)
        - a.Y * (b.X * c.Z - b.Z * c.X)
        + a.Z * (b.X * c.Y - b.Y * c.X);
      bool turn =
        Math.Abs(det - 1) < Eps
        && Math.Abs(Dot(a, a) - 1) < Eps
        && Math.Abs(Dot(b, b) - 1) < Eps
        && Math.Abs(Dot(c, c) - 1) < Eps;
      if (!turn)
        lines.Add(
          $"{name}: its placement is no rotation, determinant {Fmt.G(det)}"
        );
    }
    return counted;
  }

  private static double Dot(Vec3 a, Vec3 b) =>
    a.X * b.X + a.Y * b.Y + a.Z * b.Z;

  private static int Split(CheckInput input, List<string> lines) {
    Pouring p = Pouring.Of(input, "parting");
    double parting = p.Station.Parting!.Value;
    int counted = 0;
    for (int k = 0; k < p.Cast.Count; k++) {
      counted++;
      double lo = p.Cast[k].Min(b => b.Lo.Y),
        hi = p.Cast[k].Max(b => b.Hi.Y);
      if (!(lo < parting - Eps && hi > parting + Eps))
        lines.Add(
          $"Piece{k + 1}: y {Fmt.G(lo)} to {Fmt.G(hi)} does not span the parting at {Fmt.G(parting)}"
        );
    }
    Settings settings = input.Settings!;
    List<Box> drag = [.. Halves(input.Shape!, input, settings, "Level")];
    List<Box> cope = [.. Halves(input.Pair!, input, settings, null)];
    counted++;
    double high = drag.Count == 0 ? parting : drag.Max(b => b.Hi.Y),
      low = cope.Count == 0 ? parting : cope.Min(b => b.Lo.Y);
    if (high > parting + Tol)
      lines.Add(
        $"drag: sand reaches y {Fmt.G(high)}, over the parting at {Fmt.G(parting)}"
      );
    if (low < parting - Tol)
      lines.Add(
        $"cope: sand reaches y {Fmt.G(low)}, under the parting at {Fmt.G(parting)}"
      );
    int both = drag.Intersect(cope).Count();
    if (both > 0)
      lines.Add($"{both} sand boxes stand in both halves");
    if (input.Pattern is Shape pattern)
      counted += PatternHalves(pattern, p, parting, lines);
    return counted;
  }

  private static IEnumerable<Box> Halves(
    Shape shape,
    CheckInput input,
    Settings settings,
    string? skip
  ) =>
    Pouring
      .Leaves(shape, input, settings)
      .Where(l => l.Role == "sand" && !(skip == "Level" && l.Level))
      .SelectMany(l => l.Cells);

  private static int PatternHalves(
    Shape pattern,
    Pouring p,
    double parting,
    List<string> lines
  ) {
    var tree = new Tree(pattern);
    var frames = new Frames(pattern);
    int counted = 0;
    foreach (
      (string root, bool drag) in new[] { ("Drag", true), ("Cope", false) }
    ) {
      Node? r = tree.Roots.FirstOrDefault(n => n.Name == root);
      if (r == null)
        continue;
      foreach (Node leaf in Tree.Subtree(r).Where(n => n.IsLeaf))
        for (int k = 0; k < p.Pour.Pieces.Count; k++) {
          counted++;
          List<Box> solid = Lattice.Fine.Solid(
            p.Pour.Pieces[k] * frames.Mat(leaf),
            leaf.Size
          );
          double reach = drag ? solid.Max(b => b.Hi.Y) : solid.Min(b => b.Lo.Y);
          if (drag ? reach > parting + Eps : reach < parting - Eps)
            lines.Add(
              $"Piece{k + 1}: pattern {root} element {leaf.Name} reaches y {Fmt.G(reach)}, past the parting at {Fmt.G(parting)}"
            );
        }
    }
    return counted;
  }
}
