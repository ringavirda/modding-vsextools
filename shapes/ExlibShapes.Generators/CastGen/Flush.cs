using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Generators;

/// <summary>A rule the part record pins for one flush pair.</summary>
/// <param name="A">One leaf of the pair, by its name in the item.</param>
/// <param name="B">The other leaf.</param>
/// <param name="Rule">One of <see cref="Flush.Rules"/>.</param>
public sealed record FlushPin(string A, string B, string Rule);

/// <summary>The leaves after flush resolution and the moves that made them so.</summary>
/// <param name="Leaves">The resolved leaves.</param>
/// <param name="Moves">Every change made, in order; also written to the record handed to
/// <see cref="Flush.Resolve"/>.</param>
public sealed record FlushResult(
  IReadOnlyList<Leaf> Leaves,
  IReadOnlyList<FlushMove> Moves
);

/// <summary>Step 4: flush pairs resolved by one rule each, in the design's order (2.10, C3).</summary>
public static class Flush {
  /// <summary>The rules in the order they are tried.</summary>
  public static readonly IReadOnlyList<string> Rules =
  [
    "merge",
    "sink",
    "raise",
    "trim",
    "cap",
    "stagger",
  ];

  private const double MinExtent = 1e-3;
  private const double Ring = 0.001;
  private const int RingSteps = 4;

  /// <summary>The rules a record pins, <c>flushPins</c> in its carried slots as
  /// <c>[{"pair": [a, b], "rule": r}]</c>; none when it has no such slot.</summary>
  /// <exception cref="CastGenException">A pin has no pair of two names or names a rule that is not one of
  /// <see cref="Rules"/>.</exception>
  public static List<FlushPin> Pins(PartRecord? record) {
    var pins = new List<FlushPin>();
    if (record?.Carried["flushPins"] is not JArray rows)
      return pins;
    foreach (JObject row in rows.OfType<JObject>()) {
      string[] pair =
      [
        .. (row["pair"] as JArray ?? []).Select(t => (string?)t ?? ""),
      ];
      string rule = row.Value<string>("rule") ?? "";
      if (pair.Length != 2 || !Rules.Contains(rule))
        throw new CastGenException(
          $"flush pin {row.ToString(Newtonsoft.Json.Formatting.None)} is not a pair of two names and one of {string.Join(", ", Rules)}"
        );
      pins.Add(new FlushPin(pair[0], pair[1], rule));
    }
    return pins;
  }

