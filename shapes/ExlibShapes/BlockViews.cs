using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Vintagestory.API.Common;

namespace ExpandedLib.Shapes;

/// <summary>
/// Draws one blocktype variant the way the game draws it in the world: its own shape file under its
/// <c>shapeByType</c> turn, painted with the blocktype's texture map (the <c>all</c> entry standing
/// in for every key it does not name), or a unit cube when it ships no shape at all. A family that
/// reserves a footprint also gets that footprint's plan, in the same frame as the pictures, and the
/// machine stands the way <see cref="Presentation.Stage"/> puts it: front toward the camera.
/// </summary>
public static class BlockViews {
  /// <summary>The views a page of a block shows: the isometric one and the five faces a reader can
  /// tell apart (a block's underside is not one of them).</summary>
  public static readonly string[] DefaultViews = ["iso", "north", "east", "south", "west", "up"];

  // The texture and element prefix of the single block being drawn; nothing else shares the
  // composite, so the name only has to be stable for the manifest to report against.
  private const string Prefix = "block";

  /// <summary>The raw shape JSON of <paramref name="block"/>'s drawn model and the texture values
  /// (key to the block's own value string) it references. <paramref name="spin"/> turns the model
  /// about its own cell, the machine standing that way round.</summary>
  public static (JObject Raw, Dictionary<string, TextureRef> TextureValues) Compose(ResolvedBlock block, int spin = 0) {
    (JObject group, Dictionary<string, TextureRef> values) = Schematic.WrappedCell(block, default, Prefix, spin);
    return (new JObject { ["textures"] = new JObject(), ["elements"] = new JArray(group) }, values);
  }

  /// <summary>The drawn model of one variant: the shape every picture of it is rendered from, the
  /// textures it is painted with, and what the drawing says about how it stands.</summary>
  /// <param name="Shape">The composed model, turned by <paramref name="Angle"/> and clipped.</param>
  /// <param name="Textures">Keyed as <paramref name="Shape"/>'s faces name them.</param>
  /// <param name="Footprint">The reserved cells in the same frame, or null when none.</param>
  /// <param name="Angle">The quarter turn the model is drawn at.</param>
  /// <param name="Front">The world side the front then looks toward, null when nothing names one.</param>
  /// <param name="Hidden">The element names left out of <paramref name="Shape"/>, in tree order.</param>
  /// <param name="MissingTextures">One line per texture value that resolves to no file, sorted.</param>
  /// <param name="UnpaintedFaces">One line per face key the blocktype assigns nothing, sorted.</param>
  public readonly record struct Drawing(
    LoadedShape Shape,
    TextureSet Textures,
    Layout? Footprint,
    int Angle,
    string? Front,
    IReadOnlyList<string> Hidden,
    IReadOnlyList<string> MissingTextures,
    IReadOnlyList<string> UnpaintedFaces
  );

