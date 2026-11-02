using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Generators;

/// <summary>Step 6: uv rects by face size and texture class, and round runs unrolled.</summary>
public static class Uvs {
  private const string Generator = "castgen";
  private const double Tex = 16;
  private const double Step = 0.5;
  private const double Reach = 0.25;

  /// <summary>The leaves with every enabled face's uv rect the size of its face. A face whose rect already
  /// has that size (turned a quarter, swapped) keeps its uv and rotation; any other takes an origin by its
  /// texture's class: plain a seeded one in 0.5 steps that keeps the rect inside the texture, never 0,0 where
  /// another fits; patterned 0,0; position its place in the block; wood 0,0 with the longer side along u, the
  /// face turned 90 when v was the longer.</summary>
  /// <param name="ctx">The context; its settings give each texture's class and its Rng the origins.</param>
  /// <param name="output">The output name, the second part of every seed.</param>
  /// <param name="leaves">Root-level leaves, no children, scale 1.</param>
  /// <exception cref="SettingsException">A face's texture key is not in the registry.</exception>
  /// <exception cref="CastGenException">A position face crosses a tile.</exception>
  public static List<Leaf> Assign(
    GenContext ctx,
    string output,
    IEnumerable<Leaf> leaves
  ) {
    var outp = new List<Leaf>();
    foreach (Leaf leaf in leaves) {
      Element e = Select.Clone(leaf.Element, false);
      Vec3 size = Engine.Size(e);
      foreach (KeyValuePair<string, Face> kv in e.Faces) {
        Face face = kv.Value;
        if (face.Enabled == false)
          continue;
        (int ua, int va) = Texels.UvAxes(kv.Key);
        double u = size[ua],
          v = size[va];
        if (Matches(face, u, v))
          continue;
        face.Rotation = null;
        string key = (face.Texture ?? "").TrimStart('#');
        switch (ctx.Settings.Texture(key).Class) {
          case TextureClass.Plain:
            Plain(ctx.Rng(Generator, output, leaf.Name, kv.Key), face, u, v);
            break;
          case TextureClass.Patterned:
            face.Uv = [0, 0, u, v];
            break;
          case TextureClass.Position:
            try {
              face.Uv = Texels.PositionUv(
                kv.Key,
                Vec3.From(e.From, Vec3.Zero),
                Vec3.From(e.To, Vec3.Zero)
              );
            } catch (ArgumentException ex) {
              throw new CastGenException($"{leaf.SourcePath}: {ex.Message}");
            }
            break;
          case TextureClass.Wood:
            if (v > u) {
              face.Uv = [0, 0, v, u];
              face.Rotation = 90;
            } else
              face.Uv = [0, 0, u, v];
            break;
        }
      }
      outp.Add(leaf with { Element = e });
    }
    return outp;
  }

  private static bool Matches(Face face, double u, double v) {
    if (face.Uv is not { Length: >= 4 } uv)
      return false;
    double pu = Math.Abs(uv[2] - uv[0]),
      pv = Math.Abs(uv[3] - uv[1]);
    (double wu, double wv) = face.Rotation is 90 or 270 ? (v, u) : (u, v);
    return Math.Abs(pu - wu) < 1e-6 && Math.Abs(pv - wv) < 1e-6;
  }

  private static List<double> Origins(double extent) =>
    [
      .. Enumerable
        .Range(0, Math.Max(1, (int)((Tex - extent) / Step + 1e-6) + 1))
        .Select(i => i * Step),
    ];

  private static void Plain(Random rng, Face face, double u, double v) {
    List<double> us = Origins(u),
      vs = Origins(v);
    double u0,
      v0;
    do {
      u0 = us[rng.Next(us.Count)];
      v0 = vs[rng.Next(vs.Count)];
    } while (u0 == 0 && v0 == 0 && !(us.Count == 1 && vs.Count == 1));
    face.Uv =
    [
      Math.Round(u0, 4),
      Math.Round(v0, 4),
      Math.Round(u0 + u, 4),
      Math.Round(v0 + v, 4),
    ];
  }

