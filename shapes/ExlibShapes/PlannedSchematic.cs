using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace ExpandedLib.Shapes;

/// <summary>
/// The drawings of a <see cref="PlannedLayout"/>: the glyph and box overlay a plan SVG carries, the
/// iso composite of the shape copy with the collision boxes outlined, and the manifest naming what was
/// drawn.
/// </summary>
public static class PlannedSchematic {
  /// <summary>The element-name prefix of the bars drawn along each collision box.</summary>
  public const string BoxPrefix = "collisionbox";

  /// <summary>The texture key of the box bars.</summary>
  public const string BoxTextureKey = "__collisionbox";

  /// <summary>The thickness of a frame bar, in voxels.</summary>
  public const double FrameBar = 0.25;

  private const string CollisionStroke = "#1f5fbf";
  private const string ControlStroke = "#c0392b";

  private static readonly Dictionary<char, string> Meanings = new() {
    ['O'] = "principal",
    ['P'] = "port",
    ['+'] = "attach cell",
    ['#'] = "full cube",
    ['_'] = "cell with boxes",
    ['s'] = "sunk full cube",
    ['!'] = "conflict",
  };

  /// <summary>What <paramref name="glyph"/> stands for in <paramref name="planned"/>: the fixed
  /// glyphs by their meaning, a control's letter by its kind and id, any other letter as the page's
  /// own marker.</summary>
  public static string Meaning(PlannedLayout planned, char glyph) =>
    Meanings.TryGetValue(glyph, out string? fixedMeaning) ? fixedMeaning
    : planned.Controls.TryGetValue(glyph, out string? control) ? control
    : "marked cell";

  /// <summary>The key lines under layer <paramref name="y"/>'s plan: a line per glyph on that layer
  /// in the order the grid first holds them, then a line per box style drawn on it.</summary>
  public static IReadOnlyList<string> Key(PlannedLayout planned, int y) {
    var lines = new List<string>();
    foreach (
      char glyph in planned
        .Marks()
        .Where(m => m.At.Y == y)
        .Select(m => m.Glyph)
        .Distinct()
    )
      lines.Add($"{glyph}  {Meaning(planned, glyph)}");
    if (planned.Boxes.Any(b => b.At.Y == y && !b.Control))
      lines.Add("blue outline  collision box");
    if (planned.Boxes.Any(b => b.At.Y == y && b.Control))
      lines.Add("red dashed outline  control selection box");
    return lines;
  }

  /// <summary>Appends layer <paramref name="y"/>'s declared-cell outlines, glyphs and box outlines to a plan SVG whose grid
  /// starts at cell (<paramref name="x0"/>, <paramref name="z0"/>) and draws a cell
  /// <paramref name="cell"/> px wide. A box keeps its place inside its cell, north at the top.</summary>
  internal static void Overlay(
    StringBuilder sb,
    PlannedLayout planned,
    int y,
    int cell,
    int x0,
    int z0
  ) {
    string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    foreach (Offset f in planned.Layout.Fillers.Where(f => f.Y == y))
      sb.Append(
        $"<rect class=\"declared\" stroke=\"#a0a09c\" stroke-width=\"1\" fill=\"none\" "
          + $"x=\"{F((f.X - x0) * cell)}\" y=\"{F((f.Z - z0) * cell)}\" width=\"{cell}\" height=\"{cell}\" />"
      );
    foreach (PlannedBox b in planned.Boxes.Where(b => b.At.Y == y)) {
      string style = b.Control
        ? $"class=\"control-box\" stroke=\"{ControlStroke}\" stroke-width=\"1.5\" stroke-dasharray=\"3 2\""
        : $"class=\"box\" stroke=\"{CollisionStroke}\" stroke-width=\"1\"";
      sb.Append(
        $"<rect {style} fill=\"none\" x=\"{F((b.At.X - x0 + b.Box.X1) * cell)}\" "
          + $"y=\"{F((b.At.Z - z0 + b.Box.Z1) * cell)}\" width=\"{F((b.Box.X2 - b.Box.X1) * cell)}\" "
          + $"height=\"{F((b.Box.Z2 - b.Box.Z1) * cell)}\" />"
      );
    }
    foreach ((Offset at, char glyph) in planned.Marks().Where(m => m.At.Y == y))
      sb.Append(
        $"<text class=\"glyph\" x=\"{F((at.X - x0 + 0.5) * cell)}\" y=\"{F((at.Z - z0 + 0.5) * cell + 5)}\" "
          + "text-anchor=\"middle\" font-size=\"14\" font-weight=\"bold\" fill=\"black\" "
          + $"stroke=\"#f0f0ec\" stroke-width=\"3\" paint-order=\"stroke\">{glyph}</text>"
      );
  }

