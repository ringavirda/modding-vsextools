using System;

namespace ExpandedLib.Shapes.Core;

/// <summary>A point or direction in shape units (1/16 block); angles elsewhere are degrees.</summary>
public readonly record struct Vec3(double X, double Y, double Z) {
  /// <summary>0, 0, 0.</summary>
  public static Vec3 Zero => new(0, 0, 0);

  /// <summary>1, 1, 1.</summary>
  public static Vec3 One => new(1, 1, 1);

  /// <summary>The component <paramref name="i"/>: 0 is x, 1 is y, anything else z.</summary>
  public double this[int i] =>
    i switch {
      0 => X,
      1 => Y,
      _ => Z,
    };

  /// <summary>Component-wise sum.</summary>
  public static Vec3 operator +(Vec3 a, Vec3 b) =>
    new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

  /// <summary>Component-wise difference.</summary>
  public static Vec3 operator -(Vec3 a, Vec3 b) =>
    new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

  /// <summary>Component-wise product.</summary>
  public static Vec3 operator *(Vec3 a, Vec3 b) =>
    new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

  /// <summary>Every component multiplied by <paramref name="k"/>.</summary>
  public static Vec3 operator *(Vec3 a, double k) =>
    new(a.X * k, a.Y * k, a.Z * k);

  /// <summary>The vector of the first three entries of <paramref name="a"/>, or
  /// <paramref name="fallback"/> when <paramref name="a"/> is null or shorter than three.</summary>
  public static Vec3 From(double[]? a, Vec3 fallback) =>
    a is { Length: >= 3 } ? new Vec3(a[0], a[1], a[2]) : fallback;

  /// <summary>The components as a three-entry array.</summary>
  public double[] ToArray() => [X, Y, Z];
}

/// <summary>
/// A 4x4 double-precision transform, stored row-major and applied to column vectors
/// (<c>m @ v</c>) with the translation in the fourth column; <c>a * b</c> applies <c>b</c> first.
/// </summary>
public readonly struct Mat4d {
  private readonly double[] _m;

  private Mat4d(double[] m) => _m = m;

  /// <summary>The identity.</summary>
  public static Mat4d Identity =>
    new([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);

  /// <summary>The entry at row <paramref name="r"/>, column <paramref name="c"/> (0-3).</summary>
  public double this[int r, int c] => _m[r * 4 + c];

  /// <summary>A translation by (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>).</summary>
  public static Mat4d Translate(double x, double y, double z) =>
    new([1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1]);

  /// <summary>A translation by <paramref name="v"/>.</summary>
  public static Mat4d Translate(Vec3 v) => Translate(v.X, v.Y, v.Z);

  /// <summary>A per-axis scale about the origin.</summary>
  public static Mat4d Scale(Vec3 s) =>
    new([s.X, 0, 0, 0, 0, s.Y, 0, 0, 0, 0, s.Z, 0, 0, 0, 0, 1]);

  /// <summary>A per-axis scale about the origin.</summary>
  public static Mat4d Scale(double x, double y, double z) =>
    Scale(new Vec3(x, y, z));

  /// <summary>The game's <c>Mat4f.RotateByXYZ</c>: <c>Rx . Ry . Rz</c> for angles in degrees.</summary>
  public static Mat4d RotateByXyz(double rx, double ry, double rz) {
    double sx = Math.Sin(rx * Math.PI / 180),
      cx = Math.Cos(rx * Math.PI / 180);
    double sy = Math.Sin(ry * Math.PI / 180),
      cy = Math.Cos(ry * Math.PI / 180);
    double sz = Math.Sin(rz * Math.PI / 180),
      cz = Math.Cos(rz * Math.PI / 180);
    double[] m = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
    m[0] = cy * cz;
    m[1] = -cy * sz;
    m[2] = sy;
    m[4] = sx * sy * cz + cx * sz;
    m[5] = cx * cz - sx * sy * sz;
    m[6] = -sx * cy;
    m[8] = -cx * sy * cz + sx * sz;
    m[9] = sx * cz + cx * sy * sz;
    m[10] = cx * cy;
    return new Mat4d(m);
  }

  /// <summary>The product: <paramref name="b"/> applied first, then <paramref name="a"/>.</summary>
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

  /// <summary>The point <paramref name="p"/> transformed, translation included.</summary>
  public Vec3 Mul(Vec3 p) =>
    new(
      _m[0] * p.X + _m[1] * p.Y + _m[2] * p.Z + _m[3],
      _m[4] * p.X + _m[5] * p.Y + _m[6] * p.Z + _m[7],
      _m[8] * p.X + _m[9] * p.Y + _m[10] * p.Z + _m[11]
    );

  /// <summary>The point (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) transformed, translation included.</summary>
  public (double X, double Y, double Z) Mul(double x, double y, double z) {
    Vec3 r = Mul(new Vec3(x, y, z));
    return (r.X, r.Y, r.Z);
  }

  /// <summary>The point <paramref name="p"/> transformed, translation included.</summary>
  public (double X, double Y, double Z) Mul((double X, double Y, double Z) p) =>
    Mul(p.X, p.Y, p.Z);

  /// <summary>The direction (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) transformed by the upper-left 3x3 alone.</summary>
  public (double X, double Y, double Z) MulLinear(double x, double y, double z) {
    Vec3 r = MulLinear(new Vec3(x, y, z));
    return (r.X, r.Y, r.Z);
  }

  /// <summary>The direction <paramref name="d"/> transformed by the upper-left 3x3 alone.</summary>
  public Vec3 MulLinear(Vec3 d) =>
    new(
      _m[0] * d.X + _m[1] * d.Y + _m[2] * d.Z,
      _m[4] * d.X + _m[5] * d.Y + _m[6] * d.Z,
      _m[8] * d.X + _m[9] * d.Y + _m[10] * d.Z
    );
}
