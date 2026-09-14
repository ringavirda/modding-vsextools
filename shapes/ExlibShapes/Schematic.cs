using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Vintagestory.API.Common;

namespace ExpandedLib.Shapes;

/// <summary>One legend row's own fields, keyed by cell number in <see cref="Schematic.Manifest"/>'s
/// caller: the colour every <c>plan_svg</c>/<c>iso_png</c> call shares for a number, plus whatever
/// a <see cref="BlockIndex"/> lookup added once one was available.</summary>
public sealed class LegendEntry {
  /// <summary>The palette colour this number's cells fill with, as <c>#RRGGBB</c>.</summary>
  public string? Color { get; set; }

  /// <summary>The selector's resolved representative code, or null when it has not been resolved
  /// yet (a plain <see cref="Schematic.LegendColors"/> layout has none) or could not be.</summary>
  public string? Representative { get; set; }

  /// <summary>Whether the selector's first-resolving alternative is <c>air</c> - drawn as empty
  /// space by <see cref="Schematic.PlanSvg"/>/<see cref="Schematic.IsoPng"/> rather than warned
  /// about.</summary>
  public bool Optional { get; set; }
}

/// <summary>
/// Turns a <see cref="Layout"/> and a <see cref="BlockIndex"/> into the pictures a player reads as
/// a build schematic: one plan-grid SVG per Y layer, an isometric textured composite, and a
/// manifest the wiki's directive reads. Composition builds one raw shape JSON from every resolved
/// cell's own shape elements, translated to the cell's world offset and rotated about the cell's
/// own centre by its <c>shapeByType</c> rotation, and hands it to <see cref="Renderer"/> exactly as
/// any other shape.
/// </summary>
public static class Schematic {
  private static readonly string[] Palette = [
    "#4C72B0", "#DD8452", "#55A868", "#C44E52", "#8172B2",
    "#937860", "#DA8BC3", "#8C8C8C", "#CCB974", "#64B5CD",
  ];
  private const string FillerColor = "#BFBFBF";
  private const string FillerTextureKey = "__filler";
  private const string OutlineTextureKey = "__outline";

  /// <summary>The element and texture-key prefix <see cref="Compose"/> gives a filler-only
  /// megablock's own body, which no numbered cell names.</summary>
  public const string PrincipalPrefix = "principal";

  // Vintage Story facing normals in the XZ plane: north -Z, south +Z, east +X, west -X.
  private static readonly Dictionary<string, (int Dx, int Dz)> ArrowDir = new(StringComparer.Ordinal) {
    ["n"] = (0, -1), ["s"] = (0, 1), ["e"] = (1, 0), ["w"] = (-1, 0),
  };

  // The fixed palette for the first ten numbers; past that, a hue spaced by the golden ratio so
  // two structures with more than ten numbers (the family carries two: 11 each) still get
  // visually distinct colours instead of the palette wrapping and repeating one.
  private static string ColorFor(int i) {
    if (i < Palette.Length)
      return Palette[i];
    double hue = ((i - Palette.Length) * 0.61803398875) % 1.0;
    (double r, double g, double b) = HsvToRgb(hue, 0.55, 0.85);
    return string.Format(
      CultureInfo.InvariantCulture,
      "#{0:X2}{1:X2}{2:X2}",
      (int)Math.Round(r * 255),
      (int)Math.Round(g * 255),
      (int)Math.Round(b * 255)
    );
  }

  // HSV to RGB, the standard six-sector conversion.
  private static (double R, double G, double B) HsvToRgb(double h, double s, double v) {
    if (s == 0.0)
      return (v, v, v);
    int i = (int)(h * 6.0);
    double f = h * 6.0 - i;
    double p = v * (1.0 - s);
    double q = v * (1.0 - s * f);
    double t = v * (1.0 - s * (1.0 - f));
    return (i % 6) switch {
      0 => (v, t, p),
      1 => (q, v, p),
      2 => (p, v, t),
      3 => (p, q, v),
      4 => (t, p, v),
      _ => (v, p, q),
    };
  }

