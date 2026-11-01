using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Coplanar, same-facing, overlapping faces of different leaves, at any turn.
/// </summary>
/// <remarks>
/// <para>Two faces are flush when their normals agree (dot above 0.99999) and more than 0.01 square voxels of one
/// lies inside the other, within 0.001 of it along the normal, and shows: no leaf covers the point 0.01 out along
/// the normal (sampled on a 0.05 grid). The separation is taken at each sample, so faces a hair off parallel are
/// measured where they overlap. A face of no area, and a leaf a pose stretches to nothing, draws nothing and is
/// left out.</para>
/// <para>The check runs the shape at rest and at every integer frame of every clip (<see cref="PoseRuns"/>). The subtrees left out, faces and cover alike, are
/// <see cref="CheckInput.Hide"/>; without it, every <c>Level</c> element of a <c>Piece&lt;k&gt;</c> group: a
/// filling's sand level, which is not drawn with its parting. Lines: <c>flush  PATH FACE | PATH FACE  area A</c>
/// per pair, a pair found in a clip frame closed by <c>  (CLIP fN)</c>, then <c>N faces; M flush pairs</c> over all
/// the poses.</para>
/// </remarks>
public sealed partial class Flushcheck : ICheck {
  private static readonly double Tilt = Math.Sqrt(2 * (1 - Overlap.Parallel));

  [GeneratedRegex(@"^Piece\d+$")]
  private static partial Regex PieceName();

  /// <inheritdoc />
  public string Id => "flushcheck";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <summary>One flush pair.</summary>
  /// <param name="Path1">The first face's element path.</param>
  /// <param name="Face1">The first face.</param>
  /// <param name="Path2">The second face's element path.</param>
  /// <param name="Face2">The second face.</param>
  /// <param name="Area">The shown overlap in square voxels.</param>
  public sealed record Pair(
    string Path1,
    string Face1,
    string Path2,
    string Face2,
    double Area
  );

