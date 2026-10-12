using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SkiaSharp;
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
  public void Spear_and_rod_ground_top_face_normal_is_up(
    string file,
    string code
  ) {
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

  [Fact]
  public void Ground_rotates_in_y_z_x_order() {
    var transform = new ItemTransform(
      (0, 0, 0),
      (90, 45, 0),
      (0, 0, 0),
      (5, 5, 5),
      true
    );
    Renderer.Mat4d model = ItemViews.GroundModel(transform);
    // The gearpinion hub's box (5.8, 6.24, 0) to (10.2, 9.76, 3) turned x then z then y, with
    // scale 5 so a voxel of the shape stays a voxel of the ground; worked by hand.
    (double X, double Y, double Z)[] expected =
    [
      (8.5136, 0, 0.3111),
      (8.5136, -3, 0.3111),
      (11.0026, 0, 2.8001),
      (11.0026, -3, 2.8001),
      (11.6248, 0, -2.8001),
      (11.6248, -3, -2.8001),
      (14.1139, 0, -0.3111),
      (14.1139, -3, -0.3111),
    ];
    int i = 0;
    foreach (double x in new[] { 5.8, 10.2 })
      foreach (double y in new[] { 6.24, 9.76 })
        foreach (double z in new[] { 0.0, 3.0 }) {
          (double X, double Y, double Z) posed = model.Mul(x, y, z);
          Assert.Equal(expected[i].X, posed.X, 3);
          Assert.Equal(expected[i].Y, posed.Y, 3);
          Assert.Equal(expected[i].Z, posed.Z, 3);
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
      ItemViews.Write(file, variant, index, OutDir("token-view"), views: ["tp"])
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
