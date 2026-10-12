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
/// <param name="Files">The PNG paths written, in view order.</param>
/// <param name="Unresolved">Each code no root resolves, once, sorted.</param>
/// <param name="MissingTextures">One line per texture value of a drawn cell that names no file,
/// sorted.</param>
public sealed record SceneRendered(
  IReadOnlyList<string> Files,
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
  /// Writes <c>{stem}-plain-{view}.png</c> for each corner view in <paramref name="views"/>, all on
  /// one canvas: the union of the views' fitted canvases. Names in <paramref name="views"/> that
  /// are not corner views are left to the caller.
  /// </summary>
  /// <param name="scene">The scene to draw.</param>
  /// <param name="index">Resolves cell codes and texture values.</param>
  /// <param name="outDir">The folder the files go to; created when absent.</param>
  /// <param name="stem">The scene file's name less <c>.scene.json</c>.</param>
  /// <param name="views">View names; those in <see cref="CornerViews"/> are drawn.</param>
  /// <param name="ppu">Pixels per unit, above 0.</param>
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
    Renderer.Canvas? canvas = null;
    foreach (string name in corners)
      if (Renderer.Fit(shape, Renderer.NamedViews[name]) is { } fit)
        canvas = canvas?.Union(fit) ?? fit;
    Directory.CreateDirectory(outDir);
    var files = new List<string>();
    foreach (string name in corners) {
      using SKBitmap bitmap = Renderer.Render(
        shape,
        Renderer.NamedViews[name],
        ppu: ppu,
        textures: textures,
        fitTo: canvas
      );
      string path = Path.Combine(outDir, $"{stem}-plain-{name}.png");
      using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
      using FileStream stream = File.Create(path);
      data.SaveTo(stream);
      files.Add(path);
    }
    return new SceneRendered(
      files,
      unresolved,
      MissingLines(scene, values, textures)
    );
  }

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
