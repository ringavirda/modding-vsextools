using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Checks;

public sealed partial class Corefit {
  private static Box AlignedBox(
    Mat4d world,
    Vec3 size,
    string path,
    string gate
  ) =>
    Lattice.IsAligned(world)
      ? WorldBox.Of(world, size)
      : throw new CheckUsageException(
        $"{path}: {gate} needs a core box on the axes"
      );

  private static int CoreboxRules(
    CheckInput input,
    JObject slot,
    List<string> lines
  ) {
    Shape box = input.Shape!;
    Shape core = input.Cores is { Count: > 0 } c
      ? c[0]
      : throw new CheckUsageException(
        "corefit reads the box's core in Cores, none given"
      );
    string[] trays =
    [
      .. (slot["trays"] as JArray ?? []).Select(t => (string?)t ?? ""),
    ];
    if (trays.Length == 0)
      throw new CheckUsageException("corefit: corebox.trays is empty");
    var tree = new Tree(box);
    var frames = new Frames(box);
    var pockets =
      new List<(string Tray, Box Pocket, Box Outer, List<Node> Leaves)>();
    foreach (string tray in trays) {
      Node group =
        tree.Find(tray)
        ?? throw new CheckUsageException(
          $"corefit: the box has no tray '{tray}'"
        );
      List<Node> leaves = [.. Tree.Subtree(group).Where(n => n.IsLeaf)];
      List<Box> boxes =
      [
        .. leaves.Select(n =>
          AlignedBox(frames.Mat(n), n.Size, n.Path, "corefit")
        ),
      ];
      if (boxes.Count == 0)
        throw new CheckUsageException($"corefit: tray '{tray}' draws nothing");
      Box outer = Bounds(boxes);
      Box? pocket = Pocket(boxes, out string? fault);
      if (pocket is not Box pk) {
        lines.Add($"pocket: {tray} {fault}");
        continue;
      }
      pockets.Add((tray, pk, outer, leaves));
    }
    int counted = trays.Length;
    if (pockets.Count != trays.Length)
      return counted;
    counted += trays.Length;
    foreach (var t in pockets) {
      Vec3 size = t.Outer.Hi - t.Outer.Lo,
        inner = t.Pocket.Hi - t.Pocket.Lo;
      if (
        Math.Abs(size.X - inner.X - 2 * Wall) > Tol
        || Math.Abs(size.Z - inner.Z - 2 * Wall) > Tol
      )
        lines.Add(
          $"extent: {t.Tray} spans {Fmt.G(size.X)} x {Fmt.G(size.Z)} across, its pocket {Fmt.G(inner.X)} x {Fmt.G(inner.Z)} wants walls of {Fmt.G(Wall)}"
        );
    }
    Vec3 first = pockets[0].Pocket.Hi - pockets[0].Pocket.Lo;
    foreach (var t in pockets.Skip(1)) {
      Vec3 s = t.Pocket.Hi - t.Pocket.Lo;
      if (Math.Abs(s.X - first.X) > Tol || Math.Abs(s.Z - first.Z) > Tol)
        lines.Add(
          $"extent: {t.Tray} pocket is {Fmt.G(s.X)} x {Fmt.G(s.Z)} across, {pockets[0].Tray}'s {Fmt.G(first.X)} x {Fmt.G(first.Z)}"
        );
    }
    counted++;
    double depth = pockets.Sum(t => t.Pocket.Hi.Y - t.Pocket.Lo.Y);
    double[] made = [first.X, depth, first.Z];
    Box coreBounds = Bounds(
      new Tree(core)
        .Leaves.Select(n =>
          AlignedBox(new Frames(core).Mat(n), n.Size, n.Path, "corefit")
        )
        .ToList()
    );
    Vec3 cs = coreBounds.Hi - coreBounds.Lo;
    double[] wanted = [cs.X, cs.Y, cs.Z];
    if (
      !Enumerable
        .Zip(made.Order(), wanted.Order())
        .All(z => Math.Abs(z.First - z.Second) <= Tol)
    )
      lines.Add(
        $"pocket: the trays make {Fmt.G(first.X)} x {Fmt.G(depth)} x {Fmt.G(first.Z)}, the core is {Fmt.G(cs.X)} x {Fmt.G(cs.Y)} x {Fmt.G(cs.Z)}"
      );
    bool alongZ = first.Z >= first.X;
    foreach (var t in pockets)
      foreach (Node n in t.Leaves)
        foreach (string face in new[] { "up", "down" }) {
          if (!n.Faces.Any(f => f.Key == face))
            continue;
          counted++;
          Face spec = n.Faces.First(f => f.Key == face).Value;
          if (spec.Uv is not { Length: 4 } uv) {
            lines.Add($"grain: {n.Path} {face} has no uv");
            continue;
          }
          double along = alongZ ? n.Size.Z : n.Size.X,
            across = alongZ ? n.Size.X : n.Size.Z;
          double rotation = ((spec.Rotation ?? 0) % 360 + 360) % 360;
          bool turned = alongZ ? rotation is 90 or 270 : rotation is 0 or 180;
          if (
            !turned
            || Math.Abs(Math.Abs(uv[2] - uv[0]) - along) > Tol
            || Math.Abs(Math.Abs(uv[3] - uv[1]) - across) > Tol
          )
            lines.Add(
              $"grain: {n.Path} {face} rotation {Fmt.G(rotation)} uv {Fmt.G(Math.Abs(uv[2] - uv[0]))} x {Fmt.G(Math.Abs(uv[3] - uv[1]))}, the boards run along {(alongZ ? "z" : "x")} ({Fmt.G(along)} x {Fmt.G(across)})"
            );
        }
    return counted;
  }

