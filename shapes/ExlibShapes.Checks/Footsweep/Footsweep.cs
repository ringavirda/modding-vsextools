using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Where a shape's drawn parts leave the declared footprint, at rest and over every clip.
/// </summary>
/// <remarks>
/// <para>The declared volume is <see cref="CheckInput.Footprint"/>: the principal cell and its cells and extras,
/// less its drops. The check samples the surface of every drawn element on a 0.5 grid at rest and at every
/// integer frame of every clip, and reports per cell a sample lands in (a sample on a cell boundary counts in the
/// cell on its element's side) and per part group (the path cut to <see cref="FootprintSpec.Depth"/> segments)
/// the largest distance from a sample to the nearest declared box, its largest per-axis gap in voxels.</para>
/// <para>A <see cref="FootprintSpec.Reach"/> entry accepts the reach of the named element, and of every element
/// under it, into its cell up to its <c>Max</c>, printed with its reason and left out of its group's line and the
/// worst reach; past <c>Max</c> it fails. Where entries for one cell nest, the nearest the element holds. An entry
/// no element needs prints as unused. Fails on any other reach past <see cref="FootprintSpec.Limit"/>.</para>
/// </remarks>
public sealed class Footsweep : ICheck {
  private const double Grid = 0.5;
  private static readonly double[] Full = [0, 0, 0, 1, 1, 1];

  /// <inheritdoc />
  public string Id => "footsweep";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <summary>The cells a blocktype golden's <c>fillerOffsets</c> declare, each of its collision boxes, else its
  /// whole cell.</summary>
  /// <param name="json">The golden's JSON text.</param>
  /// <param name="attr">The <c>attributesByType</c> key to read in place of <c>attributes</c>; null reads
  /// <c>attributes</c>.</param>
  /// <exception cref="CheckUsageException"><paramref name="attr"/> names a key the golden lacks.</exception>
  public static IReadOnlyList<FootCell> GoldenCells(
    string json,
    string? attr = null
  ) {
    JObject data = JObject.Parse(json);
    JToken? block =
      attr == null
        ? data["attributes"]
        : data["attributesByType"]?[attr]
          ?? throw new CheckUsageException(
            $"the golden has no attributesByType '{attr}'"
          );
    List<FootCell> cells = [];
    foreach (JToken o in block?["fillerOffsets"] ?? new JArray()) {
      int x = (int)o["x"]!,
        y = (int)o["y"]!,
        z = (int)o["z"]!;
      JToken[] boxes =
      [
        .. o["collisionBoxes"] is JArray many ? many
        : o["collisionBox"] is { } one ? [one]
        : [],
      ];
      if (boxes.Length == 0)
        cells.Add(new FootCell(x, y, z));
      foreach (JToken b in boxes)
        cells.Add(
          new FootCell(
            x,
            y,
            z,
            [
              .. new[] { "x1", "y1", "z1", "x2", "y2", "z2" }.Select(k =>
                (double)b[k]!
              ),
            ]
          )
        );
    }
    return cells;
  }

  private static string CellText((int X, int Y, int Z) c) =>
    $"({c.X}, {c.Y}, {c.Z})";

  private static string Num(double d, int width) =>
    d.ToString("F2", CultureInfo.InvariantCulture).PadLeft(width);

  private static IReadOnlyList<(Vec3 Lo, Vec3 Hi)> Boxes(FootprintSpec spec) {
    var cells = new Dictionary<(int, int, int), List<IReadOnlyList<double>>>();
    foreach (FootCell c in spec.Cells ?? []) {
      var key = (c.X, c.Y, c.Z);
      if (!cells.TryGetValue(key, out var list))
        cells[key] = list = [];
      list.Add(c.Box ?? Full);
    }
    if (!cells.ContainsKey((0, 0, 0)))
      cells[(0, 0, 0)] = [Full];
    foreach (FootCell d in spec.Drop ?? [])
      cells.Remove((d.X, d.Y, d.Z));
    var all = cells
      .SelectMany(kv => kv.Value.Select(b => (kv.Key, b)))
      .Concat((spec.Extra ?? []).Select(c => ((c.X, c.Y, c.Z), c.Box ?? Full)));
    return
    [
      .. all.Select(cb =>
      {
        var baseAt =
          new Vec3(cb.Item1.Item1, cb.Item1.Item2, cb.Item1.Item3) * 16;
        return (
          baseAt + new Vec3(cb.Item2[0], cb.Item2[1], cb.Item2[2]) * 16,
          baseAt + new Vec3(cb.Item2[3], cb.Item2[4], cb.Item2[5]) * 16
        );
      }),
    ];
  }

  private static List<Vec3> SurfacePoints(Vec3 size) {
    double[][] axes =
    [
      .. Enumerable
        .Range(0, 3)
        .Select(a =>
        {
          int n = Math.Max(2, (int)Math.Ceiling(size[a] / Grid) + 1);
          return Enumerable
            .Range(0, n)
            .Select(i => size[a] * i / (n - 1))
            .ToArray();
        }),
    ];
    var seen = new HashSet<(double, double, double)>();
    List<Vec3> pts = [];
    for (int fixedAxis = 0; fixedAxis < 3; fixedAxis++) {
      int a = (fixedAxis + 1) % 3,
        b = (fixedAxis + 2) % 3;
      foreach (double v in new[] { 0.0, size[fixedAxis] })
        foreach (double u in axes[a])
          foreach (double w in axes[b]) {
            var c = new double[3];
            c[fixedAxis] = Math.Round(v, 4);
            c[a] = Math.Round(u, 4);
            c[b] = Math.Round(w, 4);
            if (seen.Add((c[0], c[1], c[2])))
              pts.Add(new Vec3(c[0], c[1], c[2]));
          }
    }
    return pts;
  }

