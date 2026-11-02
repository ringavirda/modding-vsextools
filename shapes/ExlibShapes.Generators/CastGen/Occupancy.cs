using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Generators;

/// <summary>A convex polygon extruded along a world axis: the solid a box turned about that axis fills.</summary>
/// <param name="Axis">The extrusion axis, 0 x, 1 y or 2 z.</param>
/// <param name="Lo">Where the extrusion starts on <paramref name="Axis"/>, in voxels.</param>
/// <param name="Hi">Where it ends.</param>
/// <param name="Polygon">The convex outline in the other two axes, <c>(axis + 1) % 3</c> as U and
/// <c>(axis + 2) % 3</c> as V, in order round the outline.</param>
public sealed record Prism(
  int Axis,
  double Lo,
  double Hi,
  IReadOnlyList<(double U, double V)> Polygon
) {
  private const double Eps = 1e-9;

  /// <summary>Whether <paramref name="p"/> lies in the prism.</summary>
  public bool Contains(Vec3 p) {
    if (p[Axis] < Lo - Eps || p[Axis] > Hi + Eps)
      return false;
    double u = p[(Axis + 1) % 3],
      v = p[(Axis + 2) % 3];
    int sign = 0;
    for (int i = 0; i < Polygon.Count; i++) {
      (double au, double av) = Polygon[i];
      (double bu, double bv) = Polygon[(i + 1) % Polygon.Count];
      double cross = (bu - au) * (v - av) - (bv - av) * (u - au);
      if (Math.Abs(cross) <= Eps)
        continue;
      int s = Math.Sign(cross);
      if (sign == 0)
        sign = s;
      else if (s != sign)
        return false;
    }
    return true;
  }

  /// <summary>The prism's axis-aligned bounds.</summary>
  public Box Bounds {
    get {
      var lo = new double[3];
      var hi = new double[3];
      lo[Axis] = Lo;
      hi[Axis] = Hi;
      lo[(Axis + 1) % 3] = Polygon.Min(p => p.U);
      hi[(Axis + 1) % 3] = Polygon.Max(p => p.U);
      lo[(Axis + 2) % 3] = Polygon.Min(p => p.V);
      hi[(Axis + 2) % 3] = Polygon.Max(p => p.V);
      return new Box(
        new Vec3(lo[0], lo[1], lo[2]),
        new Vec3(hi[0], hi[1], hi[2])
      );
    }
  }
}

/// <summary>The space leaves fill: exact boxes for the ones on the world axes, extruded convex polygons for the
/// ones turned about one world axis.</summary>
public sealed class Occupancy {
  private const double Eps = 1e-9;

  /// <summary>The boxes of the leaves on the world axes, and every box added by hand.</summary>
  public List<Box> Boxes { get; } = [];

  /// <summary>The prisms of the turned leaves.</summary>
  public List<Prism> Prisms { get; } = [];

  /// <summary>The axis-aligned bounds of every leaf placed, one per leaf, in the order added.</summary>
  public List<Box> Parts { get; } = [];

  /// <summary>The leaves placed by <paramref name="at"/>.</summary>
  /// <param name="leaves">Root-level leaves, no children, scale 1.</param>
  /// <param name="at">The placement applied to each leaf's own matrix; null is none.</param>
  /// <exception cref="CastGenException">A leaf is turned about more than one world axis.</exception>
  public static Occupancy Of(IEnumerable<Leaf> leaves, Mat4d? at = null) {
    var occ = new Occupancy();
    Mat4d place = at ?? Mat4d.Identity;
    foreach (Leaf l in leaves)
      occ.Add(place * Engine.Local(l.Element), Engine.Size(l.Element), l.Name);
    return occ;
  }

  /// <summary>Adds the box of <paramref name="size"/> placed by <paramref name="world"/>.</summary>
  /// <param name="world">The placement.</param>
  /// <param name="size">The box's extent on its own axes, in voxels.</param>
  /// <param name="name">The leaf's name, for the message.</param>
  /// <exception cref="CastGenException">The box is turned about more than one world axis.</exception>
  public void Add(Mat4d world, Vec3 size, string name) {
    Box bounds = WorldBox.Of(world, size);
    Parts.Add(bounds);
    int turned = -1;
    int along = 0;
    for (int i = 0; i < 3; i++) {
      Vec3 d = world.MulLinear(
        new Vec3(i == 0 ? 1 : 0, i == 1 ? 1 : 0, i == 2 ? 1 : 0)
      );
      int axes = Enumerable.Range(0, 3).Count(a => Math.Abs(d[a]) > Eps);
      if (axes == 1) {
        along++;
        turned = Enumerable.Range(0, 3).First(a => Math.Abs(d[a]) > Eps);
      }
    }
    if (along == 3) {
      Boxes.Add(bounds);
      return;
    }
    if (along == 0)
      throw new CastGenException(
        $"{name}: turned about more than one axis, which no extruded polygon holds"
      );
    int a0 = turned;
    int u = (a0 + 1) % 3,
      v = (a0 + 2) % 3;
    var pts = Engine
      .Corners(world, size)
      .Select(p => (U: p[u], V: p[v]))
      .ToList();
    Prisms.Add(new Prism(a0, bounds.Lo[a0], bounds.Hi[a0], Hull(pts)));
  }

  /// <summary>Adds a box on the world axes.</summary>
  public void Add(Box box) {
    Boxes.Add(box);
    Parts.Add(box);
  }

  /// <summary>Adds everything <paramref name="other"/> holds.</summary>
  public void Add(Occupancy other) {
    Boxes.AddRange(other.Boxes);
    Prisms.AddRange(other.Prisms);
    Parts.AddRange(other.Parts);
  }

  /// <summary>Whether nothing is held.</summary>
  public bool Empty => Boxes.Count == 0 && Prisms.Count == 0;

  /// <summary>The bounds of everything held.</summary>
  /// <exception cref="InvalidOperationException">Nothing is held.</exception>
  public Box Bounds {
    get {
      IEnumerable<Box> all = Boxes.Concat(Prisms.Select(p => p.Bounds));
      return new Box(
        new Vec3(
          all.Min(b => b.Lo.X),
          all.Min(b => b.Lo.Y),
          all.Min(b => b.Lo.Z)
        ),
        new Vec3(
          all.Max(b => b.Hi.X),
          all.Max(b => b.Hi.Y),
          all.Max(b => b.Hi.Z)
        )
      );
    }
  }

  private static List<(double U, double V)> Hull(List<(double U, double V)> pts) {
    List<(double U, double V)> s =
    [
      .. pts.Distinct().OrderBy(p => p.U).ThenBy(p => p.V),
    ];
    if (s.Count < 3)
      return s;
    static double Cross(
      (double U, double V) o,
      (double U, double V) a,
      (double U, double V) b
    ) => (a.U - o.U) * (b.V - o.V) - (a.V - o.V) * (b.U - o.U);
    var h = new List<(double U, double V)>();
    foreach (var p in s) {
      while (h.Count >= 2 && Cross(h[^2], h[^1], p) <= Eps)
        h.RemoveAt(h.Count - 1);
      h.Add(p);
    }
    int lower = h.Count + 1;
    for (int i = s.Count - 2; i >= 0; i--) {
      while (h.Count >= lower && Cross(h[^2], h[^1], s[i]) <= Eps)
        h.RemoveAt(h.Count - 1);
      h.Add(s[i]);
    }
    h.RemoveAt(h.Count - 1);
    return h;
  }
}
