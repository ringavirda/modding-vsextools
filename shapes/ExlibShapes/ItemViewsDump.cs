using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace ExpandedLib.Shapes;

public static partial class ItemViews {
  /// <summary>
  /// Writes the pictures of a views dump into <paramref name="outDir"/> and the
  /// <c>&lt;stem&gt;.json</c> manifest beside them, which it returns: <c>files</c>, the dump's
  /// <c>view</c> and <c>kind</c>, <c>failing</c> (one <c>&lt;gate&gt; &lt;name&gt; &lt;target&gt;</c>
  /// line per gate that did not pass), the <c>missingTextures</c> whose value names a file that is
  /// not there and <c>warnings</c>. Each picture is <c>&lt;stem&gt;-&lt;view&gt;.png</c>, drawn
  /// from the transforms the dump carries as they stand.
  /// </summary>
  /// <param name="dumpPath">A <c>.views.json</c> file; the stem is its name less that suffix.</param>
  /// <param name="index">The index the shape and the textures resolve through.</param>
  /// <param name="outDir">Created when absent.</param>
  /// <param name="ppu">Pixels per shape unit for the isometric render.</param>
  /// <param name="views">The views to draw, from <see cref="Views"/>.</param>
  /// <param name="seraphFile">A player shape file posed in place of the game's, or null.</param>
  /// <param name="frame">The frame of the held clip the player is posed at.</param>
  /// <exception cref="InvalidDataException"><paramref name="dumpPath"/> is not an
  /// <c>item-views/1</c> file.</exception>
  /// <exception cref="ArgumentException">A name in <paramref name="views"/> is not a view.</exception>
  public static JObject WriteDump(
    string dumpPath,
    BlockIndex index,
    string outDir,
    int ppu,
    IReadOnlyList<string> views,
    string? seraphFile = null,
    int frame = 0
  ) {
    foreach (string view in views)
      if (!Views.Contains(view))
        throw new ArgumentException(
          $"no such view: {view} (one of {string.Join(", ", Views)})",
          nameof(views)
        );
    ViewsFile dump = ViewsFile.Load(dumpPath);
    Directory.CreateDirectory(outDir);
    string name = Path.GetFileName(dumpPath);
    string stem = name.EndsWith(".views.json", StringComparison.Ordinal)
      ? name[..^".views.json".Length]
      : Path.GetFileNameWithoutExtension(name);
    var files = new List<string>();
    var warnings = new List<string>();
    var missing = new SortedSet<string>(StringComparer.Ordinal);
    var unpainted = new SortedSet<string>(StringComparer.Ordinal);

    ResolvedBlock item = ItemOf(dump, index);
    if (item.ShapePath != null) {
      (
        LoadedShape shape,
        TextureSet textures,
        IReadOnlyDictionary<string, TextureRef> values
      ) = Compose(item, index);
      Seraph? seraph = views.Any(v => v is "tp" or "fp")
        ? LoadSeraph(index, seraphFile, frame)
        : null;
      foreach (string view in views) {
        if (view is "tp" or "fp" && seraph == null) {
          warnings.Add(
            $"{dump.View}: the player shape is not found; {view} not drawn"
          );
          continue;
        }
        string path = Path.Combine(outDir, $"{stem}-{view}.png");
        using SKBitmap image = DrawView(
          view,
          shape,
          textures,
          dump.Gui,
          dump.Tp,
          dump.Ground,
          ppu,
          seraph,
          dump.Clips?.Idle,
          dump.Clips?.Ready,
          dump.Type == "block"
        );
        Save(image, path);
        files.Add(path);
      }
      if (seraph != null)
        warnings.AddRange(
          TpWarnings(seraph.Shape, dump.Clips?.Idle, dump.Clips?.Ready)
        );
      if (seraph != null && views.Contains("fp"))
        warnings.AddRange(
          FpWarnings(seraph.Shape, dump.Clips?.Idle, dump.Clips?.Ready)
        );
      NoteTextures(item, textures, values, missing, unpainted);
    } else
      warnings.Add(
        $"{dump.View}: shape {dump.Shape.Base} not found; {string.Join(", ", views)} not drawn"
      );
    warnings.AddRange(index.ParseWarnings);

    var manifest = new JObject {
      ["files"] = new JArray(files),
      ["view"] = dump.View,
      ["kind"] = dump.Kind,
      ["failing"] = new JArray(
        dump.Gates.Where(g => !g.Pass)
          .Select(g => $"{g.Gate} {g.Name} {g.Target}")
      ),
      ["missingTextures"] = new JArray(missing),
      ["warnings"] = new JArray(warnings),
    };
    File.WriteAllText(
      Path.Combine(outDir, $"{stem}.json"),
      manifest.ToString(Formatting.Indented)
    );
    return manifest;
  }

  // The block a dump's shape and textures name, its shape resolved through the index.
  internal static ResolvedBlock ItemOf(ViewsFile dump, BlockIndex index) =>
    new(
      dump.Codes.FirstOrDefault() ?? dump.View,
      index.ShapePath(dump.Shape.Base),
      dump.Shape.RotateX,
      dump.Shape.RotateY,
      dump.Shape.RotateZ,
      dump.Textures.ToDictionary(t => t.Key, t => new TextureRef(t.Value)),
      dump.Shape.SelectiveElements
    );
}
