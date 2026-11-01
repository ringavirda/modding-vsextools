using System;
using System.Collections.Generic;

namespace ExpandedLib.Shapes.Core;

/// <summary>One enabled face of an element as a world quad.</summary>
/// <param name="Face">The face name (north, east, south, west, up, down).</param>
/// <param name="Points">The four corners in the game's vertex order: bottom-right, top-right,
/// top-left, bottom-left as seen from outside.</param>
/// <param name="Normal">The world unit normal (zero when the transform collapses the face).</param>
/// <param name="Uv">[u1, v1, u2, v2] as written, or the face's own size when absent.</param>
/// <param name="Rotation">The face rotation in degrees.</param>
/// <param name="Texture">The texture key without its leading <c>#</c>.</param>
/// <param name="Path">The element's path.</param>
public sealed record Quad(
  string Face,
  Vec3[] Points,
  Vec3 Normal,
  double[] Uv,
  int Rotation,
  string Texture,
  string Path
);

/// <summary>An element with its path and its parent's path (null for a root).</summary>
public sealed record PathedElement(
  string Path,
  Element Element,
  string? ParentPath
);

/// <summary>The game's element transform, in double precision.</summary>
public static class Engine {
  private static readonly string[] FaceNames =
  [
    "north",
    "east",
    "south",
    "west",
    "up",
    "down",
  ];

  private static readonly Dictionary<
    string,
    (int X, int Y, int Z)[]
  > FaceCorners = new() {
    ["north"] = [(0, 0, 0), (0, 1, 0), (1, 1, 0), (1, 0, 0)],
    ["east"] = [(1, 0, 0), (1, 1, 0), (1, 1, 1), (1, 0, 1)],
    ["south"] = [(1, 0, 1), (1, 1, 1), (0, 1, 1), (0, 0, 1)],
    ["west"] = [(0, 0, 1), (0, 1, 1), (0, 1, 0), (0, 0, 0)],
    ["up"] = [(1, 1, 1), (1, 1, 0), (0, 1, 0), (0, 1, 1)],
    ["down"] = [(0, 0, 1), (0, 0, 0), (1, 0, 0), (1, 0, 1)],
  };

  private static readonly Dictionary<string, Vec3> FaceNormals = new() {
    ["north"] = new(0, 0, -1),
    ["east"] = new(1, 0, 0),
    ["south"] = new(0, 0, 1),
    ["west"] = new(-1, 0, 0),
    ["up"] = new(0, 1, 0),
    ["down"] = new(0, -1, 0),
  };

  private static readonly Dictionary<string, (int U, int V)> FaceUvAxes = new() {
    ["north"] = (0, 1),
    ["south"] = (0, 1),
    ["east"] = (2, 1),
    ["west"] = (2, 1),
    ["up"] = (0, 2),
    ["down"] = (0, 2),
  };

  /// <summary>
  /// An element's local matrix, <c>T(origin) . R(rotation + pose) . S(scale * stretch) .
  /// T(from + offset - origin)</c>, applied to points in [0, size].
  /// </summary>
  /// <param name="from">The element's <c>from</c> in its parent's frame.</param>
  /// <param name="origin">The rotation origin in the parent's frame; null (the file gave none)
  /// is 0,0,0 of the parent's frame, as the game reads it.</param>
  /// <param name="rotation">The element's rotation in degrees.</param>
  /// <param name="scale">The element's scale.</param>
  /// <param name="pose">The animation transform, or null for the rest pose.</param>
  public static Mat4d Local(
    Vec3 from,
    Vec3? origin,
    Vec3 rotation,
    Vec3 scale,
    Pose? pose = null
  ) {
    Vec3 o = origin ?? Vec3.Zero;
    Vec3 rot = pose == null ? rotation : rotation + pose.Rotation;
    Vec3 sc = pose == null ? scale : scale * pose.Stretch;
    Vec3 offset = pose?.Offset ?? Vec3.Zero;
    return Mat4d.Translate(o)
      * Mat4d.RotateByXyz(rot.X, rot.Y, rot.Z)
      * Mat4d.Scale(sc)
      * Mat4d.Translate(from + offset - o);
  }

  /// <summary>The local matrix of <paramref name="el"/> at <paramref name="pose"/>.</summary>
  public static Mat4d Local(Element el, Pose? pose = null) =>
    Local(
      Vec3.From(el.From, Vec3.Zero),
      el.RotationOrigin is { Length: >= 3 }
        ? Vec3.From(el.RotationOrigin, Vec3.Zero)
        : null,
      new Vec3(el.RotationX ?? 0, el.RotationY ?? 0, el.RotationZ ?? 0),
      new Vec3(el.ScaleX ?? 1, el.ScaleY ?? 1, el.ScaleZ ?? 1),
      pose
    );