  /// <summary>One legend row per declared number, colour only; a caller with a <see cref="BlockIndex"/>
  /// fills in <see cref="LegendEntry.Representative"/> and <see cref="LegendEntry.Optional"/>
  /// before building the manifest.</summary>
  public static Dictionary<int, LegendEntry> LegendColors(Layout layout) {
    var result = new Dictionary<int, LegendEntry>();
    int i = 0;
    foreach (int n in layout.Numbers.Keys.OrderBy(n => n))
      result[n] = new LegendEntry { Color = ColorFor(i++) };
    return result;
  }

  /// <summary>
  /// One Y layer as a plan grid: a <paramref name="cell"/>-px square per structure cell, coloured
  /// by its number's legend entry, the anchor cell outlined thicker (<c>class="anchor"</c>), a
  /// filler cell hatched with a diagonal cross, a connector face as a short arrow on the cell edge
  /// its side faces. Every layer of one structure shares the same grid (extents taken over every
  /// layer, not just <paramref name="y"/>), so the layers line up when read side by side. North is
  /// up (smaller Z is nearer the top).
  /// </summary>
  public static string PlanSvg(Layout layout, int y, IReadOnlyDictionary<int, LegendEntry> legend, int cell = 32) {
    List<int> allX = [.. layout.Cells.Select(c => c.X), .. layout.Fillers.Select(o => o.X)];
    List<int> allZ = [.. layout.Cells.Select(c => c.Z), .. layout.Fillers.Select(o => o.Z)];
    foreach (IReadOnlyList<Offset> offsets in layout.Connectors.Values) {
      allX.AddRange(offsets.Select(o => o.X));
      allZ.AddRange(offsets.Select(o => o.Z));
    }
    int x0 = allX.Min(), x1 = allX.Max();
    int z0 = allZ.Min(), z1 = allZ.Max();
    int width = (x1 - x0 + 1) * cell;
    int height = (z1 - z0 + 1) * cell;

    int Px(int x) => (x - x0) * cell;
    int Pz(int z) => (z - z0) * cell;

    const int margin = 24;
    const int caption = 18;
    var sb = new StringBuilder();
    sb.Append(
      $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width + 2 * margin}\" "
        + $"height=\"{height + 2 * margin + caption}\" font-family=\"sans-serif\" font-size=\"10\">"
    );
    sb.Append(
      "<defs><marker id=\"arrow\" markerWidth=\"6\" markerHeight=\"6\" refX=\"3\" refY=\"3\" "
        + "orient=\"auto\"><path d=\"M0,0 L6,3 L0,6 z\" fill=\"red\" /></marker></defs>"
    );
    sb.Append($"<g transform=\"translate({margin},{margin})\">");

    foreach (Cell c in layout.Cells) {
      if (c.Y != y)
        continue;
      legend.TryGetValue(c.Number, out LegendEntry? row);
      if (row?.Optional == true)
        continue; // an optional selector (@(air|...)) draws as air, same as the iso render
      string color = row?.Color ?? "#999999";
      bool isAnchor = new Offset(c.X, c.Y, c.Z) == layout.Anchor;
      string cls = isAnchor ? "cell anchor" : "cell";
      int strokeWidth = isAnchor ? 3 : 1;
      sb.Append(
        $"<rect class=\"{cls}\" x=\"{Px(c.X)}\" y=\"{Pz(c.Z)}\" width=\"{cell}\" height=\"{cell}\" "
          + $"fill=\"{color}\" stroke=\"black\" stroke-width=\"{strokeWidth}\" />"
      );
    }

    foreach (Offset f in layout.Fillers) {
      if (f.Y != y)
        continue;
      int fx = Px(f.X), fz = Pz(f.Z);
      sb.Append(
        $"<line class=\"filler\" x1=\"{fx}\" y1=\"{fz}\" x2=\"{fx + cell}\" y2=\"{fz + cell}\" stroke=\"black\" />"
      );
      sb.Append(
        $"<line class=\"filler\" x1=\"{fx + cell}\" y1=\"{fz}\" x2=\"{fx}\" y2=\"{fz + cell}\" stroke=\"black\" />"
      );
    }

