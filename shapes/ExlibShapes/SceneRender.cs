using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Vintagestory.API.Common;

namespace ExpandedLib.Shapes;

/// <summary>What <see cref="SceneRender.Write"/> produced for one scene.</summary>
/// <param name="Files">The plain PNG paths written, in view order.</param>
/// <param name="Tinted">The tinted PNG paths written, in view order.</param>
/// <param name="Unresolved">Each code no root resolves, once, sorted.</param>
/// <param name="MissingTextures">One line per texture value of a drawn cell that names no file,
/// sorted.</param>
public sealed record SceneRendered(
  IReadOnlyList<string> Files,
  IReadOnlyList<string> Tinted,
  IReadOnlyList<string> Unresolved,
  IReadOnlyList<string> MissingTextures
);

/// <summary>
/// Draws a <see cref="SceneFile"/> the way <see cref="Schematic.Compose"/> draws a layout: one
/// composite shape holding every cell's own block at its world position, rendered from the four
/// iso corners on one shared canvas.
/// </summary>
public static class SceneRender {
  /// <summary>The iso corner views, north-east first.</summary>
  public static readonly IReadOnlyList<string> CornerViews =
  [
    "ne",
    "nw",
    "se",
    "sw",
  ];

  /// <summary>The element-name and texture-key prefix of an unresolved cell's cube.</summary>
  internal const string UnresolvedPrefix = "unresolved";

  internal const string UnresolvedTextureKey = "__unresolved";

  // The element-name prefix of the port frames and open stubs, drawn as overlay.
  private const string MarkPrefix = "mark";

  // Bar thickness of a port frame, in voxels; a stub is StubSide square and StubLength long.
  private const double MarkBar = 0.5;
  private const double StubSide = 4;
  private const double StubLength = 6;

  /// <summary>
  /// The composite of the scene's drawn cells. A filler or port entry draws nothing; a fluid entry
  /// draws only where no other entry shares its cell; air draws nothing; a code no root resolves
  /// is a unit cube in a black-and-yellow checker. Cell <c>i</c> of the file is the group
  /// <c>c{i}</c>, or the cube <c>unresolved{i}</c>.
  /// </summary>
  /// <returns>The shape JSON, the texture values keyed <c>c{i}_{face key}</c>, and the unresolved
  /// codes, once each, sorted.</returns>
  internal static (
    JObject Raw,
    Dictionary<string, TextureRef> Textures,
    IReadOnlyList<string> Unresolved
  ) Compose(SceneFile scene, BlockIndex index) {
    var elements = new JArray();
    var textures = new Dictionary<string, TextureRef>();
    var unresolved = new SortedSet<string>(StringComparer.Ordinal);
    var solid = new HashSet<Offset>(
      scene.Cells.Where(c => c.Role != "fluid").Select(c => c.At)
    );
    var resolved = new Dictionary<string, ResolvedBlock?>(
      StringComparer.Ordinal
    );
    for (int i = 0; i < scene.Cells.Count; i++) {
      SceneCell c = scene.Cells[i];
      if (c.Role is "filler" or "port")
        continue;
      if (c.Role == "fluid" && solid.Contains(c.At))
        continue;
      if (index.Optional(c.Code))
        continue;
      if (!resolved.TryGetValue(c.Code, out ResolvedBlock? block))
        resolved[c.Code] = block = index.Resolve(c.Code);
      if (block == null) {
        unresolved.Add(c.Code);
        elements.Add(UnresolvedCube(c.At, $"{UnresolvedPrefix}{i}"));
        continue;
      }
      (JObject group, Dictionary<string, TextureRef> values) =
        Schematic.WrappedCell(block, c.At, $"c{i}");
      elements.Add(group);
      foreach ((string key, TextureRef value) in values)
        textures[key] = value;
    }
    var raw = new JObject {
      ["textures"] = new JObject(),
      ["elements"] = elements,
    };
    return (raw, textures, [.. unresolved]);
  }