  /// <summary>
  /// The model <see cref="Write"/> draws <paramref name="variant"/> from, without writing anything -
  /// for a caller measuring what the pictures actually show. The turn is the footprint's
  /// (<see cref="Presentation.Stage"/>) for a family that reserves one, else the art's own
  /// (<see cref="Presentation.DetailTurn"/>) for a family with no facing variant to choose between,
  /// else none - the drawn variant already faces the camera.
  /// </summary>
  /// <param name="file">The blocktype file.</param>
  /// <param name="variant">The variant to draw, from <paramref name="index"/>.</param>
  /// <param name="index">The index <paramref name="variant"/> came from.</param>
  /// <param name="angle">A quarter turn to draw the machine at instead of the one the rules above
  /// choose; null takes theirs.</param>
  /// <param name="full">Keeps the whole model, the parts an animation parks outside the block's own
  /// cells included; false clips them.</param>
  /// <exception cref="InvalidOperationException"><paramref name="index"/> cannot resolve
  /// <paramref name="variant"/>.</exception>
  public static Drawing Draw(string file, Variant variant, BlockIndex index, int? angle = null, bool full = false) {
    ResolvedBlock block =
      index.Resolve(variant.Code) ?? throw new InvalidOperationException($"{variant.Code}: the index cannot resolve it");
    (JObject rest, Dictionary<string, TextureRef> textureValues) = Compose(block);
    LoadedShape atRest = Loaded(rest, block, variant);

    Layout? placed = FootprintOf(file, variant, index);
    int spin;
    string? front;
    if (placed != null) {
      Presentation.Staged staged = Presentation.Stage(placed, variant);
      (spin, front) = (staged.Angle, staged.Front);
    } else if (BlockIndex.Facing(index.VariantsOf(file), Presentation.Facing) == null) {
      // No facing variant to choose between: the family's facing lives in its C#, so the art is
      // what says which side a player looks at.
      (spin, front) = Presentation.DetailTurn(atRest);
    } else {
      (spin, front) = (0, Presentation.FrontOf(variant));
    }
    if (angle is { } wanted) {
      front = front == null ? null : Layout.RotateSideWord(Layout.RotateSideWord(front, -spin), wanted);
      spin = wanted;
    }

    Layout? footprint = placed == null || spin == 0 ? placed : placed.Rotated(spin);
    LoadedShape loaded = spin == 0 ? atRest : Loaded(Compose(block, spin).Raw, block, variant);
    TextureSet textures = TextureSet.FromResolved(textureValues, index.ResolveTexture);
    IReadOnlyList<string> hidden = [];
    if (!full) {
      JObject clipped = (JObject)Compose(block, spin).Raw;
      hidden = Clip(clipped, loaded, footprint, Moving(block));
      if (hidden.Count > 0)
        loaded = Loaded(clipped, block, variant);
    }
    (IReadOnlyList<string> missing, IReadOnlyList<string> unpainted) = TextureLines(block, textureValues, textures);
    return new Drawing(loaded, textures, footprint, spin, front, hidden, missing, unpainted);
  }

  /// <summary>
  /// Writes <paramref name="variant"/>'s pictures into <paramref name="outDir"/> - one
  /// <c>&lt;stem&gt;-&lt;view&gt;.png</c> per view, plus <c>&lt;stem&gt;-footprint.svg</c> when
  /// <paramref name="file"/> declares a footprint - and the <c>&lt;stem&gt;.json</c> manifest
  /// beside them, which it returns: <c>files</c>, the <c>variant</c> drawn, the quarter turn
  /// (<c>angle</c>) it is drawn at, the world side its <c>front</c> then looks toward (JSON null for
  /// a block that faces no way), the <c>missingTextures</c> whose value names a file that is not
  /// there, the <c>unpaintedFaces</c> the blocktype assigns no texture at all, and <c>warnings</c>.
  /// <para>
  /// The turn is the footprint's (<see cref="Presentation.Stage"/>) for a family that reserves one,
  /// else the art's own (<see cref="Presentation.DetailTurn"/>) for a family with no facing variant
  /// to choose between, else none - the drawn variant already faces the camera.
  /// </para>
  /// </summary>
  /// <param name="file">The blocktype file, whose own name is the stem of everything written.</param>
  /// <param name="variant">The variant to draw, from <paramref name="index"/>.</param>
  /// <param name="index">The index <paramref name="variant"/> came from; its ambiguities and parse
  /// warnings are read after the block resolves, so they land in the manifest.</param>
  /// <param name="outDir">Created when absent.</param>
  /// <param name="views">Named <see cref="Renderer.NamedViews"/> entries; <see cref="DefaultViews"/>
  /// when null.</param>
  /// <param name="ppu">Pixels per shape unit, as <c>render</c> means it.</param>
  /// <param name="angle">A quarter turn to draw the machine at instead of the one the rules above
  /// choose; null takes theirs.</param>
  /// <param name="full">Draws the whole model, the parts an animation parks outside the block's own
  /// cells included; false clips them.</param>
  public static JObject Write(
    string file,
    Variant variant,
    BlockIndex index,
    string outDir,
    IReadOnlyList<string>? views = null,
    int ppu = 24,
    int? angle = null,
    bool full = false
  ) {
    ResolvedBlock block =
      index.Resolve(variant.Code) ?? throw new InvalidOperationException($"{variant.Code}: the index cannot resolve it");
    Drawing drawn = Draw(file, variant, index, angle, full);
    LoadedShape loaded = drawn.Shape;
    Layout? footprint = drawn.Footprint;
    int spin = drawn.Angle;
    string? front = drawn.Front;

    Directory.CreateDirectory(outDir);
    string stem = Path.GetFileNameWithoutExtension(file);
    var files = new List<string>();
    foreach (string view in views ?? DefaultViews) {
      string path = Path.Combine(outDir, $"{stem}-{view}.png");
      // Back faces are drawn as well as front ones: a boiler's flue openings and a hopper's
      // mouth are hollow, and culled they read as holes cut through to the paper.
      using (
        SKBitmap image =
          Renderer.Render(loaded, Renderer.NamedViews[view], ppu: ppu, textures: drawn.Textures, cull: false)
      )
      using (SKData data = image.Encode(SKEncodedImageFormat.Png, 100))
      using (FileStream stream = File.Create(path))
        data.SaveTo(stream);
      files.Add(path);
    }

    if (footprint != null) {
      string path = Path.Combine(outDir, $"{stem}-footprint.svg");
      File.WriteAllText(path, Schematic.FootprintSvg(footprint, front: front));
      files.Add(path);
    }

    var warnings = new List<string>();
    if (block.ShapePath == null)
      warnings.Add($"{block.Code}: no shape file; drawn as a unit cube");
    foreach ((string selector, IReadOnlyList<string> spanned) in index.Ambiguities)
      warnings.Add($"{selector}: ambiguous between {string.Join(", ", spanned)}");
    warnings.AddRange(index.ParseWarnings);

    var manifest = new JObject {
      ["files"] = new JArray(files),
      ["variant"] = block.Code,
      ["angle"] = spin,
      ["clipped"] = !full,
      ["hidden"] = new JArray(drawn.Hidden),
      ["front"] = front is { } side ? side : JValue.CreateNull(),
      ["missingTextures"] = new JArray(drawn.MissingTextures),
      ["unpaintedFaces"] = new JArray(drawn.UnpaintedFaces),
      ["warnings"] = new JArray(warnings),
    };
    File.WriteAllText(Path.Combine(outDir, $"{stem}.json"), manifest.ToString(Formatting.Indented));
    return manifest;
  }