  private static Box Bounds(IReadOnlyList<Box> boxes) =>
    new(
      new Vec3(
        CoreParts.Lo(boxes, 0),
        CoreParts.Lo(boxes, 1),
        CoreParts.Lo(boxes, 2)
      ),
      new Vec3(
        CoreParts.Hi(boxes, 0),
        CoreParts.Hi(boxes, 1),
        CoreParts.Hi(boxes, 2)
      )
    );

  /// <summary>The empty space a tray closes in: every cell with a wall beyond it on both x sides and both z sides and
  /// floor below, open above. Null with <paramref name="fault"/> when there is none or it is not one box.</summary>
  private static Box? Pocket(List<Box> boxes, out string? fault) {
    var grid = new BoxGrid(boxes);
    bool[] solid = grid.Mark(boxes);
    int nx = grid.X.Length - 1,
      ny = grid.Y.Length - 1,
      nz = grid.Z.Length - 1;
    bool Any(int i0, int i1, int j0, int j1, int k0, int k1) {
      for (int i = i0; i <= i1; i++)
        for (int j = j0; j <= j1; j++)
          for (int k = k0; k <= k1; k++)
            if (solid[grid.Index(i, j, k)])
              return true;
      return false;
    }
    var open = new bool[grid.Count];
    double volume = 0;
    double x0 = double.MaxValue,
      y0 = double.MaxValue,
      z0 = double.MaxValue;
    double x1 = double.MinValue,
      y1 = double.MinValue,
      z1 = double.MinValue;
    for (int i = 0; i < nx; i++)
      for (int j = 0; j < ny; j++)
        for (int k = 0; k < nz; k++) {
          if (solid[grid.Index(i, j, k)])
            continue;
          if (
            Any(0, i, j, j, k, k)
            && Any(i, nx - 1, j, j, k, k)
            && Any(i, i, j, j, 0, k)
            && Any(i, i, j, j, k, nz - 1)
            && Any(i, i, 0, j, k, k)
          ) {
            volume += grid.Volume(i, j, k);
            x0 = Math.Min(x0, grid.X[i]);
            x1 = Math.Max(x1, grid.X[i + 1]);
            y0 = Math.Min(y0, grid.Y[j]);
            y1 = Math.Max(y1, grid.Y[j + 1]);
            z0 = Math.Min(z0, grid.Z[k]);
            z1 = Math.Max(z1, grid.Z[k + 1]);
          }
        }
    fault = null;
    if (volume <= 0) {
      fault = "closes in no pocket";
      return null;
    }
    double hull = (x1 - x0) * (y1 - y0) * (z1 - z0);
    if (Math.Abs(hull - volume) > 1e-6) {
      fault = "pocket is not a box";
      return null;
    }
    return new Box(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1));
  }
}
