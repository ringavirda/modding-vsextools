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
}