  private static JObject UnresolvedCube(Offset at, string name) {
    var faces = new JObject();
    foreach (string face in Geometry.Faces)
      faces[face] = new JObject { ["texture"] = $"#{UnresolvedTextureKey}" };
    return new JObject {
      ["name"] = name,
      ["from"] = new JArray(at.X * 16, at.Y * 16, at.Z * 16),
      ["to"] = new JArray(at.X * 16 + 16, at.Y * 16 + 16, at.Z * 16 + 16),
      ["faces"] = faces,
    };
  }

  // 16x16, 4-pixel squares alternating black and yellow; neither colour is MissingImage's, the
  // schematic palette's or ColorFor's.
  internal static byte[,,] UnresolvedImage() {
    var image = new byte[16, 16, 4];
    for (int y = 0; y < 16; y++)
      for (int x = 0; x < 16; x++) {
        bool yellow = ((x / 4) + (y / 4)) % 2 == 0;
        image[y, x, 0] = (byte)(yellow ? 255 : 0);
        image[y, x, 1] = (byte)(yellow ? 210 : 0);
        image[y, x, 2] = 0;
        image[y, x, 3] = 255;
      }
    return image;
  }

  internal static LoadedShape Load(JObject raw) =>
    ShapeFile.FromRaw(
      JsonConvert.DeserializeObject<Shape>(raw.ToString())
        ?? throw new JsonException("the composed scene shape failed to parse"),
      null,
      new Dictionary<string, string>()
    );

  internal static TextureSet TexturesOf(
    Dictionary<string, TextureRef> values,
    BlockIndex index
  ) =>
    TextureSet.FromResolved(
      values,
      index.ResolveTexture,
      new Dictionary<string, byte[,,]> {
        [UnresolvedTextureKey] = UnresolvedImage(),
      }
    );

  /// <summary>
  /// Writes <c>{stem}-plain-{view}.png</c> and <c>{stem}-tint-{view}.png</c> for each corner view
  /// in <paramref name="views"/>, all on one canvas: the union of the views' fitted canvases. In a
  /// tinted view each texel takes the hue and saturation of its cell's network or line colour
  /// (<see cref="ScenePlan.KeyColors"/>) and keeps its own HSL lightness and alpha, and a cell on
  /// neither, or on a key the scene does not declare, is its texture's luminance grey;
  /// a missing texture keeps its magenta and an unresolved cube its checker. Names in
  /// <paramref name="views"/> that are not corner views are left to the caller.
  /// </summary>
  /// <param name="scene">The scene to draw.</param>
  /// <param name="index">Resolves cell codes and texture values.</param>
  /// <param name="outDir">The folder the files go to; created when absent.</param>
  /// <param name="stem">The scene file's name less <c>.scene.json</c>.</param>
  /// <param name="views">View names; those in <see cref="CornerViews"/> are drawn.</param>
  /// <param name="ppu">Pixels per unit, above 0.</param>
  /// <returns>The paths written and the findings of the drawing.</returns>
  /// <exception cref="IOException">A folder or a PNG cannot be created or written.</exception>
  public static SceneRendered Write(
    SceneFile scene,
    BlockIndex index,
    string outDir,
    string stem,
    IReadOnlyList<string> views,
    int ppu = 4
  ) {
    (
      JObject raw,
      Dictionary<string, TextureRef> values,
      IReadOnlyList<string> unresolved
    ) = Compose(scene, index);
    LoadedShape shape = Load(raw);
    TextureSet textures = TexturesOf(values, index);
    List<string> corners = [.. CornerViews.Where(views.Contains)];
    (LoadedShape Shape, TextureSet Textures)? marks = Marks(scene, index);
    Renderer.Canvas? canvas = null;
    foreach (string name in corners) {
      if (Renderer.Fit(shape, Renderer.NamedViews[name]) is { } fit)
        canvas = canvas?.Union(fit) ?? fit;
      if (
        marks is { } m
        && Renderer.Fit(m.Shape, Renderer.NamedViews[name]) is { } markFit
      )
        canvas = canvas?.Union(markFit) ?? markFit;
    }
    Directory.CreateDirectory(outDir);
    TextureSet tint = TintedTextures(scene, values, index, textures);
    var files = new List<string>();
    var tinted = new List<string>();
    foreach (string name in corners) {
      files.Add(
        WriteView(shape, name, textures, canvas, ppu, outDir, $"{stem}-plain-{name}.png")
      );
      tinted.Add(
        WriteView(
          shape,
          name,
          tint,
          canvas,
          ppu,
          outDir,
          $"{stem}-tint-{name}.png",
          marks
        )
      );
    }
    return new SceneRendered(
      files,
      tinted,
      unresolved,
      MissingLines(scene, values, textures)
    );
  }

