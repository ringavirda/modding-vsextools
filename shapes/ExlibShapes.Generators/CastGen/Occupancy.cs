using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Checks;
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

  /// <summary>The leaves placed by <paramref name="at"/>; the staves of a round run about a world axis stand for the
  /// polygon they span over the run's axial extent.</summary>
  /// <param name="leaves">Root-level leaves, no children, scale 1.</param>
  /// <param name="at">The placement applied to each leaf's own matrix; null is none.</param>
  /// <exception cref="CastGenException">A leaf is turned about more than one world axis.</exception>
  public static Occupancy Of(IEnumerable<Leaf> leaves, Mat4d? at = null) {
    var occ = new Occupancy();
    Mat4d place = at ?? Mat4d.Identity;
    List<Leaf> all = [.. leaves];
    Shape shape = Flush.ShapeOf(all);
    List<StaveRun> runs = StaveRuns.Find(new Tree(shape), new Frames(shape));
    var staves = runs.SelectMany(r => r.Members.Select(m => m.Name))
      .ToHashSet();
    foreach (Leaf l in all) {
      Mat4d world = place * Engine.Local(l.Element);
      if (staves.Contains(l.Name))
        occ.Parts.Add(WorldBox.Of(world, Engine.Size(l.Element)));
      else
        occ.Add(world, Engine.Size(l.Element), l.Name);
    }
    foreach (StaveRun run in runs)
      occ.Add(run, place);
    return occ;
  }

  private void Add(StaveRun run, Mat4d place) {
    int a = run.Axis,
      u = (a + 1) % 3,
      v = (a + 2) % 3;
    foreach (List<(double U, double V)> poly in run.Polygons) {
      var pts = new List<Vec3>();
      foreach ((double pu, double pv) in poly)
        foreach (double along in new[] { run.Lo, run.Hi }) {
          var c = new double[3];
          c[a] = along;
          c[u] = pu;
          c[v] = pv;
          pts.Add(place.Mul(new Vec3(c[0], c[1], c[2])));
        }
      Vec3 d = place.MulLinear(
        new Vec3(a == 0 ? 1 : 0, a == 1 ? 1 : 0, a == 2 ? 1 : 0)
      );
      int[] axes = [.. Enumerable.Range(0, 3).Where(i => Math.Abs(d[i]) > Eps)];
      if (axes.Length != 1)
        throw new CastGenException(
          $"{run.Members[0].Name}: a round run turned about more than one axis, which no extruded polygon holds"
        );
      int na = axes[0],
        nu = (na + 1) % 3,
        nv = (na + 2) % 3;
      Prisms.Add(
        new Prism(
          na,
          pts.Min(p => p[na]),
          pts.Max(p => p[na]),
          StaveRuns.Hull([.. pts.Select(p => (p[nu], p[nv]))])
        )
      );
    }
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
    Prisms.Add(
      new Prism(a0, bounds.Lo[a0], bounds.Hi[a0], StaveRuns.Hull(pts))
    );
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
}
