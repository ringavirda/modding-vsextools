using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json.Linq;
using SkiaSharp;
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
  public void A_texture_that_resolves_to_nothing_is_named_apart_from_a_face_assigned_none() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    string file = Blocktype("rusty");
    JObject manifest = BlockViews.Write(file, Drawn(index, file), index, OutDir("rusty"), views: ["iso"], ppu: 4);
    // The shape names `rust` a file that is not there and leaves its underside's own key
    // unassigned: one is a broken value, the other a face the block paints with nothing.
    Assert.Equal(
      ["demo:rusty: texture rust (demo:block/nonexistent) not found"],
      manifest["missingTextures"]!.Select(t => (string)t!)
    );
    Assert.Equal(
      ["demo:rusty: face texture underside is assigned nothing"],
      manifest["unpaintedFaces"]!.Select(t => (string)t!)
    );
  }

  [Fact]
  public void An_animated_part_parked_outside_the_block_is_not_drawn() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    string file = Blocktype("tooled");
    string clippedDir = OutDir("tooled");
    JObject clipped = BlockViews.Write(file, Drawn(index, file), index, clippedDir, views: ["iso"], ppu: 4);
    Assert.True((bool)clipped["clipped"]!);
    Assert.Equal(["rabble"], clipped["hidden"]!.Select(h => (string)h!));

    string fullDir = OutDir("tooled-full");
    JObject whole = BlockViews.Write(file, Drawn(index, file), index, fullDir, views: ["iso"], ppu: 4, full: true);
    Assert.False((bool)whole["clipped"]!);
    Assert.Empty((JArray)whole["hidden"]!);

    // The parked bar stands two cells above the block, so keeping it makes a taller picture.
    using SKBitmap block = SKBitmap.Decode(Path.Combine(clippedDir, "tooled-iso.png"));
    using SKBitmap all = SKBitmap.Decode(Path.Combine(fullDir, "tooled-iso.png"));
    Assert.True(all.Height > block.Height, "the parked part is drawn either way");
  }

  [Fact]
  public void A_static_part_reaching_past_the_block_is_still_drawn() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    string file = Blocktype("tooled");
    // The vice stands mostly outside the block's own cell, as the rabble does, and no animation
    // moves it: it is where a placed block shows it.
    BlockViews.Drawing drawn = BlockViews.Draw(file, Drawn(index, file), index);
    Assert.Equal(["rabble"], drawn.Hidden);
    Assert.Contains("vice", drawn.Shape.Leaves().Select(el => el.Name));
  }

  // The blocktype trees of the four mods, and whether each is read with the legacy resolution
  // order. Every megablock and structure block of the family lives under one of them.
  private static readonly (string Tree, bool Legacy)[] FamilyTrees = [
    ("exmods/legacy/smex/assets", true),
    ("exmods/legacy/ppex/assets", true),
    ("exmods/mods/iiex/tests/goldens", false),
    ("exmods/mods/siex/tests/goldens", false),
  ];

  // The blocks of the family whose art is parked outside the block it belongs to: the generator's
  // shaft and the chimney cap's control rod, each moved by an animation of its own shape. The
  // puddling door's tools stay out of its picture through its selectiveElements. Every other block
  // keeps every part, however far it reaches.
  private static readonly string[] Clipped = [
    "iiex:enginempgenerator-n: Shaft1",
    "iiex:furnace-puddlingchimneycap-n: Cube34",
  ];

  [SkippableFact]
  public void Only_the_blocks_with_a_parked_tool_lose_any_art() {
    string? exmods = FixturePath.Workspace("exmods");
    Skip.If(exmods is null, "the sibling exmods checkout is absent");
    var cut = new List<string>();
    int checkedCount = 0;
    foreach ((string tree, bool legacy) in FamilyTrees) {
      string root = Path.Combine(Path.GetDirectoryName(exmods!)!, tree.Replace('/', Path.DirectorySeparatorChar));
      Skip.If(!Directory.Exists(root), $"{tree} is absent");
      BlockIndex index = BlockIndex.Build(BlockIndex.DefaultRoots(root), null, legacy);
      foreach (
        string file in Directory
          .EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
          .Where(f => f.Split(Path.DirectorySeparatorChar).Contains("blocktypes"))
          .OrderBy(f => f, StringComparer.Ordinal)
      ) {
        IReadOnlyList<Variant> variants = index.VariantsOf(file);
        Variant? drawn = BlockIndex.Facing(variants, Presentation.Facing) ?? variants.FirstOrDefault();
        if (drawn == null)
          continue;
        checkedCount++;
        BlockViews.Drawing drawing = BlockViews.Draw(file, drawn, index);
        if (drawing.Hidden.Count > 0)
          cut.Add($"{drawn.Code}: {string.Join(", ", drawing.Hidden)}");
      }
    }
    Assert.True(checkedCount >= 150, $"only {checkedCount} blocktypes were reached");
    Assert.Equal<IEnumerable<string>>(Clipped, [.. cut.OrderBy(c => c, StringComparer.Ordinal)]);
  }

  [SkippableFact]
  public void The_blast_furnace_door_is_drawn_iron_side_out() {
    string? file = FixturePath.Workspace("exmods/legacy/smex/assets/smex/blocktypes/blastfurnace/door.json");
    Skip.If(file is null, "the sibling exmods checkout is absent");
    BlockIndex index = BlockIndex.Build(BlockIndex.DefaultRoots(file!), null, legacyFirst: true);
    IReadOnlyList<Variant> variants = index.VariantsOf(file!);
    // Refractory tiers only: the door's own facing lives in BlockBlastFurnaceDoor, so no variant
    // names it and the art is all the drawing has to go on.
    Assert.Null(BlockIndex.Facing(variants, Presentation.Facing));

    JObject manifest = BlockViews.Write(file!, variants[0], index, OutDir("door"), views: ["iso"], ppu: 4);
    Assert.Equal("south", (string?)manifest["front"]);
    // The door animates its origin alone, which stands inside the block: a picture of it is the
    // whole door, both rows of brick and both straps.
    Assert.Empty((JArray)manifest["hidden"]!);

    // The brick boxes are the block's own body and everything else is the iron door, its straps and
    // its handle; the turn stands that furniture between the brick and the camera. Measured on the
    // model the render draws, already turned, so a part cut out of it counts against the picture.
    LoadedShape drawn = BlockViews.Draw(file!, variants[0], index).Shape;
    Vector3 toward =
      Middle(drawn, name => !name.StartsWith("brick", StringComparison.Ordinal))
      - Middle(drawn, name => name.StartsWith("brick", StringComparison.Ordinal));
    Vector3 eye = Renderer.Eye(Renderer.NamedViews[Presentation.ViewName]);
    Assert.True(
      toward.X * eye.X + toward.Z * eye.Z > 0,
      $"the door's iron stands {toward} of its brick, which is away from the camera"
    );
  }

  // The mean centre of every drawn element of `shape` whose own name `wanted` accepts.
  private static Vector3 Middle(LoadedShape shape, Func<string, bool> wanted) {
    var mats = Geometry.WorldMatrices(shape);
    List<Vector3> centres = [
      .. shape
        .Leaves()
        .Where(el => wanted(el.Name))
        .Select(el => {
          (Vector3 lo, Vector3 hi) = Geometry.Aabb(Geometry.Corners(mats[el.Path], (Vector3)el.Size));
          return (lo + hi) / 2;
        }),
    ];
    Assert.NotEmpty(centres);
    return centres.Aggregate(Vector3.Zero, (a, b) => a + b) / centres.Count;
  }

  [SkippableFact]
  public void A_hollow_boiler_tells_its_firebox_end_from_its_flue_end() {
    string? file = FixturePath.Workspace("exmods/mods/siex/tests/goldens/siex/blocktypes/boiler/lancashire.json");
    Skip.If(file is null, "the sibling exmods checkout is absent");
    BlockIndex index = BlockIndex.Build(BlockIndex.DefaultRoots(file!));
    string outDir = OutDir("lancashire");
    BlockViews.Write(file!, Drawn(index, file!), index, outDir, views: ["north", "south"], ppu: 4);

    // The flue openings are tubes with nothing behind them: drawn as holes they show the paper
    // through to the far end and the two ends read as one picture.
    foreach (string view in new[] { "north", "south" }) {
      using SKBitmap image = SKBitmap.Decode(Path.Combine(outDir, $"lancashire-{view}.png"));
      Assert.Equal(0, EnclosedPaper(image));
    }

    byte[] north = File.ReadAllBytes(Path.Combine(outDir, "lancashire-north.png"));
    byte[] south = File.ReadAllBytes(Path.Combine(outDir, "lancashire-south.png"));
    Assert.False(north.AsSpan().SequenceEqual(south), "the boiler's two ends are drawn identically");
  }

  // Pixels of the render's own paper that the model encloses - paper the canvas edge cannot be
  // reached from across paper and floor grid, the eight ways a background pixel connects. Every one
  // of them is a hole in the model the reader sees the paper through; a single pixel of paper left
  // along a diagonal outline touches the paper outside it and is not one.
  private static int EnclosedPaper(SKBitmap image) {
    bool IsPaper(int x, int y) => image.GetPixel(x, y) == Renderer.Background;
    bool IsBehind(int x, int y) {
      SKColor pixel = image.GetPixel(x, y);
      return pixel == Renderer.Background || pixel == Renderer.GridLight || pixel == Renderer.GridDark;
    }
    var reached = new bool[image.Height, image.Width];
    var queue = new Queue<(int X, int Y)>();
    void Reach(int x, int y) {
      if (x < 0 || x >= image.Width || y < 0 || y >= image.Height || reached[y, x] || !IsBehind(x, y))
        return;
      reached[y, x] = true;
      queue.Enqueue((x, y));
    }
    for (int x = 0; x < image.Width; x++) {
      Reach(x, 0);
      Reach(x, image.Height - 1);
    }
    for (int y = 0; y < image.Height; y++) {
      Reach(0, y);
      Reach(image.Width - 1, y);
    }
    while (queue.Count > 0) {
      (int x, int y) = queue.Dequeue();
      for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
          Reach(x + dx, y + dy);
    }
    int enclosed = 0;
    for (int y = 0; y < image.Height; y++)
      for (int x = 0; x < image.Width; x++)
        if (!reached[y, x] && IsPaper(x, y))
          enclosed++;
    return enclosed;
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