  private static string WriteView(
    LoadedShape shape,
    string view,
    TextureSet textures,
    Renderer.Canvas? canvas,
    int ppu,
    string outDir,
    string fileName,
    (LoadedShape Shape, TextureSet Textures)? marks = null
  ) {
    using SKBitmap bitmap = Renderer.Render(
      shape,
      Renderer.NamedViews[view],
      ppu: ppu,
      textures: textures,
      fitTo: canvas
    );
    if (marks is { } m) {
      using SKBitmap layer = Renderer.Render(
        m.Shape,
        Renderer.NamedViews[view],
        ppu: ppu,
        grid: false,
        textures: m.Textures,
        edges: false,
        transparent: true,
        fitTo: canvas,
        overlay: MarkPrefix
      );
      using var surface = new SKCanvas(bitmap);
      surface.DrawBitmap(layer, 0, 0);
    }
    string path = Path.Combine(outDir, fileName);
    using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
    using FileStream stream = File.Create(path);
    data.SaveTo(stream);
    return path;
  }

  // The port frames and open stubs as bars of their own shape, so they are rendered apart from the
  // cells and laid over the tinted picture.
  private static (LoadedShape Shape, TextureSet Textures)? Marks(
    SceneFile scene,
    BlockIndex index
  ) {
    IReadOnlyDictionary<string, string> colors = ScenePlan.KeyColors(scene);
    var elements = new JArray();
    var used = new SortedSet<string>(StringComparer.Ordinal);
    for (int i = 0; i < scene.Cells.Count; i++) {
      SceneCell c = scene.Cells[i];
      if (c.Port is { } port) {
        string color =
          port.Joins != null && colors.TryGetValue(port.Joins, out string? joined)
            ? joined
            : ScenePlan.Unjoined;
        used.Add(color);
        foreach (
          JObject bar in PlannedSchematic.Frame(
            c.At,
            new LocalBox(0, 0, 0, 1, 1, 1),
            $"{MarkPrefix}frame{i}",
            MarkTextureKey(color),
            MarkBar
          )
        )
          elements.Add(bar);
      }
      foreach (string face in c.Open) {
        used.Add(ScenePlan.Unjoined);
        elements.Add(Stub(c.At, face, $"{MarkPrefix}stub{i}{face}"));
      }
    }
    if (elements.Count == 0)
      return null;
    var values = new Dictionary<string, TextureRef>();
    var extra = used.ToDictionary(
      color => MarkTextureKey(color),
      color => Solid(color)
    );
    return (
      Load(new JObject { ["textures"] = new JObject(), ["elements"] = elements }),
      TextureSet.FromResolved(values, index.ResolveTexture, extra)
    );
  }

  private static string MarkTextureKey(string hex) => $"__mark{hex[1..]}";

  private static byte[,,] Solid(string hex) {
    var image = new byte[16, 16, 4];
    for (int y = 0; y < 16; y++)
      for (int x = 0; x < 16; x++) {
        for (int c = 0; c < 3; c++)
          image[y, x, c] = Convert.ToByte(hex.Substring(1 + 2 * c, 2), 16);
        image[y, x, 3] = 255;
      }
    return image;
  }

