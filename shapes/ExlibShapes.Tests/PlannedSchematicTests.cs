using System;
using System.Collections.Generic;
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
  public void Iso_view_draws_the_copy_with_the_collision_boxes_outlined() {
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
  public void Planned_plan_outlines_each_declared_cell() {
    // Fails when the outline is dropped: the filler cells (0,0) and (2,0) would be bare glyphs.
    string svg = Plan(Run(), 0);
    Assert.Contains(
      "class=\"declared\" stroke=\"#a0a09c\" stroke-width=\"1\" fill=\"none\" x=\"0\" y=\"0\" width=\"32\" height=\"32\"",
      svg
    );
    Assert.Equal(
      PlannedLayout.Load(Planned).Layout.Fillers.Count(f => f.Y == 0),
      Regex.Matches(svg, "class=\"declared\"").Count
    );
  }

  private static (double[] From, double[] To) Extent(IEnumerable<JObject> bars) {
    double[][] from =
    [
      .. bars.Select(e =>
        ((JArray)e["from"]!).Select(v => (double)v).ToArray()
      ),
    ];
    double[][] to =
    [
      .. bars.Select(e => ((JArray)e["to"]!).Select(v => (double)v).ToArray()),
    ];
    return (
      [.. Enumerable.Range(0, 3).Select(i => from.Min(f => f[i]))],
      [.. Enumerable.Range(0, 3).Select(i => to.Max(t => t[i]))]
    );
  }

  private static IEnumerable<JObject> BarsOf(JObject raw, int box) =>
    raw["elements"]!
      .Cast<JObject>()
      .Where(e =>
        ((string)e["name"]!).StartsWith(
          $"{PlannedSchematic.BoxPrefix}{box}-",
          StringComparison.Ordinal
        )
      );

  private static string Variant(string edit) {
    string dir = Path.Combine(
      Path.GetTempPath(),
      "exlib-shapes-" + Guid.NewGuid().ToString("N")
    );
    Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "mini.json");
    File.WriteAllText(path, edit);
    return path;
  }

  [Fact]
  public void Bars_run_along_the_edges_of_a_collision_box_not_its_cell() {
    // Fails when the old cell frames return: cell (1,0,0)'s bars would span x 16..32, z 0..16.
    (JObject raw, _) = PlannedSchematic.Compose(
      PlannedLayout.Load(Planned),
      Chosen,
      0
    );
    JObject[] bars = [.. BarsOf(raw, 0)];
    Assert.Equal(12, bars.Length);
    (double[] from, double[] to) = Extent(bars);
    Assert.Equal([24, 0, 4], from);
    Assert.Equal([32, 16, 12], to);
    JObject first = bars[0];
    double[] f = ((JArray)first["from"]!).Select(v => (double)v).ToArray();
    double[] t = ((JArray)first["to"]!).Select(v => (double)v).ToArray();
    Assert.Equal(PlannedSchematic.FrameBar, t[1] - f[1]);
    Assert.Equal(PlannedSchematic.FrameBar, t[2] - f[2]);
    Assert.Equal(8, t[0] - f[0]);
    Assert.Equal(0.25, PlannedSchematic.FrameBar);
  }

  [Fact]
  public void A_cell_without_a_box_list_gets_its_cubes_edges() {
    // Fails when the missing list reads as no box: cell (-1,0,0) would have no bars.
    (JObject raw, _) = PlannedSchematic.Compose(
      PlannedLayout.Load(Planned),
      Chosen,
      0
    );
    JObject[] bars = [.. BarsOf(raw, 1)];
    Assert.Equal(12, bars.Length);
    (double[] from, double[] to) = Extent(bars);
    Assert.Equal([-16, 0, 0], from);
    Assert.Equal([0, 16, 16], to);
  }

  [Fact]
  public void A_cell_with_an_empty_box_list_draws_no_bar() {
    // Fails when an empty list reads as a full cube: the port cell would be cubed at x -16..0.
    string path = Variant(
      File.ReadAllText(Planned)
        .Replace("\"portFace\"", "\"collisionBoxes\": [], \"portFace\"")
    );
    (JObject raw, _) = PlannedSchematic.Compose(
      PlannedLayout.Load(path),
      Chosen,
      0
    );
    Assert.Equal(1 + 3 * 12, raw["elements"]!.Count());
    Assert.Empty(
      raw["elements"]!
        .Cast<JObject>()
        .Where(e =>
          ((string)e["name"]!).StartsWith(
            PlannedSchematic.BoxPrefix,
            StringComparison.Ordinal
          )
          && (double)((JArray)e["from"]!)[0]! < 0
        )
    );
  }

  [Fact]
  public void The_principal_cell_draws_its_own_boxes_and_a_cut_layer_drops_those_above() {
    // Fails when the principal is left out: the box at the origin would have no bars.
    string path = Variant(
      File.ReadAllText(Planned)
        .Replace(
          "\"planned\": {",
          "\"planned\": {\"principal\": {\"collisionBoxes\": [{\"x1\": 0, \"y1\": 0.25, \"z1\": 0, \"x2\": 1, \"y2\": 0.5, \"z2\": 1}]},"
        )
    );
    (JObject raw, _) = PlannedSchematic.Compose(
      PlannedLayout.Load(path),
      Chosen,
      0
    );
    (double[] from, double[] to) = Extent(BarsOf(raw, 0));
    Assert.Equal([0, 4, 0], from);
    Assert.Equal([16, 8, 16], to);
    (raw, _) = PlannedSchematic.Compose(
      PlannedLayout.Load(path),
      Chosen,
      0,
      cutAt: 0
    );
    Assert.Equal(1 + 4 * 12, raw["elements"]!.Count());
  }

  [Fact]
  public void Planned_plan_draws_no_cross_and_a_blocktype_plan_does() {
    // Fails when the cross returns on a planned plan, and when a blocktype plan loses it.
    Assert.DoesNotContain("class=\"filler\"", Plan(Run(), 0));
    Layout kiln = Layout.Load(FixturePath.Of("schematic/kiln.json"));
    string svg = Schematic.PlanSvg(kiln, 0, Schematic.LegendColors(kiln));
    Assert.Contains("class=\"filler\"", svg);
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
