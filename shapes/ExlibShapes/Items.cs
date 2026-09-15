using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Vintagestory.API.Common;
using ExpandedLib.Assets;

namespace ExpandedLib.Shapes;

/// <summary>
/// Draws one itemtype variant the way a player meets it: an item with a shape of its own gets the
/// isometric render that shape makes, painted with the itemtype's texture map; an item that is a
/// flat icon gets that icon enlarged on the same paper the renders use.
/// </summary>
public static class ItemViews {
  /// <summary>How many pixels a flat icon's own texel is drawn as - a 32-texel icon reads at the
  /// size of a page's other figures rather than as a thumbnail.</summary>
  public const int IconScale = 4;

  // Paper left around an enlarged icon, in pixels.
  private const int IconMargin = 8;

  // The texture key the icon is composed under; an item's own `texture` entry names no key.
  private const string IconKey = "icon";

  /// <summary>
  /// Writes <paramref name="variant"/>'s pictures into <paramref name="outDir"/> and the
  /// <c>&lt;stem&gt;.json</c> manifest beside them, which it returns: <c>files</c>, the
  /// <c>variant</c> drawn, the <c>missingTextures</c> whose value names a file that is not there,
  /// the <c>unpaintedFaces</c> the itemtype assigns no texture at all, and <c>warnings</c>.
  /// <para>
  /// <c>&lt;stem&gt;-iso.png</c> is written when the itemtype declares a shape and
  /// <c>&lt;stem&gt;-icon.png</c> when it declares a flat <c>texture</c>; an itemtype declaring
  /// both gets both, and one declaring neither is a warning and no picture.
  /// </para>
  /// </summary>
  /// <param name="file">The itemtype file, whose own name is the stem of everything written.</param>
  /// <param name="variant">The variant to draw, from <see cref="BlockIndex.ItemVariants"/>.</param>
  /// <param name="index">The index the textures and the shape resolve through.</param>
  /// <param name="outDir">Created when absent.</param>
  /// <param name="ppu">Pixels per shape unit for the isometric render.</param>
  public static JObject Write(string file, Variant variant, BlockIndex index, string outDir, int ppu = 24) {
    ResolvedBlock item = index.ResolveVariant(variant);
    Directory.CreateDirectory(outDir);
    string stem = Path.GetFileNameWithoutExtension(file);
    var files = new List<string>();
    var warnings = new List<string>();
    var missing = new SortedSet<string>(StringComparer.Ordinal);
    var unpainted = new SortedSet<string>(StringComparer.Ordinal);

    if (item.ShapePath != null) {
      (JObject raw, Dictionary<string, TextureRef> values) = BlockViews.Compose(item);
      Shape shape =
        JsonConvert.DeserializeObject<Shape>(raw.ToString())
        ?? throw new JsonException($"{variant.Code}: the composed item shape failed to parse");
      TextureSet textures = TextureSet.FromResolved(values, index.ResolveTexture);
      string path = Path.Combine(outDir, $"{stem}-iso.png");
      using (
        SKBitmap image = Renderer.Render(
          ShapeFile.FromRaw(shape, item.ShapePath, new Dictionary<string, string>(), item.Selective),
          Renderer.NamedViews[Presentation.ViewName],
          ppu: ppu,
          textures: textures,
          grid: false,
          cull: false
        )
      )
        Save(image, path);
      files.Add(path);
      foreach (string key in textures.Missing) {
        string name = key[(key.IndexOf('_') + 1)..];
        if (values[key].Unassigned)
          unpainted.Add($"{item.Code}: face texture {name} is assigned nothing");
        else
          missing.Add($"{item.Code}: texture {name} ({values[key]}) not found");
      }
    }

    if (IconOf(variant) is { } icon) {
      TextureSet textures = TextureSet.FromResolved(
        new Dictionary<string, TextureRef> { [IconKey] = icon },
        index.ResolveTexture
      );
      string path = Path.Combine(outDir, $"{stem}-icon.png");
      using (SKBitmap image = Enlarged(textures.Get(IconKey)))
        Save(image, path);
      files.Add(path);
      foreach (string key in textures.Missing)
        missing.Add($"{item.Code}: texture {key} ({icon}) not found");
    }

    if (files.Count == 0)
      warnings.Add($"{item.Code}: neither a shape nor a texture; nothing to draw");
    foreach ((string selector, IReadOnlyList<string> spanned) in index.Ambiguities)
      warnings.Add($"{selector}: ambiguous between {string.Join(", ", spanned)}");
    warnings.AddRange(index.ParseWarnings);

    var manifest = new JObject {
      ["files"] = new JArray(files),
      ["variant"] = item.Code,
      ["missingTextures"] = new JArray(missing),
      ["unpaintedFaces"] = new JArray(unpainted),
      ["warnings"] = new JArray(warnings),
    };
    File.WriteAllText(Path.Combine(outDir, $"{stem}.json"), manifest.ToString(Formatting.Indented));
    return manifest;
  }

  /// <summary>The flat icon <paramref name="variant"/> declares - its <c>texture</c> entry, read
  /// through the game's own ByType rule - or null when it draws a shape instead.</summary>
  public static TextureRef? IconOf(Variant variant) =>
    BlockTypeResolution.ByType(variant.Raw, "texture", variant.Path) is { } entry
      ? BlockIndex.TextureOf(entry, variant)
      : null;

  // The texture drawn at IconScale pixels per texel on the renders' own paper, each texel taken as
  // it is rather than blurred between its neighbours, and its transparency laid over the paper.
  private static SKBitmap Enlarged(byte[,,] texture) {
    int height = texture.GetLength(0), width = texture.GetLength(1);
    var image = new SKBitmap(
      width * IconScale + 2 * IconMargin,
      height * IconScale + 2 * IconMargin,
      SKColorType.Rgba8888,
      SKAlphaType.Unpremul
    );
    using (var canvas = new SKCanvas(image))
      canvas.Clear(Renderer.Background);
    for (int y = 0; y < height; y++)
      for (int x = 0; x < width; x++) {
        double alpha = texture[y, x, 3] / 255.0;
        var color = new SKColor(
          (byte)Math.Round(texture[y, x, 0] * alpha + Renderer.Background.Red * (1 - alpha)),
          (byte)Math.Round(texture[y, x, 1] * alpha + Renderer.Background.Green * (1 - alpha)),
          (byte)Math.Round(texture[y, x, 2] * alpha + Renderer.Background.Blue * (1 - alpha))
        );
        for (int dy = 0; dy < IconScale; dy++)
          for (int dx = 0; dx < IconScale; dx++)
            image.SetPixel(IconMargin + x * IconScale + dx, IconMargin + y * IconScale + dy, color);
      }
    return image;
  }

  private static void Save(SKBitmap image, string path) {
    using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
    using FileStream stream = File.Create(path);
    data.SaveTo(stream);
  }
}
