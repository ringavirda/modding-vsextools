using System;
using System.IO;

namespace ExpandedLib.Shapes.Core.Tests;

internal static class WorkspacePath {
  /// <summary><paramref name="relativePath"/> resolved against the nearest ancestor of the test
  /// binary that carries its first path segment as a directory; null when none holds it.</summary>
  public static string? Of(string relativePath) {
    string first = relativePath.Split('/')[0];
    for (
      DirectoryInfo? dir = new(AppContext.BaseDirectory);
      dir != null;
      dir = dir.Parent
    ) {
      if (!Directory.Exists(Path.Combine(dir.FullName, first)))
        continue;
      string candidate = Path.Combine(dir.FullName, relativePath);
      return File.Exists(candidate) || Directory.Exists(candidate)
        ? candidate
        : null;
    }
    return null;
  }
}
