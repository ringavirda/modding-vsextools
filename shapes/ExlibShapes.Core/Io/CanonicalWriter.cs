using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Core;

/// <summary>Writes the files the tool generates: fixed key order, two-space indent, LF, numbers
/// rounded to 1e-4 in shortest invariant form. The same shape always gives the same bytes.</summary>
public static class CanonicalWriter {
  /// <summary>The canonical text of <paramref name="shape"/>, ending in a line feed.</summary>
  public static string Write(Shape shape) =>
    JsonLayout.Canonical.Print(ShapeJson.Build(shape, asRead: false)) + "\n";

  /// <summary>Writes <paramref name="shape"/> to <paramref name="path"/> through <see cref="AtomicFile"/>.</summary>
  /// <exception cref="System.IO.IOException">The file cannot be written.</exception>
  public static void WriteFile(Shape shape, string path) =>
    AtomicFile.WriteAllText(path, Write(shape));
}
