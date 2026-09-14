using System;
using System.IO;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Resolves a fixture file under <c>fixtures/</c> straight off disk - fixtures are never
/// copied into the test output (see the csproj's <c>CopyToOutputDirectory="Never"</c>), since
/// <see cref="ShapeFile.Load"/> reads a shape from wherever it actually lives.</summary>
internal static class FixturePath {
  /// <summary>The absolute path to <c>fixtures/&lt;rel&gt;</c>, found by walking up from the test
  /// binary's directory to the one holding <c>ExlibShapes.Tests.csproj</c>.</summary>
  public static string Of(string rel) {
    for (
      DirectoryInfo? dir = new(AppContext.BaseDirectory);
      dir != null;
      dir = dir.Parent
    ) {
      string csproj = Path.Combine(dir.FullName, "ExlibShapes.Tests.csproj");
      if (File.Exists(csproj))
        return Path.Combine(dir.FullName, "fixtures", rel);
    }
    throw new DirectoryNotFoundException(
      "Could not find ExlibShapes.Tests.csproj above " + AppContext.BaseDirectory
    );
  }

  /// <summary>The absolute path to <c>expected/&lt;rel&gt;</c>, alongside <see cref="Of"/>.</summary>
  public static string Expected(string rel) {
    for (
      DirectoryInfo? dir = new(AppContext.BaseDirectory);
      dir != null;
      dir = dir.Parent
    ) {
      string csproj = Path.Combine(dir.FullName, "ExlibShapes.Tests.csproj");
      if (File.Exists(csproj))
        return Path.Combine(dir.FullName, "expected", rel);
    }
    throw new DirectoryNotFoundException(
      "Could not find ExlibShapes.Tests.csproj above " + AppContext.BaseDirectory
    );
  }

  /// <summary>This checkout's own root (the directory holding <c>exmod.json</c>, two levels above
  /// <c>ExlibShapes.Tests.csproj</c>) - where CI and every contributor's own clone provisions
  /// <c>.game</c>, so a test naming only <c>.game</c> can assert it exists rather than skip.</summary>
  public static string RepoRoot {
    get {
      for (
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        dir != null;
        dir = dir.Parent
      ) {
        string csproj = Path.Combine(dir.FullName, "ExlibShapes.Tests.csproj");
        if (File.Exists(csproj))
          return dir.Parent!.Parent!.FullName;
      }
      throw new DirectoryNotFoundException(
        "Could not find ExlibShapes.Tests.csproj above " + AppContext.BaseDirectory
      );
    }
  }

  /// <summary>
  /// <paramref name="relativePath"/> (e.g. <c>"exmods/mods/iiex/.../blastcore.json"</c>) resolved
  /// against the nearest ancestor of <see cref="RepoRoot"/> that carries its first path segment as
  /// a directory - the sibling family checkout a contributor's own workspace clones next to this
  /// one, whether this repo sits there directly or nested under <c>.worktrees/</c>. Null when no
  /// such ancestor exists.
  /// </summary>
  public static string? Workspace(string relativePath) {
    string first = relativePath.Split('/')[0];
    for (DirectoryInfo? dir = new(RepoRoot); dir != null; dir = dir.Parent) {
      if (!Directory.Exists(Path.Combine(dir.FullName, first)))
        continue;
      string candidate = Path.Combine(dir.FullName, relativePath);
      return File.Exists(candidate) ? candidate : null;
    }
    return null;
  }
}
