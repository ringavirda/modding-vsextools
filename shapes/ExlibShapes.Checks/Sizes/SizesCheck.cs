using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Whether each named element spans the voxels the spec states along an axis. At rest, the element's extent on
/// the axis is the span of the world boxes of the leaves at and under its path, so a rod drawn as a parent and
/// children counts as its whole length. One line per fault: <c>PATH: spans S voxels on AXIS, the spec says N</c>
/// (difference over 1e-6), <c>PATH: no such element in the shape</c>, <c>PATH: draws no faces</c>.
/// </summary>
public sealed class SizesCheck : ICheck {
  /// <inheritdoc />
  public string Id => "sizes";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null || input.Sizes == null)
      return CheckResult.Of(Id, 0, false, []);
    foreach (SizeRow row in input.Sizes)
      if (row.Axis is not ("x" or "y" or "z"))
        throw new CheckUsageException(
          $"{row.Element}: the axis is x, y or z, not '{row.Axis}'"
        );
    var tree = new Tree(input.Shape);
    var rest = new Frames(input.Shape);
    var boxes = tree.Leaves.ToDictionary(
      n => n.Path,
      n => WorldBox.Of(rest.Mat(n), n.Size)
    );
    var lines = new List<string>();
    foreach (SizeRow row in input.Sizes) {
      int axis = "xyz".IndexOf(row.Axis, StringComparison.Ordinal);
      Node? part = tree.Find(row.Element);
      if (part == null) {
        lines.Add($"{row.Element}: no such element in the shape");
        continue;
      }
      List<Box> under =
      [
        .. boxes
          .Where(kv =>
            kv.Key == part.Path
            || kv.Key.StartsWith(part.Path + "/", StringComparison.Ordinal)
          )
          .Select(kv => kv.Value),
      ];
      if (under.Count == 0) {
        lines.Add($"{row.Element}: draws no faces");
        continue;
      }
      double span = under.Max(b => b.Hi[axis]) - under.Min(b => b.Lo[axis]);
      if (Math.Abs(span - row.Voxels) > 1e-6)
        lines.Add(
          $"{row.Element}: spans {Fmt.G(span)} voxels on {row.Axis}, the spec says {Fmt.G(row.Voxels)}"
        );
    }
    bool faults = lines.Count > 0;
    if (!faults)
      lines.Add($"sizes: {input.Sizes.Count} rows ok");
    return CheckResult.Of(Id, input.Sizes.Count, faults, lines);
  }
}
