using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Covers the scene reader and the plain corner views of <c>fixtures/scene/demo.scene.json</c>,
/// compared against <c>expected/scene/</c>.</summary>
public class SceneTests {
  private static readonly string[] Corners = ["ne", "nw", "se", "sw"];

  private static string Fixture => FixturePath.Of("scene/demo.scene.json");
  private static string DemoRoot => FixturePath.Of("schematic");

  private static string Scratch() =>
    Directory.CreateTempSubdirectory("scene-test").FullName;

  private static SceneRendered Render(string outDir, int ppu = 4) =>
    SceneRender.Write(
      SceneFile.Load(Fixture),
      BlockIndex.Build([DemoRoot]),
      outDir,
      "demo",
      Corners,
      ppu
    );

  private static IEnumerable<string> Names(JObject raw) =>
    raw["elements"]!.Select(el => (string)el["name"]!);

  [Fact]
  public void The_reader_takes_the_cells_ports_and_keys_in_file_order() {
    SceneFile scene = SceneFile.Load(Fixture);
    Assert.Equal("demo", scene.Setup);
    Assert.Equal(21, scene.Cells.Count);
    SceneCell port = scene.Cells[1];
    Assert.Equal("port", port.Role);
    Assert.Equal(new ScenePort("e", "steam", "A"), port.Port);
    Assert.Equal(new Offset(0, 0, 0), port.Principal);
    Assert.Equal(["e"], scene.Cells[4].Open);
    Assert.Null(scene.Cells[13].Port!.Joins);
    Assert.Equal("M1", scene.Lines[0].Key);
    Assert.Equal("A", scene.Networks[0].Key);
  }

  [Fact]
  public void The_reader_refuses_another_format() {
    Assert.Throws<InvalidDataException>(() =>
      SceneFile.Parse("{\"format\": \"setup-scene/2\", \"setup\": \"x\"}")
    );
  }

  [Fact]
  public void The_four_plain_views_share_one_size_and_match_their_references() {
    string dir = Scratch();
    SceneRendered rendered = Render(dir);
    Assert.Equal(4, rendered.Files.Count);
    var sizes = new HashSet<(int, int)>();
    foreach (string view in Corners) {
      using SKBitmap actual = SKBitmap.Decode(
        Path.Combine(dir, $"demo-plain-{view}.png")
      );
      sizes.Add((actual.Width, actual.Height));
      using SKBitmap expected = SKBitmap.Decode(
        FixturePath.Expected($"scene/demo-plain-{view}.png")
      );
      PixelCompare.Assert(expected, actual, $"demo-plain-{view}");
    }
    Assert.Single(sizes);
  }

  [Theory]
  [InlineData("ne", 1, -1)]
  [InlineData("nw", -1, -1)]
  [InlineData("se", 1, 1)]
  [InlineData("sw", -1, 1)]
  public void Each_corner_view_stands_on_the_side_its_name_gives(
    string view,
    int xSign,
    int zSign
  ) {
    Vector3 eye = Renderer.Eye(Renderer.NamedViews[view]);
    Assert.Equal(xSign, Math.Sign(eye.X));
    Assert.Equal(zSign, Math.Sign(eye.Z));
  }

  [Fact]
  public void An_unresolved_code_is_a_checker_cube_listed_once_and_never_magenta() {
    string dir = Scratch();
    SceneRendered rendered = Render(dir);
    Assert.Equal(["demo:nothing"], rendered.Unresolved);

    SceneFile scene = SceneFile.Load(Fixture);
    var ghost = new SceneFile(
      "ghost",
      null,
      null,
      [scene.Cells.Single(c => c.Code == "demo:nothing")],
      [],
      []
    );
    SceneRender.Write(
      ghost,
      BlockIndex.Build([DemoRoot]),
      dir,
      "ghost",
      ["se"]
    );
    using SKBitmap bitmap = SKBitmap.Decode(
      Path.Combine(dir, "ghost-plain-se.png")
    );
    var colours = new HashSet<SKColor>();
    for (int y = 0; y < bitmap.Height; y++)
      for (int x = 0; x < bitmap.Width; x++)
        colours.Add(bitmap.GetPixel(x, y));
    Assert.Contains(new SKColor(0, 0, 0), colours);
    Assert.Contains(new SKColor(255, 210, 0), colours);
    Assert.DoesNotContain(new SKColor(255, 0, 255), colours);
  }

  [SkippableFact]
  public void Fillers_and_ports_draw_nothing_and_the_filler_code_resolves() {
    string? exlib = FixturePath.Workspace("exlib");
    Skip.If(exlib is null, "the sibling exlib checkout is absent");
    BlockIndex index = BlockIndex.Build([DemoRoot, exlib!]);
    Assert.NotNull(index.Resolve("exlib:structurefiller"));

    SceneFile scene = SceneFile.Load(Fixture);
    (JObject raw, _, IReadOnlyList<string> unresolved) = SceneRender.Compose(
      scene,
      index
    );
    Assert.Equal(["demo:nothing"], unresolved);
    List<string> names = [.. Names(raw)];
    List<int> hidden =
    [
      .. scene
        .Cells.Select((c, i) => (c, i))
        .Where(t => t.c.Role is "filler" or "port")
        .Select(t => t.i),
    ];
    Assert.Equal(4, hidden.Count);
    foreach (int i in hidden)
      Assert.DoesNotContain($"c{i}", names);
    Assert.Equal(scene.Cells.Count - hidden.Count - 1, names.Count);
  }