  /// <summary>The leaves with each round run of plain and wood faces mapped so one texture runs on round it:
  /// along the axis a texel is its place in the world, round the axis a rect starts where the previous one ends,
  /// the first at a seeded origin, and a wood face's grain runs along the axis. Runs are taken largest first. A run
  /// whose faces are not all plain or wood, whose faces cannot all run along the axis, whose span passes the
  /// texture or whose result fails the directed uvwrap verdict keeps its uv. Patterned and position faces keep theirs.</summary>
  /// <param name="ctx">The context; its settings give each texture's class and its Rng the origins.</param>
  /// <param name="output">The output name, the second part of every seed.</param>
  /// <param name="leaves">Root-level leaves whose faces all carry a uv, as <see cref="Assign"/> leaves them.</param>
  public static List<Leaf> Unroll(
    GenContext ctx,
    string output,
    IReadOnlyList<Leaf> leaves
  ) {
    var shape = new Shape();
    foreach (Leaf l in leaves) {
      Element e = Select.Clone(l.Element, false);
      e.Name = l.Name;
      shape.Elements.Add(e);
    }
    var tree = new Tree(shape);
    var frames = new Frames(shape);
    Func<string, bool> isWood = k =>
      ctx.Settings.Textures.TryGetValue(k, out TextureEntry? t)
      && t.Class == TextureClass.Wood;
    foreach (
      RoundRun run in RoundRuns
        .Find(tree, frames, nested: false)
        .OrderByDescending(r => r.Faces.Count)
    ) {
      string[] keys = [.. run.Faces.Select(f => f.Quad.Texture).Distinct()];
      if (
        keys.Any(k =>
          !ctx.Settings.Textures.TryGetValue(k, out TextureEntry? t)
          || t.Class is not (TextureClass.Plain or TextureClass.Wood)
        )
      )
        continue;
      Random rng = ctx.Rng(
        Generator,
        output,
        run.Parent + "|" + run.Faces[0].Member.Path,
        "unroll"
      );
      List<(double[] Uv, int Spin)>? rects = Directed(
        run,
        keys.Any(isWood),
        rng
      );
      if (rects == null)
        continue;
      List<RunFace> placed =
      [
        .. run.Faces.Zip(
          rects,
          (f, r) =>
            f with
            {
              Quad = f.Quad with { Uv = r.Uv, Rotation = 90 * r.Spin },
            }
        ),
      ];
      if (!RoundRuns.VerdictDirected(placed, run.Axis, Tex, isWood).Passes)
        continue;
      foreach ((RunFace f, (double[] uv, int spin)) in run.Faces.Zip(rects)) {
        Face face = f
          .Member.Element.Faces.First(kv => kv.Key == f.Quad.Face)
          .Value;
        face.Uv = uv;
        if (spin != 0 || face.Rotation != null)
          face.Rotation = 90 * spin;
      }
    }
    return [.. leaves.Zip(shape.Elements, (l, e) => l with { Element = e })];
  }

  private sealed record Row(Vec3[] Pts, Vec3 Normal, Vec3 W, Vec3 H);

  private static (Vec3 U, Vec3 V) TexelDirs(Row r, int spin) =>
    spin switch {
      0 => (r.W, r.H * -1),
      1 => (r.H * -1, r.W * -1),
      2 => (r.W * -1, r.H),
      _ => (r.H, r.W),
    };

  private static Vec3 Dir(Row r, int spin, int axial) =>
    axial == 0 ? TexelDirs(r, spin).U : TexelDirs(r, spin).V;

  private static int[]? Turns(List<Row> rows, Vec3 d, int axial, double sign) {
    var outp = new int[rows.Count];
    for (int i = 0; i < rows.Count; i++) {
      int best = 0;
      double fit = double.NegativeInfinity;
      for (int s = 0; s < 4; s++) {
        Vec3 v = Dir(rows[i], s, axial);
        double f = V.Dot(v, d * sign) / V.Norm(v);
        if (f > fit) {
          fit = f;
          best = s;
        }
      }
      if (fit < 0.5)
        return null;
      outp[i] = best;
    }
    return outp;
  }

  private static (double Sign, int[] Spins)? Plan(
    List<Row> rows,
    Vec3 d,
    int axial
  ) {
    int[]? first = Turns(rows, d, axial, 1);
    if (first == null)
      return null;
    double sign =
      V.Dot(Dir(rows[0], first[0], 1 - axial), V.Cross(d, rows[0].Normal)) < 0
        ? -1
        : 1;
    int[]? spins = sign == 1 ? first : Turns(rows, d, axial, sign);
    return spins == null ? null : (sign, spins);
  }

