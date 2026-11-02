using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// The structure of a filling's <c>Piece&lt;k&gt;</c> groups against the pour's piece count: the groups are
/// counted (a one-piece pour has none, nor a Bedded or Level group), numbered 1..N, each holds exactly Bedded and
/// Level, the two have the same plan bounds (each edge within one 0.25 cell), no plan cell lies in two Levels, each
/// Level's top is the top of the sand outside the pieces, and the pieces with the sand outside cover every 0.25
/// plan cell of the station's sand box. The pieces' plan centres then run in the pour's <c>order</c>
/// (<c>alt</c>, <c>south</c>, <c>west</c>, <c>pairs&lt;N&gt;</c>, <c>rows&lt;N&gt;</c>). A leaf covers the cells whose
/// centre lies in its bounds, a turned leaf in its 1/32 lattice cells; the runner box covers none. One line per fault; needs the part record's pour and the
/// settings.
/// </summary>
public sealed class Pieces : ICheck {
  private const double Cell = 0.25;
  private const double Tol = 0.01;
  private static readonly Regex PieceGroup = new(@"^Piece(\d+)$");

  /// <inheritdoc />
  public string Id => "pieces";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    Pour pour = Pour.Read(input.Record);
    Settings settings =
      input.Settings
      ?? throw new CheckUsageException("pieces reads the settings, none given");
    Station station =
      input.Station ?? Pouring.StationOf(settings, pour.Station);
    List<FillingLeaf> leaves = Pouring.Leaves(input.Shape, input, settings);
    var tree = new Tree(input.Shape);
    int want = pour.Pieces.Count > 1 ? pour.Pieces.Count : 0;
    List<Node> found =
    [
      .. tree.Nodes.Where(n =>
        PieceGroup.IsMatch(n.Name) && n.Children.Count > 0
      ),
    ];
    var lines = new List<string>();
    bool stray = tree.Nodes.Any(n => n.Name is "Bedded" or "Level");
    bool countOk = found.Count == want && (want > 0 || !stray);
    lines.Add(
      $"count: {found.Count} pieces, {want} wanted{(countOk ? "" : "  FAIL")}"
    );
    int counted = Math.Max(found.Count, 1);
    if (!countOk || want == 0)
      return CheckResult.Of(Id, counted, !countOk, Ordered(lines));
    List<int> numbers =
    [
      .. found
        .Select(g => int.Parse(PieceGroup.Match(g.Name).Groups[1].Value))
        .Order(),
    ];
    bool named = numbers.SequenceEqual(Enumerable.Range(1, numbers.Count));
    lines.Add(
      $"names: Piece{string.Join(",", numbers)}{(named ? "" : "  FAIL")}"
    );
    bool holds = found.All(g =>
      g.Children.Select(c => c.Name)
        .Order(StringComparer.Ordinal)
        .SequenceEqual(["Bedded", "Level"])
    );
    lines.Add(
      $"holds: each Piece holds Bedded and Level{(holds ? "" : "  FAIL")}"
    );
    if (!named || !holds)
      return CheckResult.Of(Id, counted, true, Ordered(lines));
    bool faults = false;
    var bedded = new Dictionary<int, HashSet<(int, int)>>();
    var feet = new Dictionary<int, HashSet<(int, int)>>();
    foreach (int k in numbers) {
      bedded[k] = Cells(
        leaves
          .Where(l => l.Piece == k && l.Bedded && l.Role != "runner")
          .SelectMany(l => l.Cells)
      );
      feet[k] = Cells(
        leaves
          .Where(l => l.Piece == k && l.Level && l.Role != "runner")
          .SelectMany(l => l.Cells)
      );
    }
    foreach (int k in numbers) {
      if (
        bedded[k].Count == 0
        || feet[k].Count == 0
        || Bounds(bedded[k])
          .Zip(Bounds(feet[k]))
          .Any(t => Math.Abs(t.First - t.Second) > 1)
      ) {
        faults = true;
        lines.Add($"Piece{k}: Bedded and Level bounds differ  FAIL");
      }
    }
    foreach (int a in numbers)
      foreach (int b in numbers.Where(b => b > a)) {
        int shared = feet[a].Intersect(feet[b]).Count();
        if (shared > 0) {
          faults = true;
          lines.Add(
            $"Piece{a} and Piece{b}: {shared} plan cells of Level in both  FAIL"
          );
        }
      }
    List<FillingLeaf> outside =
    [
      .. leaves.Where(l => l.Piece == 0 && l.Role != "runner"),
    ];
    if (outside.Count == 0) {
      faults = true;
      lines.Add("flush: no sand outside the Pieces  FAIL");
    } else {
      double top = outside.Max(l => l.Box.Hi.Y);
      foreach (int k in numbers) {
        double level = leaves
          .Where(l => l.Piece == k && l.Level)
          .Select(l => l.Box.Hi.Y)
          .DefaultIfEmpty(double.NaN)
          .Max();
        if (!(Math.Abs(level - top) <= Tol)) {
          faults = true;
          lines.Add(
            $"Piece{k}: Level top at y {Fmt.G(level)}, the sand's top is {Fmt.G(top)}  FAIL"
          );
        }
      }
    }
    HashSet<(int, int)> covered = Cells(outside.SelectMany(l => l.Cells));
    foreach (int k in numbers) {
      covered.UnionWith(feet[k]);
      covered.UnionWith(bedded[k]);
    }
    HashSet<(int, int)> need = Cells([
      new Box(
        new Vec3(station.SandFrom[0], 0, station.SandFrom[2]),
        new Vec3(station.SandTo[0], 0, station.SandTo[2])
      ),
    ]);
    int missing = need.Count(c => !covered.Contains(c));
    if (missing > 0) {
      faults = true;
      lines.Add(
        $"cover: {missing} of {need.Count} plan cells of the sand box have no sand  FAIL"
      );
    }
    if (
      pour.Order is { Length: > 0 } order
      && numbers.All(k => feet[k].Count > 0)
    )
      faults |= Order(order, numbers, feet, station, lines);
    return CheckResult.Of(Id, counted, faults, Ordered(lines));
  }

  private static List<string> Ordered(List<string> lines) =>
    [
      .. lines.Where(l => l.EndsWith("  FAIL")),
      .. lines.Where(l => !l.EndsWith("  FAIL")),
    ];

  private static bool Order(
    string order,
    List<int> ks,
    Dictionary<int, HashSet<(int, int)>> feet,
    Station station,
    List<string> lines
  ) {
    if (order == "one")
      return false;
    double rx = station.RunnerX is { Length: 2 } r
      ? (r[0] + r[1]) / 2
      : throw new CheckUsageException(
        $"order '{order}' needs the station's runnerX"
      );
    var c = ks.ToDictionary(
      k => k,
      k =>
        (
          X: (feet[k].Average(p => p.Item1) + 0.5) * Cell,
          Z: (feet[k].Average(p => p.Item2) + 0.5) * Cell
        )
    );
    var pairs = ks.Zip(ks.Skip(1)).ToList();
    bool ok;
    if (order == "south")
      ok = pairs.All(p => c[p.First].Z > c[p.Second].Z);
    else if (order == "west")
      ok = pairs.All(p => c[p.First].X > c[p.Second].X);
    else if (order == "alt")
      ok =
        ks.All(k => c[k].X < rx == (k % 2 == 1))
        && pairs
          .Where(p => (p.First + 1) / 2 != (p.Second + 1) / 2)
          .All(p => c[p.First].Z >= c[p.Second].Z - 1e-9);
    else if (Regex.Match(order, @"^(pairs|rows)(\d+)$") is { Success: true } m) {
      int n = int.Parse(m.Groups[2].Value);
      int Row(int k) => (k - 1) / n;
      if (m.Groups[1].Value == "pairs")
        ok =
          ks.All(k => c[k].X < rx == ((k - 1) % 2 == 0))
          && pairs
            .Where(p =>
              Row(p.First) == Row(p.Second)
              && (p.First - 1) % n / 2 != (p.Second - 1) % n / 2
            )
            .All(p =>
              Math.Abs(c[p.First].X - rx) <= Math.Abs(c[p.Second].X - rx) + 1e-9
            )
          && ks.All(a =>
            ks.Where(b => Row(a) < Row(b)).All(b => c[a].Z > c[b].Z)
          );
      else
        ok =
          ks.All(k =>
            (k - 1) % n == 0
              ? Math.Abs(c[k].X - rx) < 3
              : c[k].X < rx == ((k - 1) % n == 1)
          )
          && pairs
            .Where(p => Row(p.First) != Row(p.Second))
            .All(p => c[p.First].Z > c[p.Second].Z);
    } else
      throw new CheckUsageException($"unknown pour order '{order}'");
    lines.Add(
      $"order {order}: {string.Join(" ", ks.Select(k => $"Piece{k}({Fmt.G(c[k].X)},{Fmt.G(c[k].Z)})"))}{(ok ? "" : "  FAIL")}"
    );
    return !ok;
  }

  private static HashSet<(int, int)> Cells(IEnumerable<Box> boxes) {
    var set = new HashSet<(int, int)>();
    foreach (Box b in boxes) {
      int i0 = (int)Math.Ceiling(b.Lo.X / Cell - 0.5 - 1e-6),
        i1 = (int)Math.Floor(b.Hi.X / Cell - 0.5 + 1e-6),
        j0 = (int)Math.Ceiling(b.Lo.Z / Cell - 0.5 - 1e-6),
        j1 = (int)Math.Floor(b.Hi.Z / Cell - 0.5 + 1e-6);
      for (int i = i0; i <= i1; i++)
        for (int j = j0; j <= j1; j++)
          set.Add((i, j));
    }
    return set;
  }

  private static int[] Bounds(HashSet<(int, int)> cells) =>
    [
      cells.Min(c => c.Item1),
      cells.Max(c => c.Item1),
      cells.Min(c => c.Item2),
      cells.Max(c => c.Item2),
    ];
}
