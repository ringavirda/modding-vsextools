using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>The octagon reader octcheck and octregular share.</summary>
internal static class Octagons {
  private const double End = 0.005;
  private const double Reach = 0.05;
  private const double Tol = 0.02;
  private const double Tan = 0.4142;

  internal sealed record Bar(
    bool Own,
    double[]? Measures,
    List<string> Faults,
    bool Rect
  );

  internal sealed record Reading(string Line, List<string> Faults, bool Own);

  private static string F3(double v) => Sweep.F(v, 3);

  private static string F4(double v) => Sweep.F(v, 4);

  private static bool Allclose(double a, double b) =>
    Math.Abs(a - b) <= 1e-3 + 1e-5 * Math.Abs(b);

  private static (Vec3 Lo, Vec3 Hi) LoHi(Frames fr, Node head, Node el) {
    Vec3[] p = Sweep.CornersIn(fr, head, el);
    return (
      new Vec3(p.Min(c => c.X), p.Min(c => c.Y), p.Min(c => c.Z)),
      new Vec3(p.Max(c => c.X), p.Max(c => c.Y), p.Max(c => c.Z))
    );
  }

  private static bool CentredOn(Frames fr, Node head, Node k, int a, int b) {
    (Vec3 lo, Vec3 hi) = LoHi(fr, head, k);
    Vec3 mid = (lo + hi) * 0.5;
    Vec3 c = head.Size * 0.5;
    return Allclose(mid[a], c[a]) && Allclose(mid[b], c[b]);
  }

  private static bool IsTurnedAbout(Node k, int ax, params double[] degs) =>
    Rings.Turned(k) is { Axis: { } kax, Deg: var deg }
    && kax == ax
    && degs.Any(d => Math.Abs(Math.Abs(deg) - d) < 1e-6);

  private static bool IsEye(Node head, List<Node> chamfers, int ax) {
    (int a, int b) = Rings.Others(ax);
    for (int i = 0; i < chamfers.Count; i++)
      for (int j = i + 1; j < chamfers.Count; j++)
        for (int k = j + 1; k < chamfers.Count; k++)
          for (int l = k + 1; l < chamfers.Count; l++)
            if (
              EyeFour(
                head,
                [chamfers[i], chamfers[j], chamfers[k], chamfers[l]],
                a,
                b
              )
            )
              return true;
    return false;
  }

  private static bool EyeFour(Node head, Node[] four, int a, int b) {
    if (four.Any(k => OnOuterCorner(head, k, a, b)))
      return false;
    double[] pa = [.. four.Select(k => k.Origin[a]).Order()];
    double[] pb = [.. four.Select(k => k.Origin[b]).Order()];
    double[] wantA = [pa[0], pa[0], pa[3], pa[3]];
    double[] wantB = [pb[0], pb[0], pb[3], pb[3]];
    if (
      !pa.Zip(wantA).All(t => Math.Abs(t.First - t.Second) <= 1e-3)
      || !pb.Zip(wantB).All(t => Math.Abs(t.First - t.Second) <= 1e-3)
    )
      return false;
    double wa = pa[3] - pa[0],
      wb = pb[3] - pb[0];
    return wa > 1e-3
      && wb > 1e-3
      && (Math.Abs(wb - wa * Tan) < 2e-3 || Math.Abs(wa - wb * Tan) < 2e-3);
  }

  private static bool OnOuterCorner(Node head, Node k, int a, int b) =>
    new[] { 0.0, head.Size[a] }.Any(u =>
      new[] { 0.0, head.Size[b] }.Any(v =>
        Math.Abs(k.Origin[a] - u) < 1e-3 && Math.Abs(k.Origin[b] - v) < 1e-3
      )
    );

