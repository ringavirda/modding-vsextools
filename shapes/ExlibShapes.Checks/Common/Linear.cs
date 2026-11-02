using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>Vector helpers over Core's <see cref="Vec3"/>.</summary>
internal static class V {
  public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

  public static Vec3 Cross(Vec3 a, Vec3 b) =>
    new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

  public static double Norm(Vec3 a) => Math.Sqrt(Dot(a, a));

  public static Vec3 Unit(Vec3 a) => a * (1 / Norm(a));

  public static Vec3 Mean(IReadOnlyCollection<Vec3> pts) {
    Vec3 s = Vec3.Zero;
    foreach (Vec3 p in pts)
      s += p;
    return s * (1.0 / pts.Count);
  }

  /// <summary>Python's <c>%</c> for a positive modulus: the result has the modulus' sign.</summary>
  public static double Mod(double a, double m) => ((a % m) + m) % m;

  public static double Median(IEnumerable<double> values) {
    double[] s = [.. values.Order()];
    return s.Length % 2 == 1
      ? s[s.Length / 2]
      : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
  }

  /// <summary>An angle in degrees folded into [-180, 180).</summary>
  public static double Wrap(double deg) => Mod(deg + 180.0, 360.0) - 180.0;
}

/// <summary>A 3x3 matrix, row-major.</summary>
internal readonly struct M3 {
  private readonly double[] _m;

  public M3(double[] m) => _m = m;

  public double this[int r, int c] => _m[r * 3 + c];

  public Vec3 Col(int c) => new(_m[c], _m[3 + c], _m[6 + c]);

  public double Trace => _m[0] + _m[4] + _m[8];

  public M3 T() =>
    new([_m[0], _m[3], _m[6], _m[1], _m[4], _m[7], _m[2], _m[5], _m[8]]);

  public static M3 operator *(M3 a, M3 b) {
    var r = new double[9];
    for (int i = 0; i < 3; i++)
      for (int j = 0; j < 3; j++)
        for (int k = 0; k < 3; k++)
          r[i * 3 + j] += a[i, k] * b[k, j];
    return new M3(r);
  }

  public Vec3 Mul(Vec3 v) =>
    new(
      _m[0] * v.X + _m[1] * v.Y + _m[2] * v.Z,
      _m[3] * v.X + _m[4] * v.Y + _m[5] * v.Z,
      _m[6] * v.X + _m[7] * v.Y + _m[8] * v.Z
    );

  public double Frobenius(M3 other) {
    double s = 0;
    for (int i = 0; i < 9; i++)
      s += (_m[i] - other._m[i]) * (_m[i] - other._m[i]);
    return Math.Sqrt(s);
  }

  public bool IsIdentity(double atol, double rtol) {
    for (int i = 0; i < 3; i++)
      for (int j = 0; j < 3; j++) {
        double want = i == j ? 1 : 0;
        if (Math.Abs(this[i, j] - want) > atol + rtol * Math.Abs(want))
          return false;
      }
    return true;
  }

  /// <summary>The inverse; throws when singular.</summary>
  public M3 Inverse() {
    double a = _m[0],
      b = _m[1],
      c = _m[2],
      d = _m[3],
      e = _m[4],
      f = _m[5],
      g = _m[6],
      h = _m[7],
      i = _m[8];
    double co0 = e * i - f * h,
      co1 = f * g - d * i,
      co2 = d * h - e * g;
    double det = a * co0 + b * co1 + c * co2;
    if (Math.Abs(det) < 1e-300)
      throw new InvalidOperationException("singular matrix");
    double k = 1 / det;
    return new M3([
      co0 * k,
      (c * h - b * i) * k,
      (b * f - c * e) * k,
      co1 * k,
      (a * i - c * g) * k,
      (c * d - a * f) * k,
      co2 * k,
      (b * g - a * h) * k,
      (a * e - b * d) * k,
    ]);
  }

  /// <summary>Eigenpairs of a symmetric matrix by Jacobi rotation, largest eigenvalue first;
  /// the vectors are the matrix' columns.</summary>
  public (double[] Values, M3 Vectors) SymmetricEigen() {
    var a = (double[])_m.Clone();
    var v = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
    for (int sweep = 0; sweep < 64; sweep++) {
      double off = a[1] * a[1] + a[2] * a[2] + a[5] * a[5];
      if (off < 1e-30)
        break;
      foreach ((int p, int q) in new[] { (0, 1), (0, 2), (1, 2) }) {
        double apq = a[p * 3 + q];
        if (Math.Abs(apq) < 1e-300)
          continue;
        double theta = (a[q * 3 + q] - a[p * 3 + p]) / (2 * apq);
        double t =
          Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
        if (theta == 0)
          t = 1;
        double c = 1 / Math.Sqrt(t * t + 1),
          s = t * c;
        for (int k = 0; k < 3; k++) {
          double akp = a[k * 3 + p],
            akq = a[k * 3 + q];
          a[k * 3 + p] = c * akp - s * akq;
          a[k * 3 + q] = s * akp + c * akq;
        }
        for (int k = 0; k < 3; k++) {
          double apk = a[p * 3 + k],
            aqk = a[q * 3 + k];
          a[p * 3 + k] = c * apk - s * aqk;
          a[q * 3 + k] = s * apk + c * aqk;
        }
        for (int k = 0; k < 3; k++) {
          double vkp = v[k * 3 + p],
            vkq = v[k * 3 + q];
          v[k * 3 + p] = c * vkp - s * vkq;
          v[k * 3 + q] = s * vkp + c * vkq;
        }
      }
    }
    int[] order = [.. new[] { 0, 1, 2 }.OrderByDescending(i => a[i * 3 + i])];
    var vals = order.Select(i => a[i * 3 + i]).ToArray();
    var vecs = new double[9];
    for (int j = 0; j < 3; j++)
      for (int k = 0; k < 3; k++)
        vecs[k * 3 + j] = v[k * 3 + order[j]];
    return (vals, new M3(vecs));
  }

  /// <summary>Solves <c>M x = rhs</c>; null when singular.</summary>
  public Vec3? Solve(Vec3 rhs) {
    double det =
      _m[0] * (_m[4] * _m[8] - _m[5] * _m[7])
      - _m[1] * (_m[3] * _m[8] - _m[5] * _m[6])
      + _m[2] * (_m[3] * _m[7] - _m[4] * _m[6]);
    if (Math.Abs(det) < 1e-12)
      return null;
    return Inverse().Mul(rhs);
  }
}

/// <summary>An affine map: a linear part and a translation, from a world matrix.</summary>
internal sealed class Aff {
  public M3 L { get; }
  public Vec3 T { get; }

  public Aff(Mat4d m) {
    L = new M3([
      m[0, 0],
      m[0, 1],
      m[0, 2],
      m[1, 0],
      m[1, 1],
      m[1, 2],
      m[2, 0],
      m[2, 1],
      m[2, 2],
    ]);
    T = new Vec3(m[0, 3], m[1, 3], m[2, 3]);
  }

  private Aff(M3 l, Vec3 t) {
    L = l;
    T = t;
  }

  public Vec3 Apply(Vec3 p) => L.Mul(p) + T;

  public Aff Inverse() {
    M3 inv = L.Inverse();
    return new Aff(inv, inv.Mul(T) * -1);
  }
}
