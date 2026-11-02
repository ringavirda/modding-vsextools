using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Whether no element is longer than a block on any axis. One line per fault:
/// <c>PATH: x is 16.5 voxels, over 16</c>.
/// </summary>
public sealed class Split : ICheck {
  private const double Limit = 16;

  /// <inheritdoc />
  public string Id => "split";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    var lines = new List<string>();
    List<Node> leaves = [.. new Tree(input.Shape).Leaves];
    foreach (Node leaf in leaves)
      for (int a = 0; a < 3; a++)
        if (leaf.Size[a] > Limit + 1e-9)
          lines.Add(
            $"{leaf.Path}: {"xyz"[a]} is {Fmt.G(leaf.Size[a])} voxels, over {Limit}"
          );
    bool faults = lines.Count > 0;
    if (!faults)
      lines.Add($"split: {leaves.Count} elements ok");
    return CheckResult.Of(Id, leaves.Count, faults, lines);
  }
}
