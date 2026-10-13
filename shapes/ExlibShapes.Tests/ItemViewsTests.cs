using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Vintagestory.API.Common;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Covers the <c>item</c> command: the isometric render of an item that ships a model and
/// the enlarged icon of one that ships a flat texture.</summary>
public class ItemViewsTests {
  private static string DemoRoot => FixturePath.Of("schematic");

  private static string Itemtype(string name) =>
    FixturePath.Of($"schematic/mods/demo/assets/demo/itemtypes/{name}.json");

  private static string OutDir(string name) {
    string dir = Path.Combine(AppContext.BaseDirectory, "actual", "item", name);
    if (Directory.Exists(dir))
      Directory.Delete(dir, true);
    return dir;
  }

  [Fact]
  public void An_item_with_a_flat_texture_gets_its_icon_enlarged() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    string file = Itemtype("token");
    Variant variant = Assert.Single(index.ItemVariants(file));
    Assert.Equal("demo:token", variant.Code);

    string outDir = OutDir("token");
    JObject manifest = ItemViews.Write(file, variant, index, outDir);
    Assert.Equal(
      ["token-icon.png"],
      manifest["files"]!.Select(f => Path.GetFileName((string)f!))
    );
    Assert.Empty((JArray)manifest["missingTextures"]!);
    Assert.Empty((JArray)manifest["warnings"]!);
    Assert.True(
      File.Exists(Path.Combine(outDir, "token.json")),
      "the manifest is written beside the picture"
    );

