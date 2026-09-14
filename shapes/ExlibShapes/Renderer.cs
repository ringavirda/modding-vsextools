using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using SkiaSharp;

// Lets GeometryTests cross-check Geometry's public float32 chain against the double-precision
// chain below, which the pixel comparison in RendererTests already validates end to end - closing
// the coverage gap a float32-only unit test on Geometry alone would leave.
[assembly: InternalsVisibleTo("ExlibShapes.Tests")]

namespace ExpandedLib.Shapes;

/// <summary>A camera direction: yaw then pitch, both in degrees.</summary>
public readonly struct View {
  /// <summary>Degrees turned about the world y axis before <see cref="Pitch"/> is applied.</summary>
  public double Yaw { get; }

  /// <summary>Degrees turned about the (already yawed) x axis.</summary>
  public double Pitch { get; }

  /// <summary>Builds a view from its yaw and pitch, in degrees.</summary>
  public View(double yaw, double pitch) {
    Yaw = yaw;
    Pitch = pitch;
  }
}

/// <summary>
/// Orthographic software renderer for shape files.
/// <para>
/// The camera is OpenGL-style: view space is <c>Rx(pitch) . Ry(yaw) . world</c>, the camera looks
/// along -z_view, screen right is +x_view, screen up is +y_view, and a larger z_view is nearer.
/// Projection is orthographic: pixel columns follow x_view, rows follow -y_view, scaled by
/// <c>ppu</c> (pixels per unit) with a margin around the shape's projected extent.
/// </para>
/// </summary>
public static class Renderer {
  /// <summary>The canvas colour behind everything drawn.</summary>
  public static readonly SKColor Background = new(240, 240, 236);

  /// <summary>The grid line colour on a non-multiple-of-16 line.</summary>
  public static readonly SKColor GridLight = new(215, 215, 215);

  /// <summary>The grid line colour on a multiple-of-16 (chunk-boundary) line.</summary>
  public static readonly SKColor GridDark = new(170, 170, 170);

  /// <summary>The outline colour drawn over a highlighted element, with no depth test.</summary>
  public static readonly SKColor HighlightColor = new(220, 40, 40);

  /// <summary>The darkening factor an edge line multiplies the surface it lies on by.</summary>
  public const double EdgeMul = 0.62;

  /// <summary>The depth bias added to an edge's endpoints so it wins its own face's z-test.</summary>
  public const double EdgeBias = 0.02;

  /// <summary>World units a grid line repeats a darker line at (a chunk width).</summary>
  public const int GridCell = 16;

  /// <summary>The seven named views a shape is conventionally rendered from.</summary>
  public static readonly IReadOnlyDictionary<string, View> NamedViews = new Dictionary<string, View> {
    ["south"] = new(0, 0),
    ["north"] = new(180, 0),
    ["east"] = new(-90, 0),
    ["west"] = new(90, 0),
    ["up"] = new(0, 90),
    ["down"] = new(0, -90),
    ["iso"] = new(-45, 30),
  };

