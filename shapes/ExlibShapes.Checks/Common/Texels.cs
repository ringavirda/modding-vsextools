using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>Uv and texel arithmetic on world quads, and the face tables the checks share.</summary>
internal static class Texels {
  public static readonly string[] FaceNames =
  [
    "north",
    "east",
    "south",
    "west",
    "up",
    "down",
  ];

  public static (int U, int V) UvAxes(string face) =>
    face switch {
      "north" or "south" => (0, 1),
      "east" or "west" => (2, 1),
      _ => (0, 2),
    };

  private static readonly Dictionary<string, (int X, int Y, int Z)[]> Corner =
    new() {
      ["north"] = [(0, 0, 0), (0, 1, 0), (1, 1, 0), (1, 0, 0)],
      ["east"] = [(1, 0, 0), (1, 1, 0), (1, 1, 1), (1, 0, 1)],
      ["south"] = [(1, 0, 1), (1, 1, 1), (0, 1, 1), (0, 0, 1)],
      ["west"] = [(0, 0, 1), (0, 1, 1), (0, 1, 0), (0, 0, 0)],
      ["up"] = [(1, 1, 1), (1, 1, 0), (0, 1, 0), (0, 1, 1)],
      ["down"] = [(0, 0, 1), (0, 0, 0), (1, 0, 0), (1, 0, 1)],
    };

  /// <summary>The corner selector (0 = near, 1 = far on each axis) of vertex <paramref name="i"/> of a face.</summary>
  public static (int X, int Y, int Z) FaceCorner(string face, int i) =>
    Corner[face][i];

  /// <summary>The [u, v] texel at each vertex of a face in game order for the rect
  /// <paramref name="uv"/> = [u1, v1, u2, v2] turned <paramref name="rotation"/> degrees.</summary>
  public static (double U, double V)[] Vertex(double[] uv, int rotation) {
    (double, double)[] corners =
    [
      (uv[2], uv[3]),
      (uv[2], uv[1]),
      (uv[0], uv[1]),
      (uv[0], uv[3]),
    ];
    int s =
      (((int)Math.Round(rotation / 90.0, MidpointRounding.ToEven) % 4) + 4) % 4;
    return [.. Enumerable.Range(0, 4).Select(i => corners[(s + i) % 4])];
  }

  /// <summary>The [u, v] texel of <paramref name="q"/> at world point <paramref name="p"/>, extended past the edges.</summary>
  public static (double U, double V) At(Quad q, Vec3 p) {
    var t = Vertex(q.Uv, q.Rotation);
    Vec3 e1 = q.Points[1] - q.Points[0],
      e2 = q.Points[3] - q.Points[0],
      r = p - q.Points[0];
    double a = V.Dot(r, e1) / V.Dot(e1, e1),
      b = V.Dot(r, e2) / V.Dot(e2, e2);
    return (
      t[0].U + a * (t[1].U - t[0].U) + b * (t[3].U - t[0].U),
      t[0].V + a * (t[1].V - t[0].V) + b * (t[3].V - t[0].V)
    );
  }

  /// <summary>The world gradient of the quad's texel: row 0 the direction u grows, row 1 v, each as
  /// texels per voxel.</summary>
  public static Vec3[] Gradient(Quad q) {
    var t = Vertex(q.Uv, q.Rotation);
    Vec3 e1 = q.Points[1] - q.Points[0],
      e2 = q.Points[3] - q.Points[0];
    Vec3 g1 = e1 * (1 / V.Dot(e1, e1)),
      g2 = e2 * (1 / V.Dot(e2, e2));
    return
    [
      g1 * (t[1].U - t[0].U) + g2 * (t[3].U - t[0].U),
      g1 * (t[1].V - t[0].V) + g2 * (t[3].V - t[0].V),
    ];
  }

