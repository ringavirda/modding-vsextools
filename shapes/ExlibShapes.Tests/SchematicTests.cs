using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Vintagestory.API.Common;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Covers Schematic's plan SVGs and manifest, compared verbatim against
/// <c>expected/schematic/</c>, and its iso PNGs against zero differing pixels beyond the
/// per-channel tolerance <see cref="PixelCompare"/> uses.</summary>
public class SchematicTests {
  private static string Fixture => FixturePath.Of("schematic/kiln.json");
  private static string DemoRoot => FixturePath.Of("schematic");

  // The family workspace's own exmods checkout and this checkout's own client game install; the
  // facts naming them Skip.If (rather than run against nothing) when either is absent.
  private static string? BlastcoreGolden =>
    FixturePath.Workspace("exmods/mods/iiex/tests/goldens/iiex/blocktypes/furnace/blastcore.json");
  private static string? ClientGame {
    get {
      foreach (string slug in new[] { "1.22-client", "1.22" }) {
        string candidate = Path.Combine(FixturePath.RepoRoot, ".game", slug);
        // A dedicated-server install lands at this same path (Invoke-ProvisionGame's "-server"
        // suffix is only added once a client is already there), and ships no textures at all -
        // Vintagestory.dll alone does not tell the two apart.
        if (Directory.Exists(Path.Combine(candidate, "assets/game/textures/block")))
          return candidate;
      }
      return null;
    }
  }

  [Fact]
  public void Plan_svg_layer_zero_has_nine_cells_two_colours_one_anchor() {
    Layout layout = Layout.Load(Fixture);
    Dictionary<int, LegendEntry> legend = Schematic.LegendColors(layout);
    string svg = Schematic.PlanSvg(layout, 0, legend);
    Assert.Equal(9, CountOccurrences(svg, "<rect"));
    HashSet<string> fills = [.. System.Text.RegularExpressions.Regex.Matches(svg, "fill=\"(#[0-9A-Fa-f]{6})\"").Select(m => m.Groups[1].Value)];
    Assert.Equal(2, fills.Count);
    Assert.Equal(1, CountOccurrences(svg, "class=\"cell anchor\""));
  }

  [Fact]
  public void Plan_svg_hatches_a_filler_and_draws_a_connector() {
    Layout layout = Layout.Load(Fixture);
    string svg = Schematic.PlanSvg(layout, 0, Schematic.LegendColors(layout));
    Assert.Equal(4, CountOccurrences(svg, "class=\"filler\"")); // two lines per filler cell, two filler cells
    Assert.Contains("class=\"connector\"", svg);
  }

  [Fact]
  public void Plan_svg_omits_an_optional_cell_instead_of_painting_it_solid() {
    Layout layout = Layout.Load(Fixture);
    Dictionary<int, LegendEntry> legend = Schematic.LegendColors(layout);
    legend[1].Optional = true; // e.g. `*:@(air|...)`, drawn as air by the iso render too
    string svg = Schematic.PlanSvg(layout, 0, legend);
    // number 2 is the single anchor cell of layer 0; every other cell of that layer is number 1
    Assert.Equal(1, CountOccurrences(svg, "<rect"));
    Assert.Equal(1, CountOccurrences(svg, "class=\"cell anchor\""));
  }

  [Fact]
  public void Plan_svg_captions_the_layer_it_draws() {
    Layout layout = Layout.Load(Fixture);
    Dictionary<int, LegendEntry> legend = Schematic.LegendColors(layout);
    Assert.Contains(">Layer 0, the starter block's row<", Schematic.PlanSvg(layout, 0, legend));
    Assert.Contains(">Layer +1<", Schematic.PlanSvg(layout, 1, legend));
    Assert.Equal("Layer -1", Schematic.LayerCaption(-1));
  }

  [Fact]
  public void Plan_svg_matches_the_reference_text_exactly() {
    Layout layout = Layout.Load(Fixture);
    string svg = Schematic.PlanSvg(layout, 0, Schematic.LegendColors(layout));
    string expected = File.ReadAllText(FixturePath.Expected("schematic/kiln-plan-y0.svg"));
    Assert.Equal(expected, svg);
  }

  [Fact]
  public void Plan_svg_of_an_optional_cell_matches_the_reference_text_exactly() {
    Layout layout = Layout.Load(Fixture);
    Dictionary<int, LegendEntry> legend = Schematic.LegendColors(layout);
    legend[1].Optional = true;
    string svg = Schematic.PlanSvg(layout, 0, legend);
    string expected = File.ReadAllText(FixturePath.Expected("schematic/kiln-plan-y0-optional.svg"));
    Assert.Equal(expected, svg);
  }