  private static double Gap(Vec3 p, IReadOnlyList<(Vec3 Lo, Vec3 Hi)> boxes) {
    double g = double.PositiveInfinity;
    foreach ((Vec3 lo, Vec3 hi) in boxes) {
      double m = 0;
      for (int a = 0; a < 3; a++)
        m = Math.Max(m, Math.Max(lo[a] - p[a], p[a] - hi[a]));
      g = Math.Min(g, m);
    }
    return g;
  }

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    Shape shape = input.Shape;
    FootprintSpec spec = input.Footprint ?? new FootprintSpec();
    var tree = new Tree(shape);
    var paths = tree.Nodes.Select(n => Tree.Bare(n.Path)).ToHashSet();
    IReadOnlyList<ReachAllow> reach = spec.Reach ?? [];
    foreach (ReachAllow r in reach)
      if (!paths.Contains(Tree.Bare(r.Element)))
        throw new CheckUsageException($"reach: no element {r.Element}");
    var boxes = Boxes(spec);
    List<Node> leaves = [.. tree.Leaves];
    var surface = leaves.ToDictionary(n => n.Path, n => SurfacePoints(n.Size));

    var worst =
      new Dictionary<
        ((int, int, int) Cell, string Path),
        (double D, string Label)
      >();
    int enumerated = 0;
    List<string> runs = ["rest", .. PoseRuns.AllClips(shape)];
    foreach (string run in runs)
      foreach ((string label, var pose) in PoseRuns.Run(shape, run)) {
        var frames = new Frames(shape, pose);
        foreach (Node n in leaves) {
          enumerated++;
          Mat4d m = frames.Mat(n);
          Vec3 mid = m.Mul(n.Size * 0.5);
          var perCell = new Dictionary<(int, int, int), double>();
          foreach (Vec3 local in surface[n.Path]) {
            Vec3 p = m.Mul(local);
            double g = Gap(p, boxes);
            if (g < 0.05)
              continue;
            var cell = (
              (int)Math.Floor((p.X + 1e-6 * Math.Sign(mid.X - p.X)) / 16),
              (int)Math.Floor((p.Y + 1e-6 * Math.Sign(mid.Y - p.Y)) / 16),
              (int)Math.Floor((p.Z + 1e-6 * Math.Sign(mid.Z - p.Z)) / 16)
            );
            perCell[cell] = perCell.TryGetValue(cell, out double old)
              ? Math.Max(old, g)
              : g;
          }
          foreach (var (cell, d) in perCell) {
            var key = (cell, n.Path);
            if (!worst.TryGetValue(key, out var have) || d > have.D)
              worst[key] = (d, label);
          }
        }
      }

    var allow = reach.ToDictionary(
      r => (Tree.Bare(r.Element), (r.Cell[0], r.Cell[1], r.Cell[2])),
      r => r
    );
    var groups =
      new Dictionary<
        ((int, int, int) Cell, string Group),
        (double D, string Label, string Path)
      >();
    var allowed =
      new List<(
        (int, int, int) Cell,
        string Path,
        double D,
        string Label,
        ReachAllow R
      )>();
    var over =
      new List<(
        (int, int, int) Cell,
        string Path,
        double D,
        string Label,
        ReachAllow R
      )>();
    var used = new HashSet<(string, (int, int, int))>();
    foreach (var (key, val) in worst) {
      (int, int, int) c = key.Cell;
      var hits = allow
        .Keys.Where(k => k.Item2 == c && Refs.Under(key.Path, k.Item1))
        .ToList();
      if (hits.Count > 0 && val.D > spec.Limit + 1e-6) {
        var k = hits.MaxBy(h => h.Item1.Length);
        used.Add(k);
        ReachAllow r = allow[k];
        (val.D <= r.Max + 1e-6 ? allowed : over).Add(
          (c, key.Path, val.D, val.Label, r)
        );
        continue;
      }
      string group = string.Join("/", key.Path.Split('/').Take(spec.Depth));
      var gk = (c, group);
      if (!groups.TryGetValue(gk, out var cur) || val.D > cur.D)
        groups[gk] = (val.D, val.Label, key.Path);
    }

    List<string> lines = [];
    double worstReach = 0;
    foreach (var (gk, g) in groups.OrderByDescending(kv => kv.Value.D)) {
      lines.Add(
        $"cell {CellText(gk.Cell),-12} {gk.Group,-34} reach {Num(g.D, 5)} ({g.Label}) {g.Path}"
      );
      worstReach = Math.Max(worstReach, g.D);
    }
    foreach (var a in allowed)
      lines.Add(
        $"cell {CellText(a.Cell),-12} reach {Num(a.D, 5)} ({a.Label}) {a.Path}  allowed to {Fmt.G(a.R.Max)}: {a.R.Reason}"
      );
    foreach (var o in over)
      lines.Add(
        $"cell {CellText(o.Cell),-12} reach {Num(o.D, 5)} ({o.Label}) {o.Path}  past its allowed {Fmt.G(o.R.Max)}"
      );
    foreach (var (key, r) in allow)
      if (!used.Contains(key))
        lines.Add(
          $"reach: {r.Element} [{string.Join(", ", r.Cell)}] unused, the reach passes as drawn"
        );
    lines.Add(
      $"worst reach {Num(worstReach, 0)} past the footprint; limit {Fmt.G(spec.Limit)}"
    );
    bool faults = worstReach > spec.Limit + 1e-6 || over.Count > 0;
    return CheckResult.Of(Id, enumerated, faults, lines);
  }
}