  [Fact]
  public void A_fluid_under_a_solid_entry_draws_nothing_and_a_lone_fluid_draws() {
    SceneFile scene = SceneFile.Load(Fixture);
    (JObject raw, _, _) = SceneRender.Compose(
      scene,
      BlockIndex.Build([DemoRoot])
    );
    List<string> names = [.. Names(raw)];
    List<SceneCell> cells = [.. scene.Cells];
    int under = cells.FindIndex(c =>
      c.Role == "fluid" && c.At == new Offset(4, 0, 4)
    );
    int alone = cells.FindIndex(c =>
      c.Role == "fluid" && c.At == new Offset(6, 0, 5)
    );
    Assert.DoesNotContain($"c{under}", names);
    Assert.Contains($"c{alone}", names);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(1)]
  public void Each_layers_plan_equals_its_reference(int y) {
    string svg = ScenePlan.Svg(SceneFile.Load(Fixture), y);
    string expected = File.ReadAllText(
      FixturePath.Expected($"scene/demo-plan-y{y}.svg")
    );
    Assert.Equal(expected, svg);
  }

  [Fact]
  public void The_plan_labels_come_from_the_ids_and_a_filler_takes_its_principals() {
    string svg = ScenePlan.Svg(SceneFile.Load(Fixture), 0);
    Assert.DoesNotContain("demo:", svg);
    Assert.Equal(5, Regex.Matches(svg, @">kiln</text>").Count);
    Assert.Equal(3, Regex.Matches(svg, @">main</text>").Count);
  }

  [Fact]
  public void The_unjoined_port_and_the_open_face_are_red_and_the_joined_port_takes_its_networks_colour() {
    SceneFile scene = SceneFile.Load(Fixture);
    string svg = ScenePlan.Svg(scene, 0);
    string joins = ScenePlan.KeyColors(scene)["A"];
    Assert.NotEqual(ScenePlan.Unjoined, joins);
    List<string> arrows =
    [
      .. Regex
        .Matches(svg, "<line class=\"connector\"[^>]*stroke=\"(#[0-9A-F]{6})\"")
        .Select(m => m.Groups[1].Value),
    ];
    Assert.Equal([joins, ScenePlan.Unjoined], arrows);
    List<string> ticks =
    [
      .. Regex
        .Matches(svg, "<line class=\"open\"[^>]*stroke=\"(#[0-9A-F]{6})\"")
        .Select(m => m.Groups[1].Value),
    ];
    Assert.Equal([ScenePlan.Unjoined], ticks);
  }

  [Fact]
  public void A_filler_hatch_is_stroked_in_ink_that_differs_from_the_fill_it_covers() {
    string svg = ScenePlan.Svg(SceneFile.Load(Fixture), 0);
    Assert.Equal(4, Regex.Matches(svg, "<rect class=\"hatched\"").Count);
    List<(string Fill, string Stroke)> hatches =
    [
      .. Regex
        .Matches(
          svg,
          "<pattern id=\"hatch([0-9A-F]{6})\"[^>]*><line [^>]*stroke=\"([^\"]+)\""
        )
        .Select(m => ("#" + m.Groups[1].Value, m.Groups[2].Value)),
    ];
    Assert.Equal([("#999999", "black")], hatches);
  }

  [Fact]
  public void An_open_top_or_bottom_face_is_a_red_cross_in_its_cell() {
    var riser = new SceneCell(
      new Offset(0, 0, 0),
      "demo:wall-east",
      "riser",
      "block",
      null,
      "A",
      null,
      null,
      ["u"]
    );
    var scene = new SceneFile(
      "riser",
      null,
      null,
      [riser],
      [new SceneNetwork("A", "steam", 1, ["riser"])],
      []
    );
    List<string> bars =
    [
      .. Regex
        .Matches(
          ScenePlan.Svg(scene, 0),
          "<line class=\"open\"[^>]*stroke=\"(#[0-9A-F]{6})\""
        )
        .Select(m => m.Groups[1].Value),
    ];
    Assert.Equal([ScenePlan.Unjoined, ScenePlan.Unjoined], bars);
  }

  [Fact]
  public void Keys_past_the_palette_avoid_the_red_and_grey_entries() {
    var networks = Enumerable
      .Range(0, 12)
      .Select(i => new SceneNetwork(((char)('A' + i)).ToString(), "steam", 1, []))
      .ToList();
    IReadOnlyDictionary<string, string> colors = ScenePlan.KeyColors(
      new SceneFile("many", null, null, [], networks, [])
    );
    Assert.Equal(12, colors.Values.Distinct().Count());
    Assert.DoesNotContain("#C44E52", colors.Values);
    Assert.DoesNotContain("#8C8C8C", colors.Values);
    Assert.DoesNotContain("#D96262", colors.Values);
  }

  [Fact]
  public void The_legend_starts_at_the_canvas_margin_under_a_narrow_grid() {
    var cells = Enumerable
      .Range(0, 4)
      .Select(z => new SceneCell(
        new Offset(0, 0, z),
        "demo:wall-east",
        "main",
        "block",
        null,
        "A",
        null,
        null,
        []
      ))
      .ToList();
    var scene = new SceneFile(
      "narrow",
      null,
      null,
      cells,
      [new SceneNetwork("A", "steam", 4, ["main"])],
      []
    );
    string svg = ScenePlan.Svg(scene, 0);
    int canvas = int.Parse(Regex.Match(svg, "<svg[^>]* width=\"(\\d+)\"").Groups[1].Value);
    string beforeLegend = svg[..svg.IndexOf("class=\"legend\"", StringComparison.Ordinal)];
    int offset = int.Parse(
      Regex.Matches(beforeLegend, @"translate\((\d+),").Cast<Match>().Last().Groups[1].Value
    );
    int redRow = Schematic.CaptionWidth(
      "red arrow: unjoined port, red tick: open face"
    );
    Assert.True(offset + 16 + redRow <= canvas);
  }
}
