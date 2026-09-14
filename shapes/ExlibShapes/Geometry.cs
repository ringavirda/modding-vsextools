using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vintagestory.API.Common;

namespace ExpandedLib.Shapes;

/// <summary>
/// One element's keyframed offset from its animation, as <see cref="Poses.PoseAt"/> interpolates
/// it: added to (<see cref="Offset"/>, <see cref="Rotation"/>) or multiplied into
/// (<see cref="Stretch"/>) the element's own JSON values in <see cref="Geometry.LocalMatrix"/>.
/// </summary>
public sealed class Pose {
  /// <summary>World-unit offset added to the element's <c>from</c>.</summary>
  public Vector3 Offset { get; init; } = Vector3.Zero;

  /// <summary>Degrees added to the element's own rotation.</summary>
  public Vector3 Rotation { get; init; } = Vector3.Zero;

  /// <summary>Per-axis scale applied in place of the identity scale.</summary>
  public Vector3 Stretch { get; init; } = Vector3.One;
}

/// <summary>
/// World-space geometry of shape elements, following the game's <see cref="ShapeElement"/>
/// transform exactly.
/// <para>
/// Local matrix (animation version 0): <c>T(origin) . RotateByXYZ(rot + pose.rot) .
/// Scale(pose.stretch) . T(from + pose.offset - origin)</c>, applied to local vertices in
/// <c>[0, size]</c>; a child's matrix is composed onto its parent's. All lengths in shape units,
/// angles in degrees.
/// </para>
/// <para>
/// The game's own matrices act on column vectors (<c>m @ v</c>); <see cref="System.Numerics.Matrix4x4"/>
/// acts on row vectors (<c>v * m</c>, translation in the fourth row). Every matrix this class
/// builds or returns is therefore the transpose of the equivalent column-vector matrix, so that
/// <c>Vector3.Transform(v, m)</c> and matrix chaining with <c>*</c> reproduce the game's own
/// results without a caller ever transposing anything itself.
/// </para>
/// </summary>
public static class Geometry {
  /// <summary>The six face names a <see cref="Node"/> can carry, in the game's own order.</summary>
  public static readonly string[] Faces = ["north", "east", "south", "west", "up", "down"];

  /// <summary>The outward unit normal of each face in element-local space.</summary>
  public static readonly IReadOnlyDictionary<string, Vector3> FaceNormal =
    new Dictionary<string, Vector3> {
      ["north"] = new(0, 0, -1),
      ["east"] = new(1, 0, 0),
      ["south"] = new(0, 0, 1),
      ["west"] = new(-1, 0, 0),
      ["up"] = new(0, 1, 0),
      ["down"] = new(0, -1, 0),
    };

  /// <summary>
  /// Game vertex order per face (<c>CubeMeshUtil.CubeVertices</c>): bottom-right, top-right,
  /// top-left, bottom-left, as seen from outside; each entry picks the min (0) or max (1) corner
  /// on x, y, z, in units of the element's own size.
  /// </summary>
  public static readonly IReadOnlyDictionary<string, (int X, int Y, int Z)[]> FaceCorners =
    new Dictionary<string, (int, int, int)[]> {
      ["north"] = [(0, 0, 0), (0, 1, 0), (1, 1, 0), (1, 0, 0)],
      ["east"] = [(1, 0, 0), (1, 1, 0), (1, 1, 1), (1, 0, 1)],
      ["south"] = [(1, 0, 1), (1, 1, 1), (0, 1, 1), (0, 0, 1)],
      ["west"] = [(0, 0, 1), (0, 1, 1), (0, 1, 0), (0, 0, 0)],
      ["up"] = [(1, 1, 1), (1, 1, 0), (0, 1, 0), (0, 1, 1)],
      ["down"] = [(0, 0, 1), (0, 0, 0), (1, 0, 0), (1, 0, 1)],
    };

