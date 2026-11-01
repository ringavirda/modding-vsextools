using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Whether a cast item holds only what one pour makes. An element the item's part record tags as a fastener (by
/// stage or role) or as worked (by stage), itself or through its group, is not cast. One line per fault:
/// <c>PATH: tagged fastener, not cast</c> or <c>PATH: tagged worked, not cast</c>. Needs the item's record.
/// </summary>
public sealed class Castonly : ICheck {
  /// <inheritdoc />
  public string Id => "castonly";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    PartRecord record =
      input.Record
      ?? throw new CheckUsageException(
        "castonly reads the item's part record, none given"
      );
    var lines = new List<string>();
    List<Node> leaves = [.. new Tree(input.Shape).Leaves];
    foreach (Node leaf in leaves) {
      ElementPart? part = Tags.Of(record, leaf);
      if (Tags.IsFastener(part))
        lines.Add($"{leaf.Path}: tagged fastener, not cast");
      else if (Tags.LeftOutOfCast(part))
        lines.Add($"{leaf.Path}: tagged worked, not cast");
    }
    bool faults = lines.Count > 0;
    if (!faults)
      lines.Add($"castonly: {leaves.Count} elements ok");
    return CheckResult.Of(Id, leaves.Count, faults, lines);
  }
}