  // The kiln fixture with its two structure blocks swapped for the demo wall, so the iso composite
  // exercises real, renderable blocks resolved through a BlockIndex.
  private static Layout DemoLayout() {
    Layout layout = Layout.Load(Fixture);
    var numbers = new Dictionary<int, string>(layout.Numbers) {
      [1] = "demo:wall-north",
      [2] = "demo:wall-north",
    };
    // Layout's constructor is internal; the test assembly reaches it through InternalsVisibleTo.
    return new Layout(layout.Cells, numbers, layout.Fillers, layout.Facings, layout.Connectors, layout.Roles, layout.Anchor);
  }

  [Fact]
  public void Iso_png_renders_and_grows_with_ppu() {
    Layout layout = DemoLayout();
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    using SKBitmap small = Schematic.IsoPng(layout, index, ppu: 4);
    using SKBitmap big = Schematic.IsoPng(layout, index, ppu: 8);
    Assert.False(small.Width == 16 && small.Height == 16);
    Assert.True(big.Width > small.Width && big.Height > small.Height);
  }

  [Fact]
  public void Cut_at_renders_fewer_leaves_than_the_full_structure() {
    Layout layout = DemoLayout();
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    (JObject fullRaw, _) = Schematic.Compose(layout, index);
    (JObject cutRaw, _) = Schematic.Compose(layout, index, cutAt: 0);
    Assert.True(CountLeaves(cutRaw) < CountLeaves(fullRaw));
  }

  [Fact]
  public void Compose_includes_every_resolved_cell_not_only_the_fillers() {
    Layout layout = DemoLayout();
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    (JObject raw, Dictionary<string, string> textureValues) = Schematic.Compose(layout, index);
    // one leaf per resolved cell (all 18 resolve to demo:wall-north) plus one box per filler cell;
    // a compose that silently dropped the resolved geometry would still pass on filler leaves alone
    Assert.Equal(layout.Cells.Count + layout.Fillers.Count, CountLeaves(raw));
    Assert.Contains("demo:block/wall", textureValues.Values);
  }

  [Fact]
  public void A_megablock_draws_its_body_over_a_footprint_outline_and_a_structure_keeps_its_boxes() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    Layout mega = Footprint.Placed(
      Layout.Load(FixturePath.Of("schematic/mods/demo/assets/demo/blocktypes/mega.json"), "mega-north"),
      index
    );
    (JObject raw, Dictionary<string, string> values) = Schematic.Compose(mega, index);
    List<string> names = [.. raw["elements"]!.Select(el => (string)el["name"]!)];
    Assert.Contains(Schematic.PrincipalPrefix, names);
    Assert.DoesNotContain(names, n => n.StartsWith("filler", StringComparison.Ordinal));
    // Two columns, four bars each: the principal's own cell and the one filler it reserves.
    Assert.Equal(8, names.Count(n => n.StartsWith("footprint", StringComparison.Ordinal)));
    Assert.Contains("demo:block/wall", values.Values);

