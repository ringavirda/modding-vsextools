using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Ported from <c>vsshape/tests/test_schematic.py</c>; the plan SVGs and the manifest are
/// compared verbatim against the Python's own output (<c>expected/schematic/</c>), the iso PNGs
/// within the pixel tolerance <see cref="RendererTests"/> uses.</summary>
public class SchematicTests {
  private static string Fixture => FixturePath.Of("schematic/kiln.json");
  private static string DemoRoot => FixturePath.Of("schematic");
  private const string BlastcoreGolden =
    "/home/fallen/src/modding-vsex/exmods/mods/iiex/tests/goldens/iiex/blocktypes/furnace/blastcore.json";

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
    // Layout is immutable by construction (Rotated returns a new instance); its constructor is
    // internal, reachable here through the assembly's own InternalsVisibleTo, the same way the
    // Python test mutates layout.numbers in place, without giving Layout a public mutation surface
    // no production code needs.
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
  public void Manifest_has_no_warning_once_the_legend_carries_a_representative() {
    Layout layout = Layout.Load(Fixture);
    Dictionary<int, LegendEntry> legend = Schematic.LegendColors(layout);
    legend[1].Representative = "game:claybricks-fire-good";
    legend[2].Representative = "game:brickslabs-fire-south-free";
    JObject m = Schematic.Manifest(layout, legend, []);
    Assert.Empty((JArray)m["warnings"]!);
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
    IReadOnlyList<string> roots = BlockIndex.DefaultRoots(BlastcoreGolden);
    BlockIndex index = BlockIndex.Build(roots);
    Layout layout = Layout.Load(BlastcoreGolden);
    Dictionary<int, LegendEntry> legend = Schematic.LegendColors(layout);
    foreach ((int n, string selector) in layout.Numbers) {
      ResolvedBlock? block = index.Resolve(selector);
      legend[n].Representative = block?.Code;
      legend[n].Optional = index.Optional(selector);
    }
    JObject m = Schematic.Manifest(layout, legend, ["a.svg", "b.png"]);
    JObject expected = (JObject)JToken.Parse(File.ReadAllText(FixturePath.Expected("schematic/blastcore-manifest.json")));
    Assert.True(JToken.DeepEquals(expected, m), $"expected:\n{expected}\n\nactual:\n{m}");
  }

  [Fact]
  public void Blastcore_golden_plan_svg_layer_zero_matches_the_reference_text_exactly() {
    Layout layout = Layout.Load(BlastcoreGolden);
    string svg = Schematic.PlanSvg(layout, 0, Schematic.LegendColors(layout));
    string expected = File.ReadAllText(FixturePath.Expected("schematic/blastcore-plan-y0.svg"));
    Assert.Equal(expected, svg);
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

  private static void AssertMatchesWithinTolerance(SKBitmap expected, SKBitmap actual, string name) {
    Assert.Equal(expected.Width, actual.Width);
    Assert.Equal(expected.Height, actual.Height);
    int differing = 0;
    const int tolerance = 2;
    for (int y = 0; y < expected.Height; y++)
      for (int x = 0; x < expected.Width; x++) {
        SKColor e = expected.GetPixel(x, y);
        SKColor a = actual.GetPixel(x, y);
        if (
          Math.Abs(e.Red - a.Red) > tolerance
          || Math.Abs(e.Green - a.Green) > tolerance
          || Math.Abs(e.Blue - a.Blue) > tolerance
        )
          differing++;
      }
    Console.WriteLine($"{name}: {differing} differing pixel(s) beyond tolerance {tolerance}");
    Assert.Equal(0, differing);
  }

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
        JArray? faces = el["faces"] as JArray;
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