  // The lines the manifest reports a texture with, each sorted and each key once: a value naming a
  // file that is not there, and a face key the blocktype assigns nothing. Compose keys every value
  // `block_<key>`.
  private static (IReadOnlyList<string> Missing, IReadOnlyList<string> Unpainted) TextureLines(
    ResolvedBlock block,
    IReadOnlyDictionary<string, TextureRef> textureValues,
    TextureSet textures
  ) {
    var missing = new SortedSet<string>(StringComparer.Ordinal);
    var unpainted = new SortedSet<string>(StringComparer.Ordinal);
    foreach (string prefixed in textures.Missing) {
      string key = prefixed[(Prefix.Length + 1)..];
      TextureRef value = textureValues[prefixed];
      if (value.Unassigned)
        unpainted.Add($"{block.Code}: face texture {key} is assigned nothing");
      else
        missing.Add($"{block.Code}: texture {key} ({value}) not found");
    }
    return ([.. missing], [.. unpainted]);
  }

  // The names every animation of `block`'s own shape moves; empty when it has no shape file of its
  // own, declares no animation, or the file cannot be read.
  private static IReadOnlySet<string> Moving(ResolvedBlock block) {
    if (block.ShapePath == null)
      return new HashSet<string>(StringComparer.Ordinal);
    try {
      return Poses.AnimatedNames(ShapeFile.Load(block.ShapePath));
    } catch (Exception) {
      return new HashSet<string>(StringComparer.Ordinal);
    }
  }

