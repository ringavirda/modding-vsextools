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
public class SceneTests : IDisposable {
  private static readonly string[] Corners = ["ne", "nw", "se", "sw"];

  private static string Fixture => FixturePath.Of("scene/demo.scene.json");
  private static string DemoRoot => FixturePath.Of("schematic");

  private readonly List<string> _scratch = [];

  private string Scratch() {
    string dir = Directory.CreateTempSubdirectory("scene-test").FullName;
    _scratch.Add(dir);
    return dir;
  }

  public void Dispose() {
    foreach (string dir in _scratch)
      Directory.Delete(dir, true);
  }

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

  private static string Scene(string networkBody) =>
    "{\"format\": \"setup-scene/1\", \"setup\": \"x\", \"networks\": [{\"key\": \"A\", \"type\": \"steam\", "
    + networkBody
    + "}]}";

  [Fact]
  public void The_reader_refuses_a_count_that_is_text() {
    Assert.Throws<InvalidDataException>(() =>
      SceneFile.Parse(Scene("\"nodes\": \"x\""))
    );
  }

  [Fact]
  public void The_reader_refuses_a_fractional_count() {
    Assert.Throws<InvalidDataException>(() =>
      SceneFile.Parse(Scene("\"nodes\": 1.5"))
    );
  }

  [Fact]
  public void The_reader_refuses_a_count_past_the_integer_range() {
    Assert.Throws<InvalidDataException>(() =>
      SceneFile.Parse(Scene("\"nodes\": 99999999999"))
    );
  }

  [Fact]
  public void The_reader_refuses_an_object_where_a_string_belongs() {
    Assert.Throws<InvalidDataException>(() =>
      SceneFile.Parse(
        "{\"format\": \"setup-scene/1\", \"setup\": \"x\", \"cells\": [{\"at\": [0,0,0], \"code\": {}}]}"
      )
    );
  }