  /// <summary>The world points where quads <paramref name="a"/> and <paramref name="b"/> meet: the ends,
  /// along the line their planes cross, of the stretch both reach; for planes that do not cross, the
  /// midpoints of their nearest pair of corners and of the nearest pair sharing neither corner.</summary>
  public static List<Vec3> Seam(Quad a, Quad b) {
    Vec3 e = V.Cross(a.Normal, b.Normal);
    if (V.Norm(e) > 1e-3) {
      e = V.Unit(e);
      var rows = new M3([
        a.Normal.X,
        a.Normal.Y,
        a.Normal.Z,
        b.Normal.X,
        b.Normal.Y,
        b.Normal.Z,
        e.X,
        e.Y,
        e.Z,
      ]);
      Vec3 x0 =
        rows.Solve(
          new Vec3(
            V.Dot(a.Normal, a.Points[0]),
            V.Dot(b.Normal, b.Points[0]),
            0
          )
        ) ?? Vec3.Zero;
      double[] ra = [.. a.Points.Select(p => V.Dot(p, e))];
      double[] rb = [.. b.Points.Select(p => V.Dot(p, e))];
      double[] ends =
      [
        .. new[]
        {
          Math.Max(ra.Min(), rb.Min()),
          Math.Min(ra.Max(), rb.Max()),
        }.Order(),
      ];
      return [.. ends.Select(t => x0 + e * t)];
    }
    var pairs = (
      from i in Enumerable.Range(0, 4)
      from j in Enumerable.Range(0, 4)
      select (I: i, J: j, D: V.Norm(a.Points[i] - b.Points[j]))
    )
      .OrderBy(x => x.D)
      .ToList();
    (int i0, int j0, _) = pairs[0];
    var outp = new List<Vec3> { (a.Points[i0] + b.Points[j0]) * 0.5 };
    foreach ((int i2, int j2, _) in pairs.Skip(1))
      if (i2 != i0 && j2 != j0) {
        outp.Add((a.Points[i2] + b.Points[j2]) * 0.5);
        break;
      }
    return outp;
  }

  /// <summary>The uv rect that maps a tiling texture onto <paramref name="face"/> of an unturned box
  /// from <paramref name="frm"/> to <paramref name="to"/> (block coordinates) by the face's place in the block.</summary>
  /// <exception cref="ArgumentException">The face crosses a multiple of <paramref name="tile"/> on one of its axes.</exception>
  public static double[] PositionUv(
    string face,
    Vec3 frm,
    Vec3 to,
    double tile = 16.0
  ) {
    (int X, int Y, int Z) tl = Corner[face][2];
    (int ua, int va) = UvAxes(face);
    var outp = new List<(double O, double S)>();
    foreach (int a in new[] { ua, va }) {
      double lo = Math.Min(frm[a], to[a]),
        hi = Math.Max(frm[a], to[a]);
      int tla = a switch {
        0 => tl.X,
        1 => tl.Y,
        _ => tl.Z,
      };
      double o = Math.Round(V.Mod(tla != 0 ? -hi : lo, tile), 6);
      if (o > tile - 1e-6)
        o = 0.0;
      if (o + hi - lo > tile + 1e-6)
        throw new ArgumentException(
          $"{face} face {Fmt.G(lo)}..{Fmt.G(hi)} on axis {"xyz"[a]} crosses a {Fmt.G(tile)} tile"
        );
      outp.Add((o, hi - lo));
    }
    return
    [
      Math.Round(outp[0].O, 4),
      Math.Round(outp[1].O, 4),
      Math.Round(outp[0].O + outp[0].S, 4),
      Math.Round(outp[1].O + outp[1].S, 4),
    ];
  }
}

/// <summary>Number formatting that matches the report lines of the checks being ported.</summary>
internal static class Fmt {
  /// <summary>Six significant digits without trailing zeros.</summary>
  public static string G(double v) =>
    v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);

  public static string List(IEnumerable<double> v) =>
    "[" + string.Join(", ", v.Select(G)) + "]";
}
