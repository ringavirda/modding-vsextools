using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ExpandedLib.Shapes.Ops;

/// <summary>One drawing of a block and what kind of file it is.</summary>
/// <param name="Kind"><c>shipped</c> (an asset of a mod), <c>old</c> (in an <c>old</c> folder), <c>sketch</c>,
/// <c>proposed</c> or <c>editable</c> (any other).</param>
/// <param name="Path">The file.</param>
internal sealed record BlockDrawing(string Kind, string Path);

/// <summary>Finds every drawing of a block before a round starts from one of them.</summary>
internal static class BlockDrawings {
  private static readonly string[] Skipped =
  [
    ".git",
    ".worktrees",
    "bin",
    "obj",
    "node_modules",
    "renders",
    "runtime",
  ];

  /// <summary>The block's stem: a drawing's file name without <c>.json</c> and a <c>-proposed</c> or <c>-sketch</c> tail.</summary>
  public static string Stem(string drawing) {
    string name = Path.GetFileNameWithoutExtension(drawing);
    foreach (string tail in new[] { "-proposed", "-sketch" })
      if (name.EndsWith(tail, StringComparison.Ordinal))
        return name[..^tail.Length];
    return name;
  }

  /// <summary>Every shape file under <paramref name="searchRoot"/> named like the block of
  /// <paramref name="drawing"/>: the plain file, its <c>-proposed</c> and <c>-sketch</c> forms, in any folder
  /// including <c>old</c>, and the shipped asset (a file under <c>assets</c>) named like the folder the drawing is in.
  /// Records, runtime copies and build output are not drawings. When <paramref name="searchRoot"/> sits in a workspace
  /// (a folder holding <c>exmod.workspace.json</c>) the workspace's <c>workbench</c> folder is searched too.</summary>
  /// <param name="drawing">A drawing of the block.</param>
  /// <param name="searchRoot">The tree to search.</param>
  public static List<BlockDrawing> Find(string drawing, string searchRoot) {
    string stem = Stem(drawing);
    var found = new List<BlockDrawing>();
    string folder = Path.GetFileName(
      Path.GetDirectoryName(Path.GetFullPath(drawing))
    )!;
    string root = Path.GetFullPath(searchRoot);
    Walk(root, stem, folder, found);
    string? parent = Path.GetDirectoryName(root);
    string? workbench =
      parent == null ? null : Path.Combine(parent, "workbench");
    if (
      workbench != null
      && Directory.Exists(workbench)
      && File.Exists(Path.Combine(parent!, "exmod.workspace.json"))
    )
      Walk(workbench, stem, folder, found);
    return [.. found.OrderBy(d => d.Path, StringComparer.Ordinal)];
  }

  /// <summary>The nearest ancestor of <paramref name="drawing"/> that holds a <c>workbench</c> folder, moved to the
  /// workspace's <c>exmods</c> folder when it holds no <c>mods/</c> (see <see cref="ModsRoot.Of"/>); the drawing's own
  /// directory when none does.</summary>
  public static string RootOf(string drawing) {
    string dir = Path.GetDirectoryName(Path.GetFullPath(drawing))!;
    for (string? d = dir; d != null; d = Path.GetDirectoryName(d))
      if (Directory.Exists(Path.Combine(d, "workbench")))
        return ModsRoot.Of(d);
    return dir;
  }

  private static void Walk(
    string dir,
    string stem,
    string folder,
    List<BlockDrawing> found
  ) {
    foreach (string file in Directory.EnumerateFiles(dir, "*.json")) {
      string name = Path.GetFileName(file);
      if (name.EndsWith(".part.json", StringComparison.Ordinal))
        continue;
      string kind = KindOf(file);
      if (
        Stem(file) == stem
        || (kind == "shipped" && Stem(file) == folder && InShapes(file))
      )
        found.Add(new BlockDrawing(kind, file));
    }
    foreach (string sub in Directory.EnumerateDirectories(dir))
      if (!Skipped.Contains(Path.GetFileName(sub)))
        Walk(sub, stem, folder, found);
  }

  private static bool InShapes(string file) {
    string[] parts = file.Split(Path.DirectorySeparatorChar);
    for (int i = 0; i + 2 < parts.Length; i++)
      if (parts[i] == "assets" && parts[i + 2] == "shapes")
        return true;
    return false;
  }

  private static string KindOf(string file) {
    string[] parts = file.Split(Path.DirectorySeparatorChar);
    string name = Path.GetFileNameWithoutExtension(file);
    if (parts.Contains("old"))
      return "old";
    if (name.EndsWith("-sketch", StringComparison.Ordinal))
      return "sketch";
    if (name.EndsWith("-proposed", StringComparison.Ordinal))
      return "proposed";
    return parts.Contains("assets") ? "shipped" : "editable";
  }
}
