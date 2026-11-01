using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Whether the runner is where the station puts it. The filling that holds the box (an open pour's, a closed
/// pour's cope) holds exactly the station's runner box elements, by name and box within 1e-4, of role runner,
/// outside every Piece group, <c>BoxFloor</c>'s top at the sand's top and no sand in any box's volume, and no other
/// runner-role element; a closed pour's drag and a station with no runner box hold none. A filling that carries
/// feeds also holds each <c>runner</c> and <c>gate</c> feed to the station's depth from the sand's top (a closed
/// pour's, down from the parting) and at least the station's gate width across its narrow plan axis, with the first
/// runner, or the first gate where there is none, meeting the drop (a closed pour's, the sprue). One line per
/// fault. Reads axis-aligned leaves only; needs the part record's pour and the settings, the station from the input
/// or the pour.
/// </summary>
public sealed class Runner : ICheck {
  private const double Tol = 1e-4;

  /// <inheritdoc />
  public string Id => "runner";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    Pour pour = Pour.Read(input.Record);
    Settings settings =
      input.Settings
      ?? throw new CheckUsageException("runner reads the settings, none given");
    Station station =
      input.Station ?? Pouring.StationOf(settings, pour.Station);
    List<FillingLeaf> leaves = Pouring.Leaves(input.Shape, input, settings, Id);
    List<FillingLeaf> runners = [.. leaves.Where(l => l.Role == "runner")];
    var lines = new List<string>();
    int counted = leaves.Count;
    bool holds = station.RunnerBox.Count > 0 && !pour.Closed;
    if (!holds)
      lines.AddRange(
        runners.Select(r =>
          $"{r.Node.Path}: a runner-role element in a filling that holds no runner box"
        )
      );
    else {
      List<Box> sand =
      [
        .. leaves.Where(l => l.Role == "sand" && !l.Level).Select(l => l.Box),
      ];
      foreach (StationBox row in station.RunnerBox) {
        List<FillingLeaf> named =
        [
          .. leaves.Where(l => l.Node.Name == row.Name),
        ];
        if (named.Count != 1) {
          lines.Add(
            $"{row.Name}: {named.Count} elements of that name, one wanted"
          );
          continue;
        }
        FillingLeaf leaf = named[0];
        Box want = Pouring.Of(row.From, row.To);
        if (leaf.Role != "runner")
          lines.Add($"{leaf.Node.Path}: role {leaf.Role}, runner wanted");
        if (leaf.Piece != 0)
          lines.Add($"{leaf.Node.Path}: inside Piece{leaf.Piece}");
        if (!Near(leaf.Box, want))
          lines.Add(
            $"{leaf.Node.Path}: box {Fmt.List(Corners(leaf.Box))}, the station's is {Fmt.List(Corners(want))}"
          );
        if (
          row.Name == "BoxFloor"
          && Math.Abs(leaf.Box.Hi.Y - station.SandTo[1]) > Tol
        )
          lines.Add(
            $"{leaf.Node.Path}: top at y {Fmt.G(leaf.Box.Hi.Y)}, the sand's top is {Fmt.G(station.SandTo[1])}"
          );
        counted++;
        if (sand.Count > 0) {
          var grid = new BoxGrid(sand.Append(leaf.Box));
          double v = grid.Total(
            grid.Mark(sand)
              .Zip(grid.Mark([leaf.Box]), (a, b) => a && b)
              .ToArray()
          );
          if (v > 1e-6)
            lines.Add(
              $"{leaf.Node.Path}: {Fmt.G(v)} vx3 of sand in its volume"
            );
        }
      }
      string[] names = [.. station.RunnerBox.Select(r => r.Name)];
      lines.AddRange(
        runners
          .Where(r => !names.Contains(r.Node.Name))
          .Select(r =>
            $"{r.Node.Path}: a runner-role element the station's runner box does not have"
          )
      );
    }
    counted += Feeds(pour, station, lines);
    bool faults = lines.Count > 0;
    lines.Add($"runner: {counted} elements and feeds read");
    return CheckResult.Of(Id, counted, faults, lines);
  }

  private static int Feeds(Pour pour, Station station, List<string> lines) {
    List<Feed> cuts =
    [
      .. pour.Feeds.Where(f =>
        f.Kind is "runner" or "gate" && f.Boxes.Count > 0
      ),
    ];
    if (cuts.Count == 0)
      return 0;
    double depth = station.RunnerDepth ?? 0,
      across = station.GateAcross ?? 0;
    double top = pour.Closed
      ? station.Parting
        ?? throw new CheckUsageException(
          "runner: a closed pour needs a parting"
        )
      : station.SandTo[1];
    foreach (Feed f in cuts)
      foreach (Box b in f.Boxes) {
        if (
          Math.Abs(b.Hi.Y - top) > Tol
          || Math.Abs(b.Hi.Y - b.Lo.Y - depth) > Tol
        )
          lines.Add(
            $"{f.Name}: y {Fmt.G(b.Lo.Y)} to {Fmt.G(b.Hi.Y)}, wanted {Fmt.G(depth)} deep from {Fmt.G(top)}"
          );
        double narrow = Math.Min(b.Hi.X - b.Lo.X, b.Hi.Z - b.Lo.Z);
        if (narrow < across - Tol)
          lines.Add(
            $"{f.Name}: {Fmt.G(narrow)} across, at least {Fmt.G(across)} wanted"
          );
      }
    Feed first = cuts.FirstOrDefault(f => f.Kind == "runner") ?? cuts[0];
    Box[] target =
      pour.Closed
        ? [.. pour.Feeds.Where(f => f.Kind == "sprue").SelectMany(f => f.Boxes)]
      : station.Drop is double[] d ? [Pouring.Of(d)]
      : [];
    if (
      target.Length > 0
      && !first.Boxes.Any(b => target.Any(t => Touch(b, t)))
    )
      lines.Add(
        $"{first.Name}: does not meet the {(pour.Closed ? "sprue" : "drop")}"
      );
    return cuts.Sum(f => f.Boxes.Count);
  }

  private static bool Touch(Box a, Box b) =>
    Enumerable
      .Range(0, 3)
      .All(i => a.Lo[i] <= b.Hi[i] + Tol && b.Lo[i] <= a.Hi[i] + Tol);

  private static bool Near(Box a, Box b) =>
    Enumerable
      .Range(0, 3)
      .All(i =>
        Math.Abs(a.Lo[i] - b.Lo[i]) <= Tol && Math.Abs(a.Hi[i] - b.Hi[i]) <= Tol
      );

  private static double[] Corners(Box b) =>
    [b.Lo.X, b.Lo.Y, b.Lo.Z, b.Hi.X, b.Hi.Y, b.Hi.Z];
}