  /// <summary>Resolves every flush pair of <paramref name="leaves"/>, the first pair by ordinal name
  /// first, by the first rule that applies: merge, sink or raise (a face moved in by one or two
  /// allowances), trim (a side face moved to the neighbour's, smallest volume removed first), cap (cast items
  /// only: a box one allowance thick over the overlap) and stagger (a face moved in by 0.001 x k). No face
  /// of <paramref name="cutFaces"/> moves, the first-sorting name is tried first, and a candidate is taken
  /// only if the pair count falls, no leaf is thinner than 0.001 and, when <paramref name="parting"/> is
  /// given and the input drew cleanly, the draw check still passes. A pair merges, trims or caps only between
  /// elements without a rotation; a sink, raise or stagger also moves an element turned by a multiple of 90
  /// degrees, by the face whose world normal is the pair's.</summary>
  /// <param name="leaves">The leaves; names are unique.</param>
  /// <param name="cutFaces">Entries <c>NAME/FACE</c> by the drawing's element name: the faces a step cuts.</param>
  /// <param name="allowance">One allowance step in voxels.</param>
  /// <param name="cast">Whether the item is poured, so a cap is allowed.</param>
  /// <param name="pins">The rule pinned per pair; a pinned pair is tried with that rule alone.</param>
  /// <param name="parting">The flask's parting height on y; null skips the draw check.</param>
  /// <param name="record">The item's record, whose <see cref="FlushMoves.Slot"/> is set to the moves made;
  /// null leaves it out.</param>
  /// <param name="leaveCutPairs">Whether a pair whose two faces are both in <paramref name="cutFaces"/> is left as
  /// it is, one surface a step finishes together, instead of failing.</param>
  /// <exception cref="CastGenException">No rule resolves a pair; the message names the pair.</exception>
  public static FlushResult Resolve(
    IReadOnlyList<Leaf> leaves,
    IReadOnlySet<string> cutFaces,
    double allowance,
    bool cast,
    IReadOnlyList<FlushPin> pins,
    double? parting = null,
    PartRecord? record = null,
    bool leaveCutPairs = false
  ) {
    var cur = leaves.ToList();
    var moves = new List<FlushMove>();
    var ctx = new Ctx(cutFaces, allowance, cast, leaveCutPairs);
    bool drawClean = parting != null && DrawPasses(cur, parting.Value);
    while (true) {
      List<Flushcheck.Pair> pairs = PairsOf(cur, ctx);
      if (pairs.Count == 0)
        break;
      Flushcheck.Pair pair = pairs
        .OrderBy(p => Low(p), StringComparer.Ordinal)
        .ThenBy(p => High(p), StringComparer.Ordinal)
        .ThenBy(p => p.Face1, StringComparer.Ordinal)
        .First();
      Leaf a = cur.First(l => l.Name == Low(pair)),
        b = cur.First(l => l.Name == High(pair));
      string? pinned = pins.FirstOrDefault(p =>
        (p.A == a.Name && p.B == b.Name) || (p.A == b.Name && p.B == a.Name)
      )?.Rule;
      bool done = false;
      foreach (string rule in pinned == null ? Rules : [pinned]) {
        foreach (
          (List<Leaf> next, FlushMove[] made) in Candidates(
            rule,
            cur,
            a,
            b,
            pair,
            ctx
          )
        ) {
          if (!Accepts(next, pairs.Count, parting, drawClean, ctx))
            continue;
          cur = next;
          moves.AddRange(made);
          done = true;
          break;
        }
        if (done)
          break;
      }
      if (!done)
        throw new CastGenException(
          $"{a.Name} and {b.Name}: no flush rule resolves the {pair.Face1} faces"
            + (pinned == null ? "" : $" (pinned to {pinned})")
        );
    }
    if (record != null)
      FlushMoves.Write(record, moves);
    return new FlushResult(cur, moves);
  }

  private sealed record Ctx(
    IReadOnlySet<string> Cut,
    double Allowance,
    bool Cast,
    bool LeaveCutPairs
  ) {
    public bool IsCut(Leaf l, string face) =>
      Cut.Contains($"{l.SourceName}/{face}");

    public bool HasCut(Leaf l) =>
      FaceAxes.Names.Any(f => Cut.Contains($"{l.SourceName}/{f}"));
  }

  private static string Low(Flushcheck.Pair p) =>
    string.CompareOrdinal(p.Path1, p.Path2) <= 0 ? p.Path1 : p.Path2;

  private static string High(Flushcheck.Pair p) =>
    string.CompareOrdinal(p.Path1, p.Path2) <= 0 ? p.Path2 : p.Path1;

  internal static Shape ShapeOf(IEnumerable<Leaf> leaves) {
    var shape = new Shape();
    foreach (Leaf l in leaves) {
      Element e = Select.Clone(l.Element, false);
      e.Name = l.Name;
      shape.Elements.Add(e);
    }
    return shape;
  }

  private static List<Flushcheck.Pair> PairsOf(
    IReadOnlyCollection<Leaf> leaves,
    Ctx c
  ) {
    List<Flushcheck.Pair> found = Flushcheck
      .Pairs(ShapeOf(leaves), null, [])
      .Found;
    if (!c.LeaveCutPairs)
      return found;
    Dictionary<string, Leaf> byName = leaves.ToDictionary(l => l.Name);
    return
    [
      .. found.Where(p =>
        !(
          c.IsCut(byName[p.Path1], p.Face1) && c.IsCut(byName[p.Path2], p.Face2)
        )
      ),
    ];
  }

  private static bool DrawPasses(IEnumerable<Leaf> leaves, double parting) =>
    new Draw().Run(new CheckInput(ShapeOf(leaves), Parting: parting)).ExitCode
    == 0;

  private static bool Accepts(
    List<Leaf> next,
    int before,
    double? parting,
    bool drawClean,
    Ctx c
  ) {
    foreach (Leaf l in next)
      for (int a = 0; a < 3; a++)
        if (l.Element.To![a] - l.Element.From![a] < MinExtent)
          return false;
    if (PairsOf(next, c).Count >= before)
      return false;
    return parting == null || !drawClean || DrawPasses(next, parting.Value);
  }

