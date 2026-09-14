using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Covers the <c>block</c> command's files and manifest on the demo fixtures, and the
/// pictures it makes of a real megablock.</summary>
public class BlockViewsTests {
  private static string DemoRoot => FixturePath.Of("schematic");
  private static string Blocktype(string name) =>
    FixturePath.Of($"schematic/mods/demo/assets/demo/blocktypes/{name}.json");

  // A fresh directory per fact, beside the test binary.
  private static string OutDir(string name) {
    string dir = Path.Combine(AppContext.BaseDirectory, "actual", "block", name);
    if (Directory.Exists(dir))
      Directory.Delete(dir, true);
    return dir;
  }

  private static Variant Drawn(BlockIndex index, string file, string? wanted = null) {
    var variants = index.VariantsOf(file);
    return wanted == null
      ? BlockIndex.Facing(variants, Presentation.Facing) ?? variants[0]
      : variants.Single(v => v.Code == wanted);
  }

  [Fact]
  public void A_megablock_gets_a_picture_per_view_and_its_footprint_plan() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    string file = Blocktype("mega");
    string outDir = OutDir("mega");
    JObject manifest = BlockViews.Write(file, Drawn(index, file), index, outDir, ppu: 4);

    Assert.Equal("demo:mega-north", (string)manifest["variant"]!);
    Assert.Equal(
      [.. BlockViews.DefaultViews.Select(v => $"mega-{v}.png"), "mega-footprint.svg"],
      manifest["files"]!.Select(f => Path.GetFileName((string)f!))
    );
    foreach (JToken written in (JArray)manifest["files"]!)
      Assert.True(File.Exists((string)written!), (string)written!);
    Assert.True(File.Exists(Path.Combine(outDir, "mega.json")), "the manifest is written beside the pictures");
    Assert.Empty((JArray)manifest["missingTextures"]!);
    Assert.Empty((JArray)manifest["warnings"]!);

    // The plan carries the principal's own cell and the one it reserves, the principal marked,
    // in the frame the pictures are drawn in: the body reaches north, so the filler is drawn above.
    string svg = File.ReadAllText(Path.Combine(outDir, "mega-footprint.svg"));
    Assert.Equal(2, svg.Split("<rect").Length - 1);
    Assert.Contains("class=\"cell anchor\" x=\"0\" y=\"32\"", svg);
    Assert.Contains("class=\"cell\" x=\"0\" y=\"0\"", svg);
  }

  [Fact]
  public void A_named_variant_is_drawn_with_its_own_turn() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    string file = Blocktype("mega");
    JObject manifest = BlockViews.Write(
      file,
      Drawn(index, file, "demo:mega-east"),
      index,
      OutDir("mega-east"),
      views: ["iso"],
      ppu: 4
    );
    Assert.Equal("demo:mega-east", (string)manifest["variant"]!);
    Assert.Equal(
      ["mega-iso.png", "mega-footprint.svg"],
      manifest["files"]!.Select(f => Path.GetFileName((string)f!))
    );

    (JObject raw, _) = BlockViews.Compose(index.Resolve("demo:mega-east")!);
    Assert.Equal(270.0, (double)raw["elements"]![0]!["rotationY"]!);
  }

  [Fact]
  public void A_block_with_no_footprint_gets_pictures_alone() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    string file = Blocktype("wall");
    JObject manifest = BlockViews.Write(file, Drawn(index, file), index, OutDir("wall"), views: ["iso"], ppu: 4);
    Assert.Equal(["wall-iso.png"], manifest["files"]!.Select(f => Path.GetFileName((string)f!)));
  }

  [Fact]
  public void A_texture_that_resolves_to_nothing_is_named_in_the_manifest() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    string file = Blocktype("rusty");
    JObject manifest = BlockViews.Write(file, Drawn(index, file), index, OutDir("rusty"), views: ["iso"], ppu: 4);
    Assert.Equal(
      ["demo:rusty: texture rust (demo:block/nonexistent) not found"],
      manifest["missingTextures"]!.Select(t => (string)t!)
    );
  }

  [SkippableFact]
  public void A_ppex_engine_draws_its_north_variant_over_its_own_footprint() {
    string? watt = FixturePath.Workspace("exmods/legacy/ppex/assets/ppex/blocktypes/engine/watt.json");
    Skip.If(watt is null, "the sibling exmods checkout is absent");
    BlockIndex index = BlockIndex.Build(BlockIndex.DefaultRoots(watt!), null, legacyFirst: true);
    string outDir = OutDir("watt");
    JObject manifest = BlockViews.Write(watt!, Drawn(index, watt!), index, outDir, views: ["iso"], ppu: 4);

    Assert.Equal("ppex:enginewatt-north", (string)manifest["variant"]!);
    Assert.Empty((JArray)manifest["warnings"]!);
    // Three cells along z, the two it reserves north of the block placed - the engine's own
    // structure angle, which its footprint plan is drawn in.
    string svg = File.ReadAllText(Path.Combine(outDir, "watt-footprint.svg"));
    Assert.Equal(3, svg.Split("<rect").Length - 1);
    Assert.Contains("class=\"cell anchor\" x=\"0\" y=\"64\"", svg);
  }
}