  private static Bar Measure(Frames fr, Node head, int ax, ISet<string> shipped) {
    List<string> faults = [];
    (int a, int b) = Rings.Others(ax);
    Vec3 size = head.Size;
    List<Node> kids = [.. head.Children.Where(k => k.Solid)];
    List<Node> chamfers =
    [
      .. kids.Where(k =>
        IsTurnedAbout(k, ax, 45.0)
        && (Rings.OnCorner(head, k, ax) || shipped.Contains(k.Name))
      ),
    ];
    bool own =
      chamfers.Count > 0 && chamfers.All(k => shipped.Contains(k.Name));
    foreach (Node k in kids)
      if (
        Rings.Turned(k) is { Axis: { } kax, Deg: var deg }
        && kax == ax
        && Math.Abs(Math.Abs(deg) - 90.0) < 1e-6
        && Enumerable
          .Range(0, 3)
          .All(i => Math.Abs(k.Size[i] - size[i]) <= 1e-3)
      )
        faults.Add(
          $"{k.Path}: a plate turned {Fmt.G(deg)} deg with the bar's own size (four turned plates)"
        );
    List<Node> xs =
    [
      .. kids.Where(k =>
        Rings.Turned(k) is { Axis: null, Deg: 0.0 }
        && Math.Abs(k.Size[a] - size[b]) < 1e-3
        && Math.Abs(k.Size[b] - size[a]) < 1e-3
        && CentredOn(fr, head, k, a, b)
      ),
    ];
    bool rect = false;
    if (xs.Count == 0) {
      xs =
      [
        .. kids.Where(k =>
          Rings.Turned(k) is { Axis: null, Deg: 0.0 }
          && 1e-3 < size[a] - k.Size[a]
          && 1e-3 < k.Size[b] - size[b]
          && Math.Abs((size[a] - k.Size[a]) - (k.Size[b] - size[b])) < 2e-3
          && CentredOn(fr, head, k, a, b)
        ),
      ];
      rect = xs.Count == 1;
    }
    if (xs.Count != 1)
      faults.Add($"{head.Path}: {xs.Count} transposed boxes, not 1");
    if (chamfers.Count != 4)
      faults.Add(
        $"{head.Path}: {chamfers.Count} chamfers turned 45 deg, not 4"
      );
    var corners = new HashSet<(double, double)>();
    foreach (Node k in chamfers) {
      Vec3 o = k.Origin;
      double ca = new[] { 0.0, size[a] }.MinBy(v => Math.Abs(v - o[a]));
      double cb = new[] { 0.0, size[b] }.MinBy(v => Math.Abs(v - o[b]));
      corners.Add((ca, cb));
    }
    if (chamfers.Count > 0 && corners.Count != chamfers.Count)
      faults.Add($"{head.Path}: chamfers share a corner");
    var ends = new List<(double Lo, double Hi)> { (0.0, size[ax]) };
    foreach (Node k in xs.Concat(chamfers)) {
      (Vec3 lo, Vec3 hi) = LoHi(fr, head, k);
      ends.Add((lo[ax], hi[ax]));
    }
    foreach ((double lo, double hi) in ends.Skip(1))
      if (Math.Abs(lo) > Reach || Math.Abs(hi - size[ax]) > Reach)
        faults.Add(
          $"{head.Path}: a part runs {F3(lo)}..{F3(hi)} of 0..{F3(size[ax])}"
        );
    List<List<(double Lo, double Hi)>> groups =
    [
      [ends[0]],
      xs.Count > 0 ? [ends[1]] : [],
      [.. ends.Skip(1 + xs.Count)],
    ];
    for (int g1 = 0; g1 < 3; g1++)
      for (int g2 = g1 + 1; g2 < 3; g2++)
        foreach (var e1 in groups[g1])
          foreach (var e2 in groups[g2])
            if (Math.Min(Math.Abs(e1.Lo - e2.Lo), Math.Abs(e1.Hi - e2.Hi)) < End)
              faults.Add(
                $"{head.Path}: ends {F3(e1.Lo)}..{F3(e1.Hi)} and {F3(e2.Lo)}..{F3(e2.Hi)} share a plane"
              );
    if (xs.Count != 1)
      return new Bar(own, null, faults, false);
    (Vec3 xl, Vec3 xh) = LoHi(fr, head, xs[0]);
    double ha = size[a] / 2,
      sb = size[b] / 2;
    double sa = (xh[a] - xl[a]) / 2,
      hb = (xh[b] - xl[b]) / 2;
    double[] rr = Sweep.Radii(
      1.3 * Math.Max(Math.Sqrt(ha * ha + sb * sb), Math.Sqrt(sa * sa + hb * hb))
    );
    List<Node> union = [head, .. xs, .. chamfers];
    Vec3 c = size * 0.5;
    double[] ideal = Sweep.OctagonReach(ha, hb, sa, sb);
    var dev = new double[Sweep.Th.Length];
    double maxReach = 0;
    for (int t = 0; t < Sweep.Th.Length; t++) {
      double cos = Math.Cos(Sweep.Th[t]),
        sin = Math.Sin(Sweep.Th[t]);
      double reach = rr[^1];
      for (int i = 0; i < rr.Length; i++) {
        double[] g = new double[3];
        g[ax] = size[ax] / 2;
        g[a] = c[a] + rr[i] * cos;
        g[b] = c[b] + rr[i] * sin;
        var p = new Vec3(g[0], g[1], g[2]);
        if (!union.Any(k => Sweep.Inside(fr, head, p, k, 1e-6))) {
          reach = rr[i];
          break;
        }
      }
      dev[t] = reach - ideal[t];
      maxReach = Math.Max(maxReach, reach);
    }
    double tol = Math.Max(Tol, 0.01 * ideal.Max());
    int lowest = 0,
      highest = 0;
    for (int t = 1; t < dev.Length; t++) {
      if (dev[t] < dev[lowest])
        lowest = t;
      if (dev[t] > dev[highest])
        highest = t;
    }
    if (dev[lowest] < -tol)
      faults.Add(
        $"{head.Path}: a hole {F3(-dev[lowest])} deep at {Sweep.F(Sweep.Deg(lowest), 2)} deg"
      );
    if (dev[highest] > tol)
      faults.Add(
        $"{head.Path}: a bump {F3(dev[highest])} proud at {Sweep.F(Sweep.Deg(highest), 2)} deg"
      );
    double flat =
      ((hb - sb) * ha + (ha - sa) * sb)
      / Math.Sqrt((hb - sb) * (hb - sb) + (ha - sa) * (ha - sa));
    return new Bar(
      own,
      [2 * ha, 2 * hb, 2 * flat, maxReach, dev.Max(Math.Abs)],
      faults,
      rect
    );
  }