  /// <summary>Which face axes span its uv rect: (u axis index, v axis index), 0=x, 1=y, 2=z.</summary>
  public static readonly IReadOnlyDictionary<string, (int U, int V)> FaceUvAxes =
    new Dictionary<string, (int, int)> {
      ["north"] = (0, 1),
      ["south"] = (0, 1),
      ["east"] = (2, 1),
      ["west"] = (2, 1),
      ["up"] = (0, 2),
      ["down"] = (0, 2),
    };

  private static float Axis(Vector3 v, int i) => i switch { 0 => v.X, 1 => v.Y, _ => v.Z };

  /// <summary><c>Mat4f.RotateByXYZ</c> as a matrix acting on column vectors: <c>Rx . Ry . Rz</c>,
  /// returned transposed for <see cref="System.Numerics.Matrix4x4"/>'s row-vector convention (see
  /// the class remarks).</summary>
  public static Matrix4x4 RotateByXyz(float rx, float ry, float rz) {
    (float sx, float cx) = MathF.SinCos(rx * MathF.PI / 180f);
    (float sy, float cy) = MathF.SinCos(ry * MathF.PI / 180f);
    (float sz, float cz) = MathF.SinCos(rz * MathF.PI / 180f);
    return new Matrix4x4(
      cy * cz,
      sx * sy * cz + cx * sz,
      -cx * sy * cz + sx * sz,
      0f,
      -cy * sz,
      cx * cz - sx * sy * sz,
      sx * cz + cx * sy * sz,
      0f,
      sy,
      -sx * cy,
      cx * cy,
      0f,
      0f,
      0f,
      0f,
      1f
    );
  }

  /// <summary>A translation matrix (already in row-vector form: <see cref="Matrix4x4.CreateTranslation(Vector3)"/>
  /// is exactly the transpose of the column-vector translation the game builds).</summary>
  public static Matrix4x4 Translate(Vector3 v) => Matrix4x4.CreateTranslation(v);

  /// <summary>A scale matrix (diagonal, so transposing it is a no-op).</summary>
  public static Matrix4x4 Scale(Vector3 v) => Matrix4x4.CreateScale(v);

  /// <summary>
  /// An element's local matrix, optionally posed: <c>rot</c> and <c>offset</c> add the pose's, and
  /// <c>stretch</c> multiplies in place of the identity scale, exactly as animation version 0
  /// applies a keyframe.
  /// </summary>
  public static Matrix4x4 LocalMatrix(Node el, Pose? pose = null) {
    Vector3 rot = pose == null ? el.Rotation : el.Rotation + pose.Rotation;
    Vector3 offset = pose?.Offset ?? Vector3.Zero;
    Vector3 stretch = pose?.Stretch ?? Vector3.One;
    return Translate(el.From + offset - el.Origin)
      * Scale(stretch)
      * RotateByXyz(rot.X, rot.Y, rot.Z)
      * Translate(el.Origin);
  }

  /// <summary>
  /// World matrix per element path, composing each element's <see cref="LocalMatrix"/> onto its
  /// parent's. <paramref name="poses"/> maps element NAME to a <see cref="Pose"/> - keyframes
  /// address elements by name, not by path.
  /// </summary>
  public static Dictionary<string, Matrix4x4> WorldMatrices(
    LoadedShape shape,
    IReadOnlyDictionary<string, Pose>? poses = null
  ) {
    var outp = new Dictionary<string, Matrix4x4>();
    void Rec(Node el, Matrix4x4 parentM) {
      Pose? pose = null;
      poses?.TryGetValue(el.Name, out pose);
      Matrix4x4 m = LocalMatrix(el, pose) * parentM;
      outp[el.Path] = m;
      foreach (Node c in el.Children)
        Rec(c, m);
    }
    foreach (Node el in shape.Elements)
      Rec(el, Matrix4x4.Identity);
    return outp;
  }