  // A plain 3x3, column-vector rotation matrix (m @ v), kept separate from Geometry's transposed
  // System.Numerics matrices: the view transform never needs a translation row, so there is
  // nothing to gain from Geometry's row-vector convention here.
  private readonly struct Mat3 {
    private readonly double _00, _01, _02, _10, _11, _12, _20, _21, _22;

    public Mat3(
      double m00, double m01, double m02,
      double m10, double m11, double m12,
      double m20, double m21, double m22
    ) {
      _00 = m00; _01 = m01; _02 = m02;
      _10 = m10; _11 = m11; _12 = m12;
      _20 = m20; _21 = m21; _22 = m22;
    }

    public static Mat3 RotateX(double deg) {
      double r = deg * Math.PI / 180.0;
      double s = Math.Sin(r), c = Math.Cos(r);
      return new Mat3(1, 0, 0, 0, c, -s, 0, s, c);
    }

    public static Mat3 RotateY(double deg) {
      double r = deg * Math.PI / 180.0;
      double s = Math.Sin(r), c = Math.Cos(r);
      return new Mat3(c, 0, s, 0, 1, 0, -s, 0, c);
    }

    public static Mat3 operator *(Mat3 a, Mat3 b) =>
      new(
        a._00 * b._00 + a._01 * b._10 + a._02 * b._20,
        a._00 * b._01 + a._01 * b._11 + a._02 * b._21,
        a._00 * b._02 + a._01 * b._12 + a._02 * b._22,
        a._10 * b._00 + a._11 * b._10 + a._12 * b._20,
        a._10 * b._01 + a._11 * b._11 + a._12 * b._21,
        a._10 * b._02 + a._11 * b._12 + a._12 * b._22,
        a._20 * b._00 + a._21 * b._10 + a._22 * b._20,
        a._20 * b._01 + a._21 * b._11 + a._22 * b._21,
        a._20 * b._02 + a._21 * b._12 + a._22 * b._22
      );

    public (double X, double Y, double Z) Mul(double x, double y, double z) =>
      (_00 * x + _01 * y + _02 * z, _10 * x + _11 * y + _12 * z, _20 * x + _21 * y + _22 * z);

    public (double X, double Y, double Z) Mul(Vector3 v) => Mul(v.X, v.Y, v.Z);
  }

