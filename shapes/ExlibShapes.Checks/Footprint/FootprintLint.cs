using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>Coverage of a machine's declared footprint cells (16-unit cubes) by leaf elements.</summary>
public static class FootprintLint {
  private const double Cell = 16.0;

  private static string CellText(IEnumerable<int> c) =>
    "(" + string.Join(", ", c) + ")";

  /// <summary>Lints one shape against its declared cells. A cell under 5% covered is a warning, as is a cell
  /// whose touching elements all start with a pipe prefix; an element reaching more than 0.25 across a cell
  /// outside the footprint is info.</summary>
  /// <param name="shape">The shape, at rest.</param>
  /// <param name="footprint">The declared cells, x y z; none declares nothing and checks nothing.</param>
  /// <param name="pipePrefixes">Path prefixes that mark pipe elements.</param>
  /// <returns>The findings, and the declared cells plus the leaf elements checked; 0 for no cells.</returns>
  public static (IReadOnlyList<Finding> Findings, int Enumerated) Check(
    Shape shape,
    IReadOnlyList<(int X, int Y, int Z)> footprint,
    IReadOnlyList<string>? pipePrefixes = null
  ) {
    if (footprint.Count == 0)
      return ([], 0);
    var tree = new Tree(shape);
    var frames = new Frames(shape);
    var boxes = tree.Leaves.ToDictionary(
      n => n.Path,
      n => WorldBox.Of(frames.Mat(n), n.Size)
    );
    var occupied = footprint.ToHashSet();
    var findings = new List<Finding>();
    foreach ((int cx, int cy, int cz) in footprint) {
      var lo = new Vec3(cx, cy, cz) * Cell;
      var cellBox = new Box(lo, lo + new Vec3(Cell, Cell, Cell));
      double vol = 0;
      var touching = new List<string>();
      foreach (var (path, box) in boxes) {
        double ov = Volume(box, cellBox);
        if (ov > 0) {
          vol += ov;
          touching.Add(path);
        }
      }
      double coverage = Math.Min(1.0, vol / (Cell * Cell * Cell));
      if (coverage < 0.05)
        findings.Add(
          new Finding(
            "warn",
            "footprint",
            "low_coverage",
            "",
            string.Create(
              CultureInfo.InvariantCulture,
              $"cell ({cx},{cy},{cz}) is only {100 * coverage:F1}% covered"
            )
          )
        );
      if (
        pipePrefixes is { Count: > 0 }
        && touching.Count > 0
        && touching.All(p =>
          pipePrefixes.Any(prefix =>
            p.StartsWith(prefix, StringComparison.Ordinal)
          )
        )
      )
        findings.Add(
          new Finding(
            "warn",
            "footprint",
            "pipe_only_cell",
            "",
            $"cell ({cx},{cy},{cz}) contains only pipe-tagged elements"
          )
        );
    }
    foreach (var (path, box) in boxes) {
      int[] loCells =
      [
        .. Enumerable
          .Range(0, 3)
          .Select(a => (int)Math.Floor(box.Lo[a] / Cell)),
      ];
      int[] hiCells =
      [
        .. Enumerable
          .Range(0, 3)
          .Select(a => (int)Math.Floor((box.Hi[a] - 1e-9) / Cell)),
      ];
      var spills = new List<string>();
      for (int cx = loCells[0]; cx <= hiCells[0]; cx++)
        for (int cy = loCells[1]; cy <= hiCells[1]; cy++)
          for (int cz = loCells[2]; cz <= hiCells[2]; cz++) {
            if (occupied.Contains((cx, cy, cz)))
              continue;
            var clo = new Vec3(cx, cy, cz) * Cell;
            double[] d =
            [
              .. Enumerable
            .Range(0, 3)
            .Select(a =>
              Math.Max(
                0,
                Math.Min(box.Hi[a], clo[a] + Cell) - Math.Max(box.Lo[a], clo[a])
              )
            ),
        ];
            if (d.All(v => v > 0) && d.Max() > 0.25)
              spills.Add(CellText([cx, cy, cz]));
          }
      if (spills.Count > 0)
        findings.Add(
          new Finding(
            "info",
            "footprint",
            "spill",
            path,
            $"{path} extends outside the declared footprint into [{string.Join(", ", spills)}]"
          )
        );
    }
    return (findings, footprint.Count + boxes.Count);
  }

  private static double Volume(Box a, Box b) {
    double v = 1;
    for (int i = 0; i < 3; i++)
      v *= Math.Max(0, Math.Min(a.Hi[i], b.Hi[i]) - Math.Max(a.Lo[i], b.Lo[i]));
    return v;
  }
}