    foreach ((string side, IReadOnlyList<Offset> offsets) in layout.Connectors) {
      if (!ArrowDir.TryGetValue(side, out (int Dx, int Dz) dir))
        continue;
      foreach (Offset o in offsets) {
        if (o.Y != y)
          continue;
        double cx0 = Px(o.X) + cell / 2.0;
        double cz0 = Pz(o.Z) + cell / 2.0;
        double ex = cx0 + dir.Dx * cell * 0.4;
        double ez = cz0 + dir.Dz * cell * 0.4;
        sb.Append(
          $"<line class=\"connector\" x1=\"{Svg(cx0)}\" y1=\"{Svg(cz0)}\" x2=\"{Svg(ex)}\" y2=\"{Svg(ez)}\" "
            + "stroke=\"red\" stroke-width=\"2\" marker-end=\"url(#arrow)\" />"
        );
      }
    }

    sb.Append($"<text x=\"{Svg(width / 2.0)}\" y=\"-10\" text-anchor=\"middle\">north</text>");
    sb.Append($"<text x=\"{width + 4}\" y=\"{Svg(height / 2.0)}\">x</text>");
    sb.Append($"<text x=\"-14\" y=\"{Svg(height / 2.0)}\">z</text>");
    sb.Append(
      $"<text class=\"caption\" x=\"{Svg(width / 2.0)}\" y=\"{height + caption}\" text-anchor=\"middle\">"
        + $"{LayerCaption(y)}</text>"
    );
    sb.Append("</g></svg>");
    return sb.ToString();
  }

  /// <summary>The caption a plan of Y layer <paramref name="y"/> carries: the starter block stands
  /// on layer 0, and every other layer is named by its signed distance from it.</summary>
  public static string LayerCaption(int y) =>
    y switch {
      0 => "Layer 0, the starter block's row",
      > 0 => $"Layer +{y}",
      _ => $"Layer {y}",
    };

  // .NET's default double.ToString() omits the decimal point for a whole number (16, not
  // 16.0); appended back so every coordinate in the SVG text shows one.
  private static string Svg(double v) {
    string s = v.ToString("R", CultureInfo.InvariantCulture);
    return s.Contains('.') || s.Contains('e') || s.Contains('E') ? s : s + ".0";
  }

  // Deep copy of `elements` with every "#key" face texture rewritten to "#{prefix}_key", so two
  // blocks' same-named texture keys never collide in the composite.
  private static JArray Namespaced(JArray elements, string prefix) {
    var outp = new JArray();
    foreach (JToken elToken in elements) {
      var el = (JObject)elToken.DeepClone();
      if (el["faces"] is JObject faces) {
        var newFaces = new JObject();
        foreach (JProperty face in faces.Properties()) {
          var spec = (JObject)face.Value.DeepClone();
          string tex = (string?)spec["texture"] ?? "";
          if (tex.StartsWith('#'))
            spec["texture"] = $"#{prefix}_{tex[1..]}";
          newFaces[face.Name] = spec;
        }
        el["faces"] = newFaces;
      }
      if (el["children"] is JArray children)
        el["children"] = Namespaced(children, prefix);
      outp.Add(el);
    }
    return outp;
  }

  // A resolved block's own shape elements and the shape file's own texture map (many family shapes
  // are self-sufficient, Model-Creator style, with no blocktype-level assignment at all), or a
  // synthetic full unit cube (textured from whichever of all/up/north the blocktype declares) when
  // it ships no shape file - most vanilla and family blocks draw the engine's default cube rather
  // than an authored one.
  private static (JArray Elements, Dictionary<string, string> Textures) BlockElements(ResolvedBlock block) {
    if (block.ShapePath != null) {
      JObject? raw = null;
      try {
        raw = JToken.Parse(File.ReadAllText(block.ShapePath)) as JObject;
      } catch (Exception e) {
        Console.Error.WriteLine($"warning: {block.ShapePath}: shape failed to parse ({e.Message}); drawing a unit cube");
      }
      if (raw?["elements"] is JArray elements && elements.Count > 0) {
        var shapeTextures = new Dictionary<string, string>();
        if (raw["textures"] is JObject texturesJson)
          foreach (JProperty prop in texturesJson.Properties())
            shapeTextures[prop.Name] = (string)prop.Value!;
        return ((JArray)elements.DeepClone(), shapeTextures);
      }
    }

    string? key =
      new[] { "all", "up", "north" }.FirstOrDefault(k => block.Textures.ContainsKey(k))
      ?? block.Textures.Keys.FirstOrDefault();
    var faces = new JObject();
    if (key != null)
      foreach (string face in Geometry.Faces)
        faces[face] = new JObject { ["texture"] = $"#{key}" };
    var cube = new JObject {
      ["name"] = "box",
      ["from"] = new JArray(0, 0, 0),
      ["to"] = new JArray(16, 16, 16),
      ["faces"] = faces,
    };
    return (new JArray(cube), []);
  }

  // The cell's group element (world position and shapeByType rotation, its own shape elements
  // namespaced underneath) and its texture values (the shape's own map overridden by the
  // blocktype's, keyed with the same prefix). The blocktype's `all` entry is the game's own
  // catch-all: it stands in for every key the shape declares or its faces use that the blocktype
  // does not name itself.
  private static (JObject Group, Dictionary<string, string> Textures) WrappedCell(
    ResolvedBlock block,
    Offset offset,
    string prefix
  ) {
    (JArray elements, Dictionary<string, string> shapeTextures) = BlockElements(block);
    var textures = new Dictionary<string, string>(shapeTextures);
    if (block.Textures.TryGetValue("all", out string? all))
      foreach (string key in shapeTextures.Keys.Concat(FaceTextureKeys(elements)).Distinct())
        textures[key] = all;
    foreach ((string key, string value) in block.Textures)
      textures[key] = value;

    int ox = offset.X * 16, oy = offset.Y * 16, oz = offset.Z * 16;
    var group = new JObject {
      ["name"] = prefix,
      ["from"] = new JArray(ox, oy, oz),
      ["to"] = new JArray(ox, oy, oz),
      ["rotationOrigin"] = new JArray(ox + 8, oy + 8, oz + 8),
      ["rotationX"] = block.RotateX,
      ["rotationY"] = block.RotateY,
      ["rotationZ"] = block.RotateZ,
      ["children"] = Namespaced(elements, prefix),
    };
    Dictionary<string, string> prefixed = textures.ToDictionary(kv => $"{prefix}_{kv.Key}", kv => kv.Value);
    return (group, prefixed);
  }

  // Every `#key` a face of `elements` (children included) references, without the `#`.
  private static IEnumerable<string> FaceTextureKeys(JArray elements) {
    foreach (JToken el in elements) {
      if (el["faces"] is JObject faces)
        foreach (JProperty face in faces.Properties())
          if ((string?)face.Value["texture"] is { } tex && tex.StartsWith('#'))
            yield return tex[1..];
      if (el["children"] is JArray children)
        foreach (string key in FaceTextureKeys(children))
          yield return key;
    }
  }

  private static JObject FillerBox(Offset offset, string name) {
    int ox = offset.X * 16, oy = offset.Y * 16, oz = offset.Z * 16;
    var faces = new JObject();
    foreach (string face in Geometry.Faces)
      faces[face] = new JObject { ["texture"] = $"#{FillerTextureKey}" };
    return new JObject {
      ["name"] = name,
      ["from"] = new JArray(ox, oy, oz),
      ["to"] = new JArray(ox + 16, oy + 16, oz + 16),
      ["faces"] = faces,
    };
  }

  // One footprint column's ground outline: four bars a unit thick around the cell's edges, laid in
  // the unit of floor just below the lowest layer the footprint reaches, so a megablock's own body
  // is drawn over its reserved cells instead of inside a stack of grey boxes.
  private static IEnumerable<JObject> OutlineRing(int x, int z, int y, string name) {
    int ox = x * 16, oy = y * 16 - 1, oz = z * 16;
    (int X0, int Z0, int X1, int Z1)[] bars = [
      (0, 0, 16, 1),
      (0, 15, 16, 16),
      (0, 0, 1, 16),
      (15, 0, 16, 16),
    ];
    for (int i = 0; i < bars.Length; i++) {
      var faces = new JObject();
      foreach (string face in Geometry.Faces)
        faces[face] = new JObject { ["texture"] = $"#{OutlineTextureKey}" };
      yield return new JObject {
        ["name"] = $"{name}-{i}",
        ["from"] = new JArray(ox + bars[i].X0, oy, oz + bars[i].Z0),
        ["to"] = new JArray(ox + bars[i].X1, oy + 1, oz + bars[i].Z1),
        ["faces"] = faces,
      };
    }
  }

  /// <summary>
  /// The composite raw shape JSON for <paramref name="layout"/> and the texture values (key to the
  /// resolved block's own value string) it references.
  /// <para>
  /// A structure draws every non-optional, resolved cell's shape, translated and rotated into place,
  /// plus a grey box per filler cell - those are cells the player leaves clear. A filler-only
  /// megablock draws <see cref="Layout.Principal"/>'s own shape at the anchor and its footprint as a
  /// thin outline on the ground plane, since there the filler cells are the drawn body itself.
  /// </para>
  /// <para>
  /// <paramref name="cutAt"/> omits every cell and filler above that Y layer. A cell whose selector
  /// is unresolved or optional (drawn as air) is omitted.
  /// </para>
  /// </summary>
  public static (JObject Raw, Dictionary<string, string> TextureValues) Compose(
    Layout layout,
    BlockIndex index,
    int? cutAt = null
  ) {
    var elements = new JArray();
    var textureValues = new Dictionary<string, string>();
    for (int i = 0; i < layout.Cells.Count; i++) {
      Cell c = layout.Cells[i];
      if (cutAt != null && c.Y > cutAt)
        continue;
      if (!layout.Numbers.TryGetValue(c.Number, out string? selector) || index.Optional(selector))
        continue;
      ResolvedBlock? block = index.Resolve(selector);
      if (block == null)
        continue;
      (JObject group, Dictionary<string, string> values) = WrappedCell(block, new Offset(c.X, c.Y, c.Z), $"c{i}");
      elements.Add(group);
      foreach ((string key, string value) in values)
        textureValues[key] = value;
    }

    if (
      layout.Cells.Count == 0
      && layout.Principal != null
      && (cutAt == null || layout.Anchor.Y <= cutAt)
      && index.Resolve(layout.Principal) is { } principal
    ) {
      (JObject group, Dictionary<string, string> values) = WrappedCell(principal, layout.Anchor, PrincipalPrefix);
      elements.Add(group);
      foreach ((string key, string value) in values)
        textureValues[key] = value;
    }

    // A filler cell the principal's own body stands in is its volume, not a cell to fill: it draws
    // as a ground outline under the model rather than as a box over it. A megablock with no
    // structure table is all body, whatever shape it ships; elsewhere the mesh's own box decides,
    // and a cell the model does not reach keeps its box.
    Footprint.Box? body = layout.Cells.Count == 0 ? null : Footprint.PrincipalMesh(layout, index);
    var outlined = new List<Offset> { layout.Anchor };
    for (int i = 0; i < layout.Fillers.Count; i++) {
      Offset offset = layout.Fillers[i];
      if (cutAt != null && offset.Y > cutAt)
        continue;
      if (layout.Cells.Count == 0 || (body is { } box && Inside(box, offset)))
        outlined.Add(offset);
      else
        elements.Add(FillerBox(offset, $"filler{i}"));
    }

    if (outlined.Count > 1) {
      int floor = outlined.Min(c => c.Y);
      int column = 0;
      foreach ((int x, int z) in outlined.Select(c => (c.X, c.Z)).Distinct().OrderBy(c => c.Z).ThenBy(c => c.X))
        foreach (JObject bar in OutlineRing(x, z, floor, $"footprint{column++}"))
          elements.Add(bar);
    }

    var raw = new JObject { ["textures"] = new JObject(), ["elements"] = elements };
    return (raw, textureValues);
  }

  // A cell's own centre (in blocks from the principal's centre, the frame Footprint measures in)
  // within a mesh box.
  private static bool Inside(Footprint.Box box, Offset cell) =>
    cell.X >= box.Lo.X && cell.X <= box.Hi.X
    && cell.Y >= box.Lo.Y && cell.Y <= box.Hi.Y
    && cell.Z >= box.Lo.Z && cell.Z <= box.Hi.Z;

  /// <summary>
  /// One line per texture value of <paramref name="layout"/>'s composite that
  /// <see cref="BlockIndex.ResolveTexture"/> cannot find - <c>{block code}: texture {key}
  /// ({value}) not found</c>, sorted, each block and key once - the faces the iso render paints
  /// with the magenta placeholder. Empty when every value resolves.
  /// </summary>
  public static IReadOnlyList<string> MissingTextures(Layout layout, BlockIndex index) {
    (_, Dictionary<string, string> textureValues) = Compose(layout, index);
    var lines = new SortedSet<string>(StringComparer.Ordinal);
    foreach ((string prefixed, string value) in textureValues) {
      if (index.ResolveTexture(value) != null)
        continue;
      // Compose keys a cell's textures `c{cell index}_{key}` and a megablock's own body
      // `principal_{key}`.
      int underscore = prefixed.IndexOf('_');
      string owner = prefixed[..underscore];
      string key = prefixed[(underscore + 1)..];
      string? selector =
        owner == PrincipalPrefix
          ? layout.Principal
          : layout.Numbers[layout.Cells[int.Parse(owner[1..], CultureInfo.InvariantCulture)].Number];
      string code = (selector != null ? index.Resolve(selector)?.Code : null) ?? "?";
      lines.Add($"{code}: texture {key} ({value}) not found");
    }
    return [.. lines];
  }

  /// <summary>
  /// The isometric textured composite of <paramref name="layout"/>: every resolved, non-optional
  /// cell's shape plus a grey box per filler cell, or, for a filler-only megablock, its own body
  /// over its footprint outline. <paramref name="cutAt"/> omits layers above it. A vertical scale
  /// runs down the left edge, one tick per drawn Y layer labelled with the layer number, so height
  /// is counted off the picture.
  /// </summary>
  public static SKBitmap IsoPng(Layout layout, BlockIndex index, int ppu = 8, int? cutAt = null) {
    (JObject raw, Dictionary<string, string> textureValues) = Compose(layout, index, cutAt);
    Shape shape =
      JsonConvert.DeserializeObject<Shape>(raw.ToString())
      ?? throw new JsonException("the composed schematic shape failed to parse");
    LoadedShape loaded = ShapeFile.FromRaw(shape, null, new Dictionary<string, string>());

    var extra = new Dictionary<string, byte[,,]>();
    foreach ((string prefix, string key, byte level) in new[] {
      ("filler", FillerTextureKey, (byte)190),
      ("footprint", OutlineTextureKey, (byte)130),
    }) {
      if (!raw["elements"]!.Any(el => ((string?)el["name"])?.StartsWith(prefix, StringComparison.Ordinal) == true))
        continue;
      var grey = new byte[16, 16, 4];
      for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++) {
          grey[y, x, 0] = level;
          grey[y, x, 1] = level;
          grey[y, x, 2] = level;
          grey[y, x, 3] = 255;
        }
      extra[key] = grey;
    }

    TextureSet textures = TextureSet.FromResolved(textureValues, index.ResolveTexture, extra);
    View iso = Renderer.NamedViews["iso"];
    using SKBitmap drawing = Renderer.Render(loaded, iso, ppu: ppu, textures: textures);
    return WithLayerScale(drawing, Renderer.Project(loaded, iso, ppu), layout, cutAt);
  }

  // Pixels reserved left of the drawing for the layer scale: a two-character label, its tick and
  // the spine the ticks cross.
  private const int ScalePad = 40;

  // The drawing moved right by ScalePad, with a tick per drawn Y layer at the height that layer's
  // cells are drawn at. The tick row comes from the layout's own south-west column, whose vertical
  // edge the iso view lays nearest the left margin.
  private static SKBitmap WithLayerScale(SKBitmap drawing, Renderer.Projection projection, Layout layout, int? cutAt) {
    List<int> layers = [.. layout.Layers().Where(y => cutAt == null || y <= cutAt)];
    if (layers.Count == 0)
      return drawing.Copy();

    (Offset lo, Offset hi) = layout.Bounds();
    var scaled = new SKBitmap(drawing.Width + ScalePad, drawing.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
    using var canvas = new SKCanvas(scaled);
    canvas.Clear(Renderer.Background);
    canvas.DrawBitmap(drawing, ScalePad, 0);

    using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false, StrokeWidth = 1 };
    using var font = new SKFont { Size = 10 };
    var rows = new List<float>();
    foreach (int y in layers) {
      (_, double row) = projection.Screen(lo.X * 16, y * 16 + 8, (hi.Z + 1) * 16);
      rows.Add((float)row);
      canvas.DrawLine(ScalePad - 9, (float)row, ScalePad - 1, (float)row, paint);
      string label = y == 0 ? "0" : y > 0 ? $"+{y}" : y.ToString(CultureInfo.InvariantCulture);
      canvas.DrawText(label, ScalePad - 12 - font.MeasureText(label), (float)row + 3.5f, font, paint);
    }
    canvas.DrawLine(ScalePad - 5, rows.Min(), ScalePad - 5, rows.Max(), paint);
    return scaled;
  }

  /// <summary><c>{"files": [...], "legend": [rows...], "warnings": [...]}</c>; a row's
  /// <see cref="LegendEntry.Representative"/> and <see cref="LegendEntry.Optional"/> come from
  /// whatever the caller stored in <paramref name="legend"/> (a plain <see cref="LegendColors"/>
  /// layout has neither, so every number is folded into <c>warnings</c> until a
  /// <see cref="BlockIndex"/> has filled them in). <paramref name="ambiguities"/> is
  /// <see cref="BlockIndex.Ambiguities"/>, read after every selector in <paramref name="legend"/>
  /// has been resolved: each entry adds a warning naming the selector and every source file its
  /// match spanned, since only one of them was drawn. <paramref name="missingTextures"/> is
  /// <see cref="MissingTextures"/>'s lines, appended after those. <paramref name="parseWarnings"/>
  /// is <see cref="BlockIndex.ParseWarnings"/>, appended last, one line per blocktype or
  /// worldproperties file that failed to parse. <paramref name="plans"/> names each plan SVG with
  /// the Y layer it draws, as <c>{"file", "layer"}</c> rows under <c>plans</c>; the same paths stay
  /// in <c>files</c>, which lists everything written.</summary>
  public static JObject Manifest(
    Layout layout,
    IReadOnlyDictionary<int, LegendEntry> legend,
    IReadOnlyList<string> files,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? ambiguities = null,
    IReadOnlyList<string>? missingTextures = null,
    IReadOnlyList<string>? parseWarnings = null,
    IReadOnlyList<(string File, int Layer)>? plans = null
  ) {
    var rows = new JArray();
    var warnings = new JArray();
    foreach (int n in layout.Numbers.Keys.OrderBy(n => n)) {
      legend.TryGetValue(n, out LegendEntry? row);
      string selector = layout.Numbers[n];
      string? representative = row?.Representative;
      bool optional = row?.Optional ?? false;
      if (representative == null && !optional)
        warnings.Add(selector);
      if (ambiguities != null && ambiguities.TryGetValue(selector, out IReadOnlyList<string>? spanned))
        warnings.Add($"{selector}: ambiguous between {string.Join(", ", spanned)}");
      rows.Add(
        new JObject {
          ["number"] = n,
          ["selector"] = selector,
          // A null string assigns as JTokenType.String with a null value, not JTokenType.Null;
          // JValue.CreateNull() is needed for the field to serialize as JSON null.
          ["representative"] = representative == null ? JValue.CreateNull() : representative,
          ["color"] = row?.Color == null ? JValue.CreateNull() : row.Color,
          ["optional"] = optional,
        }
      );
    }
    foreach (string line in missingTextures ?? [])
      warnings.Add(line);
    foreach (string line in parseWarnings ?? [])
      warnings.Add(line);
    var planRows = new JArray();
    foreach ((string file, int layer) in plans ?? [])
      planRows.Add(new JObject { ["file"] = file, ["layer"] = layer });

    return new JObject {
      ["files"] = new JArray(files),
      ["plans"] = planRows,
      ["legend"] = rows,
      ["warnings"] = warnings,
    };
  }
}