    // The fixture's own texture is 16 texels square, drawn four pixels to the texel on paper.
    using SKBitmap icon = SKBitmap.Decode(
      Path.Combine(outDir, "token-icon.png")
    );
    Assert.Equal(16 * ItemViews.IconScale + 16, icon.Width);
    Assert.Equal(icon.Width, icon.Height);
    Assert.Equal(Renderer.Background, icon.GetPixel(0, 0));
  }

  [Fact]
  public void An_item_whose_shape_entry_selects_elements_still_draws_them() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    string file = Itemtype("chip");
    Variant variant = Assert.Single(index.ItemVariants(file));
    string outDir = OutDir("chip");
    JObject manifest = ItemViews.Write(file, variant, index, outDir, ppu: 8);
    Assert.Equal(
      ["chip-iso.png"],
      manifest["files"]!.Select(f => Path.GetFileName((string)f!))
    );
    using SKBitmap picture = SKBitmap.Decode(
      Path.Combine(outDir, "chip-iso.png")
    );
    bool drawn = false;
    for (int y = 0; y < picture.Height && !drawn; y++)
      for (int x = 0; x < picture.Width && !drawn; x++)
        drawn = picture.GetPixel(x, y) != Renderer.Background;
    Assert.True(drawn, "the kept element is drawn");
  }

  [Fact]
  public void An_item_with_a_shape_gets_the_isometric_render_of_it() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    string file = Itemtype("tool");
    // The file's own variantgroups expand the same way a blocktype's do.
    Assert.Equal(
      ["demo:tool-iron", "demo:tool-steel"],
      index.ItemVariants(file).Select(v => v.Code)
    );

    string outDir = OutDir("tool");
    JObject manifest = ItemViews.Write(
      file,
      index.ItemVariants(file)[1],
      index,
      outDir,
      ppu: 8
    );
    Assert.Equal("demo:tool-steel", (string)manifest["variant"]!);
    Assert.Equal(
      ["tool-iso.png"],
      manifest["files"]!.Select(f => Path.GetFileName((string)f!))
    );
    Assert.Empty((JArray)manifest["missingTextures"]!);
    Assert.Empty((JArray)manifest["warnings"]!);
  }

  [Fact]
  public void A_singular_texture_paints_the_face_keys_no_map_names() {
    IReadOnlyDictionary<string, TextureRef> values = SingularValues(
      "\"textures\": { \"own\": \"demo:block/wall\" }"
    );
    Assert.Equal("paint:block/top", values["block_face"].Base);
  }

  [Fact]
  public void A_singular_texture_leaves_the_key_the_shape_declares() {
    IReadOnlyDictionary<string, TextureRef> values = SingularValues(
      "\"textures\": { \"own\": \"demo:block/wall\" }"
    );
    Assert.Equal("demo:block/wall", values["block_own"].Base);
  }

  [Fact]
  public void An_item_texture_map_key_outranks_the_singular_texture() {
    IReadOnlyDictionary<string, TextureRef> values = SingularValues(
      "\"textures\": { \"own\": \"demo:block/wall\" }",
      ", \"textures\": { \"face\": \"paint:block/named\" }"
    );
    Assert.Equal("paint:block/named", values["block_face"].Base);
  }

  [Fact]
  public void A_held_clip_key_of_the_current_schema_outranks_the_obsolete_one() {
    Variant variant = ClipVariant(
      "\"heldRightTpIdleAnimation\": \"newidle\", \"heldTpIdleAnimation\": \"oldidle\""
    );
    Assert.Equal("newidle", ItemViews.IdleClipOf(variant));
    Assert.Equal(
      "oldidle",
      ItemViews.IdleClipOf(ClipVariant("\"heldTpIdleAnimation\": \"oldidle\""))
    );
  }

  private static Variant ClipVariant(string entries) {
    string root = Path.Combine(
      Path.GetTempPath(),
      "exlib-shapes-" + Guid.NewGuid().ToString("N")
    );
    string file = Path.Combine(
      root,
      "mods",
      "clip",
      "assets",
      "clip",
      "itemtypes",
      "rod.json"
    );
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    File.WriteAllText(file, "{ \"code\": \"rod\", " + entries + " }");
    try {
      return Assert.Single(BlockIndex.Build([root]).ItemVariants(file));
    } finally {
      Directory.Delete(root, true);
    }
  }

  // The texture values an item with the singular texture paint:block/top and a shape declaring
  // `own` and using `own` and `face` composes; shapeEntries is the shape's extra top-level JSON,
  // itemEntries the itemtype's.
  private static IReadOnlyDictionary<string, TextureRef> SingularValues(
    string shapeEntries,
    string itemEntries = ""
  ) {
    string root = Path.Combine(
      Path.GetTempPath(),
      "exlib-shapes-" + Guid.NewGuid().ToString("N")
    );
    string assets = Path.Combine(root, "mods", "paint", "assets", "paint");
    string file = Path.Combine(assets, "itemtypes", "plate.json");
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    Directory.CreateDirectory(Path.Combine(assets, "shapes", "item"));
    File.WriteAllText(
      Path.Combine(assets, "shapes", "item", "plate.json"),
      "{ "
        + shapeEntries
        + ", \"elements\": [ { \"name\": \"box\", \"from\": [0,0,0], \"to\": [4,4,4], "
        + "\"faces\": { \"north\": { \"texture\": \"#own\", \"uv\": [0,0,4,4] }, "
        + "\"south\": { \"texture\": \"#face\", \"uv\": [0,0,4,4] } } } ] }"
    );
    File.WriteAllText(
      file,
      "{ \"code\": \"plate\", \"shape\": { \"base\": \"paint:item/plate\" }, "
        + "\"texture\": { \"base\": \"paint:block/top\" }"
        + itemEntries
        + " }"
    );
    try {
      BlockIndex index = BlockIndex.Build([DemoRoot, root]);
      Variant variant = Assert.Single(index.ItemVariants(file));
      ResolvedBlock item = index.ResolveVariant(variant);
      return ItemViews
        .Compose(ItemViews.WithSingularTexture(item, variant), index)
        .Values;
    } finally {
      Directory.Delete(root, true);
    }
  }

  [SkippableFact]
  public void The_smex_burden_draws_the_shape_it_borrows_from_the_game() {
    string? file = FixturePath.Workspace(
      "exmods-legacy/smex/assets/smex/itemtypes/burden.json"
    );
    Skip.If(file is null, "the sibling exmods-legacy checkout is absent");
    BlockIndex index = BlockIndex.Build(
      BlockIndex.DefaultRoots(file!),
      null,
      legacyFirst: true
    );
    Variant variant = Assert.Single(index.ItemVariants(file!));
    Assert.Equal("smex:burden", variant.Code);

    string outDir = OutDir("burden");
    JObject manifest = ItemViews.Write(file!, variant, index, outDir, ppu: 8);
    Assert.Equal(
      ["burden-iso.png"],
      manifest["files"]!.Select(f => Path.GetFileName((string)f!))
    );
    Assert.Empty((JArray)manifest["missingTextures"]!);
    Assert.Empty((JArray)manifest["warnings"]!);

    // The ore-pile shape it borrows is wider than it is tall, and nothing of it is the placeholder.
    using SKBitmap iso = SKBitmap.Decode(
      Path.Combine(outDir, "burden-iso.png")
    );
    Assert.True(
      iso.Width > iso.Height,
      $"the pile is drawn {iso.Width}x{iso.Height}"
    );
    Assert.False(Magenta(iso), "the render paints the magenta placeholder");
  }

  [SkippableFact]
  public void An_iiex_item_with_its_own_shape_renders_every_texture() {
    string? file = FixturePath.Workspace(
      "exmods/mods/iiex/tests/goldens/iiex/itemtypes/machined/spurgear.json"
    );
    Skip.If(file is null, "the sibling exmods checkout is absent");
    BlockIndex index = BlockIndex.Build(BlockIndex.DefaultRoots(file!));
    Variant variant = Assert.Single(index.ItemVariants(file!));
    ResolvedBlock item = index.ResolveVariant(variant);
    Assert.EndsWith(
      Path.Combine("iiex", "shapes", "item", "gearspur.json"),
      item.ShapePath
    );

    string outDir = OutDir("spurgear");
    JObject manifest = ItemViews.Write(file!, variant, index, outDir, ppu: 8);
    Assert.Equal(
      ["spurgear-iso.png"],
      manifest["files"]!.Select(f => Path.GetFileName((string)f!))
    );
    Assert.Empty((JArray)manifest["missingTextures"]!);
    using SKBitmap iso = SKBitmap.Decode(
      Path.Combine(outDir, "spurgear-iso.png")
    );
    Assert.False(Magenta(iso), "the render paints the magenta placeholder");
  }

  // Whether any pixel carries the magenta placeholder, at whatever shade its face took.
  private static bool Magenta(SKBitmap image) {
    for (int y = 0; y < image.Height; y++)
      for (int x = 0; x < image.Width; x++) {
        SKColor pixel = image.GetPixel(x, y);
        if (pixel.Green == 0 && pixel.Red == pixel.Blue && pixel.Red >= 64)
          return true;
      }
    return false;
  }

  // The client install the vanilla item tests need. CI provisions one (.github/workflows/ci.yml),
  // so its absence there fails rather than skipping and leaving the views unguarded.
  private static string RequireClientGame() {
    string? game = FixturePath.ClientGame("assets/survival/shapes");
    Skip.If(
      game is null
        && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == null,
      "a client install is absent"
    );
    Assert.NotNull(game);
    return game!;
  }

  private static (
    LoadedShape Shape,
    TextureSet Textures,
    ItemTransform Gui,
    ItemTransform Ground
  ) Vanilla(string file, string code) {
    string path = Path.Combine(
      RequireClientGame(),
      "assets/survival/itemtypes",
      file
    );
    BlockIndex index = BlockIndex.Build(BlockIndex.DefaultRoots(path));
    Variant variant = index.ItemVariants(path).Single(v => v.Path == code);
    (LoadedShape shape, TextureSet textures, _) = ItemViews.Compose(
      index.ResolveVariant(variant),
      index
    );
    return (
      shape,
      textures,
      ItemViews.ReadTransform(variant, ItemViews.GuiKey),
      ItemViews.ReadTransform(variant, ItemViews.GroundKey)
    );
  }

  // The voxel corners of every leaf box of a shape whose elements carry no rotation.
  private static IEnumerable<(double X, double Y, double Z)> Corners(
    LoadedShape shape
  ) {
    foreach (Node el in shape.Leaves())
      foreach (double x in new[] { el.From.X, el.From.X + el.Size.X })
        foreach (double y in new[] { el.From.Y, el.From.Y + el.Size.Y })
          foreach (double z in new[] { el.From.Z, el.From.Z + el.Size.Z })
            yield return (x, y, z);
  }

  // A point turned by the game's rotation order: z first, then y, then x.
  private static (double X, double Y, double Z) Turn(
    (double X, double Y, double Z) p,
    (double X, double Y, double Z) deg
  ) {
    double rad(double d) => d * Math.PI / 180;
    (double x, double y, double z) = p;
    (x, y) = (
      x * Math.Cos(rad(deg.Z)) - y * Math.Sin(rad(deg.Z)),
      x * Math.Sin(rad(deg.Z)) + y * Math.Cos(rad(deg.Z))
    );
    (x, z) = (
      x * Math.Cos(rad(deg.Y)) + z * Math.Sin(rad(deg.Y)),
      -x * Math.Sin(rad(deg.Y)) + z * Math.Cos(rad(deg.Y))
    );
    (y, z) = (
      y * Math.Cos(rad(deg.X)) - z * Math.Sin(rad(deg.X)),
      y * Math.Sin(rad(deg.X)) + z * Math.Cos(rad(deg.X))
    );
    return (x, y, z);
  }

  // The columns and rows, in picture pixels, the opaque pixels of the gui item layer span.
  internal static (int MinCol, int MaxCol, int MinRow, int MaxRow) Bounds(
    SKBitmap layer
  ) {
    int minCol = int.MaxValue,
      maxCol = -1,
      minRow = int.MaxValue,
      maxRow = -1;
    for (int y = 0; y < layer.Height; y++)
      for (int x = 0; x < layer.Width; x++)
        if (layer.GetPixel(x, y).Alpha > 0) {
          minCol = Math.Min(minCol, x);
          maxCol = Math.Max(maxCol, x);
          minRow = Math.Min(minRow, y);
          maxRow = Math.Max(maxRow, y);
        }
    return (minCol, maxCol, minRow, maxRow);
  }

  [SkippableFact]
  public void Rod_gui_silhouette_bounds_match_the_slot_matrix() {
    (LoadedShape shape, TextureSet textures, ItemTransform gui, _) = Vanilla(
      "part/rod.json",
      "rod-iron"
    );
    // The slot matrix worked by hand: the box about the origin, turned, scaled to 25.6 px per
    // block, moved by the origin and translation in pixels and the item's (-3, -1) shift, then
    // read with y down from the grid's centre.
    var cols = new List<double>();
    var rows = new List<double>();
    foreach ((double X, double Y, double Z) c in Corners(shape)) {
      (double x, double y, double z) = Turn(
        (
          c.X / 16 - gui.Origin.X,
          c.Y / 16 - gui.Origin.Y,
          c.Z / 16 - gui.Origin.Z
        ),
        gui.Rotation
      );
      cols.Add(
        x * 25.6 * gui.ScaleXyz.X + gui.Origin.X + gui.Translation.X - 3
      );
      rows.Add(
        y * 25.6 * gui.ScaleXyz.Y + gui.Origin.Y + gui.Translation.Y - 1
      );
    }
    using SKBitmap layer = ItemViews.RenderGuiItem(shape, textures, gui);
    (int minCol, int maxCol, int minRow, int maxRow) = Bounds(layer);
    int ppu = ItemViews.GuiPpu;
    int tolerance = ppu;
    Assert.InRange(
      minCol,
      (72 + cols.Min()) * ppu - tolerance,
      (72 + cols.Min()) * ppu + tolerance
    );
    Assert.InRange(
      maxCol + 1,
      (72 + cols.Max()) * ppu - tolerance,
      (72 + cols.Max()) * ppu + tolerance
    );
    Assert.InRange(
      minRow,
      (72 + rows.Min()) * ppu - tolerance,
      (72 + rows.Min()) * ppu + tolerance
    );
    Assert.InRange(
      maxRow + 1,
      (72 + rows.Max()) * ppu - tolerance,
      (72 + rows.Max()) * ppu + tolerance
    );

    // The bounds alone are the same mirrored top to bottom; the highest corner fixes which end of
    // the rod is up.
    int top = rows.IndexOf(rows.Min());
    double topCol = (72 + cols[top]) * ppu;
    bool atTop = false;
    for (int x = 0; x < layer.Width; x++)
      for (int y = minRow; y < minRow + 2 * ppu; y++)
        atTop |=
          layer.GetPixel(x, y).Alpha > 0 && Math.Abs(x - topCol) <= 2 * tolerance;
    Assert.True(
      atTop,
      "the rod's highest corner is drawn at the top of its bounds"
    );
  }

  [SkippableFact]
  public void Spear_gui_runs_past_the_centre_slot() {
    (LoadedShape shape, TextureSet textures, ItemTransform gui, _) = Vanilla(
      "tool/spear.json",
      "spear-generic-iron"
    );
    using SKBitmap layer = ItemViews.RenderGuiItem(shape, textures, gui);
    (int minCol, int maxCol, int minRow, int maxRow) = Bounds(layer);
    int slot = 48 * ItemViews.GuiPpu;
    // The spear's tip stays in the slot and its shaft leaves it: it runs past the slot's far
    // corner, so its bounds are wider and taller than the slot.
    Assert.True(maxCol - minCol > slot, $"columns {minCol}..{maxCol}");
    Assert.True(maxRow - minRow > slot, $"rows {minRow}..{maxRow}");
    Assert.True(maxCol >= 2 * slot && maxRow >= 2 * slot);
  }

  [SkippableTheory]
  [InlineData("part/rod.json", "rod-iron")]
  [InlineData("tool/spear.json", "spear-generic-iron")]
  public void Spear_and_rod_lie_flat_on_the_ground(string file, string code) {
    (LoadedShape shape, _, _, ItemTransform ground) = Vanilla(file, code);
    // The item's thinnest box axis (a tie goes to y, the authored up) is its top face's normal.
    Dictionary<string, Renderer.Mat4d> mats = Renderer.WorldMatricesD(
      shape,
      null
    );
    var points = shape
      .Leaves()
      .SelectMany(el => Renderer.FaceQuadsD(el, mats[el.Path]))
      .SelectMany(q => q.Points)
      .ToList();
    double[] extent =
    [
      points.Max(p => p.X) - points.Min(p => p.X),
      points.Max(p => p.Y) - points.Min(p => p.Y),
      points.Max(p => p.Z) - points.Min(p => p.Z),
    ];
    int thin =
      extent[1] <= extent[0] && extent[1] <= extent[2] ? 1
      : extent[0] <= extent[2] ? 0
      : 2;
    (double nx, double ny, double nz) = ItemViews
      .GroundModel(ground)
      .MulLinear(thin == 0 ? 1 : 0, thin == 1 ? 1 : 0, thin == 2 ? 1 : 0);
    double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
    Assert.Equal(1.0, Math.Abs(ny) / length, 6);
  }

  private static (
    LoadedShape Shape,
    TextureSet Textures,
    ItemTransform Tp,
    ItemViews.Seraph Seraph
  ) VanillaTp(string file, string code) {
    string path = Path.Combine(
      RequireClientGame(),
      "assets/survival/itemtypes",
      file
    );
    BlockIndex index = BlockIndex.Build(BlockIndex.DefaultRoots(path));
    Variant variant = index.ItemVariants(path).Single(v => v.Path == code);
    (LoadedShape shape, TextureSet textures, _) = ItemViews.Compose(
      index.ResolveVariant(variant),
      index
    );
    return (
      shape,
      textures,
      ItemViews.ReadTransform(variant, ItemViews.TpKey),
      ItemViews.LoadSeraph(index)!
    );
  }

  private static Renderer.Mat4d Anchor(ItemViews.Seraph seraph, string clip) =>
    Renderer.WorldMatricesD(seraph.Shape, Poses.PoseAt(seraph.Shape, clip, 0))[
      seraph.Shape.Find("ItemAnchor")!.Path
    ];

  // A point of the held item in the player's voxels, worked from the hand chain: the point turned
  // by the item's rotation plus the attachment point's -180 about y, moved by the translation,
  // scaled, all about the origin, in blocks, then through the anchor.
  private static (double X, double Y, double Z) Held(
    Renderer.Mat4d anchor,
    ItemTransform t,
    (double X, double Y, double Z) p
  ) {
    (double x, double y, double z) = Turn(
      (p.X / 16 - t.Origin.X, p.Y / 16 - t.Origin.Y, p.Z / 16 - t.Origin.Z),
      (t.Rotation.X, t.Rotation.Y - 180, t.Rotation.Z)
    );
    return anchor.Mul(
      16 * (t.Origin.X + t.ScaleXyz.X * (t.Translation.X + x)),
      16 * (t.Origin.Y + t.ScaleXyz.Y * (t.Translation.Y + y)),
      16 * (t.Origin.Z + t.ScaleXyz.Z * (t.Translation.Z + z))
    );
  }

  // The two ends of a shape along x.
  private static (
    (double X, double Y, double Z) Low,
    (double X, double Y, double Z) High
  ) Ends(LoadedShape shape) {
    Dictionary<string, Renderer.Mat4d> mats = Renderer.WorldMatricesD(
      shape,
      null
    );
    var points = shape
      .Leaves()
      .SelectMany(el => Renderer.FaceQuadsD(el, mats[el.Path]))
      .SelectMany(q => q.Points)
      .ToList();
    return (points.MinBy(p => p.X), points.MaxBy(p => p.X));
  }

  // The item's two ends drawn in the tp view's side and three-quarter angles lie within a pixel of
  // where the hand chain puts them, and the pictures' world points are the ones the chain gives.
  private static (
    (double X, double Y, double Z) Low,
    (double X, double Y, double Z) High,
    double PlayerHeight
  ) AssertTpEnds(string file, string code, string clip) {
    (
      LoadedShape shape,
      TextureSet textures,
      ItemTransform tp,
      ItemViews.Seraph seraph
    ) = VanillaTp(file, code);
    Renderer.RenderPart[] parts = ItemViews.TpParts(
      shape,
      textures,
      tp,
      seraph,
      clip
    );
    Renderer.Mat4d anchor = Anchor(seraph, clip);
    ((double, double, double) low, (double, double, double) high) = Ends(shape);
    foreach (View view in ItemViews.TpAngles) {
      Renderer.Projection projection = Renderer.Project(
        parts,
        view,
        ItemViews.TpPpu
      );
      foreach ((double, double, double) end in new[] { low, high }) {
        (double wx, double wy, double wz) = Held(anchor, tp, end);
        (double expectedCol, double expectedRow) = projection.Screen(
          wx,
          wy,
          wz
        );
        (double mx, double my, double mz) = parts[1]
          .Model.Mul(end.Item1, end.Item2, end.Item3);
        (double col, double row) = projection.Screen(mx, my, mz);
        Assert.InRange(col, expectedCol - 1, expectedCol + 1);
        Assert.InRange(row, expectedRow - 1, expectedRow + 1);
      }
    }
    Dictionary<string, Renderer.Mat4d> bodyMats = Renderer.WorldMatricesD(
      seraph.Shape,
      null
    );
    double[] ys =
    [
      .. seraph
        .Shape.Leaves()
        .SelectMany(el => Renderer.FaceQuadsD(el, bodyMats[el.Path]))
        .SelectMany(q => q.Points)
        .Select(p => p.Y),
    ];
    return (Held(anchor, tp, low), Held(anchor, tp, high), ys.Max() - ys.Min());
  }

  [SkippableFact]
  public void Spear_tp_projected_corners() {
    (
      (double X, double Y, double Z) low,
      (double X, double Y, double Z) high,
      double height
    ) = AssertTpEnds("tool/spear.json", "spear-generic-iron", "spearidle");
    // The blade's point is at the shape's far end along x.
    var (tip, butt) = (high, low);
    // The player faces -x: the tip is the end the more to -x, and the spear is about 1.24 player
    // heights long.
    Assert.True(tip.X < butt.X, $"tip x {tip.X}, butt x {butt.X}");
    double length = Math.Sqrt(
      Math.Pow(tip.X - butt.X, 2)
        + Math.Pow(tip.Y - butt.Y, 2)
        + Math.Pow(tip.Z - butt.Z, 2)
    );
    Assert.InRange(length / height, 1.19, 1.29);
  }

  [SkippableFact]
  public void Rod_tp_projected_corners() {
    AssertTpEnds("part/rod.json", "rod-iron", "helditemready");
  }

  // Where a point of the player's voxels lands in the fp picture, worked from the game's model
  // matrix: the hands move up by the offset, turn about the eye by the share of the gaze pitch
  // they follow, and the camera turns by the whole pitch; then a pinhole with the eye at the origin.
  private static (double Col, double Row, double Depth) FpPixel(
    (double X, double Y, double Z) p,
    double pitchDeg,
    double follow,
    double offset
  ) {
    const double eyeY = 27.2;
    (double x, double y, double z) = (
      p.X - 8,
      p.Y + offset * 16 - eyeY,
      p.Z - 8
    );
    double a = pitchDeg * follow * Math.PI / 180;
    (x, y) = (
      x * Math.Cos(a) - y * Math.Sin(a),
      x * Math.Sin(a) + y * Math.Cos(a)
    );
    (double vx, double vy, double vz) = (-z, y, x);
    double c = pitchDeg * Math.PI / 180;
    (vy, vz) = (
      vy * Math.Cos(c) - vz * Math.Sin(c),
      vy * Math.Sin(c) + vz * Math.Cos(c)
    );
    double focal = ItemViews.FpCamera.Focal;
    return (
      ItemViews.FpWidth / 2.0 + focal * vx / -vz,
      ItemViews.FpHeight / 2.0 - focal * vy / -vz,
      -vz
    );
  }

  // The item's two ends drawn in the fp view lie within a pixel of where the hand chain, the
  // hands' offset and pitch follow put them.
  private static (
    (double Col, double Row, double Depth) Low,
    (double Col, double Row, double Depth) High
  ) AssertFpEnds(
    string file,
    string code,
    string clip,
    double pitchDeg = 0,
    double follow = ItemViews.FpPitchFollow,
    double offset = ItemViews.FpHandsYOffset
  ) {
    (
      LoadedShape shape,
      TextureSet textures,
      ItemTransform tp,
      ItemViews.Seraph seraph
    ) = VanillaTp(file, code);
    Renderer.RenderPart[] parts = ItemViews.FpParts(
      shape,
      textures,
      tp,
      seraph,
      clip,
      pitchDeg,
      follow,
      offset
    );
    Renderer.Projection projection = Renderer.Project(
      parts,
      new View(ItemViews.FpLevel.Yaw, pitchDeg),
      perspective: ItemViews.FpCamera
    );
    Renderer.Mat4d anchor = Anchor(seraph, clip + "-fp");
    ((double, double, double) low, (double, double, double) high) = Ends(shape);
    var pixels = new List<(double Col, double Row, double Depth)>();
    foreach ((double, double, double) end in new[] { low, high }) {
      (double col, double row, double depth) = FpPixel(
        Held(anchor, tp, end),
        pitchDeg,
        follow,
        offset
      );
      (double mx, double my, double mz) = parts[1]
        .Model.Mul(end.Item1, end.Item2, end.Item3);
      (double actualCol, double actualRow) = projection.Screen(mx, my, mz);
      Assert.InRange(actualCol, col - 1, col + 1);
      Assert.InRange(actualRow, row - 1, row + 1);
      pixels.Add((col, row, depth));
    }
    return (pixels[0], pixels[1]);
  }

  [SkippableFact]
  public void Spear_fp_projected_corners() {
    var (butt, tip) = AssertFpEnds(
      "tool/spear.json",
      "spear-generic-iron",
      "spearidle",
      offset: 0.1
    );
    // The tip is ahead of the eye and in the picture's lower right; the butt, 1.24 player heights
    // behind it, is behind the eye.
    Assert.True(tip.Depth > 0 && butt.Depth < 0, $"{tip} {butt}");
    Assert.InRange(tip.Col, ItemViews.FpWidth / 2, ItemViews.FpWidth);
    Assert.InRange(tip.Row, ItemViews.FpHeight / 2, ItemViews.FpHeight);
  }

  [SkippableFact]
  public void Rod_fp_projected_corners() {
    AssertFpEnds("part/rod.json", "rod-iron", "helditemready", pitchDeg: 30);
  }

  [SkippableFact]
  public void Hands_at_full_follow_stay_where_they_are_with_the_gaze() {
    (
      LoadedShape shape,
      TextureSet textures,
      ItemTransform tp,
      ItemViews.Seraph seraph
    ) = VanillaTp("part/rod.json", "rod-iron");
    (double, double) At(double pitchDeg) {
      Renderer.RenderPart[] parts = ItemViews.FpParts(
        shape,
        textures,
        tp,
        seraph,
        "helditemready",
        pitchDeg,
        follow: 1
      );
      Renderer.Projection projection = Renderer.Project(
        parts,
        new View(ItemViews.FpLevel.Yaw, pitchDeg),
        perspective: ItemViews.FpCamera
      );
      (double x, double y, double z) = parts[1].Model.Mul(0, 0, 0);
      return projection.Screen(x, y, z);
    }
    (double level, double levelRow) = At(0);
    (double down, double downRow) = At(40);
    Assert.Equal(level, down, 3);
    Assert.Equal(levelRow, downRow, 3);
  }

  [SkippableFact]
  public void The_fp_hands_are_the_arms_alone() {
    (
      LoadedShape shape,
      TextureSet textures,
      ItemTransform tp,
      ItemViews.Seraph seraph
    ) = VanillaTp("part/rod.json", "rod-iron");
    Renderer.RenderPart hands = ItemViews.FpParts(
      shape,
      textures,
      tp,
      seraph,
      "helditemready",
      90,
      follow: 0
    )[0];
    Assert.NotNull(hands.Only);
    Assert.All(
      hands.Only!,
      path => Assert.Contains("UpperArm", path, StringComparison.Ordinal)
    );
    int Opaque(Renderer.RenderPart part) {
      using SKBitmap image = Renderer.Render(
        [part],
        new View(ItemViews.FpLevel.Yaw, 90),
        ItemViews.FpCamera.Focal,
        grid: false,
        edges: false,
        transparent: true,
        perspective: ItemViews.FpCamera
      );
      int count = 0;
      for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
          if (image.GetPixel(x, y).Alpha > 0)
            count++;
      return count;
    }
    Assert.InRange(Opaque(hands), 1, Opaque(hands with { Only = null }) - 1);
  }

  [SkippableFact]
  public void A_held_clip_with_no_fp_twin_warns_and_falls_back_to_itself() {
    ItemViews.Seraph seraph = VanillaTp("part/rod.json", "rod-iron").Seraph;
    Assert.Equal(
      "helditemready-fp",
      ItemViews.FpClip(seraph.Shape, "helditemready")
    );
    string bare = seraph
      .Shape.Animations.Select(a => a.Code ?? a.Name)
      .First(name =>
        !name.EndsWith("-fp", StringComparison.Ordinal)
        && seraph.Shape.Animations.All(a =>
          a.Code != name + "-fp" && a.Name != name + "-fp"
        )
      );
    Assert.Equal(bare, ItemViews.FpClip(seraph.Shape, bare));
    Assert.Equal(
      [$"held clip {bare} has no {bare}-fp twin on the player shape"],
      ItemViews.FpWarnings(seraph.Shape, bare, null)
    );
    Assert.Empty(ItemViews.FpWarnings(seraph.Shape, "spearidle", null));
  }

  [SkippableFact]
  public void Held_clip_falls_from_idle_to_ready_to_the_generic_clip() {
    ItemViews.Seraph seraph = VanillaTp("part/rod.json", "rod-iron").Seraph;
    Assert.Equal(
      "spearidle",
      ItemViews.HeldClip(seraph.Shape, "spearidle", "spearready")
    );
    Assert.Equal(
      "spearready",
      ItemViews.HeldClip(seraph.Shape, "no-such-clip", "spearready")
    );
    Assert.Equal(
      "helditemready",
      ItemViews.HeldClip(seraph.Shape, "no-such-clip", null)
    );
    Assert.Equal(
      ["held clip no-such-clip is not on the player shape"],
      ItemViews.TpWarnings(seraph.Shape, "no-such-clip", "spearready")
    );
    Assert.Empty(ItemViews.TpWarnings(seraph.Shape, "spearidle", null));
  }

  [SkippableFact]
  public void Seraph_null_faces_are_not_drawn() {
    ItemViews.Seraph seraph = VanillaTp("part/rod.json", "rod-iron").Seraph;
    Dictionary<string, Renderer.Mat4d> mats = Renderer.WorldMatricesD(
      seraph.Shape,
      null
    );
    // The shape marks 50 faces #null and leaves each disabled; none reaches a quad.
    Assert.DoesNotContain(
      seraph
        .Shape.Leaves()
        .SelectMany(el => Renderer.FaceQuadsD(el, mats[el.Path])),
      q => q.Texture == "null"
    );
    using SKBitmap image = Renderer.Render(
      [
        new Renderer.RenderPart(
          seraph.Shape,
          seraph.Textures,
          null,
          Renderer.Mat4d.Identity,
          seraph.UvSize
        ),
      ],
      ItemViews.TpAngles[0],
      ItemViews.TpPpu,
      grid: false,
      edges: false
    );
    Assert.False(Magenta(image), "a #null face is drawn as a missing texture");
  }

  [Fact]
  public void An_enabled_null_face_is_drawn_and_a_disabled_one_is_not() {
    var raw = JsonConvert.DeserializeObject<Shape>(
      """
      {"elements":[{"name":"box","from":[0,0,0],"to":[1,1,1],
        "faces":{"north":{"texture":"#null","uv":[0,0,1,1],"enabled":false},
                 "east":{"texture":"#null","uv":[0,0,1,1]},
                 "south":{"texture":"#skin","uv":[0,0,1,1]}}}]}
      """
    )!;
    LoadedShape shape = ShapeFile.FromRaw(raw, null);
    Node box = Assert.Single(shape.Leaves());
    Assert.Equal(
      ["null", "skin"],
      Renderer
        .FaceQuadsD(box, Renderer.Mat4d.Identity)
        .Select(q => q.Texture)
        .Order(StringComparer.Ordinal)
    );
  }

  [Fact]
  public void Seraph_uv_is_in_the_shapes_texture_units() {
    var raw = JsonConvert.DeserializeObject<Shape>(
      """
      {"elements":[{"name":"box","from":[0,0,0],"to":[8,8,1],
        "faces":{"north":{"texture":"#t","uv":[0,0,16,38]}}}]}
      """
    )!;
    byte[,,] skin = new byte[2, 2, 4];
    byte[][] colours =
    [
      [255, 0, 0],
      [0, 255, 0],
      [0, 0, 255],
      [255, 255, 0],
    ];
    for (int i = 0; i < 4; i++) {
      for (int c = 0; c < 3; c++)
        skin[i / 2, i % 2, c] = colours[i][c];
      skin[i / 2, i % 2, 3] = 255;
    }
    var textures = TextureSet.FromResolved(
      new Dictionary<string, TextureRef>(),
      _ => null,
      new Dictionary<string, byte[,,]> { ["t"] = skin }
    );
    using SKBitmap image = Renderer.Render(
      [
        new Renderer.RenderPart(
          ShapeFile.FromRaw(raw, null),
          textures,
          null,
          Renderer.Mat4d.Identity,
          (32, 76)
        ),
      ],
      Renderer.NamedViews["north"],
      8,
      grid: false,
      edges: false
    );
    // The face's uv spans the top-left quarter of a 32 x 76 sheet, the skin's first texel only.
    int red = 0;
    for (int y = 0; y < image.Height; y++)
      for (int x = 0; x < image.Width; x++) {
        SKColor c = image.GetPixel(x, y);
        if (c.Red > 100 && c.Green == 0 && c.Blue == 0)
          red++;
        Assert.False(c.Green > 100 && c.Red < 50, "the second texel is drawn");
        Assert.False(c.Blue > 100 && c.Red < 50, "the third texel is drawn");
      }
    Assert.True(red > 8 * 8 * 8 * 8 / 2, $"{red} red pixels");
  }

  [SkippableFact]
  public void Seraph_texture_resolves_to_the_player_skin() {
    string game = RequireClientGame();
    BlockIndex index = BlockIndex.Build(
      BlockIndex.DefaultRoots(
        Path.Combine(game, "assets/survival/itemtypes/part/rod.json")
      )
    );
    ItemViews.Seraph seraph = ItemViews.LoadSeraph(index)!;
    // The player entity names the skin; the shape writes its UVs in 32 x 76 and the skin is twice
    // that on each axis.
    JObject player = JObject.Parse(
      File.ReadAllText(
        Path.Combine(game, "assets/game/entities/humanoid/player.json")
      )
    );
    Assert.Equal(
      "game:" + (string)player["client"]!["texture"]!["base"]!,
      ItemViews.SeraphSkin
    );
    JObject shape = JObject.Parse(
      File.ReadAllText(
        Path.Combine(
          game,
          "assets/game/shapes/entity/humanoid/seraph-faceless.json"
        )
      )
    );
    var sizes = (JArray)shape["textureSizes"]!["seraph"]!;
    Assert.Equal(((double)sizes[0], (double)sizes[1]), seraph.UvSize);
    byte[,,] skin = seraph.Textures.Get("seraph");
    Assert.Empty(seraph.Textures.Missing);
    Assert.Equal(
      (2 * (double)sizes[0], 2 * (double)sizes[1]),
      ((double)skin.GetLength(1), (double)skin.GetLength(0))
    );
  }

  [SkippableFact]
  public void Gearpinion_tp_matches_its_golden() {
    string game = RequireClientGame();
    BlockIndex index = BlockIndex.Build([DemoRoot], game);
    string file = Itemtype("gearpinion");
    Variant variant = Assert.Single(index.ItemVariants(file));
    (LoadedShape shape, TextureSet textures, _) = ItemViews.Compose(
      index.ResolveVariant(variant),
      index
    );
    ItemViews.Seraph seraph = ItemViews.LoadSeraph(index)!;
    // Only the item's pixels are the golden: the player is vanilla art.
    using (
      SKBitmap layer = ItemViews.RenderTpItem(
        shape,
        textures,
        ItemViews.ReadTransform(variant, ItemViews.TpKey),
        seraph,
        null,
        null
      )
    ) {
      string actualDir = OutDir("gearpinion-tp-layer");
      Directory.CreateDirectory(actualDir);
      using (
        FileStream stream = File.Create(
          Path.Combine(actualDir, "gearpinion-tp-item.png")
        )
      )
        layer.Encode(SKEncodedImageFormat.Png, 100).SaveTo(stream);
      using SKBitmap expected = SKBitmap.Decode(
        FixturePath.Expected("item-views/gearpinion-tp-item.png")
      );
      PixelCompare.Assert(expected, layer, "gearpinion-tp-item");
    }

    string outDir = OutDir("gearpinion-tp");
    JObject manifest = ItemViews.Write(
      file,
      variant,
      index,
      outDir,
      views: ["tp"]
    );
    Assert.Equal(
      ["gearpinion-tp.png"],
      manifest["files"]!.Select(f => Path.GetFileName((string)f!))
    );
    Assert.Empty((JArray)manifest["warnings"]!);
    using SKBitmap picture = SKBitmap.Decode(
      Path.Combine(outDir, "gearpinion-tp.png")
    );
    Assert.True(
      picture.Width > 2 * 8 * ItemViews.TpPpu
        && picture.Height > 30 * ItemViews.TpPpu
    );
  }

  [SkippableFact]
  public void Gearpinion_fp_matches_its_golden() {
    string game = RequireClientGame();
    BlockIndex index = BlockIndex.Build([DemoRoot], game);
    string file = Itemtype("gearpinion");
    Variant variant = Assert.Single(index.ItemVariants(file));
    (LoadedShape shape, TextureSet textures, _) = ItemViews.Compose(
      index.ResolveVariant(variant),
      index
    );
    ItemViews.Seraph seraph = ItemViews.LoadSeraph(index)!;
    using (
      SKBitmap layer = ItemViews.RenderFpItem(
        shape,
        textures,
        ItemViews.ReadTransform(variant, ItemViews.TpKey),
        seraph,
        null,
        null
      )
    ) {
      string actualDir = OutDir("gearpinion-fp-layer");
      Directory.CreateDirectory(actualDir);
      using (
        FileStream stream = File.Create(
          Path.Combine(actualDir, "gearpinion-fp-item.png")
        )
      )
        layer.Encode(SKEncodedImageFormat.Png, 100).SaveTo(stream);
      using SKBitmap expected = SKBitmap.Decode(
        FixturePath.Expected("item-views/gearpinion-fp-item.png")
      );
      PixelCompare.Assert(expected, layer, "gearpinion-fp-item");
    }

    string outDir = OutDir("gearpinion-fp");
    JObject manifest = ItemViews.Write(
      file,
      variant,
      index,
      outDir,
      views: ["fp"]
    );
    Assert.Equal(
      ["gearpinion-fp.png"],
      manifest["files"]!.Select(f => Path.GetFileName((string)f!))
    );
    Assert.Empty((JArray)manifest["warnings"]!);
    using SKBitmap picture = SKBitmap.Decode(
      Path.Combine(outDir, "gearpinion-fp.png")
    );
    Assert.Equal(
      (ItemViews.FpWidth, ItemViews.FpHeight),
      (picture.Width, picture.Height)
    );
  }

  [Fact]
  public void The_tp_view_without_the_player_shape_is_a_warning() {
    string empty = OutDir("no-game-install");
    Directory.CreateDirectory(empty);
    BlockIndex index = BlockIndex.Build([DemoRoot], empty);
    Variant variant = Assert.Single(index.ItemVariants(Itemtype("gearpinion")));
    string outDir = OutDir("gearpinion-no-player");
    JObject manifest = ItemViews.Write(
      Itemtype("gearpinion"),
      variant,
      index,
      outDir,
      views: ["tp", "gui"]
    );
    Assert.Equal(
      ["gearpinion-gui.png"],
      manifest["files"]!.Select(f => Path.GetFileName((string)f!))
    );
    Assert.Contains(
      "demo:gearpinion: the player shape is not found; tp not drawn",
      manifest["warnings"]!.Select(w => (string)w!)
    );
  }

  // The game's gui chain, InventoryItemRenderer.RenderItemstackToGui without the origRotate spin,
  // written in the game's order on a y-down slot and turned y up for the renderer.
  private static Renderer.Mat4d GameSlotChain(ItemTransform t, bool block) {
    const double size = 25.6;
    return Renderer.Mat4d.Scale(1, -1, 1)
      * Renderer.Mat4d.Translate(block ? 0 : -3, block ? 0 : -1, 0)
      * Renderer.Mat4d.Translate(
        t.Origin.X + t.Translation.X,
        t.Origin.Y + t.Translation.Y,
        t.Origin.Z * size + t.Translation.Z
      )
      * Renderer.Mat4d.Scale(
        size * t.ScaleXyz.X,
        size * t.ScaleXyz.Y,
        size * t.ScaleXyz.Z
      )
      * Renderer.Mat4d.RotateByXyz(
        t.Rotation.X + (block ? 180 : 0),
        t.Rotation.Y,
        t.Rotation.Z
      )
      * Renderer.Mat4d.Translate(-t.Origin.X, -t.Origin.Y, -t.Origin.Z)
      * Renderer.Mat4d.Scale(1.0 / 16, 1.0 / 16, 1.0 / 16);
  }

  private static readonly ItemTransform SlotSample = new(
    (2, -3, 1),
    (-22.6, -45.3, 7),
    (0.5, 0.4, 0.5),
    (0.8, 0.9, 1.1),
    true
  );

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public void Slot_matrix_is_the_games_chain_per_class(bool block) {
    Renderer.Mat4d expected = GameSlotChain(SlotSample, block);
    Renderer.Mat4d actual = ItemViews.SlotModel(SlotSample, block);
    foreach (double x in new[] { 0.0, 16.0 })
      foreach (double y in new[] { 0.0, 16.0 })
        foreach (double z in new[] { 0.0, 16.0 }) {
          (double X, double Y, double Z) e = expected.Mul(x, y, z);
          (double X, double Y, double Z) a = actual.Mul(x, y, z);
          Assert.InRange(a.X, e.X - 1e-9, e.X + 1e-9);
          Assert.InRange(a.Y, e.Y - 1e-9, e.Y + 1e-9);
          Assert.InRange(a.Z, e.Z - 1e-9, e.Z + 1e-9);
        }
  }

  [Fact]
  public void Block_slot_centres_on_the_slot_and_stands_on_its_base() {
    var flat = new ItemTransform(
      (0, 0, 0),
      (0, 0, 0),
      (0.5, 0.5, 0.5),
      (1, 1, 1),
      false
    );
    Renderer.Mat4d model = ItemViews.SlotModel(flat, true);
    (double X, double Y, double Z) centre = model.Mul(8, 8, 8);
    (double X, double Y, double Z) bottom = model.Mul(8, 0, 8);
    // The game adds the origin's x and y in pixels, unscaled: half a pixel off the slot centre.
    Assert.InRange(centre.X, 0.5 - 1e-9, 0.5 + 1e-9);
    Assert.InRange(centre.Y, -0.5 - 1e-9, -0.5 + 1e-9);
    Assert.InRange(bottom.Y, -13.3 - 1e-9, -13.3 + 1e-9);

    (double X, double Y, double Z) itemCentre = ItemViews
      .SlotModel(flat)
      .Mul(8, 8, 8);
    Assert.InRange(itemCentre.X, -2.5 - 1e-9, -2.5 + 1e-9);
    Assert.InRange(itemCentre.Y, 0.5 - 1e-9, 0.5 + 1e-9);
  }

  [Fact]
  public void Ground_rotates_in_y_z_x_order() {
    var transform = new ItemTransform(
      (0, 0, 0),
      (90, 45, 30),
      (0, 0, 0),
      (5, 5, 5),
      true
    );
    Renderer.Mat4d model = ItemViews.GroundModel(transform);
    // The gearpinion hub's box (5.8, 6.24, 0) to (10.2, 9.76, 3) turned x, then z, then y, with
    // scale 5 so a voxel of the shape stays a voxel of the ground; worked by hand.
    (double X, double Y, double Z)[] expected =
    [
      (7.96411, 2.90000, 0.86059),
      (9.02477, 0.30192, -0.20007),
      (10.45312, 2.90000, 3.34960),
      (11.51378, 0.30192, 2.28894),
      (10.65855, 5.10000, -1.83385),
      (11.71921, 2.50192, -2.89451),
      (13.14756, 5.10000, 0.65516),
      (14.20822, 2.50192, -0.40550),
    ];
    int i = 0;
    foreach (double x in new[] { 5.8, 10.2 })
      foreach (double y in new[] { 6.24, 9.76 })
        foreach (double z in new[] { 0.0, 3.0 }) {
          (double X, double Y, double Z) posed = model.Mul(x, y, z);
          Assert.InRange(posed.X, expected[i].X - 0.0005, expected[i].X + 0.0005);
          Assert.InRange(posed.Y, expected[i].Y - 0.0005, expected[i].Y + 0.0005);
          Assert.InRange(posed.Z, expected[i].Z - 0.0005, expected[i].Z + 0.0005);
          i++;
        }
  }

  [Fact]
  public void Gearpinion_gui_and_ground_match_their_goldens() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    string file = Itemtype("gearpinion");
    Variant variant = Assert.Single(index.ItemVariants(file));
    string outDir = OutDir("gearpinion");
    JObject manifest = ItemViews.Write(
      file,
      variant,
      index,
      outDir,
      views: ["gui", "ground"]
    );
    Assert.Equal(
      ["gearpinion-gui.png", "gearpinion-ground.png"],
      manifest["files"]!.Select(f => Path.GetFileName((string)f!))
    );
    Assert.Empty((JArray)manifest["missingTextures"]!);
    Assert.Empty((JArray)manifest["warnings"]!);
    foreach (string view in new[] { "gui", "ground" }) {
      using SKBitmap actual = SKBitmap.Decode(
        Path.Combine(outDir, $"gearpinion-{view}.png")
      );
      using SKBitmap expected = SKBitmap.Decode(
        FixturePath.Expected($"item-views/gearpinion-{view}.png")
      );
      PixelCompare.Assert(expected, actual, $"gearpinion-{view}");
    }
  }

  [Fact]
  public void A_view_the_item_is_not_drawn_in_is_refused() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    string file = Itemtype("token");
    Variant variant = Assert.Single(index.ItemVariants(file));
    Assert.Throws<ArgumentException>(() =>
      ItemViews.Write(
        file,
        variant,
        index,
        OutDir("token-view"),
        views: ["side"]
      )
    );
    JObject manifest = ItemViews.Write(
      file,
      variant,
      index,
      OutDir("token-view"),
      views: ["gui"]
    );
    Assert.Empty((JArray)manifest["files"]!);
    Assert.Equal(
      "demo:token: no shape; gui not drawn",
      (string)Assert.Single((JArray)manifest["warnings"]!)!
    );
  }

  [Fact]
  public void Omitted_transform_keys_keep_the_type_default() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    Variant variant = Assert.Single(index.ItemVariants(Itemtype("gearpinion")));
    ItemTransform tp = ItemViews.ReadTransform(variant, ItemViews.TpKey);
    Assert.Equal((-1.5, -1.6, -1.4), tp.Translation);
    Assert.Equal((0.0, -62.0, 18.0), tp.Rotation);
    Assert.Equal((0.33, 0.33, 0.33), tp.ScaleXyz);
    ItemTransform ground = ItemViews.ReadTransform(
      variant,
      ItemViews.GroundKey
    );
    Assert.Equal((3.31, 3.31, 3.31), ground.ScaleXyz);
    Assert.True(ground.Rotate);
  }

  [Fact]
  public void A_by_type_entry_is_laid_over_the_plain_key_and_the_default() {
    var variant = new Variant(
      "demo:thing-b",
      "demo",
      JObject.Parse(
        """
        {
          "guiTransform": { "scale": 2, "origin": { "x": 1 } },
          "guiTransformByType": { "thing-b": { "scale": 3 }, "*": { "scale": 9 } }
        }
        """
      ),
      new Dictionary<string, string>(),
      "thing.json"
    );
    ItemTransform gui = ItemViews.ReadTransform(variant, ItemViews.GuiKey);
    Assert.Equal((3.0, 3.0, 3.0), gui.ScaleXyz);
    Assert.Equal((1.0, 0.0, 0.0), gui.Origin);
    Assert.False(gui.Rotate);
  }

  [Fact]
  public void A_group_naming_a_properties_file_and_states_takes_both() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    Assert.Equal(
      [
        "demo:mixed-top",
        "demo:mixed-north",
        "demo:mixed-east",
        "demo:mixed-south",
        "demo:mixed-west",
      ],
      index.ItemVariants(Itemtype("mixed")).Select(v => v.Code)
    );
  }

  [Fact]
  public void Scale_xyz_sets_each_axis_and_the_later_key_wins() {
    static Variant Of(string json) =>
      new(
        "demo:thing",
        "demo",
        JObject.Parse(json),
        new Dictionary<string, string>(),
        "thing.json"
      );
    ItemTransform mirrored = ItemViews.ReadTransform(
      Of(
        """{ "guiTransform": { "scaleXyz": { "x": -1.3, "y": 1.3, "z": 1.3 } } }"""
      ),
      ItemViews.GuiKey
    );
    Assert.Equal((-1.3, 1.3, 1.3), mirrored.ScaleXyz);
    ItemTransform later = ItemViews.ReadTransform(
      Of(
        """{ "guiTransform": { "scaleXyz": { "x": 2, "y": 3, "z": 4 }, "scale": 5 } }"""
      ),
      ItemViews.GuiKey
    );
    Assert.Equal((5.0, 5.0, 5.0), later.ScaleXyz);
  }
}