  // A bar StubSide square leaving the middle of `face` of cell `at` StubLength out.
  private static JObject Stub(Offset at, string face, string name) {
    double[] origin = [at.X * 16, at.Y * 16, at.Z * 16];
    double[] lo = [origin[0] + 8, origin[1] + 8, origin[2] + 8];
    double[] size = [StubSide, StubSide, StubSide];
    int axis = face is "e" or "w" ? 0 : face is "u" or "d" ? 1 : 2;
    bool positive = face is "e" or "s" or "u";
    for (int a = 0; a < 3; a++)
      lo[a] -= StubSide / 2;
    size[axis] = StubLength;
    lo[axis] = origin[axis] + (positive ? 16 : -StubLength);
    var faces = new JObject();
    foreach (string f in Geometry.Faces)
      faces[f] = new JObject { ["texture"] = $"#{MarkTextureKey(ScenePlan.Unjoined)}" };
    return new JObject {
      ["name"] = name,
      ["from"] = new JArray(lo[0], lo[1], lo[2]),
      ["to"] = new JArray(lo[0] + size[0], lo[1] + size[1], lo[2] + size[2]),
      ["faces"] = faces,
    };
  }

  private static TextureSet TintedTextures(
    SceneFile scene,
    Dictionary<string, TextureRef> values,
    BlockIndex index,
    TextureSet plain
  ) {
    IReadOnlyDictionary<string, string> colors = ScenePlan.KeyColors(scene);
    var extra = new Dictionary<string, byte[,,]> {
      [UnresolvedTextureKey] = UnresolvedImage(),
    };
    foreach (string key in values.Keys) {
      if (plain.Missing.Contains(key))
        continue;
      SceneCell cell = scene.Cells[CellOf(key)];
      string? hex = cell.Network ?? cell.Line;
      extra[key] = Tint(
        plain.Get(key),
        hex != null && colors.TryGetValue(hex, out string? color) ? color : null
      );
    }
    return TextureSet.FromResolved(values, index.ResolveTexture, extra);
  }

  // Cell keys are `c{i}_{face key}`.
  private static int CellOf(string textureKey) =>
    int.Parse(
      textureKey[1..textureKey.IndexOf('_')],
      CultureInfo.InvariantCulture
    );

  private static byte[,,] Tint(byte[,,] image, string? hex) {
    int h = image.GetLength(0),
      w = image.GetLength(1);
    var result = new byte[h, w, 4];
    float hue = 0,
      saturation = 0;
    if (hex != null)
      SKColor.Parse(hex).ToHsl(out hue, out saturation, out float _);
    for (int y = 0; y < h; y++)
      for (int x = 0; x < w; x++) {
        if (hex == null) {
          byte grey = (byte)Math.Round(
            0.299 * image[y, x, 0] + 0.587 * image[y, x, 1] + 0.114 * image[y, x, 2]
          );
          for (int c = 0; c < 3; c++)
            result[y, x, c] = grey;
        } else {
          new SKColor(image[y, x, 0], image[y, x, 1], image[y, x, 2]).ToHsl(
            out float _,
            out float _,
            out float lightness
          );
          SKColor tinted = SKColor.FromHsl(hue, saturation, lightness);
          result[y, x, 0] = tinted.Red;
          result[y, x, 1] = tinted.Green;
          result[y, x, 2] = tinted.Blue;
        }
        result[y, x, 3] = image[y, x, 3];
      }
    return result;
  }

