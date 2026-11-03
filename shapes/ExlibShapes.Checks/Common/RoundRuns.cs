using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>An outward face of a round run.</summary>
/// <param name="Angle">The angle of the face's normal round the axis in degrees, 0 to 360.</param>
/// <param name="Member">The element.</param>
/// <param name="Quad">The face as a world quad.</param>
/// <param name="Lo">Low end of the uv rect along the circumference.</param>
/// <param name="Hi">High end of the uv rect along the circumference.</param>
internal sealed record RunFace(
  double Angle,
  Node Member,
  Quad Quad,
  double Lo,
  double Hi
);

/// <summary>Six or more boxes that circle one line and turn with their place on it, or an octagon, or a plate ring.</summary>
internal sealed record RoundRun(
  string Parent,
  List<Node> Members,
  Vec3 Point,
  Vec3 Axis,
  List<RunFace> Faces
);

/// <summary>
/// The round-run detector and the verdicts on whether a run's uv runs on round it, so a texture reads
/// as one unbroken surface.
/// </summary>
internal static class RoundRuns {
  internal static readonly HashSet<string> LegacyWood =
  [
    "oak",
    "generic",
    "wood",
    "charred",
  ];

  private const double SizeTol = 0.05;
  private const int MinRun = 6;
  private const double CircleTol = 0.15;
  private const double TurnTol = 15.0;
  private const double SameWay = 3.0;
  private const double Join = 0.01;
  private const double Meet = 0.05;
  private const double OnAxis = 0.1;
  private static readonly double Outward = Math.Cos(30 * Math.PI / 180);
  private static readonly double EndFace = Math.Sin(17 * Math.PI / 180);

  private static double Rad(double deg) => deg * Math.PI / 180;

  private static double Deg(double rad) => rad * 180 / Math.PI;

  private static bool Similar(Node a, Node b) =>
    Enumerable
      .Range(0, 3)
      .Count(i =>
        Math.Abs(a.Size[i] - b.Size[i])
        <= Math.Max(0.05, SizeTol * Math.Max(a.Size[i], b.Size[i]))
      ) >= 2;

  private static (Vec3 F1, Vec3 F2) Frame(Vec3 d) {
    Vec3 f1 = V.Cross(
      d,
      Math.Abs(d.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0)
    );
    f1 = V.Unit(f1);
    return (f1, V.Cross(d, f1));
  }