  private static List<Leaf> Replace(List<Leaf> cur, Leaf was, Leaf now) =>
    [.. cur.Select(l => l.Name == was.Name ? now : l)];

  private static IEnumerable<(List<Leaf>, FlushMove[])> Candidates(
    string rule,
    List<Leaf> cur,
    Leaf a,
    Leaf b,
    Flushcheck.Pair pair,
    Ctx c
  ) {
    string fa = FaceOn(pair, a),
      fb = FaceOn(pair, b);
    bool square = RightAngled(a) && RightAngled(b);
    if (
      !square
      && (
        rule is "merge" or "trim" or "cap"
        || !FaceNormal(a, fa)
        || !FaceNormal(b, fb)
      )
    )
      yield break;
    bool plain = a.AxisAligned && b.AxisAligned && fa == fb;
    string face = fa;
    (int axis, bool hi) = FaceAxes.Of(face);
    switch (rule) {
      case "merge":
        if (!plain)
          yield break;
        foreach (var m in Merge(cur, a, b, c))
          yield return m;
        break;
      case "sink" or "raise":
        if (WorldHi(a, fa) != (rule == "sink"))
          yield break;
        foreach ((Leaf l, string lf) in new[] { (a, fa), (b, fb) }) {
          if (c.IsCut(l, lf))
            continue;
          for (int k = 1; k <= 2; k++) {
            double d = -k * c.Allowance;
            yield return (
              Replace(cur, l, FaceAxes.Push(l, lf, d)),
              [new FlushMove(l.Name, rule, lf, d)]
            );
          }
        }
        break;
      case "trim":
        if (!plain)
          yield break;
        foreach (var m in Trim(cur, a, b, axis, c))
          yield return m;
        break;
      case "cap":
        if (plain && c.Cast && !c.IsCut(a, face) && !c.IsCut(b, face))
          yield return Cap(cur, a, b, face, axis, hi, c);
        break;
      case "stagger":
        foreach ((Leaf l, string lf) in new[] { (a, fa), (b, fb) }) {
          if (c.IsCut(l, lf))
            continue;
          for (int k = 1; k <= RingSteps; k++) {
            double d = -Ring * k;
            yield return (
              Replace(cur, l, FaceAxes.Push(l, lf, d)),
              [new FlushMove(l.Name, rule, lf, d)]
            );
          }
        }
        break;
    }
  }

  private static string FaceOn(Flushcheck.Pair p, Leaf l) =>
    p.Path1 == l.Name ? p.Face1 : p.Face2;

  private static bool RightAngled(Leaf l) =>
    new[] { l.Element.RotationX, l.Element.RotationY, l.Element.RotationZ }.All(
      r => Math.Abs((r ?? 0) % 90) < 1e-6
    );

  private static bool FaceNormal(Leaf l, string face) {
    if (RightAngled(l))
      return true;
    double?[] r =
    [
      l.Element.RotationX,
      l.Element.RotationY,
      l.Element.RotationZ,
    ];
    int[] turned = [.. Enumerable.Range(0, 3).Where(i => (r[i] ?? 0) != 0)];
    return turned.Length == 1 && turned[0] == FaceAxes.Of(face).Axis;
  }

  private static bool WorldHi(Leaf l, string face) {
    (int axis, bool hi) = FaceAxes.Of(face);
    double[] n = [0, 0, 0];
    n[axis] = hi ? 1 : -1;
    (double X, double Y, double Z) w = Engine
      .Local(l.Element)
      .MulLinear(n[0], n[1], n[2]);
    double[] v = [w.X, w.Y, w.Z];
    return v[v.Select(Math.Abs).ToList().IndexOf(v.Max(Math.Abs))] > 0;
  }