  /// <summary>
  /// The scene's <c>{stem}.json</c> manifest: files, networks and lines with their colours, the
  /// unjoined ports, the open faces, the unresolved codes, the missing textures and the warnings.
  /// </summary>
  /// <param name="scene">The scene drawn.</param>
  /// <param name="files">Every file written, as the manifest lists them.</param>
  /// <param name="rendered">What <see cref="Write"/> returned.</param>
  /// <param name="warnings">The index's parse warnings.</param>
  /// <returns>The manifest object, ready to serialise.</returns>
  public static JObject Manifest(
    SceneFile scene,
    IReadOnlyList<string> files,
    SceneRendered rendered,
    IReadOnlyList<string> warnings
  ) {
    IReadOnlyDictionary<string, string> colors = ScenePlan.KeyColors(scene);
    var unjoined = new JArray();
    var open = new JArray();
    foreach (SceneCell c in scene.Cells) {
      if (c.Port is { Joins: null } port)
        unjoined.Add(
          new JObject {
            ["at"] = AtOf(c.At),
            ["principal"] = c.Principal is { } p ? AtOf(p) : JValue.CreateNull(),
            ["id"] = IdOf(scene, c) is { } id ? id : JValue.CreateNull(),
            ["face"] = port.Face == null ? JValue.CreateNull() : port.Face,
            ["type"] = port.Type,
          }
        );
      foreach (string face in c.Open)
        open.Add(
          new JObject {
            ["at"] = AtOf(c.At),
            ["id"] = IdOf(scene, c) is { } id ? id : JValue.CreateNull(),
            ["face"] = face,
          }
        );
    }
    return new JObject {
      ["files"] = new JArray(files),
      ["networks"] = new JArray(
        scene.Networks.Select(n => new JObject {
          ["key"] = n.Key,
          ["type"] = n.Type,
          ["nodes"] = n.Nodes,
          ["ids"] = new JArray(n.Ids),
          ["color"] = colors[n.Key],
        })
      ),
      ["lines"] = new JArray(
        scene.Lines.Select(l => new JObject {
          ["key"] = l.Key,
          ["members"] = l.Members,
          ["ids"] = new JArray(l.Ids),
          ["color"] = colors[l.Key],
        })
      ),
      ["unjoined"] = unjoined,
      ["open"] = open,
      ["unresolved"] = new JArray(rendered.Unresolved),
      ["missingTextures"] = new JArray(rendered.MissingTextures),
      ["warnings"] = new JArray(warnings),
    };
  }

  /// <summary>
  /// One line per finding of the scene, unjoined ports first and then open faces, in cell order:
  /// <c>unjoined: {stem} {id} {face} {type} at x,y,z</c> and <c>open: {stem} {id} {face} at
  /// x,y,z</c>. An absent id or face reads <c>-</c>.
  /// </summary>
  /// <param name="scene">The scene drawn.</param>
  /// <param name="stem">The scene file's name less <c>.scene.json</c>.</param>
  /// <returns>The finding lines; empty when the scene has none.</returns>
  public static IReadOnlyList<string> FindingLines(SceneFile scene, string stem) {
    var lines = new List<string>();
    foreach (SceneCell c in scene.Cells)
      if (c.Port is { Joins: null } port)
        lines.Add(
          $"unjoined: {stem} {IdOf(scene, c) ?? "-"} {port.Face ?? "-"} "
            + $"{port.Type} at {Position(c.At)}"
        );
    foreach (SceneCell c in scene.Cells)
      foreach (string face in c.Open)
        lines.Add(
          $"open: {stem} {IdOf(scene, c) ?? "-"} {face} at {Position(c.At)}"
        );
    return lines;
  }

  private static string Position(Offset at) => $"{at.X},{at.Y},{at.Z}";

  private static JArray AtOf(Offset at) => new(at.X, at.Y, at.Z);

  // A filler or port is counted under its principal's id.
  private static string? IdOf(SceneFile scene, SceneCell cell) =>
    cell.Id
    ?? (
      cell.Principal is { } p
        ? scene.Cells.FirstOrDefault(c => c.At == p && c.Id != null)?.Id
        : null
    );

  private static IReadOnlyList<string> MissingLines(
    SceneFile scene,
    Dictionary<string, TextureRef> values,
    TextureSet textures
  ) {
    var lines = new SortedSet<string>(StringComparer.Ordinal);
    foreach (string prefixed in textures.Missing) {
      if (
        !values.TryGetValue(prefixed, out TextureRef? value) || value.Unassigned
      )
        continue;
      int underscore = prefixed.IndexOf('_');
      int cell = int.Parse(
        prefixed[1..underscore],
        CultureInfo.InvariantCulture
      );
      lines.Add(
        $"{scene.Cells[cell].Code}: texture {prefixed[(underscore + 1)..]} ({value}) not found"
      );
    }
    return [.. lines];
  }
}
