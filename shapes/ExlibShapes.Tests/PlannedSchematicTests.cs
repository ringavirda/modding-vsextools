using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Covers <c>schematic</c> on a planned layout: the glyph at each cell, the box outlines in
/// their cells, the chosen shape copy and the manifest's layers and glyphs.</summary>
public class PlannedSchematicTests {
  private static string Planned =>
    FixturePath.Of("planned/workbench/layouts-planned/mini/mini.json");

  private static string Chosen =>
    FixturePath.Of("planned/other/chosen-copy.json");

  private static string Run(params string[] extra) {
    string outDir = Path.Combine(
      Path.GetTempPath(),
      "exlib-shapes-" + Guid.NewGuid().ToString("N")
    );
    Assert.Equal(
      0,
      Program.RunSchematic([
        Planned,
        "--out",
        outDir,
        "--views",
        "plan",
        .. extra,
      ])
    );
    return outDir;
  }

  private static string Plan(string outDir, int y) =>
    File.ReadAllText(Path.Combine(outDir, $"mini-plan-y{y}.svg"));

  private static char? GlyphAt(string svg, string x, string y) =>
    Regex.Match(svg, $"class=\"glyph\" x=\"{x}\" y=\"{y}\"[^>]*>(.)</text>")
      is { Success: true } m
      ? m.Groups[1].Value[0]
      : null;

  [Fact]
  public void Plan_svg_puts_each_glyph_in_its_cell() {
    // Fails when the grid's rows are read in the wrong z order: W would land at the top row.
    string svg = Plan(Run(), 0);
    Assert.Equal('P', GlyphAt(svg, "16", "21"));
    Assert.Equal('O', GlyphAt(svg, "48", "21"));
    Assert.Equal('_', GlyphAt(svg, "80", "21"));
    Assert.Equal('W', GlyphAt(svg, "48", "53"));
    Assert.Equal(4, Regex.Matches(svg, "class=\"glyph\"").Count);
  }

  [Fact]
  public void Plan_svg_draws_each_layer_from_its_own_rows() {
    string svg = Plan(Run(), 1);
    Assert.Equal('#', GlyphAt(svg, "48", "21"));
    Assert.Equal(1, Regex.Matches(svg, "class=\"glyph\"").Count);
  }

  [Fact]
  public void Plan_svg_outlines_a_collision_box_and_a_control_box_inside_their_cells() {
    // Fails when the box outlines are dropped from the overlay.
    string svg = Plan(Run(), 0);
    Assert.Contains(
      "class=\"box\" stroke=\"#1f5fbf\" stroke-width=\"1\" fill=\"none\" x=\"80\" y=\"8\" width=\"16\" height=\"16\"",
      svg
    );
    Assert.Contains(
      "class=\"control-box\" stroke=\"#c0392b\" stroke-width=\"1.5\" stroke-dasharray=\"3 2\" fill=\"none\" x=\"40\" y=\"48\" width=\"16\" height=\"16\"",
      svg
    );
  }

  [Fact]
  public void Plan_svg_key_lists_only_the_glyphs_and_boxes_of_its_layer() {
    string layer0 = Plan(Run(), 0);
    Assert.Contains(">W  control valve, bypass<", layer0);
    Assert.Contains(">P  port<", layer0);
    Assert.Contains(">blue outline  collision box<", layer0);
    string layer1 = Plan(Run(), 1);
    Assert.Contains(">#  full cube<", layer1);
    Assert.DoesNotContain("collision box", layer1);
  }

  [Fact]
  public void Plan_svg_turns_glyphs_and_boxes_with_angle() {
    // At 180 the cell (1,0,0) lands at (-1,0,0) and its box turns inside it to x 0..0.5, z 0.25..0.75.
    string svg = Plan(Run("--angle", "180"), 0);
    Assert.Equal('_', GlyphAt(svg, "16", "53"));
    Assert.Contains(
      "class=\"box\" stroke=\"#1f5fbf\" stroke-width=\"1\" fill=\"none\" x=\"0\" y=\"40\" width=\"16\" height=\"16\"",
      svg
    );
  }

  [Fact]
  public void Manifest_names_the_layers_the_glyphs_and_the_copy_read() {
    string outDir = Run();
    JObject manifest = JObject.Parse(
      File.ReadAllText(Path.Combine(outDir, "mini.json"))
    );
    JObject planned = (JObject)manifest["planned"]!;
    JArray layers = (JArray)planned["layers"]!;
    Assert.Equal([0, 1], layers.Select(l => (int)l["layer"]!));
    Assert.Equal(
      ["P O _", ". W ."],
      ((JArray)layers[0]["rows"]!).Select(r => (string)r!)
    );
    Assert.Equal(
      [". # .", ". . ."],
      ((JArray)layers[1]["rows"]!).Select(r => (string)r!)
    );
    Assert.Equal(
      ["P", "O", "_", "W", "#"],
      ((JArray)planned["glyphs"]!).Select(g => (string)g["glyph"]!)
    );
    Assert.EndsWith(
      Path.Combine(
        "planned",
        "workbench",
        "shapes-finished",
        "mini",
        "mini-copy.json"
      ),
      (string)planned["copy"]!
    );
    Assert.Equal(2, ((JArray)manifest["plans"]!).Count);
  }

  [Fact]
  public void Shape_flag_picks_the_copy_instead_of_the_one_the_file_names() {
    // Fails when --shape is ignored: the manifest would name the file's own copy.
    string outDir = Run("--shape", Chosen);
    JObject manifest = JObject.Parse(
      File.ReadAllText(Path.Combine(outDir, "mini.json"))
    );
    Assert.Equal(Chosen, (string)manifest["planned"]!["copy"]!);
  }

  [Fact]
  public void Iso_view_draws_the_copy_with_the_cells_framed() {
    string outDir = Path.Combine(
      Path.GetTempPath(),
      "exlib-shapes-" + Guid.NewGuid().ToString("N")
    );
    Assert.Equal(
      0,
      Program.RunSchematic([Planned, "--out", outDir, "--views", "iso"])
    );
    Assert.True(File.Exists(Path.Combine(outDir, "mini-iso.png")));
    (JObject raw, _) = PlannedSchematic.Compose(
      PlannedLayout.Load(Planned),
      Chosen,
      0
    );
    Assert.Equal(1 + 4 * 12, raw["elements"]!.Count());
    (raw, _) = PlannedSchematic.Compose(
      PlannedLayout.Load(Planned),
      Chosen,
      0,
      cutAt: 0
    );
    Assert.Equal(1 + 3 * 12, raw["elements"]!.Count());
  }

  [Fact]
  public void Shape_flag_is_refused_for_a_blocktype_file() {
    string kiln = FixturePath.Of("schematic/kiln.json");
    Assert.Throws<UsageException>(() =>
      Program.RunSchematic([
        kiln,
        "--out",
        Path.GetTempPath(),
        "--shape",
        Chosen,
      ])
    );
  }

  [Fact]
  public void A_blocktype_file_is_not_a_planned_layout() {
    Assert.True(PlannedLayout.Is(Planned));
    Assert.False(PlannedLayout.Is(FixturePath.Of("schematic/kiln.json")));
  }
}