  // A double-precision mirror of Geometry's world-matrix and face-quad chain, used only for the
  // pixel data this renderer produces. Model Creator shapes routinely abut two elements at an
  // exact shared face; System.Numerics' float32 can, after several composed local matrices,
  // flip which of two coincident faces wins the z-buffer's strict `>` test at a seam pixel -
  // a difference in which face is drawn, not a rounding difference in one face's own colour, so
  // the pixel tolerance cannot absorb it. Geometry's own float32 API is unaffected and stays as
  // specified for every other consumer, which tolerates the schematic drawings' own rounding.
  internal readonly struct Mat4d {
    private readonly double[] _m; // row-major 4x4, m[r*4+c], column-vector convention (m @ v)

    private Mat4d(double[] m) => _m = m;

    public static Mat4d Identity =>
      new([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);

    public static Mat4d Translate(double x, double y, double z) {
      double[] m = [1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1];
      return new Mat4d(m);
    }

    public static Mat4d Scale(double x, double y, double z) =>
      new([x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, 0, 0, 0, 1]);

    // Mat4f.RotateByXYZ, column-vector convention.
    public static Mat4d RotateByXyz(double rx, double ry, double rz) {
      double sx = Math.Sin(rx * Math.PI / 180), cx = Math.Cos(rx * Math.PI / 180);
      double sy = Math.Sin(ry * Math.PI / 180), cy = Math.Cos(ry * Math.PI / 180);
      double sz = Math.Sin(rz * Math.PI / 180), cz = Math.Cos(rz * Math.PI / 180);
      double[] m = Identity._m.ToArray();
      m[0] = cy * cz; m[1] = -cy * sz; m[2] = sy;
      m[4] = sx * sy * cz + cx * sz; m[5] = cx * cz - sx * sy * sz; m[6] = -sx * cy;
      m[8] = -cx * sy * cz + sx * sz; m[9] = sx * cz + cx * sy * sz; m[10] = cx * cy;
      return new Mat4d(m);
    }

    public static Mat4d operator *(Mat4d a, Mat4d b) {
      var r = new double[16];
      for (int row = 0; row < 4; row++)
        for (int col = 0; col < 4; col++) {
          double s = 0;
          for (int k = 0; k < 4; k++)
            s += a._m[row * 4 + k] * b._m[k * 4 + col];
          r[row * 4 + col] = s;
        }
      return new Mat4d(r);
    }

    public (double X, double Y, double Z) Mul(double x, double y, double z) {
      double[] v = [x, y, z, 1];
      var r = new double[4];
      for (int row = 0; row < 4; row++)
        for (int k = 0; k < 4; k++)
          r[row] += _m[row * 4 + k] * v[k];
      return (r[0], r[1], r[2]);
    }

    public (double X, double Y, double Z) Mul((double X, double Y, double Z) v) => Mul(v.X, v.Y, v.Z);

    // The upper-left 3x3 applied to a direction: computed straight from the
    // matrix's own linear entries, not by subtracting two transformed points - that subtraction
    // rounds differently at the ULP level, enough to matter at an exactly grazing face (view-space
    // normal.z essentially 0) the cull test is deciding.
    public (double X, double Y, double Z) MulLinear(double x, double y, double z) =>
      (
        _m[0] * x + _m[1] * y + _m[2] * z,
        _m[4] * x + _m[5] * y + _m[6] * z,
        _m[8] * x + _m[9] * y + _m[10] * z
      );
  }

  internal static Mat4d LocalMatrixD(Node el, Pose? pose) {
    (double X, double Y, double Z) rot =
      pose == null
        ? (el.Rotation.X, el.Rotation.Y, el.Rotation.Z)
        : (el.Rotation.X + pose.Rotation.X, el.Rotation.Y + pose.Rotation.Y, el.Rotation.Z + pose.Rotation.Z);
    (double X, double Y, double Z) offset = pose == null ? (0, 0, 0) : (pose.Offset.X, pose.Offset.Y, pose.Offset.Z);
    (double X, double Y, double Z) stretch = pose == null ? (1, 1, 1) : (pose.Stretch.X, pose.Stretch.Y, pose.Stretch.Z);
    return Mat4d.Translate(el.Origin.X, el.Origin.Y, el.Origin.Z)
      * Mat4d.RotateByXyz(rot.X, rot.Y, rot.Z)
      * Mat4d.Scale(stretch.X, stretch.Y, stretch.Z)
      * Mat4d.Translate(
        el.From.X + offset.X - el.Origin.X,
        el.From.Y + offset.Y - el.Origin.Y,
        el.From.Z + offset.Z - el.Origin.Z
      );
  }

  internal static Dictionary<string, Mat4d> WorldMatricesD(LoadedShape shape, IReadOnlyDictionary<string, Pose>? poses) {
    var outp = new Dictionary<string, Mat4d>();
    void Rec(Node el, Mat4d parentM) {
      Pose? pose = null;
      poses?.TryGetValue(el.Name, out pose);
      Mat4d m = parentM * LocalMatrixD(el, pose);
      outp[el.Path] = m;
      foreach (Node c in el.Children)
        Rec(c, m);
    }
    foreach (Node el in shape.Elements)
      Rec(el, Mat4d.Identity);
    return outp;
  }

  internal sealed class QuadD {
    public required (double X, double Y, double Z)[] Points { get; init; }
    public required (double X, double Y, double Z) Normal { get; init; }
    public required float[] Uv { get; init; }
    public required int Rotation { get; init; }
    public required string Texture { get; init; }
    public required string Path { get; init; }
  }

  internal static List<QuadD> FaceQuadsD(Node el, Mat4d m) {
    var outp = new List<QuadD>();
    (double X, double Y, double Z) size = (el.Size.X, el.Size.Y, el.Size.Z);
    foreach ((string face, Vintagestory.API.Common.ShapeElementFace spec) in el.Faces) {
      if (!Geometry.FaceCorners.TryGetValue(face, out (int X, int Y, int Z)[]? corners))
        continue;
      var pts = new (double, double, double)[4];
      for (int i = 0; i < 4; i++) {
        (int cx, int cy, int cz) = corners[i];
        pts[i] = m.Mul(cx * size.X, cy * size.Y, cz * size.Z);
      }
      Vector3 n3 = Geometry.FaceNormal[face];
      (double X, double Y, double Z) n = m.MulLinear(n3.X, n3.Y, n3.Z);
      double len = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
      if (len > 0)
        n = (n.X / len, n.Y / len, n.Z / len);
      (int ua, int va) = Geometry.FaceUvAxes[face];
      float[] uv =
        spec.Uv is { Length: >= 4 }
          ? [spec.Uv[0], spec.Uv[1], spec.Uv[2], spec.Uv[3]]
          : [0f, 0f, (float)AxisD(size, ua), (float)AxisD(size, va)];
      outp.Add(
        new QuadD {
          Points = pts,
          Normal = n,
          Uv = uv,
          Rotation = (int)spec.Rotation,
          Texture = (spec.Texture ?? "").TrimStart('#'),
          Path = el.Path,
        }
      );
    }
    return outp;
  }

  private static double AxisD((double X, double Y, double Z) v, int i) => i switch { 0 => v.X, 1 => v.Y, _ => v.Z };

  /// <summary>Face shading factor from the dominant axis of a world-space normal (up 1.0, down
  /// 0.45, x-facing 0.75, z-facing 0.6). Ties (equal magnitude on two axes) keep x, then y, then
  /// z's factor.</summary>
  private static double Shading((double X, double Y, double Z) normal) {
    double ax = Math.Abs(normal.X), ay = Math.Abs(normal.Y), az = Math.Abs(normal.Z);
    int axis = 0;
    double best = ax;
    if (ay > best) { axis = 1; best = ay; }
    if (az > best) { axis = 2; }
    if (axis == 1)
      return normal.Y > 0 ? 1.0 : 0.45;
    if (axis == 0)
      return 0.75;
    return 0.6;
  }

  // UV assigned to each quad corner [BR, TR, TL, BL], texture rotated clockwise by `rotation`
  // degrees.
  private static (double U, double V)[] RotateUvCorners(float[] uv, int rotation) {
    double u1 = uv[0], v1 = uv[1], u2 = uv[2], v2 = uv[3];
    (double, double)[] baseCorners = [(u2, v2), (u2, v1), (u1, v1), (u1, v2)];
    int k = ((rotation / 90) % 4 + 4) % 4;
    var outp = new (double, double)[4];
    for (int i = 0; i < 4; i++)
      outp[i] = baseCorners[(i + k) % 4];
    return outp;
  }

  // Barycentric weights of grid point (px, py) with respect to triangle a, b, c.
  private static bool Barycentric(
    double px, double py,
    (double X, double Y) a, (double X, double Y) b, (double X, double Y) c,
    out double w0, out double w1, out double w2
  ) {
    double denom = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
    if (denom == 0) {
      w0 = w1 = w2 = 0;
      return false;
    }
    w0 = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / denom;
    w1 = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / denom;
    w2 = 1.0 - w0 - w1;
    return true;
  }

  // One screen-space triangle: (col, row, z_view) per vertex.
  private readonly record struct ScreenPoint(double Col, double Row, double Z);

  private static void RasterizeTriangle(
    double[,,] colorBuf,
    double[,] zBuf,
    ScreenPoint p0, ScreenPoint p1, ScreenPoint p2,
    (double U, double V) uv0, (double U, double V) uv1, (double U, double V) uv2,
    double shade,
    byte[,,] texture,
    int width, int height
  ) {
    double xmin = Math.Min(p0.Col, Math.Min(p1.Col, p2.Col));
    double xmax = Math.Max(p0.Col, Math.Max(p1.Col, p2.Col));
    double ymin = Math.Min(p0.Row, Math.Min(p1.Row, p2.Row));
    double ymax = Math.Max(p0.Row, Math.Max(p1.Row, p2.Row));
    int x0 = Math.Max(0, (int)Math.Floor(xmin));
    int x1 = Math.Min(width - 1, (int)Math.Ceiling(xmax));
    int y0 = Math.Max(0, (int)Math.Floor(ymin));
    int y1 = Math.Min(height - 1, (int)Math.Ceiling(ymax));
    if (x0 > x1 || y0 > y1)
      return;

    int th = texture.GetLength(0);
    int tw = texture.GetLength(1);
    (double X, double Y) a = (p0.Col, p0.Row);
    (double X, double Y) b = (p1.Col, p1.Row);
    (double X, double Y) c = (p2.Col, p2.Row);

    for (int row = y0; row <= y1; row++) {
      double py = row + 0.5;
      for (int col = x0; col <= x1; col++) {
        double px = col + 0.5;
        if (!Barycentric(px, py, a, b, c, out double w0, out double w1, out double w2))
          continue;
        if (w0 < 0 || w1 < 0 || w2 < 0)
          continue;
        double z = w0 * p0.Z + w1 * p1.Z + w2 * p2.Z;
        if (z <= zBuf[row, col])
          continue;
        double u = w0 * uv0.U + w1 * uv1.U + w2 * uv2.U;
        double v = w0 * uv0.V + w1 * uv1.V + w2 * uv2.V;
        double texU = u * (tw / 16.0);
        double texV = v * (th / 16.0);
        int texCol = Mod((int)Math.Floor(texU), tw);
        int texRow = Mod((int)Math.Floor(texV), th);
        byte alpha = texture[texRow, texCol, 3];
        if (alpha < 128)
          continue;
        zBuf[row, col] = z;
        colorBuf[row, col, 0] = Math.Clamp(texture[texRow, texCol, 0] * shade, 0, 255);
        colorBuf[row, col, 1] = Math.Clamp(texture[texRow, texCol, 1] * shade, 0, 255);
        colorBuf[row, col, 2] = Math.Clamp(texture[texRow, texCol, 2] * shade, 0, 255);
      }
    }
  }

  private static int Mod(int a, int m) => ((a % m) + m) % m;

  // Draws a straight screen-space segment, optionally requiring it be no farther than the
  // z-buffer. With `mul` the segment darkens the pixels it crosses instead of painting `color`.
  private static void DrawLine(
    double[,,] colorBuf,
    double[,] zBuf,
    ScreenPoint p0, ScreenPoint p1,
    SKColor? color,
    int width, int height,
    bool zTest,
    double? mul = null
  ) {
    int steps = Math.Max(2, (int)Math.Ceiling(Math.Max(Math.Abs(p1.Col - p0.Col), Math.Abs(p1.Row - p0.Row))) + 1);
    // Collected, then applied once per distinct pixel: the reference reads/writes the whole line
    // as one gather-scatter over its (possibly repeating, for a short line with many steps) pixel
    // list, so a pixel the line crosses twice darkens once, not mul-squared - an in-place multiply
    // inside this loop would darken it a second time.
    var pixels = new HashSet<(int Row, int Col)>();
    for (int i = 0; i < steps; i++) {
      double t = steps == 1 ? 0 : (double)i / (steps - 1);
      double x = p0.Col + (p1.Col - p0.Col) * t;
      double y = p0.Row + (p1.Row - p0.Row) * t;
      double z = p0.Z + (p1.Z - p0.Z) * t;
      // Math.Round defaults to round-half-to-even.
      int col = (int)Math.Round(x);
      int row = (int)Math.Round(y);
      if (col < 0 || col >= width || row < 0 || row >= height)
        continue;
      if (zTest && z < zBuf[row, col] - 1e-6)
        continue;
      pixels.Add((row, col));
    }
    foreach ((int row, int col) in pixels) {
      if (mul is { } m) {
        colorBuf[row, col, 0] *= m;
        colorBuf[row, col, 1] *= m;
        colorBuf[row, col, 2] *= m;
      } else if (color is { } c) {
        colorBuf[row, col, 0] = c.Red;
        colorBuf[row, col, 1] = c.Green;
        colorBuf[row, col, 2] = c.Blue;
      }
    }
  }

  /// <summary>
  /// Where <see cref="Render"/> lays a world point on the canvas it produces: the view-space
  /// extents it fitted the shape into, at <see cref="Ppu"/> pixels per world unit.
  /// </summary>
  public readonly record struct Projection(double XMin, double YMax, int Ppu, View View) {
    /// <summary>The (column, row) a world point falls on, in pixels from the canvas's top left.
    /// Points outside the rendered shape project outside the canvas.</summary>
    public (double Col, double Row) Screen(double x, double y, double z) {
      (double vx, double vy, double _) = (Mat3.RotateX(View.Pitch) * Mat3.RotateY(View.Yaw)).Mul(x, y, z);
      return ((vx - XMin) * Ppu, (YMax - vy) * Ppu);
    }
  }

  /// <summary>
  /// The mapping <see cref="Render"/> draws <paramref name="shape"/> with for the same arguments -
  /// for a caller annotating the canvas afterwards. A shape with no drawable face projects about
  /// the origin.
  /// </summary>
  public static Projection Project(
    LoadedShape shape,
    View view,
    int ppu = 24,
    int margin = 2,
    IReadOnlySet<string>? only = null,
    IReadOnlyDictionary<string, Pose>? poses = null
  ) {
    Mat3 viewRot = Mat3.RotateX(view.Pitch) * Mat3.RotateY(view.Yaw);
    double xmin = double.PositiveInfinity, ymax = double.NegativeInfinity;
    foreach ((double X, double Y, double Z) p in ViewPoints(shape, viewRot, only, poses)) {
      xmin = Math.Min(xmin, p.X);
      ymax = Math.Max(ymax, p.Y);
    }
    if (double.IsInfinity(xmin))
      return new Projection(0, 0, ppu, view);
    return new Projection(xmin - margin, ymax + margin, ppu, view);
  }

  // Every face corner of every drawn leaf, in view space - the points both the canvas extents and
  // the rasterizer's own screen positions come from.
  private static IEnumerable<(double X, double Y, double Z)> ViewPoints(
    LoadedShape shape,
    Mat3 viewRot,
    IReadOnlySet<string>? only,
    IReadOnlyDictionary<string, Pose>? poses
  ) {
    Dictionary<string, Mat4d> mats = WorldMatricesD(shape, poses);
    foreach (Node el in shape.Leaves()) {
      if (only != null && !only.Any(p => el.Path.StartsWith(p, StringComparison.Ordinal)))
        continue;
      foreach (QuadD q in FaceQuadsD(el, mats[el.Path]))
        foreach ((double X, double Y, double Z) p in q.Points)
          yield return viewRot.Mul(p.X, p.Y, p.Z);
    }
  }

  /// <summary>
  /// Renders <paramref name="shape"/>'s leaves (or only those under a path in
  /// <paramref name="only"/>) from <paramref name="view"/>, <paramref name="ppu"/> pixels per
  /// world unit, textured with <paramref name="textures"/> (resolved from the shape's own path
  /// when null), posed by <paramref name="poses"/> when given, with back faces culled unless
  /// <paramref name="cull"/> is false, a floor grid unless <paramref name="grid"/> is false, face
  /// outlines unless <paramref name="edges"/> is false, and every path in
  /// <paramref name="highlight"/> outlined in <see cref="HighlightColor"/> regardless of depth.
  /// </summary>
  public static SKBitmap Render(
    LoadedShape shape,
    View view,
    int ppu = 24,
    IReadOnlyDictionary<string, Pose>? poses = null,
    bool cull = true,
    bool grid = true,
    IReadOnlySet<string>? highlight = null,
    IReadOnlySet<string>? only = null,
    TextureSet? textures = null,
    int margin = 2,
    bool edges = true
  ) {
    textures ??= TextureSet.ForShape(shape, TextureRoots.Build(null, null, shape.Path));
    Dictionary<string, Mat4d> mats = WorldMatricesD(shape, poses);
    List<Node> leaves =
      [.. shape.Leaves().Where(el => only == null || only.Any(p => el.Path.StartsWith(p, StringComparison.Ordinal)))];

    Mat3 viewRot = Mat3.RotateX(view.Pitch) * Mat3.RotateY(view.Yaw);

    var quads = new List<(QuadD Quad, (double X, double Y, double Z)[] PtsView, (double X, double Y, double Z) NormalView)>();
    foreach (Node el in leaves) {
      Mat4d m = mats[el.Path];
      foreach (QuadD q in FaceQuadsD(el, m)) {
        var ptsView = new (double, double, double)[4];
        for (int i = 0; i < 4; i++)
          ptsView[i] = viewRot.Mul(q.Points[i].X, q.Points[i].Y, q.Points[i].Z);
        (double, double, double) normalView = viewRot.Mul(q.Normal.X, q.Normal.Y, q.Normal.Z);
        quads.Add((q, ptsView, normalView));
      }
    }

    if (quads.Count == 0) {
      var empty = new SKBitmap(16, 16, SKColorType.Rgba8888, SKAlphaType.Unpremul);
      using (var canvas = new SKCanvas(empty))
        canvas.Clear(Background);
      return empty;
    }

    // The canvas's own origin comes from Project, so a caller annotating the result afterwards
    // measures with the very numbers this render laid the shape out on.
    Projection projection = Project(shape, view, ppu, margin, only, poses);
    double xmin = projection.XMin, ymax = projection.YMax;
    double xmax = quads.SelectMany(q => q.PtsView).Max(p => p.Item1) + margin;
    double ymin = quads.SelectMany(q => q.PtsView).Min(p => p.Item2) - margin;

    int width = Math.Max(1, (int)Math.Ceiling((xmax - xmin) * ppu));
    int height = Math.Max(1, (int)Math.Ceiling((ymax - ymin) * ppu));

    ScreenPoint ToScreen((double X, double Y, double Z) pv) =>
      new((pv.X - xmin) * ppu, (ymax - pv.Y) * ppu, pv.Z);

    var colorBuf = new double[height, width, 3];
    for (int y = 0; y < height; y++)
      for (int x = 0; x < width; x++) {
        colorBuf[y, x, 0] = Background.Red;
        colorBuf[y, x, 1] = Background.Green;
        colorBuf[y, x, 2] = Background.Blue;
      }
    var zBuf = new double[height, width];
    for (int y = 0; y < height; y++)
      for (int x = 0; x < width; x++)
        zBuf[y, x] = double.NegativeInfinity;

    var highlightQuads = new List<(QuadD Quad, ScreenPoint[] Screen)>();
    var drawn = new List<ScreenPoint[]>();
    foreach ((QuadD q, (double, double, double)[] ptsView, (double, double, double) normalView) in quads) {
      if (highlight != null && highlight.Contains(q.Path))
        highlightQuads.Add((q, [.. ptsView.Select(ToScreen)]));
      if (cull && normalView.Item3 <= 0)
        continue;
      ScreenPoint[] screen = [.. ptsView.Select(ToScreen)];
      drawn.Add(screen);
      byte[,,] tex = textures.Get(q.Texture);
      (double U, double V)[] uvCorners = RotateUvCorners(q.Uv, q.Rotation);
      double shade = Shading(q.Normal);
      foreach ((int a, int b, int c) in new[] { (0, 1, 2), (0, 2, 3) })
        RasterizeTriangle(
          colorBuf, zBuf,
          screen[a], screen[b], screen[c],
          uvCorners[a], uvCorners[b], uvCorners[c],
          shade, tex, width, height
        );
    }

    if (edges)
      // Face outlines darken the surface they lie on, so two parts of one texture keep their
      // silhouettes; the small depth bias keeps an edge on its own face instead of losing to it.
      foreach (ScreenPoint[] screen in drawn)
        for (int i = 0; i < 4; i++) {
          ScreenPoint a = screen[i] with { Z = screen[i].Z + EdgeBias };
          ScreenPoint b = screen[(i + 1) % 4] with { Z = screen[(i + 1) % 4].Z + EdgeBias };
          DrawLine(colorBuf, zBuf, a, b, null, width, height, zTest: true, mul: EdgeMul);
        }

    if (grid) {
      Dictionary<string, ((double X, double Y, double Z) Lo, (double X, double Y, double Z) Hi)> boxes =
        ElementBoxesD(shape, mats);
      if (boxes.Count > 0) {
        (double X, double Y, double Z) lo = boxes.Values
          .Select(b => b.Lo)
          .Aggregate((a, b) => (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)));
        (double X, double Y, double Z) hi = boxes.Values
          .Select(b => b.Hi)
          .Aggregate((a, b) => (Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
        int x0 = (int)Math.Floor(lo.X), x1 = (int)Math.Ceiling(hi.X);
        int z0 = (int)Math.Floor(lo.Z), z1 = (int)Math.Ceiling(hi.Z);
        for (int x = x0; x <= x1; x++) {
          ScreenPoint a = ToScreen(viewRot.Mul(x, 0.0, z0));
          ScreenPoint b = ToScreen(viewRot.Mul(x, 0.0, z1));
          SKColor color = x % GridCell == 0 ? GridDark : GridLight;
          DrawLine(colorBuf, zBuf, a, b, color, width, height, zTest: true);
        }
        for (int z = z0; z <= z1; z++) {
          ScreenPoint a = ToScreen(viewRot.Mul(x0, 0.0, z));
          ScreenPoint b = ToScreen(viewRot.Mul(x1, 0.0, z));
          SKColor color = z % GridCell == 0 ? GridDark : GridLight;
          DrawLine(colorBuf, zBuf, a, b, color, width, height, zTest: true);
        }
      }
    }

    foreach ((QuadD _, ScreenPoint[] screen) in highlightQuads)
      for (int i = 0; i < 4; i++)
        DrawLine(colorBuf, zBuf, screen[i], screen[(i + 1) % 4], HighlightColor, width, height, zTest: false);

    var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
    for (int y = 0; y < height; y++)
      for (int x = 0; x < width; x++)
        bmp.SetPixel(
          x, y,
          new SKColor(
            (byte)Math.Clamp((int)colorBuf[y, x, 0], 0, 255),
            (byte)Math.Clamp((int)colorBuf[y, x, 1], 0, 255),
            (byte)Math.Clamp((int)colorBuf[y, x, 2], 0, 255)
          )
        );
    return bmp;
  }

  // World AABB per leaf element path, double precision.
  private static Dictionary<string, ((double X, double Y, double Z) Lo, (double X, double Y, double Z) Hi)> ElementBoxesD(
    LoadedShape shape, Dictionary<string, Mat4d> mats
  ) {
    var outp = new Dictionary<string, ((double, double, double), (double, double, double))>();
    foreach (Node el in shape.Leaves()) {
      Mat4d m = mats[el.Path];
      (double X, double Y, double Z) size = (el.Size.X, el.Size.Y, el.Size.Z);
      (double X, double Y, double Z) lo = (double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
      (double X, double Y, double Z) hi = (double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
      for (int x = 0; x <= 1; x++)
        for (int y = 0; y <= 1; y++)
          for (int z = 0; z <= 1; z++) {
            (double X, double Y, double Z) p = m.Mul(x * size.X, y * size.Y, z * size.Z);
            lo = (Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z));
            hi = (Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z));
          }
      outp[el.Path] = (lo, hi);
    }
    return outp;
  }

  /// <summary>Places <paramref name="images"/> side by side, each labelled top-left with the
  /// matching entry of <paramref name="labels"/>.</summary>
  public static SKBitmap Strip(IReadOnlyList<SKBitmap> images, IReadOnlyList<string> labels) {
    int height = images.Max(im => im.Height);
    int width = images.Sum(im => im.Width);
    var outBmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
    using var canvas = new SKCanvas(outBmp);
    canvas.Clear(Background);
    using var paint = new SKPaint { Color = SKColors.Black };
    using var font = new SKFont { Size = 12 };
    int x = 0;
    for (int i = 0; i < images.Count; i++) {
      canvas.DrawBitmap(images[i], x, 0);
      canvas.DrawText(labels[i], x + 2, 12, font, paint);
      x += images[i].Width;
    }
    return outBmp;
  }
}
