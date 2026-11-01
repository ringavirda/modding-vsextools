using System;
using System.Collections.Generic;
using System.Linq;

namespace ExpandedLib.Shapes.Core;

/// <summary>An axis-aligned box in world space, shape units.</summary>
public readonly record struct Box(Vec3 Lo, Vec3 Hi) {
  /// <summary>Hi minus Lo.</summary>
  public Vec3 Size => Hi - Lo;

  /// <summary>The middle of the box.</summary>
  public Vec3 Centre => (Lo + Hi) * 0.5;
}

/// <summary>World-space boxes of a shape's elements, and moving an element to one.</summary>
public static class WorldBox {
  /// <summary>The axis-aligned bounds of a box of <paramref name="size"/> placed by
  /// <paramref name="world"/>.</summary>
  public static Box Of(Mat4d world, Vec3 size) {
    Vec3[] pts = Engine.Corners(world, size);
    return new Box(
      new Vec3(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z)),
      new Vec3(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z))
    );
  }

  /// <summary>The world bounds of the element at <paramref name="path"/> under
  /// <paramref name="poses"/> (by element name; null is the rest pose).</summary>
  /// <exception cref="KeyNotFoundException">No element has that path.</exception>
  public static Box Of(
    Shape shape,
    string path,
    IReadOnlyDictionary<string, Pose>? poses = null
  ) {
    Dictionary<string, Mat4d> world = Engine.WorldMatrices(shape, poses);
    PathedElement pe =
      Engine.Walk(shape).FirstOrDefault(e => e.Path == path)
      ?? throw new KeyNotFoundException($"no element '{path}'");
    return Of(world[path], Engine.Size(pe.Element));
  }

  /// <summary>
  /// Moves and resizes the element at <paramref name="path"/> so its rest-pose world bounds are
  /// <paramref name="box"/>, writing parent-relative <c>from</c>, <c>to</c> and, when the element
  /// rotates or scales, <c>rotationOrigin</c>. Rotation and scale stay as they are; the element's
  /// children move with it.
  /// </summary>
  /// <exception cref="KeyNotFoundException">No element has that path.</exception>
  /// <exception cref="ArgumentException">The element's or an ancestor's transform cannot be
  /// inverted, the box is too small for the element's rotation to fit, or the element's rotation
  /// is degenerate (45 degrees about one axis) and the box's size is not the current one; such an
  /// element can only be translated.</exception>
  public static void SetWorld(Shape shape, string path, Box box) {
    List<PathedElement> all = Engine.Walk(shape);
    PathedElement pe =
      all.FirstOrDefault(e => e.Path == path)
      ?? throw new KeyNotFoundException($"no element '{path}'");
    Dictionary<string, Mat4d> world = Engine.WorldMatrices(shape);
    Element el = pe.Element;
    Mat4d w = world[path];
    Mat4d parent =
      pe.ParentPath == null ? Mat4d.Identity : world[pe.ParentPath];

    var abs = new double[9];
    for (int r = 0; r < 3; r++)
      for (int c = 0; c < 3; c++)
        abs[r * 3 + c] = Math.Abs(w[r, c]);
    Vec3 size = Engine.Size(el);
    double[] s;
    double[]? solved = Solve3(abs, [box.Size.X, box.Size.Y, box.Size.Z]);
    if (solved != null)
      s = solved;
    else {
      Vec3 now = Of(w, size).Size;
      Vec3 diff = box.Size - now;
      if (
        Math.Abs(diff.X) > 1e-9
        || Math.Abs(diff.Y) > 1e-9
        || Math.Abs(diff.Z) > 1e-9
      )
        throw new ArgumentException(
          $"'{path}' has a degenerate rotation, so only a box of its current size {now} can be set"
        );
      s = [size.X, size.Y, size.Z];
    }
    if (s.Any(v => v < 0))
      throw new ArgumentException(
        $"'{path}' has no box of that size under its rotation"
      );

    Vec3 from = Vec3.From(el.From, Vec3.Zero);
    Vec3 newSize = new(s[0], s[1], s[2]);
    Vec3 newFrom = from + (size - newSize) * 0.5;

    Vec3 delta = box.Centre - w.Mul(size * 0.5);
    var lin = new double[9];
    for (int r = 0; r < 3; r++)
      for (int c = 0; c < 3; c++)
        lin[r * 3 + c] = parent[r, c];
    double[] d =
      Solve3(lin, [delta.X, delta.Y, delta.Z])
      ?? throw new ArgumentException(
        $"'{path}' sits under a transform that cannot be inverted"
      );
    var shift = new Vec3(d[0], d[1], d[2]);
    newFrom += shift;

    bool transformed =
      (el.RotationX ?? 0) != 0
      || (el.RotationY ?? 0) != 0
      || (el.RotationZ ?? 0) != 0
      || (el.ScaleX ?? 1) != 1
      || (el.ScaleY ?? 1) != 1
      || (el.ScaleZ ?? 1) != 1;
    if (el.RotationOrigin != null || (transformed && shift != Vec3.Zero))
      el.RotationOrigin = (
        Vec3.From(el.RotationOrigin, Vec3.Zero) + shift
      ).ToArray();
    el.From = newFrom.ToArray();
    el.To = (newFrom + newSize).ToArray();
  }

  /// <summary>The volume of the union of every element's rest- or posed-world bounds, overlaps
  /// counted once.</summary>
  public static double UnionVolume(
    Shape shape,
    IReadOnlyDictionary<string, Pose>? poses = null
  ) {
    Dictionary<string, Mat4d> world = Engine.WorldMatrices(shape, poses);
    List<Box> boxes =
    [
      .. Engine
        .Walk(shape)
        .Select(pe => Of(world[pe.Path], Engine.Size(pe.Element))),
    ];
    boxes.RemoveAll(b => b.Size.X <= 0 || b.Size.Y <= 0 || b.Size.Z <= 0);
    double[] xs =
    [
      .. boxes.SelectMany(b => new[] { b.Lo.X, b.Hi.X }).Distinct().Order(),
    ];
    double total = 0;
    for (int i = 0; i + 1 < xs.Length; i++) {
      List<Box> slab =
      [
        .. boxes.Where(b => b.Lo.X <= xs[i] && b.Hi.X >= xs[i + 1]),
      ];
      double[] ys =
      [
        .. slab.SelectMany(b => new[] { b.Lo.Y, b.Hi.Y }).Distinct().Order(),
      ];
      double area = 0;
      for (int j = 0; j + 1 < ys.Length; j++) {
        var zs = slab.Where(b => b.Lo.Y <= ys[j] && b.Hi.Y >= ys[j + 1])
          .Select(b => (b.Lo.Z, b.Hi.Z))
          .OrderBy(z => z.Item1);
        double len = 0,
          end = double.NegativeInfinity;
        foreach ((double lo, double hi) in zs) {
          if (hi > end) {
            len += hi - Math.Max(lo, end);
            end = hi;
          }
        }
        area += len * (ys[j + 1] - ys[j]);
      }
      total += area * (xs[i + 1] - xs[i]);
    }
    return total;
  }

  private static double[]? Solve3(double[] a, double[] b) {
    double Det(double[] m) =>
      m[0] * (m[4] * m[8] - m[5] * m[7])
      - m[1] * (m[3] * m[8] - m[5] * m[6])
      + m[2] * (m[3] * m[7] - m[4] * m[6]);
    double det = Det(a);
    if (Math.Abs(det) < 1e-12)
      return null;
    var x = new double[3];
    for (int col = 0; col < 3; col++) {
      double[] m = [.. a];
      for (int r = 0; r < 3; r++)
        m[r * 3 + col] = b[r];
      x[col] = Det(m) / det;
    }
    return x;
  }
}
