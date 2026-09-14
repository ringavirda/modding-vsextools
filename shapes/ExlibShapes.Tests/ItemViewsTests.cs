using System;
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
    Assert.Equal(["token-icon.png"], manifest["files"]!.Select(f => Path.GetFileName((string)f!)));
    Assert.Empty((JArray)manifest["missingTextures"]!);
    Assert.Empty((JArray)manifest["warnings"]!);
    Assert.True(File.Exists(Path.Combine(outDir, "token.json")), "the manifest is written beside the picture");

    // The fixture's own texture is 16 texels square, drawn four pixels to the texel on paper.
    using SKBitmap icon = SKBitmap.Decode(Path.Combine(outDir, "token-icon.png"));
    Assert.Equal(16 * ItemViews.IconScale + 16, icon.Width);
    Assert.Equal(icon.Width, icon.Height);
    Assert.Equal(Renderer.Background, icon.GetPixel(0, 0));
  }

  [Fact]
  public void An_item_with_a_shape_gets_the_isometric_render_of_it() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    string file = Itemtype("tool");
    // The file's own variantgroups expand the same way a blocktype's do.
    Assert.Equal(["demo:tool-iron", "demo:tool-steel"], index.ItemVariants(file).Select(v => v.Code));

    string outDir = OutDir("tool");
    JObject manifest = ItemViews.Write(file, index.ItemVariants(file)[1], index, outDir, ppu: 8);
    Assert.Equal("demo:tool-steel", (string)manifest["variant"]!);
    Assert.Equal(["tool-iso.png"], manifest["files"]!.Select(f => Path.GetFileName((string)f!)));
    Assert.Empty((JArray)manifest["missingTextures"]!);
    Assert.Empty((JArray)manifest["warnings"]!);
  }

  [SkippableFact]
  public void The_smex_burden_draws_the_shape_it_borrows_from_the_game() {
    string? file = FixturePath.Workspace("exmods/legacy/smex/assets/smex/itemtypes/burden.json");
    Skip.If(file is null, "the sibling exmods checkout is absent");
    BlockIndex index = BlockIndex.Build(BlockIndex.DefaultRoots(file!), null, legacyFirst: true);
    Variant variant = Assert.Single(index.ItemVariants(file!));
    Assert.Equal("smex:burden", variant.Code);

    string outDir = OutDir("burden");
    JObject manifest = ItemViews.Write(file!, variant, index, outDir, ppu: 8);
    Assert.Equal(["burden-iso.png"], manifest["files"]!.Select(f => Path.GetFileName((string)f!)));
    Assert.Empty((JArray)manifest["missingTextures"]!);
    Assert.Empty((JArray)manifest["warnings"]!);

    // The ore-pile shape it borrows is wider than it is tall, and nothing of it is the placeholder.
    using SKBitmap iso = SKBitmap.Decode(Path.Combine(outDir, "burden-iso.png"));
    Assert.True(iso.Width > iso.Height, $"the pile is drawn {iso.Width}x{iso.Height}");
    Assert.False(Magenta(iso), "the render paints the magenta placeholder");
  }

  [SkippableFact]
  public void An_iiex_item_with_a_shape_is_painted_from_the_itemtype_s_own_map() {
    string? file = FixturePath.Workspace("exmods/mods/iiex/tests/goldens/iiex/itemtypes/spurgear.json");
    Skip.If(file is null, "the sibling exmods checkout is absent");
    BlockIndex index = BlockIndex.Build(BlockIndex.DefaultRoots(file!));
    Variant variant = Assert.Single(index.ItemVariants(file!));
    ResolvedBlock item = index.ResolveVariant(variant);
    // The shape is the game's own gear; the cast-iron it is painted with is the mod's.
    Assert.NotNull(item.ShapePath);
    Assert.Equal("iiex:block/metal/castiron", item.Textures["rusty-iron"].Base);

    string outDir = OutDir("spurgear");
    JObject manifest = ItemViews.Write(file!, variant, index, outDir, ppu: 8);
    Assert.Equal(["spurgear-iso.png"], manifest["files"]!.Select(f => Path.GetFileName((string)f!)));
    Assert.Empty((JArray)manifest["missingTextures"]!);
    using SKBitmap iso = SKBitmap.Decode(Path.Combine(outDir, "spurgear-iso.png"));
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
}
