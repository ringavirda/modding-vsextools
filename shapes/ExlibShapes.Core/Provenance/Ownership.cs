using System.IO;

namespace ExpandedLib.Shapes.Core;

/// <summary>Tells a file the tool generated from one fallen owns. A shape file is generated only when
/// its bytes hash to its provenance row; any other existing shape file is owned and agent writes to it
/// are refused. Part records are never owned and do not pass through here.</summary>
public static class Ownership {
  /// <summary>Whether writing <paramref name="path"/> is allowed: the file does not exist yet, or its
  /// current sha256 equals its provenance row's.</summary>
  public static bool CanWrite(Provenance provenance, string path) {
    path = Path.GetFullPath(path, provenance.BaseDir);
    if (!File.Exists(path))
      return true;
    ProvenanceRow? row = provenance.Find(path);
    return row != null
      && row.Sha256 == Provenance.Sha256Of(File.ReadAllBytes(path));
  }

  /// <summary>Throws unless <see cref="CanWrite"/>.</summary>
  /// <exception cref="ShapeWriteException">The file is owned; nothing was written.</exception>
  public static void Guard(Provenance provenance, string path) {
    if (!CanWrite(provenance, path))
      throw new ShapeWriteException(
        $"{path} is not generated (no provenance row matches its bytes); it is owned and not written"
      );
  }

  /// <summary>Writes <paramref name="shape"/> canonically to <paramref name="path"/> after
  /// <see cref="Guard"/>. The caller appends the provenance row.</summary>
  /// <exception cref="ShapeWriteException">The file is owned; nothing was written.</exception>
  /// <exception cref="IOException">The file cannot be written.</exception>
  public static void WriteGenerated(
    Provenance provenance,
    string path,
    Shape shape
  ) {
    Guard(provenance, path);
    CanonicalWriter.WriteFile(shape, path);
  }
}
