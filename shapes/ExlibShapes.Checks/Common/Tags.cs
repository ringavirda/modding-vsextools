using System.Text.RegularExpressions;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>What a part record says about an element, and the names the cast set is read by.</summary>
internal static partial class Tags {
  [GeneratedRegex(@"_s\d+$")]
  private static partial Regex SplitSuffix();

  /// <summary>The record row of the element or, failing that, of its nearest tagged ancestor; null when none.</summary>
  public static ElementPart? Of(PartRecord? record, Node node) {
    if (record == null)
      return null;
    for (Node? n = node; n != null; n = n.Parent)
      if (record.Elements.TryGetValue(n.Name, out ElementPart? part))
        return part;
    return null;
  }

  /// <summary>Whether the row marks the element as a fastener, by stage or role.</summary>
  public static bool IsFastener(ElementPart? part) =>
    part is { Stage: "fastener" } or { Role: "fastener" };

  /// <summary>Whether the row marks the element as left out of the cast: worked or fastener by stage.</summary>
  public static bool LeftOutOfCast(ElementPart? part) =>
    part is { Stage: "fastener" or "worked" };

  /// <summary>The name without the <c>_sN</c> suffix a split part carries.</summary>
  public static string Unsplit(string name) => SplitSuffix().Replace(name, "");
}
