namespace ExpandedLib.Shapes.Core;

/// <summary>Saves a shape in the layout Vintage Story Model Creator writes (tabs, CRLF, plain-valued
/// objects and arrays on one line, a blank line between clips), with keys in the file's own order and
/// numbers in the forms the file used, so a diff of an edited file shows only the edit.</summary>
public static class VsmcWriter {
  /// <summary>The text of <paramref name="shape"/>; it ends in a line break only when the file it was
  /// read from did (<see cref="Shape.FinalNewline"/>).</summary>
  public static string Write(Shape shape) =>
    JsonLayout.Vsmc.Print(ShapeJson.Build(shape, asRead: true))
    + (shape.FinalNewline ? "\r\n" : "");

  /// <summary>Writes <paramref name="shape"/> to <paramref name="path"/> through <see cref="AtomicFile"/>.</summary>
  /// <exception cref="System.IO.IOException">The file cannot be written.</exception>
  public static void WriteFile(Shape shape, string path) =>
    AtomicFile.WriteAllText(path, Write(shape));
}