  /// <summary>Which proportions of the octagons <see cref="Read"/> holds.</summary>
  internal enum Hold {
    /// <summary>Construction, reach and a half-across arm; octagons the regular globs name are not held to arms.</summary>
    Structure,

    /// <summary>Only the octagons the regular globs name, held to the regular arm and diagonals.</summary>
    Regular,
  }

  internal static List<Reading> Read(
    Tree tree,
    Frames fr,
    ISet<string> shipped,
    IReadOnlyList<string> regular,
    Hold hold
  ) {
    bool IsRegular(string path) => regular.Any(g => Sweep.GlobMatches(path, g));
    var outp = new List<Reading>();
    foreach (Node el in tree.Nodes) {
      if (!el.Solid)
        continue;
      int[] axes =
      [
        .. el
          .Children.Select(Rings.Turned)
          .Where(t => t is { Axis: not null })
          .Where(t => Math.Abs(Math.Abs(t!.Value.Deg) - 45.0) < 1e-6)
          .Select(t => t!.Value.Axis!.Value)
          .Distinct()
          .Order(),
      ];
      foreach (int ax in axes) {
        List<Node> chs =
        [
          .. el.Children.Where(k => IsTurnedAbout(k, ax, 45.0, 135.0)),
        ];
        if (IsEye(el, chs, ax)) {
          if (hold == Hold.Regular)
            continue;
          outp.Add(
            new Reading(
              $"{el.Path}  bored gear hub (eye chamfers), not held",
              [],
              true
            )
          );
          continue;
        }
        List<Node> solid =
        [
          .. chs.Where(k => IsTurnedAbout(k, ax, 45.0) && k.Solid),
        ];
        Bar bar = Measure(fr, el, ax, shipped);
        if (
          bar.Measures == null
          && solid.Count > 0
          && !solid.Any(k =>
            Rings.OnCorner(el, k, ax) || shipped.Contains(k.Name)
          )
        )
          continue;
        List<string> faults = hold == Hold.Structure ? bar.Faults : [];
        bool held = false;
        if (bar.Measures is { } m && !bar.Own && !bar.Rect) {
          (int a, int b) = Rings.Others(ax);
          bool reg = IsRegular(el.Path);
          held = reg == (hold == Hold.Regular);
          double want = el.Size[a] * (reg ? Tan : 0.5);
          if (held && Math.Abs(el.Size[b] - want) > 1e-3)
            faults.Add(
              $"{el.Path}: arms {F4(el.Size[b])} wide on {F4(el.Size[a])} across, not {F4(want)} ({(reg ? "regular" : "half its across")})"
            );
          if (held && reg && Math.Abs(m[2] - m[0]) > 0.01)
            faults.Add(
              $"{el.Path}: {F4(m[2])} across the diagonals against {F4(m[0])} on the axes"
            );
        }
        if (hold == Hold.Regular && !held)
          continue;
        Vec3[] span = Engine.Corners(fr.Mat(el), el.Size);
        Mat4d w = fr.Mat(el);
        int aw = Enumerable.Range(0, 3).MaxBy(i => Math.Abs(w[i, ax]));
        string line =
          $"{el.Path}  axis {"xyz"[aw]} {F3(span.Min(p => p[aw]))}..{F3(span.Max(p => p[aw]))}"
          + (bar.Own ? "  (fallen's, shipped)" : "");
        if (bar.Measures is { } mm)
          line +=
            $"  across {F4(mm[0])} x {F4(mm[1])} on the axes, {F4(mm[2])} on the diagonals, corners {F3(mm[3])} out, worst {F3(mm[4])}";
        outp.Add(new Reading(line, bar.Own ? [] : faults, bar.Own));
      }
    }
    if (hold == Hold.Regular)
      return outp;
    foreach (PlateRing ring in Rings.PlateRings(tree, Rings.Stray)) {
      bool own = ring.Plates.All(k => shipped.Contains(k.Name));
      (RingMeasure m, List<string> faults) = PlateRingMeasure.Measure(fr, ring);
      foreach (Node k in ring.Plates)
        if (Math.Round(V.Mod(k.Rot[ring.Axis], 45), 4) is not (0.0 or 45.0))
          faults.Add(
            $"{k.Path}: a plate turned {Fmt.G(k.Rot[ring.Axis])} deg, off the 45 deg steps"
          );
      int w = PlateRingMeasure.WorldAxis(fr, ring.Group, ring.Axis);
      double[] ends =
      [
        .. ring.Plates.SelectMany(k =>
          Engine.Corners(fr.Mat(k), k.Size).Select(p => p[w])
        ),
      ];
      string line =
        $"{ring.Group.Path}  {(m.Regular ? "regular " : "")}plate ring of {ring.Plates.Count} on {"xyz"[w]} {F3(ends.Min())}..{F3(ends.Max())}"
        + (own ? "  (fallen's, shipped)" : "")
        + $"  across {F4(m.Flats)} on the flats, {F4(m.Diagonals)} on the diagonals, bore {F4(m.Bore)}, worst {F3(m.Worst)}";
      outp.Add(new Reading(line, own ? [] : faults, own));
    }
    return outp;
  }
}