  /// <summary>The 8 world corners of an element, index bits <c>x*4 + y*2 + z</c>.</summary>
  public static Vector3[] Corners(Matrix4x4 m, Vector3 size) {
    var pts = new Vector3[8];
    for (int x = 0; x <= 1; x++)
      for (int y = 0; y <= 1; y++)
        for (int z = 0; z <= 1; z++)
          pts[x * 4 + y * 2 + z] = Vector3.Transform(
            new Vector3(x * size.X, y * size.Y, z * size.Z),
            m
          );
    return pts;
  }

  /// <summary>The axis-aligned bounds (min, max) of a point set.</summary>
  public static (Vector3 Lo, Vector3 Hi) Aabb(IReadOnlyList<Vector3> points) {
    Vector3 lo = points[0];
    Vector3 hi = points[0];
    for (int i = 1; i < points.Count; i++) {
      lo = Vector3.Min(lo, points[i]);
      hi = Vector3.Max(hi, points[i]);
    }
    return (lo, hi);
  }

  /// <summary>Whether two AABBs (lo, hi) overlap or abut within <paramref name="tol"/> on every axis.</summary>
  public static bool Touching((Vector3 Lo, Vector3 Hi) a, (Vector3 Lo, Vector3 Hi) b, float tol = 0.05f) {
    Vector3 lo = Vector3.Max(a.Lo, b.Lo);
    Vector3 hi = Vector3.Min(a.Hi, b.Hi);
    return hi.X - lo.X >= -tol && hi.Y - lo.Y >= -tol && hi.Z - lo.Z >= -tol;
  }

  /// <summary>One face of an element, transformed to world space.</summary>
  public sealed class Quad {
    /// <summary>The face name (<c>north</c>, ... <c>down</c>).</summary>
    public required string Face { get; init; }

    /// <summary>World corners, game order: bottom-right, top-right, top-left, bottom-left.</summary>
    public required Vector3[] Points { get; init; }

    /// <summary><c>[u1, v1, u2, v2]</c> in texture pixels.</summary>
    public required float[] Uv { get; init; }

    /// <summary>The face's texture rotation in degrees (0, 90, 180 or 270).</summary>
    public required int Rotation { get; init; }

    /// <summary>The texture key, with any leading <c>#</c> stripped.</summary>
    public required string Texture { get; init; }

    /// <summary>World-space unit normal.</summary>
    public required Vector3 Normal { get; init; }

    /// <summary>The owning element's path.</summary>
    public required string Path { get; init; }
  }

  /// <summary>The enabled faces of an element as world-space quads.</summary>
  public static List<Quad> FaceQuads(Node el, Matrix4x4 m) {
    var outp = new List<Quad>();
    Vector3 size = el.Size;
    foreach ((string face, ShapeElementFace spec) in el.Faces) {
      if (!FaceCorners.TryGetValue(face, out (int X, int Y, int Z)[]? corners))
        continue;
      var pts = new Vector3[4];
      for (int i = 0; i < 4; i++) {
        (int cx, int cy, int cz) = corners[i];
        pts[i] = Vector3.Transform(new Vector3(cx * size.X, cy * size.Y, cz * size.Z), m);
      }
      Vector3 n = Vector3.Normalize(Vector3.TransformNormal(FaceNormal[face], m));
      (int ua, int va) = FaceUvAxes[face];
      float[] uv =
        spec.Uv is { Length: >= 4 }
          ? [spec.Uv[0], spec.Uv[1], spec.Uv[2], spec.Uv[3]]
          : [0f, 0f, Axis(size, ua), Axis(size, va)];
      string tex = (spec.Texture ?? "").TrimStart('#');
      outp.Add(
        new Quad {
          Face = face,
          Points = pts,
          Uv = uv,
          Rotation = (int)spec.Rotation,
          Texture = tex,
          Normal = n,
          Path = el.Path,
        }
      );
    }
    return outp;
  }
}