  /// <summary>The size of <paramref name="el"/>, <c>to - from</c>.</summary>
  public static Vec3 Size(Element el) =>
    Vec3.From(el.To, Vec3.Zero) - Vec3.From(el.From, Vec3.Zero);

  /// <summary>
  /// Every element of <paramref name="shape"/> in pre-order with its path: names joined by
  /// <c>/</c>, a repeated sibling name taking <c>#2</c>, <c>#3</c>... and an unnamed element
  /// <c>?</c>.
  /// </summary>
  public static List<PathedElement> Walk(Shape shape) {
    var outp = new List<PathedElement>();
    void Rec(IEnumerable<Element> siblings, string prefix, string? parent) {
      var seen = new Dictionary<string, int>();
      foreach (Element el in siblings) {
        string name = el.Name ?? "?";
        int n = seen[name] = seen.GetValueOrDefault(name) + 1;
        string path =
          (prefix.Length > 0 ? prefix + "/" : "")
          + name
          + (n > 1 ? "#" + n : "");
        outp.Add(new PathedElement(path, el, parent));
        Rec(el.Children, path, path);
      }
    }
    Rec(shape.Elements, "", null);
    return outp;
  }

  /// <summary>
  /// The world matrix of every element by path, each element's local matrix composed onto its
  /// parent's.
  /// </summary>
  /// <param name="shape">The shape.</param>
  /// <param name="poses">Poses by element name, or null for the rest pose.</param>
  public static Dictionary<string, Mat4d> WorldMatrices(
    Shape shape,
    IReadOnlyDictionary<string, Pose>? poses = null
  ) {
    var outp = new Dictionary<string, Mat4d>();
    foreach (PathedElement pe in Walk(shape)) {
      Pose? pose = null;
      poses?.TryGetValue(pe.Element.Name ?? "?", out pose);
      Mat4d local = Local(pe.Element, pose);
      outp[pe.Path] =
        pe.ParentPath == null ? local : outp[pe.ParentPath] * local;
    }
    return outp;
  }

  /// <summary>The 8 world corners of a box of <paramref name="size"/> placed by <paramref name="m"/>,
  /// index bits <c>x*4 + y*2 + z</c>.</summary>
  public static Vec3[] Corners(Mat4d m, Vec3 size) {
    var pts = new Vec3[8];
    for (int x = 0; x <= 1; x++)
      for (int y = 0; y <= 1; y++)
        for (int z = 0; z <= 1; z++)
          pts[x * 4 + y * 2 + z] = m.Mul(
            new Vec3(x * size.X, y * size.Y, z * size.Z)
          );
    return pts;
  }

  private static string? FaceOf(string key) =>
    key.Length == 0
      ? null
      : char.ToLowerInvariant(key[0]) switch {
        'n' => "north",
        'e' => "east",
        's' => "south",
        'w' => "west",
        'u' => "up",
        'd' => "down",
        _ => null,
      };

  /// <summary>The enabled faces of <paramref name="el"/>, at <paramref name="path"/>, as world
  /// quads under the world matrix <paramref name="m"/>.</summary>
  public static List<Quad> FaceQuads(Element el, string path, Mat4d m) {
    var outp = new List<Quad>();
    Vec3 size = Size(el);
    foreach (string face in FaceNames)
      foreach (KeyValuePair<string, Face> kv in el.Faces) {
        if (FaceOf(kv.Key) != face || kv.Value.Enabled == false)
          continue;
        Face spec = kv.Value;
        var pts = new Vec3[4];
        for (int i = 0; i < 4; i++) {
          (int cx, int cy, int cz) = FaceCorners[face][i];
          pts[i] = m.Mul(new Vec3(cx * size.X, cy * size.Y, cz * size.Z));
        }
        Vec3 n = m.MulLinear(FaceNormals[face]);
        double len = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
        if (len > 0)
          n = n * (1 / len);
        (int ua, int va) = FaceUvAxes[face];
        double[] uv = spec.Uv is { Length: >= 4 }
          ? [spec.Uv[0], spec.Uv[1], spec.Uv[2], spec.Uv[3]]
          : [0, 0, size[ua], size[va]];
        outp.Add(
          new Quad(
            face,
            pts,
            n,
            uv,
            (int)(spec.Rotation ?? 0),
            (spec.Texture ?? "").TrimStart('#'),
            path
          )
        );
      }
    return outp;
  }
}
