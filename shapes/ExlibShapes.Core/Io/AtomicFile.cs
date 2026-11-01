using System;
using System.IO;
using System.Text;

namespace ExpandedLib.Shapes.Core;

/// <summary>Writes a file so a reader never sees it half written.</summary>
public static class AtomicFile {
  /// <summary>Writes <paramref name="text"/> as UTF-8 without a byte order mark, unchanged, to a
  /// temporary file beside <paramref name="path"/> and renames it into place. Creates missing
  /// directories; replaces an existing file; leaves no temporary file behind on failure.</summary>
  /// <exception cref="IOException">The file cannot be written or replaced.</exception>
  public static void WriteAllText(string path, string text) {
    string full = Path.GetFullPath(path);
    string dir = Path.GetDirectoryName(full)!;
    Directory.CreateDirectory(dir);
    string temp = Path.Combine(
      dir,
      $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp"
    );
    try {
      File.WriteAllText(temp, text, new UTF8Encoding(false));
      File.Move(temp, full, overwrite: true);
    } finally {
      if (File.Exists(temp))
        File.Delete(temp);
    }
  }
}
