using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Generators;

/// <summary>A drawn element lifted to a root-level element whose own transform equals the world
/// matrix it had in the drawing.</summary>
/// <param name="Name">The element's name in the output; unique within one selection.</param>
/// <param name="SourceName">The name in the drawing, the key of its part-record entry.</param>
/// <param name="SourcePath">The element's path in the drawing.</param>
/// <param name="Element">The flattened element: no children, scale 1.</param>
/// <param name="Stage">The record stage once classified; null before.</param>
/// <param name="Role">The texture role once classified; null before.</param>
public sealed record Leaf(
  string Name,
  string SourceName,
  string SourcePath,
  Element Element,
  string? Stage = null,
  string? Role = null
) {
  /// <summary>The element's world bounds (it is root-level, so its local matrix is its world matrix).</summary>
  public Box Box => WorldBox.Of(Engine.Local(Element), Engine.Size(Element));

  /// <summary>Whether the element has no rotation.</summary>
  public bool AxisAligned =>
    (Element.RotationX ?? 0) == 0
    && (Element.RotationY ?? 0) == 0
    && (Element.RotationZ ?? 0) == 0;
}

/// <summary>Step 1: selects groups of a drawing and flattens their elements.</summary>
public static class Select {
  private const double Eps = 1e-9;

  /// <summary>Starts a group string that selects one element alone: <c>element:Root/Ram</c>.</summary>
  public const string AlonePrefix = "element:";

  /// <summary>The group string of <paramref name="path"/> selected alone.</summary>
  public static string Alone(string path) => AlonePrefix + path;

  /// <summary>Every drawn element (one with an enabled face) under each of
  /// <paramref name="groups"/>, as root-level leaves whose world matrix is
  /// <paramref name="placement"/> times the drawing's.</summary>
  /// <param name="shape">The drawing.</param>
  /// <param name="groups">Element paths; a path selects itself and all it contains, an
  /// <see cref="Alone"/> string only the named element, its children left to other entries.</param>
  /// <param name="placement">Applied to every world matrix; null is none.</param>
  /// <param name="taken">Names already used by the caller's other selections; a repeated name
  /// gets <c>_2</c>, <c>_3</c>; the names used here are added.</param>
  /// <exception cref="CastGenException">A group is not in the drawing, an element is unnamed, or a
  /// world matrix is not a rotation and positive scale.</exception>
  /// <exception cref="SpecException">An <see cref="Alone"/> path names no element.</exception>
  public static List<Leaf> Groups(
    Shape shape,
    IReadOnlyList<string> groups,
    Mat4d? placement = null,
    ISet<string>? taken = null
  ) {
    taken ??= new HashSet<string>(StringComparer.Ordinal);
    Mat4d place = placement ?? Mat4d.Identity;
    List<PathedElement> all = Engine.Walk(shape);
    Dictionary<string, Mat4d> world = Engine.WorldMatrices(shape);
    var outp = new List<Leaf>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (string group in groups) {
      bool alone = group.StartsWith(AlonePrefix, StringComparison.Ordinal);
      string path = alone ? group[AlonePrefix.Length..] : group;
      string prefix = path + "/";
      bool found = false;
      foreach (PathedElement pe in all) {
        if (
          pe.Path != path
          && (alone || !pe.Path.StartsWith(prefix, StringComparison.Ordinal))
        )
          continue;
        found = true;
        if (!seen.Add(pe.Path) || !Drawn(pe.Element))
          continue;
        string source =
          pe.Element.Name
          ?? throw new CastGenException($"{pe.Path}: element has no name");
        outp.Add(
          new Leaf(
            Unique(source, taken),
            source,
            pe.Path,
            Flatten(pe.Element, place * world[pe.Path], pe.Path)
          )
        );
      }
      if (!found)
        throw alone
          ? new SpecException($"element \"{path}\" is not in the drawing")
          : new CastGenException($"group \"{group}\" is not in the drawing");
    }
    return outp;
  }

  private static bool Drawn(Element e) =>
    e.Faces.Any(f => f.Value.Enabled != false);

  private static string Unique(string name, ISet<string> taken) {
    string n = name;
    for (int i = 2; !taken.Add(n); i++)
      n = $"{name}_{i}";
    return n;
  }