  /// <summary>
  /// The composite raw shape JSON for <paramref name="planned"/> and the texture values it
  /// references: the copy at <paramref name="copyPath"/>, turned by the plan's frame turn and
  /// <paramref name="angle"/> and shifted so the frame anchor lands on the origin, and thin
  /// bars along the edges of every collision box of a cell not above <paramref name="cutAt"/>: the
  /// principal's (when the plan names it), a full cube for a cell without a box list, none for an empty list. The copy is drawn
  /// whole whatever <paramref name="cutAt"/> is. <paramref name="planned"/> is already turned by
  /// <paramref name="angle"/>.
  /// </summary>
  public static (
    JObject Raw,
    Dictionary<string, TextureRef> TextureValues
  ) Compose(
    PlannedLayout planned,
    string copyPath,
    int angle,
    int? cutAt = null
  ) {
    var block = new ResolvedBlock(
      "planned:copy",
      copyPath,
      0,
      planned.FrameTurn,
      0,
      new Dictionary<string, TextureRef>()
    );
    Offset anchor = planned.FrameAnchor;
    Offset shift = Layout.RotateOffset(
      new Offset(-anchor.X, -anchor.Y, -anchor.Z),
      angle
    );
    (JObject group, Dictionary<string, TextureRef> values) =
      Schematic.WrappedCell(block, shift, Schematic.PrincipalPrefix, angle);
    var elements = new JArray { group };
    int i = 0;
    foreach (PlannedCell cell in planned.Collision) {
      if (cutAt != null && cell.At.Y > cutAt)
        continue;
      foreach (LocalBox box in cell.Boxes)
        foreach (JObject bar in Frame(cell.At, box, $"{BoxPrefix}{i++}"))
          elements.Add(bar);
    }
    return (
      new JObject { ["textures"] = new JObject(), ["elements"] = elements },
      values
    );
  }

  // The twelve edges of a box as bars FrameBar thick, laid inside the box, in cell `cell`.
  private static IEnumerable<JObject> Frame(
    Offset cell,
    LocalBox box,
    string name
  ) {
    double[] lo = [box.X1 * 16, box.Y1 * 16, box.Z1 * 16];
    double[] extent =
    [
      (box.X2 - box.X1) * 16,
      (box.Y2 - box.Y1) * 16,
      (box.Z2 - box.Z1) * 16,
    ];
    double[] bar = [.. extent.Select(e => Math.Min(FrameBar, e))];
    double[] origin = [cell.X * 16, cell.Y * 16, cell.Z * 16];
    int n = 0;
    for (int axis = 0; axis < 3; axis++) {
      int u = (axis + 1) % 3,
        v = (axis + 2) % 3;
      foreach (bool atFarU in new[] { false, true })
        foreach (bool atFarV in new[] { false, true }) {
          double[] from = [.. lo];
          double[] size = [.. bar];
          size[axis] = extent[axis];
          if (atFarU)
            from[u] += extent[u] - bar[u];
          if (atFarV)
            from[v] += extent[v] - bar[v];
          var faces = new JObject();
          foreach (string face in Geometry.Faces)
            faces[face] = new JObject { ["texture"] = $"#{BoxTextureKey}" };
          yield return new JObject {
            ["name"] = $"{name}-{n++}",
            ["from"] = new JArray(
              origin[0] + from[0],
              origin[1] + from[1],
              origin[2] + from[2]
            ),
            ["to"] = new JArray(
              origin[0] + from[0] + size[0],
              origin[1] + from[1] + size[1],
              origin[2] + from[2] + size[2]
            ),
            ["faces"] = faces,
          };
        }
    }
  }