  private static IEnumerable<(List<Leaf>, FlushMove[])> Merge(
    List<Leaf> cur,
    Leaf a,
    Leaf b,
    Ctx c
  ) {
    if (a.Stage != b.Stage || a.Role != b.Role || c.HasCut(a) || c.HasCut(b))
      yield break;
    if (
      !FaceAxes.Names.All(f =>
        TextureOf(a.Element, f) == TextureOf(b.Element, f)
      )
    )
      yield break;
    double[] lo = new double[3],
      hi = new double[3];
    double overlap = 1;
    for (int i = 0; i < 3; i++) {
      double[] af = a.Element.From!,
        at = a.Element.To!,
        bf = b.Element.From!,
        bt = b.Element.To!;
      lo[i] = Math.Min(af[i], bf[i]);
      hi[i] = Math.Max(at[i], bt[i]);
      overlap *= Math.Max(0, Math.Min(at[i], bt[i]) - Math.Max(af[i], bf[i]));
    }
    double union = Volume(a) + Volume(b) - overlap;
    if (
      Math.Abs(union - (hi[0] - lo[0]) * (hi[1] - lo[1]) * (hi[2] - lo[2]))
      > 1e-6
    )
      yield break;
    Element e = Select.Clone(a.Element, true);
    e.From = lo;
    e.To = hi;
    List<Leaf> next =
    [
      .. cur.Where(l => l.Name != b.Name)
        .Select(l => l.Name == a.Name ? a with { Element = e } : l),
    ];
    yield return (next, [new FlushMove(b.Name, "merge", "", 0, a.Name)]);
  }

  private static IEnumerable<(List<Leaf>, FlushMove[])> Trim(
    List<Leaf> cur,
    Leaf a,
    Leaf b,
    int normal,
    Ctx c
  ) {
    var options =
      new List<(double Removed, Leaf Leaf, string Face, double Delta)>();
    foreach ((Leaf l, Leaf n) in new[] { (a, b), (b, a) })
      for (int t = 0; t < 3; t++) {
        if (t == normal)
          continue;
        double[] lf = l.Element.From!,
          lt = l.Element.To!,
          nf = n.Element.From!,
          nt = n.Element.To!;
        double cross = Extent(l, 3 - normal - t) * Extent(l, normal);
        string loFace =
          t == 0 ? "west"
          : t == 1 ? "down"
          : "north";
        string hiFace =
          t == 0 ? "east"
          : t == 1 ? "up"
          : "south";
        if (nt[t] > lf[t] && nt[t] < lt[t] && !c.IsCut(l, loFace))
          options.Add(((nt[t] - lf[t]) * cross, l, loFace, -(nt[t] - lf[t])));
        if (nf[t] > lf[t] && nf[t] < lt[t] && !c.IsCut(l, hiFace))
          options.Add(((lt[t] - nf[t]) * cross, l, hiFace, -(lt[t] - nf[t])));
      }
    foreach (
      var o in options
        .OrderBy(o => o.Removed)
        .ThenBy(o => o.Leaf.Name, StringComparer.Ordinal)
        .ThenBy(o => o.Face, StringComparer.Ordinal)
    )
      yield return (
        Replace(cur, o.Leaf, FaceAxes.Push(o.Leaf, o.Face, o.Delta)),
        [new FlushMove(o.Leaf.Name, "trim", o.Face, o.Delta)]
      );
  }

  private static (List<Leaf>, FlushMove[]) Cap(
    List<Leaf> cur,
    Leaf a,
    Leaf b,
    string face,
    int axis,
    bool hi,
    Ctx c
  ) {
    double[] lo = new double[3],
      up = new double[3];
    for (int i = 0; i < 3; i++) {
      lo[i] = Math.Max(a.Element.From![i], b.Element.From![i]);
      up[i] = Math.Min(a.Element.To![i], b.Element.To![i]);
    }
    double plane = hi ? a.Element.To![axis] : a.Element.From![axis];
    lo[axis] = hi ? plane : plane - c.Allowance;
    up[axis] = hi ? plane + c.Allowance : plane;
    var taken = cur.Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
    string name = $"{a.Name}_cap";
    for (int i = 2; taken.Contains(name); i++)
      name = $"{a.Name}_cap{i}";
    Element e = Select.Clone(a.Element, true);
    e.Name = name;
    e.From = [.. lo.Select(FaceAxes.Round)];
    e.To = [.. up.Select(FaceAxes.Round)];
    e.RotationOrigin = null;
    var cap = new Leaf(name, name, a.SourcePath, e, a.Stage, a.Role);
    return (
      [.. cur, cap],
      [new FlushMove(name, "cap", face, c.Allowance, null, a.Name)]
    );
  }

  private static double Extent(Leaf l, int axis) =>
    l.Element.To![axis] - l.Element.From![axis];

  private static double Volume(Leaf l) =>
    Extent(l, 0) * Extent(l, 1) * Extent(l, 2);

  private static string? TextureOf(Element e, string face) =>
    e.Faces.FirstOrDefault(f => f.Key == face).Value?.Texture;
}
