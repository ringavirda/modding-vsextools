using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
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
    SceneRender.Write(ghost, BlockIndex.Build([DemoRoot]), dir, "ghost", ["se"]);
    using SKBitmap bitmap = SKBitmap.Decode(Path.Combine(dir, "ghost-plain-se.png"));
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
    List<int> hidden = [
      .. scene.Cells
        .Select((c, i) => (c, i))
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
    (JObject raw, _, _) = SceneRender.Compose(scene, BlockIndex.Build([DemoRoot]));
    List<string> names = [.. Names(raw)];
    List<SceneCell> cells = [.. scene.Cells];
    int under = cells.FindIndex(c => c.Role == "fluid" && c.At == new Offset(4, 0, 4));
    int alone = cells.FindIndex(c => c.Role == "fluid" && c.At == new Offset(6, 0, 5));
    Assert.DoesNotContain($"c{under}", names);
    Assert.Contains($"c{alone}", names);
  }
}
