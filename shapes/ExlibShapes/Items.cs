using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExpandedLib.Assets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Vintagestory.API.Common;

namespace ExpandedLib.Shapes;

/// <summary>
/// Draws one itemtype variant the way a player meets it: an item with a shape of its own gets the
/// isometric render that shape makes, painted with the itemtype's texture map; an item that is a
/// flat icon gets that icon enlarged on the same paper the renders use.
/// </summary>
public static partial class ItemViews {
  /// <summary>How many pixels a flat icon's own texel is drawn as - a 32-texel icon reads at the
  /// size of a page's other figures rather than as a thumbnail.</summary>
  public const int IconScale = 4;

  /// <summary>The views <see cref="Write"/> draws for a shape: <c>iso</c>, <c>gui</c>, <c>tp</c>
  /// and <c>ground</c>.</summary>
  public static readonly IReadOnlyList<string> Views =
  [
    "iso",
    "gui",
    "tp",
    "ground",
  ];

  // Slot edge in screen pixels at GUI scale 1, and the pixels of picture drawn per screen pixel.
  private const int SlotSize = 48;
  internal const int GuiPpu = 4;

  // Pixels per voxel of the ground view, fixed so a small item draws small beside the block.
  private const int GroundPpu = 8;

  // The grey of the slot squares and the stone block, and the darker slot outline.
  private static readonly SKColor SlotFill = new(139, 139, 139);
  private static readonly SKColor SlotEdge = new(90, 90, 90);
  private static readonly byte[] StoneRgb = [150, 150, 146];

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
  /// <param name="views">Null draws the pictures above. A list draws exactly the views it names,
  /// each to <c>&lt;stem&gt;-&lt;view&gt;.png</c>: <c>iso</c> is the isometric render of the shape,
  /// <c>gui</c> the item in the centre slot of a 3 x 3 grid of inventory slots, <c>tp</c> the
  /// player holding the item seen from the right side and from the front right, <c>ground</c> the
  /// item lying on a stone block. A view needing a shape on an itemtype without one is a warning
  /// and no picture, and <c>tp</c> needs the game's player shape among the index's assets.</param>
  public static JObject Write(
    string file,
    Variant variant,
    BlockIndex index,
    string outDir,
    int ppu = 24,
    IReadOnlyList<string>? views = null
  ) {
    ResolvedBlock item = WithSingularTexture(
      index.ResolveVariant(variant),
      variant
    );
    Directory.CreateDirectory(outDir);
    string stem = Path.GetFileNameWithoutExtension(file);
    var files = new List<string>();
    var warnings = new List<string>();
    var missing = new SortedSet<string>(StringComparer.Ordinal);
    var unpainted = new SortedSet<string>(StringComparer.Ordinal);

    if (views != null) {
      foreach (string view in views)
        if (!Views.Contains(view))
          throw new ArgumentException(
            $"no such view: {view} (one of {string.Join(", ", Views)})",
            nameof(views)
          );
    }

    if (item.ShapePath != null) {
      (
        LoadedShape shape,
        TextureSet textures,
        IReadOnlyDictionary<string, TextureRef> values
      ) = Compose(item, index);
      ItemTransform gui = ReadTransform(variant, GuiKey);
      ItemTransform tp = ReadTransform(variant, TpKey);
      ItemTransform ground = ReadTransform(variant, GroundKey);
      string? idle = ClipOf(variant, "heldTpIdleAnimation");
      string? ready = ClipOf(variant, "heldRightReadyAnimation");
      Seraph? seraph = views?.Contains("tp") == true ? LoadSeraph(index) : null;
      foreach (string view in views ?? ["iso"]) {
        if (view == "tp" && seraph == null) {
          warnings.Add(
            $"{item.Code}: the player shape is not found; tp not drawn"
          );
          continue;
        }
        string path = Path.Combine(outDir, $"{stem}-{view}.png");
        using SKBitmap image = DrawView(
          view,
          shape,
          textures,
          gui,
          tp,
          ground,
          ppu,
          seraph,
          idle,
          ready
        );
        Save(image, path);
        files.Add(path);
      }
      if (seraph != null)
        warnings.AddRange(TpWarnings(seraph.Shape, idle, ready));
      NoteTextures(item, textures, values, missing, unpainted);
    } else if (views != null)
      warnings.Add(
        $"{item.Code}: no shape; {string.Join(", ", views)} not drawn"
      );

    if (views == null && IconOf(variant) is { } icon) {
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

    if (files.Count == 0 && views == null)
      warnings.Add(
        $"{item.Code}: neither a shape nor a texture; nothing to draw"
      );
    foreach (
      (string selector, IReadOnlyList<string> spanned) in index.Ambiguities
    )
      warnings.Add(
        $"{selector}: ambiguous between {string.Join(", ", spanned)}"
      );
    warnings.AddRange(index.ParseWarnings);

    var manifest = new JObject {
      ["files"] = new JArray(files),
      ["variant"] = item.Code,
      ["missingTextures"] = new JArray(missing),
      ["unpaintedFaces"] = new JArray(unpainted),
      ["warnings"] = new JArray(warnings),
    };
    File.WriteAllText(
      Path.Combine(outDir, $"{stem}.json"),
      manifest.ToString(Formatting.Indented)
    );
    return manifest;
  }

  /// <summary>The flat icon <paramref name="variant"/> declares - its <c>texture</c> entry, read
  /// through the game's own ByType rule - or null when it draws a shape instead.</summary>
  public static TextureRef? IconOf(Variant variant) =>
    BlockTypeResolution.ByType(variant.Raw, "texture", variant.Path)
      is { } entry
      ? BlockIndex.TextureOf(entry, variant)
      : null;

  // The singular `texture` is the `all` texture, set over any `all` the textures map names
  // (CollectibleType.OnDeserialized); the shape keys the map does not name take it through `all`.
  private static ResolvedBlock WithSingularTexture(
    ResolvedBlock item,
    Variant variant
  ) =>
    IconOf(variant) is { } all
      ? item with {
        Textures = new Dictionary<string, TextureRef>(item.Textures) {
          ["all"] = all,
        },
      }
      : item;

  // The texture drawn at IconScale pixels per texel on the renders' own paper, each texel taken as
  // it is rather than blurred between its neighbours, and its transparency laid over the paper.
  private static SKBitmap Enlarged(byte[,,] texture) {
    int height = texture.GetLength(0),
      width = texture.GetLength(1);
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
          (byte)
            Math.Round(
              texture[y, x, 0] * alpha + Renderer.Background.Red * (1 - alpha)
            ),
          (byte)
            Math.Round(
              texture[y, x, 1] * alpha + Renderer.Background.Green * (1 - alpha)
            ),
          (byte)
            Math.Round(
              texture[y, x, 2] * alpha + Renderer.Background.Blue * (1 - alpha)
            )
        );
        for (int dy = 0; dy < IconScale; dy++)
          for (int dx = 0; dx < IconScale; dx++)
            image.SetPixel(
              IconMargin + x * IconScale + dx,
              IconMargin + y * IconScale + dy,
              color
            );
      }
    return image;
  }

  private static void Save(SKBitmap image, string path) {
    using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
    using FileStream stream = File.Create(path);
    data.SaveTo(stream);
  }

  // The clip an itemtype names under key, read through the ByType rule; null when it sets none.
  private static string? ClipOf(Variant variant, string key) =>
    BlockTypeResolution.ByType(variant.Raw, key, variant.Path)
      is { Type: JTokenType.String } clip
      ? (string?)clip
      : null;

  private static SKBitmap DrawView(
    string view,
    LoadedShape shape,
    TextureSet textures,
    ItemTransform gui,
    ItemTransform tp,
    ItemTransform ground,
    int ppu,
    Seraph? seraph,
    string? idle,
    string? ready
  ) =>
    view switch {
      "gui" => RenderGui(shape, textures, gui),
      "tp" => RenderTp(shape, textures, tp, seraph!, idle, ready),
      "ground" => RenderGround(shape, textures, ground),
      _ => Renderer.Render(
        shape,
        Renderer.NamedViews[Presentation.ViewName],
        ppu: ppu,
        textures: textures,
        grid: false,
        cull: false
      ),
    };

  private static void NoteTextures(
    ResolvedBlock item,
    TextureSet textures,
    IReadOnlyDictionary<string, TextureRef> values,
    SortedSet<string> missing,
    SortedSet<string> unpainted
  ) {
    foreach (string key in textures.Missing) {
      string name = key[(key.IndexOf('_') + 1)..];
      if (values[key].Unassigned)
        unpainted.Add($"{item.Code}: face texture {name} is assigned nothing");
      else
        missing.Add($"{item.Code}: texture {name} ({values[key]}) not found");
    }
  }

  // The item's composed shape and its textures, with the texture values the itemtype assigned.
  internal static (
    LoadedShape Shape,
    TextureSet Textures,
    IReadOnlyDictionary<string, TextureRef> Values
  ) Compose(ResolvedBlock item, BlockIndex index) {
    (JObject raw, Dictionary<string, TextureRef> values) = BlockViews.Compose(
      item
    );
    Shape shape =
      JsonConvert.DeserializeObject<Shape>(raw.ToString())
      ?? throw new JsonException(
        $"{item.Code}: the composed item shape failed to parse"
      );
    TextureSet textures = TextureSet.FromResolved(values, index.ResolveTexture);
    // Compose already kept the entry's selectiveElements and wrapped the tree, so the patterns
    // match nothing here; a second pass would empty the picture.
    return (
      ShapeFile.FromRaw(
        shape,
        item.ShapePath,
        new Dictionary<string, string>()
      ),
      textures,
      values
    );
  }

  /// <summary>
  /// The item layer of the gui view: <paramref name="shape"/> placed by
  /// <see cref="SlotModel"/> on a transparent canvas covering the 3 x 3 slot grid, at
  /// <see cref="GuiPpu"/> pixels per screen pixel, so the centre slot is the square from
  /// <c>SlotSize * GuiPpu</c> to <c>2 * SlotSize * GuiPpu</c> on both axes.
  /// </summary>
  internal static SKBitmap RenderGuiItem(
    LoadedShape shape,
    TextureSet textures,
    ItemTransform transform
  ) {
    double half = 1.5 * SlotSize;
    return Renderer.Render(
      [new Renderer.RenderPart(shape, textures, null, SlotModel(transform))],
      Renderer.NamedViews["south"],
      GuiPpu,
      grid: false,
      edges: false,
      transparent: true,
      fitTo: new Renderer.Canvas(-half, half, -half, half, 0, 0, 0, 0)
    );
  }

  /// <summary>The gui view: <see cref="RenderGuiItem"/> laid over a 3 x 3 grid of grey slots on
  /// the renders' paper.</summary>
  internal static SKBitmap RenderGui(
    LoadedShape shape,
    TextureSet textures,
    ItemTransform transform
  ) {
    using SKBitmap item = RenderGuiItem(shape, textures, transform);
    var image = new SKBitmap(
      item.Width,
      item.Height,
      SKColorType.Rgba8888,
      SKAlphaType.Unpremul
    );
    using var canvas = new SKCanvas(image);
    canvas.Clear(Renderer.Background);
    using var fill = new SKPaint { Color = SlotFill };
    using var edge = new SKPaint { Color = SlotEdge };
    int slot = SlotSize * GuiPpu;
    for (int row = 0; row < 3; row++)
      for (int col = 0; col < 3; col++) {
        canvas.DrawRect(col * slot, row * slot, slot, slot, edge);
        canvas.DrawRect(
          col * slot + GuiPpu,
          row * slot + GuiPpu,
          slot - 2 * GuiPpu,
          slot - 2 * GuiPpu,
          fill
        );
      }
    canvas.DrawBitmap(item, 0, 0);
    return image;
  }

  /// <summary>The ground view: the item posed by <see cref="GroundModel"/> on the top face of a
  /// stone-grey block, from the south-east at <c>GroundPpu</c> pixels per voxel.</summary>
  internal static SKBitmap RenderGround(
    LoadedShape shape,
    TextureSet textures,
    ItemTransform transform
  ) {
    var stone = new byte[16, 16, 4];
    for (int y = 0; y < 16; y++)
      for (int x = 0; x < 16; x++) {
        stone[y, x, 0] = StoneRgb[0];
        stone[y, x, 1] = StoneRgb[1];
        stone[y, x, 2] = StoneRgb[2];
        stone[y, x, 3] = 255;
      }
    var face = new { texture = "#stone", uv = new[] { 0, 0, 16, 16 } };
    LoadedShape block = ShapeFile.FromRaw(
      JsonConvert.DeserializeObject<Shape>(
        JsonConvert.SerializeObject(
          new {
            elements = new[]
            {
              new
              {
                name = "stone",
                from = new[] { 0, 0, 0 },
                to = new[] { 16, 16, 16 },
                faces = new
                {
                  north = face,
                  east = face,
                  south = face,
                  west = face,
                  up = face,
                  down = face,
                },
              },
            },
          }
        )
      )!,
      null,
      new Dictionary<string, string>()
    );
    TextureSet blockTextures = TextureSet.FromResolved(
      new Dictionary<string, TextureRef>(),
      _ => null,
      new Dictionary<string, byte[,,]> { ["stone"] = stone }
    );
    return Renderer.Render(
      [
        new Renderer.RenderPart(
          block,
          blockTextures,
          null,
          Renderer.Mat4d.Identity
        ),
        new Renderer.RenderPart(
          shape,
          textures,
          null,
          Renderer.Mat4d.Translate(8, 16, 8) * GroundModel(transform)
        ),
      ],
      Renderer.NamedViews[Presentation.ViewName],
      GroundPpu,
      grid: false
    );
  }
}