  internal static (Vec3 Point, Vec3 Axis)? Circle(List<Node> members, Frames fr) {
    Vec3[] pts = [.. members.Select(fr.Centre)];
    Vec3 mean = V.Mean(pts);
    var ata = new double[9];
    foreach (Vec3 p in pts) {
      Vec3 c = p - mean;
      for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
          ata[i * 3 + j] += c[i] * c[j];
    }
    (double[] ev, M3 vt) = new M3(ata).SymmetricEigen();
    double[] s = [.. ev.Select(e => Math.Sqrt(Math.Max(e, 0)))];
    if (s[0] < 1e-6 || s[1] < 0.15 * s[0] || s[2] > 0.3 * s[1])
      return null;
    Vec3 d = V.Unit(vt.Col(2));
    Vec3 f1 = vt.Col(0);
    Vec3 f2 = V.Cross(d, f1);
    (double X, double Y)[] xy =
    [
      .. pts.Select(p => (V.Dot(p - mean, f1), V.Dot(p - mean, f2))),
    ];
    var nm = new double[9];
    var rhs = new double[3];
    foreach ((double x, double y) in xy) {
      double[] row = [2 * x, 2 * y, 1];
      double b = x * x + y * y;
      for (int i = 0; i < 3; i++) {
        rhs[i] += row[i] * b;
        for (int j = 0; j < 3; j++)
          nm[i * 3 + j] += row[i] * row[j];
      }
    }
    Vec3? sol = new M3(nm).Solve(new Vec3(rhs[0], rhs[1], rhs[2]));
    if (sol == null)
      return null;
    double cx = sol.Value.X,
      cy = sol.Value.Y;
    double r = Math.Sqrt(Math.Max(sol.Value.Z + cx * cx + cy * cy, 0.0));
    if (
      r < 0.05
      || xy.Max(p =>
        Math.Abs(
          Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)) - r
        )
      )
        > CircleTol * r
    )
      return null;
    Vec3 p0 = mean + f1 * cx + f2 * cy;
    M3[] rots = [.. members.Select(m => fr.World(m).L)];
    int col = Enumerable
      .Range(0, 3)
      .MinBy(j => Math.Abs(V.Dot(rots[0].Col(j), d)));
    Vec3 c0 = rots[0].Col(col);
    if (V.Norm(c0 - d * V.Dot(c0, d)) < 0.5)
      return null;
    double Angle(Vec3 v) => Deg(Math.Atan2(V.Dot(v, f2), V.Dot(v, f1)));
    double baseAngle = Angle(c0);
    double phi0 = Math.Atan2(xy[0].Y - cy, xy[0].X - cx);
    for (int i = 0; i < rots.Length; i++) {
      double place = Deg(Math.Atan2(xy[i].Y - cy, xy[i].X - cx) - phi0);
      if (
        Math.Abs(V.Wrap(2 * (place - (Angle(rots[i].Col(col)) - baseAngle))))
        > 2 * TurnTol
      )
        return null;
    }
    return (p0, d);
  }

  private static (Vec3 Point, Vec3 Axis)? Pivots(List<Node> members, Frames fr) {
    M3[] rots = [.. members.Select(m => fr.World(m).L)];
    var axes = new List<(double Angle, Vec3 Axis)>();
    foreach (M3 rot in rots.Skip(1)) {
      M3 rel = rot * rots[0].T();
      double angle = Deg(
        Math.Acos(Math.Max(-1.0, Math.Min(1.0, (rel.Trace - 1) / 2)))
      );
      if (angle < 5.0)
        continue;
      var sym = new M3([
        .. Enumerable
          .Range(0, 9)
          .Select(k => (rel[k / 3, k % 3] + rel[k % 3, k / 3]) / 2),
      ]);
      axes.Add((angle, sym.SymmetricEigen().Vectors.Col(0)));
    }
    if (axes.Count == 0 || axes.Max(a => a.Angle) < 30.0)
      return null;
    Vec3 d = axes.MaxBy(a => a.Angle).Axis;
    if (axes.Any(a => Math.Abs(V.Dot(a.Axis, d)) < Math.Cos(Rad(10.0))))
      return null;
    Vec3[] pts =
    [
      .. members
        .Zip(rots)
        .Where(mr => mr.Second.Frobenius(rots[0]) > 0.05)
        .Select(mr => fr.World(mr.First).Apply(mr.First.Origin - mr.First.Frm)),
    ];
    if (pts.Length == 0)
      return null;
    Vec3 mean = V.Mean(pts);
    if (pts.Max(p => V.Norm((p - mean) - d * V.Dot(p - mean, d))) > 0.1)
      return null;
    if (!TurnWithPlace(members, rots, mean, d, fr))
      return null;
    return (mean, d);
  }

  private static bool TurnWithPlace(
    List<Node> members,
    M3[] rots,
    Vec3 p0,
    Vec3 d,
    Frames fr
  ) {
    (Vec3 f1, Vec3 f2) = Frame(d);
    int col = Enumerable
      .Range(0, 3)
      .MinBy(j => Math.Abs(V.Dot(rots[0].Col(j), d)));
    (double Place, double Turn)? reference = null;
    for (int i = 0; i < members.Count; i++) {
      Vec3 c = fr.Centre(members[i]) - p0;
      c -= d * V.Dot(c, d);
      if (V.Norm(c) <= OnAxis)
        continue;
      Vec3 v = rots[i].Col(col);
      double place = Deg(Math.Atan2(V.Dot(c, f2), V.Dot(c, f1)));
      double turn = Deg(Math.Atan2(V.Dot(v, f2), V.Dot(v, f1)));
      if (reference == null)
        reference = (place, turn);
      else if (
        Math.Abs(
          V.Wrap(
            2
              * (
                (place - reference.Value.Place) - (turn - reference.Value.Turn)
              )
          )
        )
        > 2 * TurnTol
      )
        return false;
    }
    return true;
  }

  private static bool MeetPair(RunFace a, RunFace b, Vec3 d) {
    (Vec3 f1, Vec3 f2) = Frame(d);
    double phi = Rad(a.Angle + V.Wrap(b.Angle - a.Angle) / 2);
    Vec3 t = f1 * -Math.Sin(phi) + f2 * Math.Cos(phi);
    double[] pa = [.. a.Quad.Points.Select(p => V.Dot(p, t)).Order()];
    double[] pb = [.. b.Quad.Points.Select(p => V.Dot(p, t)).Order()];
    return Math.Max(pa[0], pb[0]) - Math.Min(pa[3], pb[3]) <= Meet;
  }

  private static bool Joined(List<RunFace> faces, Vec3 d) {
    int n = faces.Count;
    if (n < 2)
      return true;
    var (gaps, _) = Steps(faces);
    double step = V.Median(gaps);
    bool closed = n > 2 && gaps.Max() <= 1.5 * step;
    return Enumerable
      .Range(0, closed ? n : n - 1)
      .All(i =>
        MeetPair(faces[i], faces[(i + 1) % n], d) || gaps[i] > 1.5 * step
      );
  }

  private static List<RunFace> FacesOf(
    List<Node> members,
    Frames fr,
    Vec3 p0,
    Vec3 d
  ) {
    (Vec3 f1, Vec3 f2) = Frame(d);
    var found =
      new List<(
        double Angle,
        double Reach,
        Node Member,
        Quad Quad,
        double Lo,
        double Hi
      )>();
    foreach (Node m in members)
      foreach (Quad q in Engine.FaceQuads(m.Element, m.Path, fr.Mat(m))) {
        Vec3 centre = V.Mean(q.Points) - p0;
        Vec3 radial = centre - d * V.Dot(centre, d);
        double reach = V.Norm(radial);
        if (
          reach < 1e-6
          || Math.Abs(V.Dot(q.Normal, d)) > EndFace
          || V.Dot(q.Normal, radial) / reach < Outward
        )
          continue;
        Vec3 u = q.Points[1] - q.Points[2],
          v = q.Points[3] - q.Points[2];
        int c =
          Math.Abs(V.Dot(u, d)) / V.Norm(u) > Math.Abs(V.Dot(v, d)) / V.Norm(v)
            ? 1
            : 0;
        if (q.Rotation is 90 or 270)
          c = 1 - c;
        double lo = Math.Min(q.Uv[c], q.Uv[c + 2]),
          hi = Math.Max(q.Uv[c], q.Uv[c + 2]);
        found.Add(
          (
            V.Mod(
              Deg(Math.Atan2(V.Dot(q.Normal, f2), V.Dot(q.Normal, f1))),
              360.0
            ),
            reach,
            m,
            q,
            lo,
            hi
          )
        );
      }
    var kept =
      new List<(
        double Angle,
        double Reach,
        Node Member,
        Quad Quad,
        double Lo,
        double Hi
      )>();
    foreach (var f in found.OrderByDescending(f => f.Reach))
      if (kept.All(k => Math.Abs(V.Wrap(f.Angle - k.Angle)) > SameWay))
        kept.Add(f);
    return
    [
      .. kept.OrderBy(f => f.Angle)
        .Select(f => new RunFace(f.Angle, f.Member, f.Quad, f.Lo, f.Hi)),
    ];
  }

  private static List<(int First, string Text)> Breaks(
    List<(double Lo, double Hi)> rects,
    bool closed,
    double tex
  ) {
    int n = rects.Count;
    var outp = new List<(int, string)>();
    for (int i = 0; i < (closed ? n : n - 1); i++) {
      double end = rects[i].Hi;
      (double start, double stop) = rects[(i + 1) % n];
      if (
        Math.Abs(end - start) <= Join
        || (start < Join && end + (stop - start) > tex + Join)
      )
        continue;
      outp.Add((i, $"ends at {Fmt.G(end)}, the next starts at {Fmt.G(start)}"));
    }
    return outp;
  }

  private static (List<double> Gaps, bool Closed) Steps(List<RunFace> faces) {
    int n = faces.Count;
    List<double> gaps =
    [
      .. Enumerable
        .Range(0, n)
        .Select(i => V.Mod(faces[(i + 1) % n].Angle - faces[i].Angle, 360.0)),
    ];
    return (gaps, n > 2 && gaps.Max() <= 1.5 * V.Median(gaps));
  }

  /// <summary>Whether the faces' uv runs on round the run, judged on the uv rects along the circumference.</summary>
  /// <param name="faces">A run's faces in angle order.</param>
  /// <param name="tex">The texture's width in texels.</param>
  /// <returns>Whether it passes and, when not, the first break; empty on a pass.</returns>
  public static (bool Passes, string Break) Verdict(
    List<RunFace> faces,
    double tex
  ) {
    if (faces.Count == 0)
      return (false, "no outward face");
    if (faces.Any(f => f.Quad.Rotation is 90 or 270))
      return (false, "a face is turned a quarter");
    if (
      faces.Count > 1
      && faces
        .Select(f => string.Join(",", f.Quad.Uv.Select(x => Math.Round(x, 2))))
        .Distinct()
        .Count() == 1
    )
      return (false, $"all {faces.Count} faces carry one rect");
    var (_, closed) = Steps(faces);
    int[] forward = [.. Enumerable.Range(0, faces.Count)];
    (int[] Order, List<(int First, string Text)> Found)? best = null;
    foreach (int[] order in new[] { forward, [.. forward.Reverse()] }) {
      var found = Breaks(
        [.. order.Select(j => (faces[j].Lo, faces[j].Hi))],
        closed,
        tex
      );
      if (best == null || found.Count < best.Value.Found.Count)
        best = (order, found);
    }
    (int[] ord, var list) = best!.Value;
    if (list.Count <= (closed ? 1 : 0))
      return (true, "");
    (int i, string text) = list[0];
    RunFace a = faces[ord[i]],
      b = faces[ord[(i + 1) % ord.Length]];
    return (
      false,
      $"{a.Member.Path} {a.Quad.Face} to {b.Member.Path} {b.Quad.Face}: {text}"
    );
  }

  private static int Axial(Quad q, Vec3 d) {
    Vec3[] g = Texels.Gradient(q);
    double n0 = V.Norm(g[0]),
      n1 = V.Norm(g[1]);
    return
      Math.Abs(V.Dot(g[0], d)) / (n0 == 0 ? 1.0 : n0)
      >= Math.Abs(V.Dot(g[1], d)) / (n1 == 0 ? 1.0 : n1)
      ? 0
      : 1;
  }

  private static bool Restarts(Quad x, Quad y, Vec3 p, Vec3 d, double tex) {
    int c = 1 - Axial(y, d);
    double[] t =
    [
      .. Texels.Vertex(y.Uv, y.Rotation).Select(v => c == 0 ? v.U : v.V),
    ];
    (double u, double v) = Texels.At(x, p);
    return t.Min() <= Join
      && (c == 0 ? u : v) + (t.Max() - t.Min()) > tex + Join;
  }

  /// <summary>Whether neighbouring faces meet one texel at each end of the edge they share, judged in world
  /// terms; a wood face's u must grow more along the axis than its v.</summary>
  /// <param name="faces">A run's faces in angle order.</param>
  /// <param name="d">The run's unit axis.</param>
  /// <param name="tex">The texture's width in texels.</param>
  /// <param name="isWood">Whether a texture key names a wood texture.</param>
  /// <returns>Whether it passes and, when not, the first break; empty on a pass.</returns>
  public static (bool Passes, string Break) VerdictDirected(
    List<RunFace> faces,
    Vec3 d,
    double tex,
    Func<string, bool> isWood
  ) {
    if (faces.Count == 0)
      return (false, "no outward face");
    foreach (RunFace f in faces)
      if (isWood(f.Quad.Texture) && Axial(f.Quad, d) != 0)
        return (
          false,
          $"{f.Member.Path} {f.Quad.Face}: the grain runs round the axis"
        );
    var (gaps, closed) = Steps(faces);
    double step = V.Median(gaps);
    var found = new List<string>();
    for (int i = 0; i < (closed ? faces.Count : faces.Count - 1); i++) {
      Quad a = faces[i].Quad,
        b = faces[(i + 1) % faces.Count].Quad;
      if (gaps[i] > 1.5 * step)
        continue;
      foreach (Vec3 p in Texels.Seam(a, b)) {
        var ta = Texels.At(a, p);
        var tb = Texels.At(b, p);
        if (
          Math.Max(Math.Abs(ta.U - tb.U), Math.Abs(ta.V - tb.V)) > Join
          && !(Restarts(a, b, p, d, tex) || Restarts(b, a, p, d, tex))
        ) {
          found.Add(
            $"{faces[i].Member.Path} {a.Face} to {faces[(i + 1) % faces.Count].Member.Path} {b.Face}: "
              + $"texel {Fmt.G(ta.U)},{Fmt.G(ta.V)} meets {Fmt.G(tb.U)},{Fmt.G(tb.V)}"
          );
          break;
        }
      }
    }
    return found.Count <= (closed ? 1 : 0) ? (true, "") : (false, found[0]);
  }

  private static IEnumerable<(
    string Parent,
    List<Node> Members,
    Vec3 Point,
    Vec3 Axis
  )> Generic(Tree tree, Frames fr) {
    var groups = new List<(string, List<Node>)> { ("", tree.Roots) };
    groups.AddRange(
      tree.Nodes.Where(e => e.Children.Count > 0)
        .Select(e => (e.Path, e.Children))
    );
    foreach ((string parent, List<Node> kids) in groups) {
      List<Node> boxes = [.. kids.Where(k => k.IsLeaf && k.Solid)];
      while (boxes.Count > 0) {
        Node first = boxes[0];
        List<Node> same = [.. boxes.Where(b => Similar(first, b))];
        boxes = [.. boxes.Where(b => !same.Contains(b))];
        if (same.Count >= MinRun) {
          var axis = Circle(same, fr) ?? Pivots(same, fr);
          if (axis != null)
            yield return (parent, same, axis.Value.Point, axis.Value.Axis);
        }
      }
    }
  }

  private static IEnumerable<(
    string Parent,
    List<Node> Members,
    Vec3 Point,
    Vec3 Axis
  )> Octagons(Tree tree, Frames fr) {
    foreach (Node el in tree.Nodes) {
      if (!el.Solid)
        continue;
      var axes = el
        .Children.Select(k => Rings.Turned(k))
        .Where(t => t != null && t.Value.Axis != null)
        .Select(t => t!.Value.Axis!.Value)
        .Distinct()
        .Order();
      foreach (int ax in axes) {
        List<Node> chamfers =
        [
          .. el.Children.Where(k =>
            Rings.Turned(k) is { Axis: { } kax, Deg: var deg }
            && kax == ax
            && Math.Abs(Math.Abs(deg) - 45.0) < 1e-6
            && k.Solid
            && Rings.OnCorner(el, k, ax)
          ),
        ];
        if (chamfers.Count != 4)
          continue;
        (int a, int b) = Rings.Others(ax);
        List<Node> flat =
        [
          .. el.Children.Where(k =>
            Rings.Turned(k) is { Axis: null, Deg: 0.0 }
            && k.Solid
            && Math.Abs((k.Frm[a] + k.To[a] - el.Size[a]) / 2) < 0.05
            && Math.Abs((k.Frm[b] + k.To[b] - el.Size[b]) / 2) < 0.05
          ),
        ];
        Vec3 d = fr.World(el).L.Col(ax);
        yield return (
          el.Parent?.Path ?? "",
          [el, .. flat, .. chamfers],
          fr.Centre(el),
          V.Unit(d)
        );
      }
    }
  }

  private static IEnumerable<(
    string Parent,
    List<Node> Members,
    Vec3 Point,
    Vec3 Axis
  )> PlateRingRuns(Tree tree, Frames fr) {
    foreach (PlateRing ring in Rings.PlateRings(tree, Rings.Stray)) {
      Vec3 d = fr.World(ring.Group).L.Col(ring.Axis);
      yield return (
        ring.Group.Path,
        ring.Plates,
        fr.World(ring.Group).Apply(ring.Pivot),
        V.Unit(d)
      );
    }
  }

  /// <summary>Every round run of the tree: six or more sibling boxes of one size whose centres circle one line
  /// and turn with their place on it, or that turn about one axis through one pivot, or an octagon or a plate ring;
  /// each with its outward faces in angle round the axis.</summary>
  /// <param name="tree">The shape.</param>
  /// <param name="fr">The shape's rest frames.</param>
  /// <param name="nested">False drops a run whose members are all members of another run found after it as well.</param>
  public static List<RoundRun> Find(Tree tree, Frames fr, bool nested = true) {
    var seen = new List<HashSet<string>>();
    var found = new List<(RoundRun Run, HashSet<string> Paths)>();
    IEnumerable<(string, List<Node>, Vec3, Vec3)> all = Generic(tree, fr)
      .Concat(Octagons(tree, fr))
      .Concat(PlateRingRuns(tree, fr))
      .Select(x => (x.Parent, x.Members, x.Point, x.Axis));
    foreach ((string parent, List<Node> members, Vec3 p0, Vec3 d) in all) {
      var paths = members.Select(m => m.Path).ToHashSet();
      if (members.Count < MinRun || seen.Any(s => paths.IsSubsetOf(s)))
        continue;
      List<RunFace> faces = FacesOf(members, fr, p0, d);
      if (!Joined(faces, d))
        continue;
      seen.Add(paths);
      found.Add((new RoundRun(parent, members, p0, d, faces), paths));
    }
    return
    [
      .. found
        .Where(f =>
          nested || !found.Any(g => f.Paths.IsProperSubsetOf(g.Paths))
        )
        .Select(f => f.Run),
    ];
  }
}