  /// <summary>Keeps the part of each leaf inside <paramref name="window"/> (x0, y0, z0, x1, y1,
  /// z1); a leaf cut by it loses its explicit uv rectangles.</summary>
  /// <exception cref="CastGenException">A rotated leaf crosses the window.</exception>
  public static List<Leaf> Window(IEnumerable<Leaf> leaves, double[] window) {
    var outp = new List<Leaf>();
    foreach (Leaf leaf in leaves) {
      Box b = leaf.Box;
      bool outside = false,
        inside = true;
      for (int a = 0; a < 3; a++) {
        if (b.Hi[a] <= window[a] + Eps || b.Lo[a] >= window[a + 3] - Eps)
          outside = true;
        if (b.Lo[a] < window[a] - Eps || b.Hi[a] > window[a + 3] + Eps)
          inside = false;
      }
      if (outside)
        continue;
      if (inside) {
        outp.Add(leaf);
        continue;
      }
      if (!leaf.AxisAligned)
        throw new CastGenException(
          $"{leaf.SourcePath}: a rotated element crosses the window"
        );
      Element e = Clone(leaf.Element, true);
      var from = (double[])e.From!.Clone();
      var to = (double[])e.To!.Clone();
      for (int a = 0; a < 3; a++) {
        from[a] = Math.Max(from[a], window[a]);
        to[a] = Math.Min(to[a], window[a + 3]);
      }
      e.From = from;
      e.To = to;
      outp.Add(leaf with { Element = e });
    }
    return outp;
  }

  /// <summary>A deep copy of <paramref name="src"/> without children; with
  /// <paramref name="dropUv"/> its explicit uv rectangles are left out.</summary>
  public static Element Clone(Element src, bool dropUv) {
    var e = new Element {
      Name = src.Name,
      From = (double[]?)src.From?.Clone(),
      To = (double[]?)src.To?.Clone(),
      RotationOrigin = (double[]?)src.RotationOrigin?.Clone(),
      RotationX = src.RotationX,
      RotationY = src.RotationY,
      RotationZ = src.RotationZ,
      ScaleX = src.ScaleX,
      ScaleY = src.ScaleY,
      ScaleZ = src.ScaleZ,
    };
    foreach (KeyValuePair<string, Face> kv in src.Faces)
      e.Faces.Add(
        new(
          kv.Key,
          new Face {
            Texture = kv.Value.Texture,
            Uv = dropUv ? null : (double[]?)kv.Value.Uv?.Clone(),
            Rotation = kv.Value.Rotation,
            Enabled = kv.Value.Enabled,
          }
        )
      );
    return e;
  }

  /// <summary>A deep copy of <paramref name="src"/> with its children, recursively; with
  /// <paramref name="dropUv"/> every explicit uv rectangle is left out.</summary>
  public static Element CloneTree(Element src, bool dropUv) {
    Element e = Clone(src, dropUv);
    foreach (Element child in src.Children)
      e.Children.Add(CloneTree(child, dropUv));
    return e;
  }

  /// <summary>An element whose own transform is <paramref name="world"/>: its near corner at
  /// the world position of the original's, rotated about it, any scale folded into its size.</summary>
  /// <exception cref="CastGenException">The matrix has a shear or a mirror.</exception>
  internal static Element Flatten(Element src, Mat4d world, string path) {
    var s = new double[3];
    var r = new double[3, 3];
    for (int j = 0; j < 3; j++) {
      s[j] = Math.Sqrt(
        world[0, j] * world[0, j]
          + world[1, j] * world[1, j]
          + world[2, j] * world[2, j]
      );
      if (s[j] < 1e-12)
        throw new CastGenException($"{path}: collapsed by its transform");
      for (int i = 0; i < 3; i++)
        r[i, j] = world[i, j] / s[j];
    }
    double ry = Math.Asin(Math.Clamp(r[0, 2], -1, 1));
    double rx,
      rz;
    if (Math.Abs(Math.Cos(ry)) > 1e-9) {
      rx = Math.Atan2(-r[1, 2], r[2, 2]);
      rz = Math.Atan2(-r[0, 1], r[0, 0]);
    } else {
      rz = 0;
      rx = Math.Atan2(r[2, 1], r[1, 1]);
    }
    double[] deg =
    [
      Tidy(rx * 180 / Math.PI),
      Tidy(ry * 180 / Math.PI),
      Tidy(rz * 180 / Math.PI),
    ];
    Vec3 t = world.Mul(Vec3.Zero);
    Vec3 size = Engine.Size(src);
    bool rotated = deg.Any(d => d != 0);
    bool scaled = s.Any(v => Math.Abs(v - 1) > Eps);
    Element e = Clone(src, scaled);
    e.From = t.ToArray();
    e.To = (
      t + new Vec3(size.X * s[0], size.Y * s[1], size.Z * s[2])
    ).ToArray();
    e.RotationOrigin = rotated ? t.ToArray() : null;
    e.RotationX = deg[0] == 0 ? null : deg[0];
    e.RotationY = deg[1] == 0 ? null : deg[1];
    e.RotationZ = deg[2] == 0 ? null : deg[2];
    e.ScaleX = e.ScaleY = e.ScaleZ = null;

    Mat4d got = Engine.Local(e) * Mat4d.Scale(new Vec3(s[0], s[1], s[2]));
    for (int i = 0; i < 4; i++)
      for (int j = 0; j < 4; j++)
        if (Math.Abs(got[i, j] - world[i, j]) > 1e-7)
          throw new CastGenException(
            $"{path}: its world transform is not a rotation with a positive scale"
          );
    return e;
  }

  private static double Tidy(double v) => Math.Abs(v) < 1e-10 ? 0 : v;
}
