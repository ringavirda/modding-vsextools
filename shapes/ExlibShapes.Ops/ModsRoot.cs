using System.IO;

namespace ExpandedLib.Shapes.Ops;

/// <summary>Finds the checkout that holds <c>mods/</c> for a root found by a <c>workbench</c> marker.</summary>
public static class ModsRoot {
  /// <summary>
  /// <paramref name="root"/> when it holds <c>mods/</c>; else the <c>exmods</c> folder of the nearest
  /// ancestor (<paramref name="root"/> included) holding <c>exmod.workspace.json</c>, when that
  /// folder holds <c>mods/</c>; else <paramref name="root"/>.
  /// </summary>
  public static string Of(string root) {
    if (Directory.Exists(Path.Combine(root, "mods")))
      return root;
    for (string? d = root; d != null; d = Path.GetDirectoryName(d))
      if (File.Exists(Path.Combine(d, "exmod.workspace.json"))) {
        string exmods = Path.Combine(d, "exmods");
        return Directory.Exists(Path.Combine(exmods, "mods")) ? exmods : root;
      }
    return root;
  }
}