  /// <summary>The subtree references a filling's structure hides: each <c>Level</c> under a
  /// <c>Piece&lt;k&gt;</c> group.</summary>
  public static IReadOnlyList<string> FillingHides(Shape shape) =>
    [
      .. new Tree(shape)
        .Nodes.Where(n =>
          n.Name == "Level"
          && n.Parent != null
          && PieceName().IsMatch(n.Parent.Name)
        )
        .Select(n => n.Path),
    ];

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    IReadOnlyList<string> hide = input.Hide ?? FillingHides(input.Shape);
    var paths = new Tree(input.Shape).Nodes.Select(n => n.Path).ToList();
    string[] missing =
    [
      .. hide.Where(h => !paths.Any(p => Refs.Matches(p, h))),
    ];
    if (missing.Length > 0)
      throw new CheckUsageException($"no element {string.Join(", ", missing)}");
    int faces = 0,
      pairs = 0;
    List<string> lines = [];
    List<string> runs = ["rest", .. PoseRuns.AllClips(input.Shape)];
    foreach (string run in runs)
      foreach ((string label, var pose) in PoseRuns.Run(input.Shape, run)) {
        (int n, List<Pair> found) = Pairs(input.Shape, pose, hide);
        faces += n;
        pairs += found.Count;
        lines.AddRange(
          found.Select(p =>
            FormattableString.Invariant(
              $"flush  {p.Path1} {p.Face1} | {p.Path2} {p.Face2}  area {p.Area:F2}"
            ) + (pose == null ? "" : $"  ({label})")
          )
        );
      }
    lines.Add($"{faces} faces; {pairs} flush pairs");
    return CheckResult.Of(Id, faces, pairs > 0, lines);
  }

  private sealed record FaceInfo(
    string Path,
    string Face,
    Vec3[] Pts,
    Vec3 Normal,
    double Plane
  );

  /// <summary>The face count and the flush pairs of the shape posed by <paramref name="pose"/> (poses by element
  /// name, null for rest), less the subtrees <paramref name="hide"/> names.</summary>
  public static (int Faces, List<Pair> Found) Pairs(
    Shape shape,
    IReadOnlyDictionary<string, Pose>? pose,
    IReadOnlyList<string> hide
  ) {
    var tree = new Tree(shape);
    var frames = new Frames(shape, pose);
    List<Node> leaves =
    [
      .. tree.Leaves.Where(n =>
        !hide.Any(h => Refs.Matches(n.Path, h))
        && Math.Abs(Overlap.Det(frames.Mat(n))) > 1e-12
      ),
    ];
    List<FaceInfo> faces = [];
    foreach (Node n in leaves)
      foreach (Quad q in Engine.FaceQuads(n.Element, n.Path, frames.Mat(n))) {
        double area =
          V.Norm(V.Cross(q.Points[2] - q.Points[0], q.Points[3] - q.Points[1]))
          / 2;
        if (area > 1e-6)
          faces.Add(
            new FaceInfo(
              n.Path,
              q.Face,
              q.Points,
              q.Normal,
              V.Dot(q.Normal, q.Points[0])
            )
          );
      }
    if (faces.Count == 0)
      return (0, []);
    Func<Vec3, bool> shows = Overlap.Uncovered(leaves, frames);
    int count = faces.Count;
    var lo = new Vec3[count];
    var hi = new Vec3[count];
    double reach = 0;
    for (int i = 0; i < count; i++) {
      Vec3[] pts = faces[i].Pts;
      lo[i] = new Vec3(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z));
      hi[i] = new Vec3(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z));
      reach = Math.Max(
        reach,
        pts.Max(p =>
          Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z)))
        )
      );
    }
    double step = Math.Max(
      0.02,
      2 * (Overlap.Apart + Tilt * reach * Math.Sqrt(3))
    );
    var buckets = new Dictionary<(long, long, long, long), List<int>>();
    for (int i = 0; i < count; i++) {
      Vec3 n = faces[i].Normal;
      var key = (
        (long)Math.Round(n.X / 0.02),
        (long)Math.Round(n.Y / 0.02),
        (long)Math.Round(n.Z / 0.02),
        (long)Math.Round(faces[i].Plane / step)
      );
      if (!buckets.TryGetValue(key, out List<int>? list))
        buckets[key] = list = [];
      list.Add(i);
    }
    var pairs = new List<(int, int)>();
    foreach (var (key, members) in buckets) {
      var near = new SortedSet<int>();
      for (int a = -1; a <= 1; a++)
        for (int b = -1; b <= 1; b++)
          for (int c = -1; c <= 1; c++)
            for (int d = -1; d <= 1; d++)
              if (
                buckets.TryGetValue(
                  (key.Item1 + a, key.Item2 + b, key.Item3 + c, key.Item4 + d),
                  out List<int>? other
                )
              )
                near.UnionWith(other);
      foreach (int i in members)
        foreach (int j in near.Where(j => j > i)) {
          FaceInfo fi = faces[i],
            fj = faces[j];
          if (
            V.Dot(fj.Normal, fi.Normal) < Overlap.Parallel
            || fj.Path == fi.Path
          )
            continue;
          Vec3 l = new(
            Math.Max(lo[j].X, lo[i].X),
            Math.Max(lo[j].Y, lo[i].Y),
            Math.Max(lo[j].Z, lo[i].Z)
          );
          Vec3 h = new(
            Math.Min(hi[j].X, hi[i].X),
            Math.Min(hi[j].Y, hi[i].Y),
            Math.Min(hi[j].Z, hi[i].Z)
          );
          Vec3 span = h - l;
          if (span.X <= -0.002 || span.Y <= -0.002 || span.Z <= -0.002)
            continue;
          Vec3 mid = (l + h) * 0.5;
          double gap =
            (fj.Plane - V.Dot(fj.Normal, mid)) / V.Dot(fj.Normal, fi.Normal)
            - (fi.Plane - V.Dot(mid, fi.Normal));
          double swing = V.Norm(fj.Normal - fi.Normal) * V.Norm(span) / 2;
          if (Math.Abs(gap) <= Overlap.Apart + swing + 1e-6)
            pairs.Add((i, j));
        }
    }
    pairs.Sort();
    var found = new List<Pair>();
    foreach ((int i, int j) in pairs) {
      FaceInfo a = faces[i],
        b = faces[j];
      double area = Overlap.Shown(
        a.Pts,
        a.Normal,
        a.Plane,
        b.Pts,
        b.Normal,
        b.Plane,
        shows
      );
      if (area > 0.01)
        found.Add(new Pair(a.Path, a.Face, b.Path, b.Face, area));
    }
    return (faces.Count, found);
  }
}