  // `raw` with every part an animation parks outside the cells `footprint` reserves cut out of it,
  // and the names of the parts that went, each carrying whatever hangs off it. A part goes when
  // both hold: an animation names it, so a placed block moves it elsewhere, and less than half the
  // bulk of it and its children lies inside those cells, so the rest pose is not where the block
  // stands. Either test alone cuts art a block does show - a static vice bolted to the end of a
  // bench reaches past its cell, and a furnace door animates the group its whole body hangs from.
  private static IReadOnlyList<string> Clip(
    JObject raw,
    LoadedShape shape,
    Layout? footprint,
    IReadOnlySet<string> moving
  ) {
    if (moving.Count == 0)
      return [];
    Footprint.Box cells = Footprint.CellBox(footprint == null ? [] : Footprint.Reserved(footprint));
    // Footprint measures in blocks about the block's own centre; the composed model is drawn in
    // shape units, sixteen to a block, from that cell's own corner. Art may stand the overhang
    // proud of its cells before any of it counts as outside.
    Vector3 lo = (cells.Lo + new Vector3(0.5f - (float)Footprint.Overhang)) * 16;
    Vector3 hi = (cells.Hi + new Vector3(0.5f + (float)Footprint.Overhang)) * 16;

    Dictionary<string, Matrix4x4> mats = Geometry.WorldMatrices(shape);
    var outside = new HashSet<string>(StringComparer.Ordinal);
    foreach (Node element in shape.Walk()) {
      if (!moving.Contains(element.Name))
        continue;
      double inside = 0, bulk = 0;
      foreach (Node leaf in Drawn(element)) {
        (Vector3 elLo, Vector3 elHi) = Geometry.Aabb(Geometry.Corners(mats[leaf.Path], (Vector3)leaf.Size));
        // A face-thin element has no volume of its own to weigh; every part is taken half a unit
        // thicker on each axis so a plane inside the cells counts as inside them.
        elLo -= new Vector3(0.5f);
        elHi += new Vector3(0.5f);
        inside += Shared(elLo, elHi, lo, hi);
        bulk += Volume(elLo, elHi);
      }
      if (bulk > 0 && inside * 2 < bulk)
        outside.Add(element.Path);
    }
    if (outside.Count == 0)
      return [];

    var hidden = new List<string>();
    void Cut(JArray elements, string prefix) {
      for (int i = elements.Count - 1; i >= 0; i--) {
        var element = (JObject)elements[i];
        string name = (string?)element["name"] ?? "?";
        string path = (prefix.Length > 0 ? prefix + "/" : "") + name;
        if (outside.Contains(path)) {
          hidden.Add(name);
          elements.RemoveAt(i);
          continue;
        }
        if (element["children"] is JArray children)
          Cut(children, path);
      }
    }
    Cut((JArray)raw["elements"]!, "");
    hidden.Reverse();
    return hidden;
  }

  // Every drawn element of `element`'s own subtree, itself included.
  private static List<Node> Drawn(Node element) {
    var leaves = new List<Node>();
    void Walk(Node node) {
      if (node.IsLeaf)
        leaves.Add(node);
      foreach (Node child in node.Children)
        Walk(child);
    }
    Walk(element);
    return leaves;
  }

  private static double Volume(Vector3 lo, Vector3 hi) =>
    (double)(hi.X - lo.X) * (hi.Y - lo.Y) * (hi.Z - lo.Z);

  // The volume two boxes share, zero when they miss each other on any axis.
  private static double Shared(Vector3 aLo, Vector3 aHi, Vector3 bLo, Vector3 bHi) =>
    (double)Math.Max(0, Math.Min(aHi.X, bHi.X) - Math.Max(aLo.X, bLo.X))
    * Math.Max(0, Math.Min(aHi.Y, bHi.Y) - Math.Max(aLo.Y, bLo.Y))
    * Math.Max(0, Math.Min(aHi.Z, bHi.Z) - Math.Max(aLo.Z, bLo.Z));

  // The composed model as a shape the renderer draws, named for the block it came from.
  private static LoadedShape Loaded(JObject raw, ResolvedBlock block, Variant variant) {
    Shape shape =
      JsonConvert.DeserializeObject<Shape>(raw.ToString())
      ?? throw new JsonException($"{variant.Code}: the composed block shape failed to parse");
    return ShapeFile.FromRaw(shape, block.ShapePath, new Dictionary<string, string>());
  }

  // The block's own reserved footprint, turned into the frame its model is drawn in, or null when
  // the file declares none - a plain block, and a structure whose cells are the player's own blocks.
  private static Layout? FootprintOf(string file, Variant variant, BlockIndex index) {
    Layout layout;
    try {
      layout = Layout.Load(file, variant.Path);
    } catch (LayoutError) {
      return null;
    }
    return layout.Fillers.Count > 0 ? Footprint.Placed(layout, index) : null;
  }
}