  [Fact]
  public void A_port_at_the_scene_edge_keeps_its_frame_inside_every_tinted_view() {
    var core = new SceneCell(
      new Offset(0, 0, 0),
      "demo:masonry",
      "c",
      "block",
      null,
      null,
      null,
      null,
      []
    );
    var port = new SceneCell(
      new Offset(1, 0, 0),
      "demo:masonry",
      "p",
      "port",
      new Offset(0, 0, 0),
      null,
      null,
      new ScenePort("e", "steam", null),
      []
    );
    var scene = new SceneFile("edge", null, null, [core, port], [], []);
    string dir = Scratch();
    SceneRender.Write(
      scene,
      BlockIndex.Build([DemoRoot]),
      dir,
      "edge",
      Corners
    );
    foreach (string view in Corners) {
      using SKBitmap bitmap = SKBitmap.Decode(
        Path.Combine(dir, $"edge-tint-{view}.png")
      );
      int reds = 0;
      for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++) {
          SKColor c = bitmap.GetPixel(x, y);
          bool red = c.Red > 150 && c.Green < 90 && c.Blue < 90;
          if (!red)
            continue;
          reds++;
          Assert.True(
            x > 0 && y > 0 && x < bitmap.Width - 1 && y < bitmap.Height - 1,
            $"{view}: red pixel on the edge at {x},{y} of {bitmap.Width}x{bitmap.Height}"
          );
        }
      Assert.True(reds > 0, $"{view}: no red frame");
    }
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
      .Select(i => new SceneNetwork(
        ((char)('A' + i)).ToString(),
        "steam",
        1,
        []
      ))
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
    int canvas = int.Parse(
      Regex.Match(svg, "<svg[^>]* width=\"(\\d+)\"").Groups[1].Value
    );
    string beforeLegend = svg[
      ..svg.IndexOf("class=\"legend\"", StringComparison.Ordinal)
    ];
    int offset = int.Parse(
      Regex
        .Matches(beforeLegend, @"translate\((\d+),")
        .Cast<Match>()
        .Last()
        .Groups[1]
        .Value
    );
    int redRow = Schematic.CaptionWidth(
      "red arrow: unjoined port, red tick: open face"
    );
    Assert.True(offset + 16 + redRow <= canvas);
  }

  private static SceneCell Wall(string? network, string? line = null) =>
    new(
      new Offset(0, 0, 0),
      "demo:wall-east",
      "solo",
      "block",
      null,
      network,
      line,
      null,
      []
    );

  private static SKBitmap TintedSe(SceneCell cell, string key, string dir) {
    var scene = new SceneFile(
      "solo",
      null,
      null,
      [cell],
      key == "A" ? [new SceneNetwork("A", "steam", 1, ["solo"])] : [],
      []
    );
    SceneRender.Write(scene, BlockIndex.Build([DemoRoot]), dir, "solo", ["se"]);
    return SKBitmap.Decode(Path.Combine(dir, "solo-tint-se.png"));
  }

  // The pixels whose four neighbours are drawn too, so the silhouette's blend with the
  // background is left out.
  private static IEnumerable<SKColor> Drawn(SKBitmap bitmap) {
    SKColor background = bitmap.GetPixel(0, 0);
    bool Solid(int x, int y) => bitmap.GetPixel(x, y) != background;
    for (int y = 1; y < bitmap.Height - 1; y++)
      for (int x = 1; x < bitmap.Width - 1; x++)
        if (
          Solid(x, y)
          && Solid(x - 1, y)
          && Solid(x + 1, y)
          && Solid(x, y - 1)
          && Solid(x, y + 1)
        )
          yield return bitmap.GetPixel(x, y);
  }

  [Fact]
  public void The_four_views_share_one_size_and_each_draws_centred_in_it() {
    string dir = Scratch();
    SceneRender.Write(
      SceneFile.Load(FixturePath.Of("scene/far.scene.json")),
      BlockIndex.Build([DemoRoot]),
      dir,
      "far",
      Corners
    );
    foreach (string kind in new[] { "plain", "tint" }) {
      var sizes = new HashSet<(int, int)>();
      foreach (string view in Corners) {
        using SKBitmap bitmap = SKBitmap.Decode(
          Path.Combine(dir, $"far-{kind}-{view}.png")
        );
        sizes.Add((bitmap.Width, bitmap.Height));
        SKColor background = bitmap.GetPixel(0, 0);
        int x0 = int.MaxValue,
          x1 = -1,
          y0 = int.MaxValue,
          y1 = -1;
        for (int y = 0; y < bitmap.Height; y++)
          for (int x = 0; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, y) != background) {
              x0 = Math.Min(x0, x);
              x1 = Math.Max(x1, x);
              y0 = Math.Min(y0, y);
              y1 = Math.Max(y1, y);
            }
        Assert.InRange(Math.Abs((x0 + x1) / 2.0 - bitmap.Width / 2.0), 0, 4);
        Assert.InRange(Math.Abs((y0 + y1) / 2.0 - bitmap.Height / 2.0), 0, 4);
      }
      Assert.Single(sizes);
    }
  }

  private static JObject DemoManifest(SceneRendered rendered) {
    SceneFile scene = SceneFile.Load(Fixture);
    IEnumerable<string> files = rendered
      .Files.Concat(rendered.Tinted)
      .Select(f => Path.GetFileName(f))
      .Concat(ScenePlan.Layers(scene).Select(y => $"demo-plan-y{y}.svg"));
    return SceneRender.Manifest(scene, [.. files], rendered, []);
  }

  [Fact]
  public void The_manifest_equals_its_reference_and_the_printed_lines_name_the_findings() {
    string dir = Scratch();
    JObject manifest = DemoManifest(Render(dir));
    JObject expected = JObject.Parse(
      File.ReadAllText(FixturePath.Expected("scene/demo-manifest.json"))
    );
    Assert.True(JToken.DeepEquals(expected, manifest), manifest.ToString());
    Assert.Equal(
      ["unjoined: demo kiln s steam at 2,0,3", "open: demo main e at 4,0,0"],
      SceneRender.FindingLines(SceneFile.Load(Fixture), "demo")
    );
  }

  [Fact]
  public void The_scene_command_writes_the_manifest_and_prints_the_files_and_findings() {
    string dir = Scratch();
    var stdout = new StringWriter();
    TextWriter saved = Console.Out;
    Console.SetOut(stdout);
    try {
      Assert.Equal(
        0,
        Program.RunScene([Fixture, "--out", dir, "--roots", DemoRoot])
      );
    } finally {
      Console.SetOut(saved);
    }
    JObject written = JObject.Parse(
      File.ReadAllText(Path.Combine(dir, "demo.json"))
    );
    written["files"] = new JArray(
      written["files"]!.Select(f => (JToken)Path.GetFileName((string)f!))
    );
    JObject expected = JObject.Parse(
      File.ReadAllText(FixturePath.Expected("scene/demo-manifest.json"))
    );
    Assert.True(JToken.DeepEquals(expected, written), written.ToString());
    string[] lines = stdout
      .ToString()
      .Split(
        '\n',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
      );
    Assert.Equal(
      ["unjoined: demo kiln s steam at 2,0,3", "open: demo main e at 4,0,0"],
      lines[^2..]
    );
    Assert.Equal(Path.Combine(dir, "demo.json"), lines[^3]);
    Assert.Equal(10, lines.Length - 3);
  }

  [Fact]
  public void The_manifest_lists_a_block_whose_texture_has_no_file() {
    var rusty = new SceneCell(
      new Offset(0, 0, 0),
      "demo:rusty",
      "r",
      "block",
      null,
      null,
      null,
      null,
      []
    );
    var scene = new SceneFile("rusty", null, null, [rusty], [], []);
    SceneRendered rendered = SceneRender.Write(
      scene,
      BlockIndex.Build([DemoRoot]),
      Scratch(),
      "rusty",
      ["se"]
    );
    string line = "demo:rusty: texture rust (demo:block/nonexistent) not found";
    Assert.Equal([line], rendered.MissingTextures);
    JObject manifest = SceneRender.Manifest(scene, [], rendered, []);
    Assert.Equal([line], manifest["missingTextures"]!.Select(t => (string)t!));
  }

  // The pixel of demo-tint-se.png at the top face's centre of the cell at `at`.
  private static SKColor DemoTopPixel(SKBitmap tinted, Offset at) {
    SceneFile scene = SceneFile.Load(Fixture);
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    (JObject raw, _, _) = SceneRender.Compose(scene, index);
    View view = Renderer.NamedViews["se"];
    Renderer.Canvas canvas = SceneRender.Canvases(
      SceneRender.Load(raw),
      SceneRender.Marks(scene, index),
      Corners
    )["se"];
    (double col, double row) = new Renderer.Projection(
      canvas.XMin,
      canvas.YMax,
      4,
      view
    ).Screen(at.X * 16 + 8, 16, at.Z * 16 + 8);
    return tinted.GetPixel((int)col, (int)row);
  }

  [Fact]
  public void A_run_cells_top_face_takes_the_networks_hue_and_a_no_key_cell_is_grey() {
    string dir = Scratch();
    SceneRender.Write(
      SceneFile.Load(Fixture),
      BlockIndex.Build([DemoRoot]),
      dir,
      "demo",
      ["se"]
    );
    using SKBitmap tinted = SKBitmap.Decode(
      Path.Combine(dir, "demo-tint-se.png")
    );
    SKColor
      .Parse(ScenePlan.KeyColors(SceneFile.Load(Fixture))["A"])
      .ToHsl(out float keyHue, out float keySaturation, out float _);

    DemoTopPixel(tinted, new Offset(2, 0, 0))
      .ToHsl(out float hue, out float saturation, out float _);
    float apart = Math.Abs(hue - keyHue);
    Assert.InRange(Math.Min(apart, 360 - apart), 0, 10);
    Assert.True(
      saturation >= keySaturation / 2,
      $"saturation {saturation} of key's {keySaturation}"
    );

    DemoTopPixel(tinted, new Offset(6, 0, 5))
      .ToHsl(out float _, out float greySaturation, out float _);
    Assert.Equal(0, greySaturation);
  }

  [Fact]
  public void A_cell_naming_a_key_the_scene_does_not_declare_is_grey_in_the_tint() {
    using SKBitmap tinted = TintedSe(Wall("B"), "", Scratch());
    List<SKColor> pixels = [.. Drawn(tinted)];
    Assert.NotEmpty(pixels);
    Assert.All(
      pixels,
      c => {
        Assert.InRange(Math.Abs(c.Red - c.Green), 0, PixelCompare.Tolerance);
        Assert.InRange(Math.Abs(c.Green - c.Blue), 0, PixelCompare.Tolerance);
      }
    );
  }

  [Fact]
  public void A_cell_on_no_key_is_grey_in_the_tint() {
    using SKBitmap tinted = TintedSe(Wall(null), "", Scratch());
    List<SKColor> pixels = [.. Drawn(tinted)];
    Assert.NotEmpty(pixels);
    Assert.All(
      pixels,
      c => {
        Assert.InRange(Math.Abs(c.Red - c.Green), 0, PixelCompare.Tolerance);
        Assert.InRange(Math.Abs(c.Green - c.Blue), 0, PixelCompare.Tolerance);
      }
    );
  }

  [Fact]
  public void The_four_tinted_views_share_the_plain_views_size() {
    string dir = Scratch();
    SceneRendered rendered = Render(dir);
    Assert.Equal(4, rendered.Tinted.Count);
    foreach (string view in Corners) {
      using SKBitmap plain = SKBitmap.Decode(
        Path.Combine(dir, $"demo-plain-{view}.png")
      );
      using SKBitmap tint = SKBitmap.Decode(
        Path.Combine(dir, $"demo-tint-{view}.png")
      );
      Assert.Equal((plain.Width, plain.Height), (tint.Width, tint.Height));
    }
  }

  [Fact]
  public void A_port_with_a_null_face_is_read() {
    SceneFile scene = SceneFile.Parse(
      """
      {"format": "setup-scene/1", "setup": "x", "cells": [
        {"at": [0,0,0], "code": "a:b", "role": "port", "principal": [0,0,0],
         "port": {"face": null, "type": "mechanical", "joins": "M1"}}]}
      """
    );
    Assert.Equal(new ScenePort(null, "mechanical", "M1"), scene.Cells[0].Port);
  }

  [Theory]
  [InlineData("{\"code\": \"a:b\"}")]
  [InlineData("{\"at\": [0,0,0]}")]
  [InlineData("{\"at\": [0,0.5,0], \"code\": \"a:b\"}")]
  [InlineData("{\"at\": [0,\"y\",0], \"code\": \"a:b\"}")]
  public void A_cell_without_a_position_or_code_or_with_a_fractional_one_is_refused(
    string cell
  ) {
    Assert.Throws<InvalidDataException>(() =>
      SceneFile.Parse(
        $"{{\"format\": \"setup-scene/1\", \"setup\": \"x\", \"cells\": [{cell}]}}"
      )
    );
  }

  [Fact]
  public void An_unresolved_code_in_two_cells_is_listed_once() {
    SceneCell ghost(int x) =>
      new(
        new Offset(x, 0, 0),
        "demo:nothing",
        "g",
        "block",
        null,
        null,
        null,
        null,
        []
      );
    var scene = new SceneFile(
      "ghosts",
      null,
      null,
      [ghost(0), ghost(1)],
      [],
      []
    );
    SceneRendered rendered = SceneRender.Write(
      scene,
      BlockIndex.Build([DemoRoot]),
      Scratch(),
      "ghosts",
      ["se"]
    );
    Assert.Equal(["demo:nothing"], rendered.Unresolved);
  }

  private static readonly int[] Shades = [100, 75, 60];

  private static bool IsShadeOf(SKColor c, string hex) {
    SKColor key = SKColor.Parse(hex);
    return Shades.Any(s =>
      Math.Abs(c.Red - key.Red * s / 100) <= PixelCompare.Tolerance
      && Math.Abs(c.Green - key.Green * s / 100) <= PixelCompare.Tolerance
      && Math.Abs(c.Blue - key.Blue * s / 100) <= PixelCompare.Tolerance
    );
  }

  private static SceneFile Walled(string? joins, bool withPort) {
    List<SceneCell> cells =
    [
      new(
        new Offset(1, 0, 1),
        "exlib:structurefiller",
        null,
        "port",
        new Offset(1, 0, 1),
        null,
        null,
        withPort ? new ScenePort("s", "steam", joins) : null,
        []
      ),
    ];
    foreach (
      Offset at in new Offset[]
      {
        new(0, 0, 1),
        new(2, 0, 1),
        new(1, 0, 0),
        new(1, 0, 2),
        new(1, 1, 1),
      }
    )
      cells.Add(
        new(at, "demo:masonry", "guard", "block", null, null, null, null, [])
      );
    return new SceneFile(
      "walled",
      null,
      null,
      cells,
      joins == null ? [] : [new SceneNetwork("A", "steam", 0, [])],
      []
    );
  }

  // Runs `check` on the tinted views of a walled port and of the same scene with the port's
  // frame left out, then disposes both.
  private void WalledViews(
    string? joins,
    Action<string, SKBitmap, SKBitmap> check
  ) {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    string dir = Scratch();
    SceneRender.Write(Walled(joins, true), index, dir, "framed", Corners);
    SceneRender.Write(Walled(joins, false), index, dir, "bare", Corners);
    foreach (string view in Corners) {
      using SKBitmap framed = SKBitmap.Decode(
        Path.Combine(dir, $"framed-tint-{view}.png")
      );
      using SKBitmap bare = SKBitmap.Decode(
        Path.Combine(dir, $"bare-tint-{view}.png")
      );
      check(view, framed, bare);
    }
  }

  private static List<SKColor> Changed(SKBitmap framed, SKBitmap bare) {
    var pixels = new List<SKColor>();
    for (int y = 0; y < framed.Height; y++)
      for (int x = 0; x < framed.Width; x++)
        if (framed.GetPixel(x, y) != bare.GetPixel(x, y))
          pixels.Add(framed.GetPixel(x, y));
    return pixels;
  }

  [Fact]
  public void A_walled_unjoined_ports_frame_shows_red_in_each_tinted_view() =>
    WalledViews(
      null,
      (_, framed, bare) =>
        Assert.Contains(
          Changed(framed, bare),
          c => IsShadeOf(c, ScenePlan.Unjoined)
        )
    );

  [Fact]
  public void The_top_face_of_a_walled_port_cell_keeps_the_tint_inside_its_frame() =>
    WalledViews(
      null,
      (view, framed, bare) => {
        BlockIndex index = BlockIndex.Build([DemoRoot]);
        (JObject raw, _, _) = SceneRender.Compose(Walled(null, true), index);
        LoadedShape shape = SceneRender.Load(raw);
        Renderer.Canvas canvas = SceneRender.Canvases(
          shape,
          SceneRender.Marks(Walled(null, true), index),
          Corners
        )[view];
        var projection = new Renderer.Projection(
          canvas.XMin,
          canvas.YMax,
          4,
          Renderer.NamedViews[view]
        );
        (double centreCol, _) = projection.Screen(24, 16, 24);
        int checkedPoints = 0;
        foreach (
          (int dx, int dz) in new[] { (4, 4), (4, -4), (-4, 4), (-4, -4) }
        ) {
          (double col, double row) = projection.Screen(24 + dx, 16, 24 + dz);
          if (Math.Abs(col - centreCol) < 10)
            continue;
          checkedPoints++;
          SKColor inside = bare.GetPixel((int)col, (int)row);
          Assert.NotEqual(bare.GetPixel(0, 0), inside);
          Assert.True(
            inside == framed.GetPixel((int)col, (int)row),
            $"{view} at {col},{row}: {inside} vs {framed.GetPixel((int)col, (int)row)}"
          );
        }
        Assert.Equal(2, checkedPoints);
      }
    );

  [Fact]
  public void A_joined_ports_frame_is_its_networks_colour_and_not_red() {
    string joins = ScenePlan.KeyColors(Walled("A", true))["A"];
    WalledViews(
      "A",
      (_, framed, bare) => {
        List<SKColor> changed = Changed(framed, bare);
        Assert.Contains(changed, c => IsShadeOf(c, joins));
        Assert.DoesNotContain(changed, c => IsShadeOf(c, ScenePlan.Unjoined));
      }
    );
  }

  [Theory]
  [InlineData("ne")]
  [InlineData("nw")]
  [InlineData("se")]
  [InlineData("sw")]
  public void Each_tinted_view_equals_its_reference(string view) {
    string dir = Scratch();
    Render(dir);
    using SKBitmap actual = SKBitmap.Decode(
      Path.Combine(dir, $"demo-tint-{view}.png")
    );
    using SKBitmap expected = SKBitmap.Decode(
      FixturePath.Expected($"scene/demo-tint-{view}.png")
    );
    PixelCompare.Assert(expected, actual, $"demo-tint-{view}");
  }
}