  private static double Reached(Vec3[] pts, Vec3 p) {
    Vec3 e1 = pts[1] - pts[0],
      e2 = pts[3] - pts[0];
    double a = V.Dot(p - pts[0], e1) / V.Norm(e1),
      b = V.Dot(p - pts[0], e2) / V.Norm(e2);
    return Math.Sqrt(
      Math.Pow(Math.Max(0, Math.Max(-a, a - V.Norm(e1))), 2)
        + Math.Pow(Math.Max(0, Math.Max(-b, b - V.Norm(e2))), 2)
    );
  }

  private static List<(double[] Uv, int Spin)>? Directed(
    RoundRun run,
    bool wood,
    Random rng
  ) {
    Vec3 d = V.Unit(run.Axis);
    List<Row> rows =
    [
      .. run.Faces.Select(f =>
      {
        Vec3[] p = f.Quad.Points;
        return new Row(p, f.Quad.Normal, p[0] - p[3], p[1] - p[0]);
      }),
    ];
    int n = rows.Count;
    int axial;
    (double Sign, int[] Spins)? plan;
    if (wood) {
      axial = 0;
      plan = Plan(rows, d, 0);
    } else {
      var a = Plan(rows, d, 0);
      var b = Plan(rows, d, 1);
      if (a == null || b == null)
        return null;
      axial =
        b.Value.Spins.Sum(s => s % 2) < a.Value.Spins.Sum(s => s % 2) ? 1 : 0;
      plan = axial == 0 ? a : b;
    }
    if (plan == null)
      return null;
    (double sign, int[] spins) = plan.Value;
    Vec3 ax = d * sign;
    var circ = new Vec3[n];
    var qmin = new double[n];
    var length = new double[n];
    var lo = new double[n];
    var la = new double[n];
    for (int k = 0; k < n; k++) {
      circ[k] = V.Unit(Dir(rows[k], spins[k], 1 - axial));
      double[] c = [.. rows[k].Pts.Select(p => V.Dot(p, circ[k]))];
      qmin[k] = c.Min();
      length[k] = c.Max() - qmin[k];
      double[] a = [.. rows[k].Pts.Select(p => V.Dot(p, ax))];
      lo[k] = a.Min();
      la[k] = a.Max() - lo[k];
    }
    var enter = new Vec3?[n];
    var leave = new Vec3?[n];
    for (int i = 0; i < (n > 2 ? n : n - 1); i++) {
      int j = (i + 1) % n;
      List<Vec3> seam = Texels.Seam(run.Faces[i].Quad, run.Faces[j].Quad);
      Vec3 mid = V.Mean(seam);
      if (
        Reached(rows[i].Pts, mid) <= Reach
        && Reached(rows[j].Pts, mid) <= Reach
      ) {
        leave[i] = mid;
        enter[j] = mid;
      }
    }
    double baseLo = lo.Min();
    double aTotal = Enumerable.Range(0, n).Max(k => lo[k] - baseLo + la[k]);
    double cTotal = length.Sum();
    if (aTotal > Tex + 1e-6 || length.Max() > Tex + 1e-6)
      return null;
    int aSlack = (int)((Tex - aTotal) / Step + 1e-6) + 1;
    int cSlack =
      (int)(
        ((cTotal <= Tex + 1e-6 ? Tex - cTotal : Tex - length[0])) / Step + 1e-6
      ) + 1;
    double a0,
      c0;
    do {
      a0 = rng.Next(aSlack) * Step;
      c0 = rng.Next(cSlack) * Step;
    } while (a0 == 0 && c0 == 0 && !(aSlack == 1 && cSlack == 1));
    var outp = new List<(double[], int)>();
    double exit = 0;
    for (int k = 0; k < n; k++) {
      double off = enter[k] is { } e ? V.Dot(e, circ[k]) - qmin[k] : 0;
      double start = k == 0 ? c0 : exit - off;
      if (k > 0 && (start + length[k] > Tex + 1e-6 || start < -1e-6))
        start = 0;
      exit =
        start + (leave[k] is { } l ? V.Dot(l, circ[k]) - qmin[k] : length[k]);
      double u0 = a0 + lo[k] - baseLo;
      double[] rect =
        axial == 0
          ? [u0, start, u0 + la[k], start + length[k]]
          : [start, u0, start + length[k], u0 + la[k]];
      outp.Add(([.. rect.Select(x => Math.Round(x, 4))], spins[k]));
    }
    return outp;
  }
}
