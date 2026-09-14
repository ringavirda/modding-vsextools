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
  /// space by <see cref="Schematic.IsoPng"/> and as an outlined, hatched cell by
  /// <see cref="Schematic.PlanSvg"/>, rather than warned about.</summary>
  public bool Optional { get; set; }

  /// <summary>The number this row is drawn and listed under, counted up from 1 over the layout's
  /// own numbers in order; the mod's own number can skip and is kept in the manifest beside it.
  /// Null until a caller numbers the rows (<see cref="Schematic.LegendColors"/> does).</summary>
  public int? Display { get; set; }
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
  /// <summary>The point size every label in a plan or footprint SVG is drawn at.</summary>
  public const int FontSize = 10;

  /// <summary>
  /// The width a caption of <paramref name="text"/> takes, in pixels: a sans-serif glyph at
  /// <see cref="FontSize"/> averages a little over half the point size, and a canvas sized to the
  /// grid alone clips the caption at both ends.
  /// </summary>
  public static int CaptionWidth(string text) => (int)Math.Ceiling(0.55 * FontSize * text.Length);

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
    foreach (int n in layout.Numbers.Keys.OrderBy(n => n)) {
      result[n] = new LegendEntry { Color = ColorFor(i), Display = i + 1 };
      i++;
    }
    return result;
  }

  /// <summary>
  /// One Y layer as a plan grid: a <paramref name="cell"/>-px square per structure cell, coloured
  /// by its number's legend entry and carrying that entry's display number, the anchor cell
  /// outlined thicker (<c>class="anchor"</c>), an optional cell outlined and hatched, a filler cell
  /// hatched with a diagonal cross, a connector face as a short arrow on the cell edge its side
  /// faces. Every layer of one structure shares the same grid (extents taken over every layer, not
  /// just <paramref name="y"/>), so the layers line up when read side by side.
  /// <para>
  /// The grid is drawn in the machine's own frame, smaller Z nearer the top:
  /// <paramref name="front"/> names the side a player stands at, which labels the edges.
  /// </para>
  /// </summary>
  public static string PlanSvg(
    Layout layout,
    int y,
    IReadOnlyDictionary<int, LegendEntry> legend,
    int cell = 32,
    string? front = null
  ) {
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
    int canvas = Math.Max(width, CaptionWidth(LayerCaption(y))) + 2 * margin;
    int left = (canvas - width) / 2;
    var sb = new StringBuilder();
    sb.Append(
      // The iso render's paper colour behind the grid, so the labels read on a dark page too.
      $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{canvas}\" "
        + $"height=\"{height + 2 * margin + CaptionSpace}\" font-family=\"sans-serif\" "
        + $"font-size=\"{FontSize}\" style=\"background-color:#f0f0ec\">"
    );
    sb.Append(
      "<defs><marker id=\"arrow\" markerWidth=\"6\" markerHeight=\"6\" refX=\"3\" refY=\"3\" "
        + "orient=\"auto\"><path d=\"M0,0 L6,3 L0,6 z\" fill=\"red\" /></marker>"
        + Hatch
        + "</defs>"
    );
    sb.Append($"<g transform=\"translate({left},{margin})\">");

    foreach (Cell c in layout.Cells) {
      if (c.Y != y)
        continue;
      legend.TryGetValue(c.Number, out LegendEntry? row);
      bool optional = row?.Optional == true;
      string color = row?.Color ?? "#999999";
      bool isAnchor = new Offset(c.X, c.Y, c.Z) == layout.Anchor;
      string cls = (optional ? "cell optional" : "cell") + (isAnchor ? " anchor" : "");
      int strokeWidth = isAnchor ? 3 : 1;
      // An optional cell is air the player may leave filled; it is outlined and hatched rather
      // than painted, so the legend's row is on the picture as well as in the key.
      sb.Append(
        $"<rect class=\"{cls}\" x=\"{Px(c.X)}\" y=\"{Pz(c.Z)}\" width=\"{cell}\" height=\"{cell}\" "
          + $"fill=\"{(optional ? "url(#hatch)" : color)}\" stroke=\"black\" stroke-width=\"{strokeWidth}\" />"
      );
      if (row?.Display is not { } display)
        continue;
      sb.Append(
        $"<text class=\"number\" x=\"{Svg(Px(c.X) + cell / 2.0)}\" y=\"{Svg(Pz(c.Z) + cell / 2.0 + 3.5)}\" "
          + $"text-anchor=\"middle\" fill=\"{(optional ? "black" : Ink(color))}\">{display}</text>"
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

    Edges(sb, width, height, front);
    sb.Append(
      $"<text class=\"caption\" x=\"{Svg(width / 2.0)}\" y=\"{height + CaptionSpace - 4}\" "
        + $"text-anchor=\"middle\">{LayerCaption(y)}</text>"
    );
    sb.Append("</g></svg>");
    return sb.ToString();
  }

  // Pixels reserved under a grid for the edge label and the caption below it.
  private const int CaptionSpace = 30;

  // A light diagonal hatch over the paper, the fill of a cell the player may leave as air.
  private const string Hatch =
    "<pattern id=\"hatch\" width=\"6\" height=\"6\" patternUnits=\"userSpaceOnUse\" "
      + "patternTransform=\"rotate(45)\">"
      + "<line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"6\" stroke=\"#999999\" stroke-width=\"1\" /></pattern>";

  // Black on a light fill, white on a dark one, by the fill's own luminance.
  private static string Ink(string color) {
    int r = Convert.ToInt32(color.Substring(1, 2), 16);
    int g = Convert.ToInt32(color.Substring(3, 2), 16);
    int b = Convert.ToInt32(color.Substring(5, 2), 16);
    return 0.299 * r + 0.587 * g + 0.114 * b > 140 ? "black" : "white";
  }

  // The edges named for the machine rather than the compass: the side a player stands at is the
  // front, the one opposite it the back, each written against its own edge. North is the top of
  // every grid and is marked there in small text unless the back already stands for it.
  private static void Edges(StringBuilder sb, int width, int height, string? front) {
    void Label(string text, double x, double y, string anchor, int size) =>
      sb.Append(
        $"<text class=\"edge\" x=\"{Svg(x)}\" y=\"{Svg(y)}\" text-anchor=\"{anchor}\""
          + $"{(size == FontSize ? "" : $" font-size=\"{size}\"")}>{text}</text>"
      );

    // The four edges of the grid with the point each label hangs from.
    (string Side, double X, double Y, string Anchor)[] edges = [
      ("north", width / 2.0, -10, "middle"),
      ("south", width / 2.0, height + 12, "middle"),
      ("east", width + 4.0, height / 2.0, "start"),
      ("west", -4.0, height / 2.0, "end"),
    ];
    foreach ((string side, double x, double yy, string anchor) in edges) {
      string? text = front == null ? null : front == side ? "front" : Presentation.Front(side) == front ? "back" : null;
      if (text != null)
        Label(text, x, yy, anchor, FontSize);
    }
    if (front != "south")
      Label("north", width, -10, "end", 8);
  }

  /// <summary>The caption every footprint plan carries under its grid.</summary>
  public const string FootprintCaption = "Footprint, the block's own cell marked";

  /// <summary>The caption a plan of Y layer <paramref name="y"/> carries: the starter block stands
  /// on layer 0, and every other layer is named by its signed distance from it.</summary>
  public static string LayerCaption(int y) =>
    y switch {
      0 => "Layer 0, the starter block's row",
      > 0 => $"Layer +{y}",
      _ => $"Layer {y}",
    };

  /// <summary>
  /// A megablock's reserved footprint as one plan: a <paramref name="cell"/>-px square per (x, z)
  /// column its body occupies (<see cref="Footprint.Cells"/> with every Y layer projected onto one),
  /// the principal's own column filled in the first legend colour and outlined thicker
  /// (<c>class="anchor"</c>). The grid is drawn in the machine's own frame, smaller Z nearer the
  /// top; <paramref name="front"/> names the side a player stands at, which labels the edges.
  /// </summary>
  public static string FootprintSvg(Layout layout, int cell = 32, string? front = null) {
    IReadOnlyList<Offset> cells = Footprint.Cells(layout);
    List<(int X, int Z)> columns = [.. cells.Select(c => (c.X, c.Z)).Distinct().OrderBy(c => c.Z).ThenBy(c => c.X)];
    int x0 = columns.Min(c => c.X), x1 = columns.Max(c => c.X);
    int z0 = columns.Min(c => c.Z), z1 = columns.Max(c => c.Z);
    int width = (x1 - x0 + 1) * cell;
    int height = (z1 - z0 + 1) * cell;

    const int margin = 24;
    int canvas = Math.Max(width, CaptionWidth(FootprintCaption)) + 2 * margin;
    int left = (canvas - width) / 2;
    var sb = new StringBuilder();
    sb.Append(
      // The iso render's paper colour behind the grid, so the labels read on a dark page too.
      $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{canvas}\" "
        + $"height=\"{height + 2 * margin + CaptionSpace}\" font-family=\"sans-serif\" "
        + $"font-size=\"{FontSize}\" style=\"background-color:#f0f0ec\">"
    );
    sb.Append($"<g transform=\"translate({left},{margin})\">");
    foreach ((int x, int z) in columns) {
      bool isAnchor = x == layout.Anchor.X && z == layout.Anchor.Z;
      sb.Append(
        $"<rect class=\"{(isAnchor ? "cell anchor" : "cell")}\" x=\"{(x - x0) * cell}\" y=\"{(z - z0) * cell}\" "
          + $"width=\"{cell}\" height=\"{cell}\" fill=\"{(isAnchor ? Palette[0] : FillerColor)}\" "
          + $"stroke=\"black\" stroke-width=\"{(isAnchor ? 3 : 1)}\" />"
      );
    }
    Edges(sb, width, height, front);
    sb.Append(
      $"<text class=\"caption\" x=\"{Svg(width / 2.0)}\" y=\"{height + CaptionSpace - 4}\" "
        + $"text-anchor=\"middle\">{FootprintCaption}</text>"
    );
    sb.Append("</g></svg>");
    return sb.ToString();
  }

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
  private static (JArray Elements, Dictionary<string, TextureRef> Textures) BlockElements(ResolvedBlock block) {
    if (block.ShapePath != null) {
      JObject? raw = null;
      try {
        raw = JToken.Parse(File.ReadAllText(block.ShapePath)) as JObject;
      } catch (Exception e) {
        Console.Error.WriteLine($"warning: {block.ShapePath}: shape failed to parse ({e.Message}); drawing a unit cube");
      }
      if (raw?["elements"] is JArray elements && elements.Count > 0) {
        var shapeTextures = new Dictionary<string, TextureRef>();
        if (raw["textures"] is JObject texturesJson)
          foreach (JProperty prop in texturesJson.Properties())
            shapeTextures[prop.Name] = new TextureRef((string)prop.Value!);
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
    return (new JArray(cube), new Dictionary<string, TextureRef>());
  }

  // The cell's group element (world position, shapeByType rotation and `spin` degrees more about
  // the cell's own centre, its own shape elements namespaced underneath) and its texture values
  // (the shape's own map overridden by the blocktype's, keyed with the same prefix). A blocktype
  // names whole sets of faces at once as well as single keys, which is how vanilla's slabs paint a
  // shape whose faces they never name one by one.
  internal static (JObject Group, Dictionary<string, TextureRef> Textures) WrappedCell(
    ResolvedBlock block,
    Offset offset,
    string prefix,
    int spin = 0
  ) {
    (JArray elements, Dictionary<string, TextureRef> shapeTextures) = BlockElements(block);
    var textures = new Dictionary<string, TextureRef>(shapeTextures);
    List<string> keys = [.. shapeTextures.Keys.Concat(FaceTextureKeys(elements)).Distinct()];
    foreach ((string shorthand, IReadOnlyList<string>? faces) in Shorthands)
      if (block.Textures.TryGetValue(shorthand, out TextureRef? stands))
        foreach (string key in faces == null ? keys : keys.Intersect(faces))
          textures[key] = stands;
    foreach ((string key, TextureRef value) in block.Textures)
      textures[key] = value;
    // A face whose key nothing assigns is painted with the placeholder wherever a picture shows it;
    // it is carried here as an unassigned entry, which the manifest reports apart from a value that
    // names a file and misses. `#null` is Model Creator's own marker for a face with no texture,
    // which no block ever assigns.
    foreach (string key in FaceTextureKeys(elements))
      if (key != "null" && !textures.ContainsKey(key))
        textures[key] = new TextureRef("");

    int ox = offset.X * 16, oy = offset.Y * 16, oz = offset.Z * 16;
    var group = new JObject {
      ["name"] = prefix,
      ["from"] = new JArray(ox, oy, oz),
      ["to"] = new JArray(ox, oy, oz),
      ["rotationOrigin"] = new JArray(ox + 8, oy + 8, oz + 8),
      ["rotationX"] = block.RotateX,
      ["rotationY"] = block.RotateY + spin,
      ["rotationZ"] = block.RotateZ,
      ["children"] = Namespaced(elements, prefix),
    };
    Dictionary<string, TextureRef> prefixed = textures.ToDictionary(kv => $"{prefix}_{kv.Key}", kv => kv.Value);
    return (group, prefixed);
  }

  // The game's shorthand texture keys and the face keys each stands in for; a null list stands in
  // for every key the shape declares or its faces use, and a longer list wins over a shorter one.
  private static readonly (string Key, IReadOnlyList<string>? Faces)[] Shorthands = [
    ("all", null),
    ("sides", null),
    ("horizontals", new[] { "north", "east", "south", "west" }),
    ("verticals", new[] { "up", "down" }),
  ];

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
  /// <para>
  /// <paramref name="spin"/> is the turn <see cref="Presentation.Stage"/> made of the layout, in
  /// degrees: the machine is built that way round, so every drawn mesh turns with it about its own
  /// cell. A cell whose selector carries facing data has already turned, its code naming the
  /// rotated variant, and keeps its own rotation.
  /// </para>
  /// </summary>
  public static (JObject Raw, Dictionary<string, TextureRef> TextureValues) Compose(
    Layout layout,
    BlockIndex index,
    int? cutAt = null,
    int spin = 0
  ) {
    var elements = new JArray();
    var textureValues = new Dictionary<string, TextureRef>();
    for (int i = 0; i < layout.Cells.Count; i++) {
      Cell c = layout.Cells[i];
      if (cutAt != null && c.Y > cutAt)
        continue;
      if (!layout.Numbers.TryGetValue(c.Number, out string? selector) || index.Optional(selector))
        continue;
      ResolvedBlock? block = index.Resolve(selector);
      if (block == null)
        continue;
      (JObject group, Dictionary<string, TextureRef> values) = WrappedCell(
        block,
        new Offset(c.X, c.Y, c.Z),
        $"c{i}",
        layout.Facings.ContainsKey(selector) ? 0 : spin
      );
      elements.Add(group);
      foreach ((string key, TextureRef value) in values)
        textureValues[key] = value;
    }

    if (
      layout.Cells.Count == 0
      && layout.Principal != null
      && (cutAt == null || layout.Anchor.Y <= cutAt)
      && index.Resolve(layout.Principal) is { } principal
    ) {
      (JObject group, Dictionary<string, TextureRef> values) = WrappedCell(
        principal,
        layout.Anchor,
        PrincipalPrefix,
        spin
      );
      elements.Add(group);
      foreach ((string key, TextureRef value) in values)
        textureValues[key] = value;
    }

    // A filler cell the principal's own body stands in is its volume, not a cell to fill: it draws
    // as a ground outline under the model rather than as a box over it. A megablock with no
    // structure table is all body, whatever shape it ships; elsewhere the mesh's own box decides,
    // and a cell the model does not reach keeps its box.
    Footprint.Box? body =
      layout.Cells.Count == 0 || Footprint.PrincipalMesh(layout, index) is not { } mesh
        ? null
        : Footprint.Turned(mesh, spin);
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

  /// <summary>Whether every image <paramref name="value"/> names resolves to a file through
  /// <paramref name="index"/> - a base that does not, or an overlay that does not, paints the face
  /// with the placeholder.</summary>
  public static bool Resolves(TextureRef value, BlockIndex index) =>
    !value.Unassigned
    && index.ResolveTexture(value.Base) != null
    && value.Overlays.All(o => index.ResolveTexture(o) != null);

  // A cell's own centre (in blocks from the principal's centre, the frame Footprint measures in)
  // within a mesh box.
  private static bool Inside(Footprint.Box box, Offset cell) =>
    cell.X >= box.Lo.X && cell.X <= box.Hi.X
    && cell.Y >= box.Lo.Y && cell.Y <= box.Hi.Y
    && cell.Z >= box.Lo.Z && cell.Z <= box.Hi.Z;

  /// <summary>
  /// One line per texture value of <paramref name="layout"/>'s composite whose file
  /// <see cref="BlockIndex.ResolveTexture"/> cannot find - <c>{block code}: texture {key}
  /// ({value}) not found</c>, sorted, each block and key once. Empty when every value a block
  /// assigns resolves; a face assigned nothing at all is <see cref="UnpaintedFaces"/>.
  /// </summary>
  public static IReadOnlyList<string> MissingTextures(Layout layout, BlockIndex index) =>
    TextureLines(layout, index, unassigned: false);

  /// <summary>
  /// One line per face key of <paramref name="layout"/>'s composite that its own block assigns no
  /// texture - <c>{block code}: face texture {key} is assigned nothing</c>, sorted, each block and
  /// key once. Such a face is drawn with the magenta placeholder wherever the picture shows it,
  /// which for an interior face of a structure is nowhere.
  /// </summary>
  public static IReadOnlyList<string> UnpaintedFaces(Layout layout, BlockIndex index) =>
    TextureLines(layout, index, unassigned: true);

  private static IReadOnlyList<string> TextureLines(Layout layout, BlockIndex index, bool unassigned) {
    (_, Dictionary<string, TextureRef> textureValues) = Compose(layout, index);
    var lines = new SortedSet<string>(StringComparer.Ordinal);
    foreach ((string prefixed, TextureRef value) in textureValues) {
      if (Resolves(value, index) || value.Unassigned != unassigned)
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
      lines.Add(
        unassigned ? $"{code}: face texture {key} is assigned nothing" : $"{code}: texture {key} ({value}) not found"
      );
    }
    return [.. lines];
  }

  /// <summary>
  /// The isometric textured composite of <paramref name="layout"/>: every resolved, non-optional
  /// cell's shape plus a grey box per filler cell, or, for a filler-only megablock, its own body
  /// over its footprint outline. <paramref name="cutAt"/> omits layers above it. A vertical scale
  /// runs down the left edge, one labelled tick per drawn Y layer carried across the picture as a
  /// faint guide (<see cref="ScaleRows"/>), so height is counted off the face a reader is looking
  /// at. <paramref name="spin"/> turns every drawn mesh, as
  /// <see cref="Compose"/> means it.
  /// </summary>
  public static SKBitmap IsoPng(Layout layout, BlockIndex index, int ppu = 8, int? cutAt = null, int spin = 0) {
    (JObject raw, Dictionary<string, TextureRef> textureValues) = Compose(layout, index, cutAt, spin);
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

  /// <summary>
  /// The screen row each drawn Y layer of <paramref name="layout"/> reads at, ascending by layer:
  /// the height of that layer's own middle on the corner column nearest the camera, which is the
  /// plane a reader counts height against. A projection is true in one vertical plane only, and on
  /// any other the layers of the picture stand above or below their own ticks.
  /// </summary>
  /// <param name="projection">The mapping the drawing was laid out with
  /// (<see cref="Renderer.Project"/>).</param>
  /// <param name="layout">The layout drawn, whose reserved cells give the corner column.</param>
  /// <param name="cutAt">Omits every layer above it; null keeps all.</param>
  public static IReadOnlyList<(int Layer, double Row)> ScaleRows(
    Renderer.Projection projection,
    Layout layout,
    int? cutAt = null
  ) {
    IReadOnlyList<Offset> cells = Footprint.Reserved(layout);
    int x = (cells.Max(c => c.X) + 1) * 16;
    int z = (cells.Max(c => c.Z) + 1) * 16;
    return [
      .. layout
        .Layers()
        .Where(y => cutAt == null || y <= cutAt)
        .Select(y => (y, projection.Screen(x, y * 16 + 8, z).Row)),
    ];
  }

  // The drawing moved right by ScalePad, with a tick and a faint guide across the picture per drawn
  // Y layer, at the row that layer reads at on the plane nearest the camera (ScaleRows).
  private static SKBitmap WithLayerScale(SKBitmap drawing, Renderer.Projection projection, Layout layout, int? cutAt) {
    IReadOnlyList<(int Layer, double Row)> ticks = ScaleRows(projection, layout, cutAt);
    if (ticks.Count == 0)
      return drawing.Copy();

    var scaled = new SKBitmap(drawing.Width + ScalePad, drawing.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
    using var canvas = new SKCanvas(scaled);
    canvas.Clear(Renderer.Background);
    canvas.DrawBitmap(drawing, ScalePad, 0);

    using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false, StrokeWidth = 1 };
    using var guide = new SKPaint { Color = new SKColor(0, 0, 0, 40), IsAntialias = false, StrokeWidth = 1 };
    using var font = new SKFont(Text.Face, 10);
    var rows = new List<float>();
    foreach ((int y, double value) in ticks) {
      var row = (float)value;
      rows.Add(row);
      canvas.DrawLine(ScalePad, row, scaled.Width, row, guide);
      canvas.DrawLine(ScalePad - 9, row, ScalePad - 1, row, paint);
      string label = y == 0 ? "0" : y > 0 ? $"+{y}" : y.ToString(CultureInfo.InvariantCulture);
      canvas.DrawText(label, ScalePad - 12 - font.MeasureText(label), row + 3.5f, font, paint);
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
  /// in <c>files</c>, which lists everything written. <paramref name="front"/> is the world side the
  /// drawn machine's front looks toward (<see cref="Presentation.FrontOf"/>, turned with the
  /// layout), under <c>front</c>, and is JSON null for a structure that faces no way.
  /// <paramref name="unpaintedFaces"/> is <see cref="UnpaintedFaces"/>'s lines, under
  /// <c>unpaintedFaces</c> rather than among the warnings: a face a block assigns nothing is only
  /// drawn where a picture shows it.</summary>
  public static JObject Manifest(
    Layout layout,
    IReadOnlyDictionary<int, LegendEntry> legend,
    IReadOnlyList<string> files,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? ambiguities = null,
    IReadOnlyList<string>? missingTextures = null,
    IReadOnlyList<string>? parseWarnings = null,
    IReadOnlyList<(string File, int Layer)>? plans = null,
    string? front = null,
    IReadOnlyList<string>? unpaintedFaces = null
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
          ["display"] = row?.Display ?? n,
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
      ["front"] = front == null ? JValue.CreateNull() : front,
      ["legend"] = rows,
      ["unpaintedFaces"] = new JArray(unpaintedFaces ?? []),
      ["warnings"] = warnings,
    };
  }
}