    // The kiln's fillers stand clear of its anchor block, so they stay boxes.
    (JObject kiln, _) = Schematic.Compose(DemoLayout(), index);
    Assert.Equal(
      2,
      kiln["elements"]!.Count(el => ((string)el["name"]!).StartsWith("filler", StringComparison.Ordinal))
    );
  }

  [Fact]
  public void Iso_png_reserves_its_left_edge_for_the_layer_scale() {
    Layout layout = DemoLayout();
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    (JObject raw, Dictionary<string, string> textureValues) = Schematic.Compose(layout, index);
    Shape shape = Newtonsoft.Json.JsonConvert.DeserializeObject<Shape>(raw.ToString())!;
    using SKBitmap plain = Renderer.Render(
      ShapeFile.FromRaw(shape, null, new Dictionary<string, string>()),
      Renderer.NamedViews["iso"],
      ppu: 8,
      textures: TextureSet.FromResolved(textureValues, index.ResolveTexture, new Dictionary<string, byte[,,]>())
    );
    using SKBitmap scaled = Schematic.IsoPng(layout, index, ppu: 8);
    Assert.Equal(plain.Height, scaled.Height);
    Assert.True(scaled.Width > plain.Width, "the scale is drawn beside the composite, not over it");
    // The ticks and their labels are the only ink left of the drawing.
    int ink = 0;
    for (int y = 0; y < scaled.Height; y++)
      for (int x = 0; x < scaled.Width - plain.Width; x++)
        if (scaled.GetPixel(x, y).Red < 128)
          ink++;
    Assert.True(ink > 0, "no tick was drawn in the reserved margin");
  }

  [Fact]
  public void A_blocktypes_all_texture_stands_in_for_every_key_of_its_shape() {
    // demo:masonry's shape declares and uses `brick`, naming a texture that does not exist; the
    // blocktype's `all` is what the game paints every face with. demo:rusty declares no textures
    // at all, so its shape's own unresolvable `rust` is reported, not painted over.
    Layout layout = Layout.Load(FixturePath.Of("schematic/textures-kiln.json"));
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    (_, Dictionary<string, string> values) = Schematic.Compose(layout, index);
    Assert.Equal("demo:block/wall", values["c0_brick"]);
    Assert.Equal("demo:block/wall", values["c0_all"]);
    Assert.Equal("demo:block/nonexistent", values["c1_rust"]);
    Assert.Equal(
      ["demo:rusty: texture rust (demo:block/nonexistent) not found"],
      Schematic.MissingTextures(layout, index)
    );

    JObject manifest = Schematic.Manifest(
      layout,
      Schematic.LegendColors(layout),
      [],
      null,
      Schematic.MissingTextures(layout, index)
    );
    Assert.Contains("demo:rusty: texture rust (demo:block/nonexistent) not found", manifest["warnings"]!.Select(w => (string)w!));
  }

  [Fact]
  public void Manifest_lists_every_number_and_warns_when_unresolved() {
    Layout layout = Layout.Load(Fixture);
    Dictionary<int, LegendEntry> legend = Schematic.LegendColors(layout);
    JObject m = Schematic.Manifest(layout, legend, ["a.svg", "b.png"]);
    Assert.Equal(["a.svg", "b.png"], m["files"]!.Select(t => (string)t!));
    Assert.Equal(new HashSet<int> { 1, 2 }, m["legend"]!.Select(r => (int)r["number"]!).ToHashSet());
    Assert.Equal(
      new HashSet<string> { "game:claybricks-fire-*", "game:brickslabs-fire-south-free" },
      m["warnings"]!.Select(w => (string)w!).ToHashSet()
    );
  }

  [Fact]
  public void Manifest_names_each_plan_with_the_layer_it_draws() {
    Layout layout = Layout.Load(Fixture);
    JObject m = Schematic.Manifest(
      layout,
      Schematic.LegendColors(layout),
      ["kiln-plan-y0.svg", "kiln-plan-y1.svg", "kiln-iso.png"],
      plans: [("kiln-plan-y0.svg", 0), ("kiln-plan-y1.svg", 1)]
    );
    Assert.Equal(3, ((JArray)m["files"]!).Count);
    Assert.Equal(
      [("kiln-plan-y0.svg", 0), ("kiln-plan-y1.svg", 1)],
      m["plans"]!.Select(p => ((string)p["file"]!, (int)p["layer"]!))
    );
  }

  [Fact]
  public void Manifest_has_no_warning_once_the_legend_carries_a_representative() {
    Layout layout = Layout.Load(Fixture);
    Dictionary<int, LegendEntry> legend = Schematic.LegendColors(layout);
    legend[1].Representative = "game:claybricks-fire-good";
    legend[2].Representative = "game:brickslabs-fire-south-free";
    JObject m = Schematic.Manifest(layout, legend, []);
    Assert.Empty((JArray)m["warnings"]!);
  }

  [Fact]
  public void Manifest_warns_about_a_selector_whose_match_spanned_two_files() {
    Layout layout = Layout.Load(Fixture);
    Dictionary<int, LegendEntry> legend = Schematic.LegendColors(layout);
    legend[1].Representative = "game:claybricks-fire-good";
    legend[2].Representative = "game:brickslabs-fire-south-free";
    var ambiguities = new Dictionary<string, IReadOnlyList<string>> {
      ["game:claybricks-fire-*"] = ["a/claybricks.json", "b/claybricks.json"],
    };
    JObject m = Schematic.Manifest(layout, legend, [], ambiguities);
    string warning = Assert.Single(m["warnings"]!.Select(w => (string)w!));
    Assert.Contains("game:claybricks-fire-*", warning);
    Assert.Contains("a/claybricks.json", warning);
    Assert.Contains("b/claybricks.json", warning);
  }

  [Fact]
  public void Manifest_matches_the_reference_json_field_for_field() {
    Layout layout = Layout.Load(Fixture);
    Dictionary<int, LegendEntry> legend = Schematic.LegendColors(layout);
    JObject m = Schematic.Manifest(layout, legend, ["a.svg", "b.png"]);
    JObject expected = (JObject)JToken.Parse(File.ReadAllText(FixturePath.Expected("schematic/kiln-manifest.json")));
    Assert.True(JToken.DeepEquals(expected, m), $"expected:\n{expected}\n\nactual:\n{m}");
  }

  [Fact]
  public void Blastcore_golden_manifest_matches_the_reference_json_field_for_field() {
    if (BlastcoreGolden is not { } golden)
      return; // skips when the sibling exmods checkout is absent
    IReadOnlyList<string> roots = BlockIndex.DefaultRoots(golden);
    BlockIndex index = BlockIndex.Build(roots);
    Layout layout = Layout.Load(golden);
    Dictionary<int, LegendEntry> legend = Schematic.LegendColors(layout);
    foreach ((int n, string selector) in layout.Numbers) {
      ResolvedBlock? block = index.Resolve(selector);
      legend[n].Representative = block?.Code;
      legend[n].Optional = index.Optional(selector);
    }
    Variant drawn = BlockIndex.Facing(index.VariantsOf(golden), Presentation.Facing)!;
    JObject m = Schematic.Manifest(layout, legend, ["a.svg", "b.png"], front: Presentation.FrontOf(drawn));
    JObject expected = (JObject)JToken.Parse(File.ReadAllText(FixturePath.Expected("schematic/blastcore-manifest.json")));
    Assert.True(JToken.DeepEquals(expected, m), $"expected:\n{expected}\n\nactual:\n{m}");
  }

  [SkippableFact]
  public void Blastcore_golden_plan_svg_layer_zero_matches_the_reference_text_exactly() {
    string? golden = BlastcoreGolden;
    Skip.If(golden is null, "the sibling exmods checkout is absent");
    Layout layout = Layout.Load(golden!);
    string svg = Schematic.PlanSvg(layout, 0, Schematic.LegendColors(layout));
    string expected = File.ReadAllText(FixturePath.Expected("schematic/blastcore-plan-y0.svg"));
    Assert.Equal(expected, svg);
  }

  [SkippableFact]
  public void Blastcore_golden_iso_png_matches_the_reference_render_through_a_real_domain_root() {
    // Every cell here resolves through the game:/iiex: domain roots rather than the
    // self-contained fixture textures. The install is named explicitly, so the fact does not
    // depend on what VINTAGE_STORY points at.
    string? golden = BlastcoreGolden;
    string? game = ClientGame;
    Skip.If(golden is null, "the sibling exmods checkout is absent");
    Skip.If(game is null, "a client install with real textures is absent");
    IReadOnlyList<string> roots = BlockIndex.DefaultRoots(golden!);
    BlockIndex index = BlockIndex.Build(roots, game!);
    Layout layout = Layout.Load(golden!);
    using SKBitmap actual = Schematic.IsoPng(layout, index, ppu: 8);
    using SKBitmap expected = SKBitmap.Decode(FixturePath.Expected("schematic/blastcore-iso.png"));
    AssertMatchesWithinTolerance(expected, actual, "blastcore-iso");
  }

  [Fact]
  public void Iso_png_matches_the_reference_render_within_tolerance() {
    Layout layout = DemoLayout();
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    using SKBitmap actual = Schematic.IsoPng(layout, index, ppu: 8);
    using SKBitmap expected = SKBitmap.Decode(FixturePath.Expected("schematic/kiln-iso.png"));
    AssertMatchesWithinTolerance(expected, actual, "kiln-iso");
  }

  [Fact]
  public void Iso_png_cut_at_matches_the_reference_render_within_tolerance() {
    Layout layout = DemoLayout();
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    using SKBitmap actual = Schematic.IsoPng(layout, index, ppu: 8, cutAt: 0);
    using SKBitmap expected = SKBitmap.Decode(FixturePath.Expected("schematic/kiln-iso-y0.png"));
    AssertMatchesWithinTolerance(expected, actual, "kiln-iso-y0");
  }

  private static void AssertMatchesWithinTolerance(SKBitmap expected, SKBitmap actual, string name) =>
    PixelCompare.Assert(expected, actual, name);

  private static int CountOccurrences(string haystack, string needle) {
    int count = 0, index = 0;
    while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0) {
      count++;
      index += needle.Length;
    }
    return count;
  }

  private static int CountLeaves(JObject raw) {
    int count = 0;
    void Walk(JArray elements) {
      foreach (JToken el in elements) {
        bool hasFaces = el["faces"] is JObject fo && fo.Properties().Any();
        if (hasFaces)
          count++;
        if (el["children"] is JArray children)
          Walk(children);
      }
    }
    Walk((JArray)raw["elements"]!);
    return count;
  }
}
