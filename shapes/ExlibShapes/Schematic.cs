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

  // colorsys.hsv_to_rgb, the same algorithm and branch order.
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
    var sb = new StringBuilder();
    sb.Append(
      $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width + 2 * margin}\" "
        + $"height=\"{height + 2 * margin}\" font-family=\"sans-serif\" font-size=\"10\">"
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
          $"<line class=\"connector\" x1=\"{Py(cx0)}\" y1=\"{Py(cz0)}\" x2=\"{Py(ex)}\" y2=\"{Py(ez)}\" "
            + "stroke=\"red\" stroke-width=\"2\" marker-end=\"url(#arrow)\" />"
        );
      }
    }

    sb.Append($"<text x=\"{Py(width / 2.0)}\" y=\"-10\" text-anchor=\"middle\">north</text>");
    sb.Append($"<text x=\"{width + 4}\" y=\"{Py(height / 2.0)}\">x</text>");
    sb.Append($"<text x=\"-14\" y=\"{Py(height / 2.0)}\">z</text>");
    sb.Append("</g></svg>");
    return sb.ToString();
  }

  // Python's f-string of a float always shows a decimal point (16.0, not 16); .NET's default
  // double.ToString() omits it for a whole number - appended back so the SVG text matches the
  // Python renderer's byte for byte, the two languages already agreeing digit for digit on the
  // shortest round-trip representation itself.
  private static string Py(double v) {
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
  // blocktype's, keyed with the same prefix).
  private static (JObject Group, Dictionary<string, string> Textures) WrappedCell(
    ResolvedBlock block,
    Offset offset,
    string prefix
  ) {
    (JArray elements, Dictionary<string, string> shapeTextures) = BlockElements(block);
    var textures = new Dictionary<string, string>(shapeTextures);
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

  /// <summary>
  /// The composite raw shape JSON for <paramref name="layout"/> (every non-optional, resolved
  /// cell's shape, translated and rotated into place, plus a box per filler cell) and the texture
  /// values (key to the resolved block's own value string) it references. <paramref name="cutAt"/>
  /// omits every cell and filler above that Y layer. A cell whose selector is unresolved or
  /// optional (drawn as air) is omitted.
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

    for (int i = 0; i < layout.Fillers.Count; i++) {
      Offset offset = layout.Fillers[i];
      if (cutAt != null && offset.Y > cutAt)
        continue;
      elements.Add(FillerBox(offset, $"filler{i}"));
    }

    var raw = new JObject { ["textures"] = new JObject(), ["elements"] = elements };
    return (raw, textureValues);
  }

  /// <summary>The isometric textured composite of <paramref name="layout"/> - every resolved,
  /// non-optional cell's shape plus a translucent grey box per filler cell, <paramref name="cutAt"/>
  /// omitting layers above it.</summary>
  public static SKBitmap IsoPng(Layout layout, BlockIndex index, int ppu = 8, int? cutAt = null) {
    (JObject raw, Dictionary<string, string> textureValues) = Compose(layout, index, cutAt);
    Shape shape =
      JsonConvert.DeserializeObject<Shape>(raw.ToString())
      ?? throw new JsonException("the composed schematic shape failed to parse");
    LoadedShape loaded = ShapeFile.FromRaw(shape, null, new Dictionary<string, string>());

    var extra = new Dictionary<string, byte[,,]>();
    bool anyFiller = raw["elements"]!.Any(el => ((string?)el["name"])?.StartsWith("filler", StringComparison.Ordinal) == true);
    if (anyFiller) {
      var grey = new byte[16, 16, 4];
      for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++) {
          grey[y, x, 0] = 190;
          grey[y, x, 1] = 190;
          grey[y, x, 2] = 190;
          grey[y, x, 3] = 255;
        }
      extra[FillerTextureKey] = grey;
    }

    TextureSet textures = TextureSet.FromResolved(textureValues, index.ResolveTexture, extra);
    return Renderer.Render(loaded, Renderer.NamedViews["iso"], ppu: ppu, textures: textures);
  }

  /// <summary><c>{"files": [...], "legend": [rows...], "warnings": [...]}</c>; a row's
  /// <see cref="LegendEntry.Representative"/> and <see cref="LegendEntry.Optional"/> come from
  /// whatever the caller stored in <paramref name="legend"/> (a plain <see cref="LegendColors"/>
  /// layout has neither, so every number is folded into <c>warnings</c> until a
  /// <see cref="BlockIndex"/> has filled them in).</summary>
  public static JObject Manifest(Layout layout, IReadOnlyDictionary<int, LegendEntry> legend, IReadOnlyList<string> files) {
    var rows = new JArray();
    var warnings = new JArray();
    foreach (int n in layout.Numbers.Keys.OrderBy(n => n)) {
      legend.TryGetValue(n, out LegendEntry? row);
      string selector = layout.Numbers[n];
      string? representative = row?.Representative;
      bool optional = row?.Optional ?? false;
      if (representative == null && !optional)
        warnings.Add(selector);
      rows.Add(
        new JObject {
          ["number"] = n,
          ["selector"] = selector,
          // A null string assigns as JTokenType.String with a null value, not JTokenType.Null -
          // JValue.CreateNull() is needed for this to compare equal to Python's json.dumps(None).
          ["representative"] = representative == null ? JValue.CreateNull() : representative,
          ["color"] = row?.Color == null ? JValue.CreateNull() : row.Color,
          ["optional"] = optional,
        }
      );
    }
    return new JObject {
      ["files"] = new JArray(files),
      ["legend"] = rows,
      ["warnings"] = warnings,
    };
  }
}