  /// <summary>The isometric picture of <see cref="Compose"/>: the copy, with the declared cells
  /// framed and the layer scale of <paramref name="planned"/>'s cells beside it.</summary>
  public static SKBitmap IsoPng(
    PlannedLayout planned,
    string copyPath,
    BlockIndex index,
    int angle,
    int ppu = 8,
    int? cutAt = null
  ) {
    (JObject raw, Dictionary<string, TextureRef> values) = Compose(
      planned,
      copyPath,
      angle,
      cutAt
    );
    return Schematic.Iso(raw, values, planned.Layout, index, ppu, cutAt);
  }

  /// <summary>One line per texture value of the copy whose file <paramref name="index"/> cannot
  /// find, sorted.</summary>
  public static IReadOnlyList<string> MissingTextures(
    PlannedLayout planned,
    string copyPath,
    BlockIndex index
  ) {
    (_, Dictionary<string, TextureRef> values) = Compose(planned, copyPath, 0);
    return
    [
      .. values
        .Where(kv =>
          !kv.Value.Unassigned && !Schematic.Resolves(kv.Value, index)
        )
        .Select(kv =>
          $"planned copy: texture {kv.Key[(kv.Key.IndexOf('_') + 1)..]} ({kv.Value}) not found"
        )
        .OrderBy(l => l, StringComparer.Ordinal),
    ];
  }

  /// <summary>Every layer's glyph rows, ascending by y: each row is the glyphs of one z across the
  /// grid's whole x extent, a space between, '.' where the grid holds none or an overhang
  /// ','; the first row is the most negative z.</summary>
  public static IReadOnlyList<(int Layer, IReadOnlyList<string> Rows)> Rows(
    PlannedLayout planned
  ) {
    int xLo = planned.Grid.Min(g => g.At.X),
      xHi = planned.Grid.Max(g => g.At.X);
    int zLo = planned.Grid.Min(g => g.At.Z),
      zHi = planned.Grid.Max(g => g.At.Z);
    var result = new List<(int, IReadOnlyList<string>)>();
    foreach (
      int y in planned.Grid.Select(g => g.At.Y).Distinct().OrderBy(y => y)
    ) {
      Dictionary<(int, int), char> at = planned
        .Grid.Where(g => g.At.Y == y)
        .ToDictionary(g => (g.At.X, g.At.Z), g => g.Glyph);
      var rows = new List<string>();
      for (int z = zLo; z <= zHi; z++)
        rows.Add(
          string.Join(
            ' ',
            Enumerable
              .Range(xLo, xHi - xLo + 1)
              .Select(x =>
                at.TryGetValue((x, z), out char g) && g != ',' ? g : '.'
              )
          )
        );
      result.Add((y, rows));
    }
    return result;
  }

  /// <summary>
  /// <see cref="Schematic.Manifest"/> of the plan's declared cells with a <c>planned</c> object
  /// added: <c>copy</c> (the shape file read), <c>frameTurn</c>, <c>layers</c> (<c>{"layer", "rows"}</c>
  /// per y, ascending) and <c>glyphs</c> (<c>{"glyph", "meaning"}</c> per glyph drawn on any layer,
  /// in the order the grid first holds them).
  /// </summary>
  public static JObject Manifest(
    PlannedLayout planned,
    string copyPath,
    IReadOnlyList<string> files,
    IReadOnlyList<(string File, int Layer)> plans,
    IReadOnlyList<string> warnings
  ) {
    JObject manifest = Schematic.Manifest(
      planned.Layout,
      new Dictionary<int, LegendEntry>(),
      files,
      missingTextures: warnings,
      plans: plans
    );
    manifest["planned"] = new JObject {
      ["copy"] = copyPath,
      ["frameTurn"] = planned.FrameTurn,
      ["layers"] = new JArray(
        Rows(planned)
          .Select(l => new JObject {
            ["layer"] = l.Layer,
            ["rows"] = new JArray(l.Rows),
          })
      ),
      ["glyphs"] = new JArray(
        planned
          .Marks()
          .Select(m => m.Glyph)
          .Distinct()
          .Select(g => new JObject {
            ["glyph"] = g.ToString(),
            ["meaning"] = Meaning(planned, g),
          })
      ),
    };
    return manifest;
  }
}
