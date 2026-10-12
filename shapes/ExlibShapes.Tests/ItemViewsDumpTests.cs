using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Covers the <c>item-views</c> command: the <c>item-views/1</c> reader and the pictures
/// and manifest drawn from a dump.</summary>
public class ItemViewsDumpTests {
  private static string DemoRoot => FixturePath.Of("schematic");

  private static string Folder => FixturePath.Of("item-views");

  private static string Dump =>
    Path.Combine(Folder, "demo-gearpinion.views.json");

  private static string Scratch(string name) {
    string dir = Path.Combine(
      AppContext.BaseDirectory,
      "actual",
      "item-views",
      name
    );
    if (Directory.Exists(dir))
      Directory.Delete(dir, true);
    return dir;
  }

  [Fact]
  public void Reader_refuses_another_format() {
    string text = File.ReadAllText(Dump)
      .Replace("item-views/1", "item-views/2", StringComparison.Ordinal);
    InvalidDataException e = Assert.Throws<InvalidDataException>(() =>
      ViewsFile.Parse(text)
    );
    Assert.Equal(
      "not a views file: format item-views/2 is not item-views/1",
      e.Message
    );
    Assert.Equal("demo:gearpinion", ViewsFile.Load(Dump).View);
  }

  [Fact]
  public void Reader_reads_the_transforms_and_gates_of_the_fixture() {
    ViewsFile dump = ViewsFile.Load(Dump);
    Assert.Equal((3.0, 0.0, 0.0), dump.Gui.Translation);
    Assert.Equal((1.85, 1.85, 1.85), dump.Gui.ScaleXyz);
    Assert.Equal((0.0, -62.0, 18.0), dump.Tp.Rotation);
    Assert.True(dump.Ground.Rotate);
    Assert.Equal(["G1", "G2"], dump.Gates.Select(g => g.Gate));
    Assert.Equal([false], dump.Gates.Where(g => !g.Pass).Select(g => g.Pass));
  }

  [Fact]
  public void Reader_reads_the_held_clips_and_the_grip() {
    ViewsFile bare = ViewsFile.Load(Dump);
    Assert.Equal(new ViewsClips(null, null, null, null), bare.Clips);
    Assert.Equal([10.0749, 8.7533, 8.6509], bare.Grip);
    string text = File.ReadAllText(Dump)
      .Replace(
        "\"idle\": null",
        "\"idle\": \"spearidle\"",
        StringComparison.Ordinal
      )
      .Replace(
        "\"use\": null",
        "\"use\": \"spearhit\"",
        StringComparison.Ordinal
      );
    ViewsFile dump = ViewsFile.Parse(text);
    Assert.Equal(
      new ViewsClips("spearidle", null, null, "spearhit"),
      dump.Clips
    );
  }

  [Fact]
  public void Gui_silhouette_bounds_equal_the_footprint() {
    ViewsFile dump = ViewsFile.Load(Dump);
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    (LoadedShape shape, TextureSet textures, _) = ItemViews.Compose(
      ItemViews.ItemOf(dump, index),
      index
    );
    using SKBitmap layer = ItemViews.RenderGuiItem(shape, textures, dump.Gui);
    (int minCol, int maxCol, int minRow, int maxRow) = ItemViewsTests.Bounds(
      layer
    );
    int ppu = ItemViews.GuiPpu;
    // The centre slot's top left is at (48, 48) screen pixels of the 3 x 3 grid.
    double[] footprint = [.. dump.Footprint!];
    Assert.InRange(
      minCol,
      (48 + footprint[0]) * ppu - ppu,
      (48 + footprint[0]) * ppu + ppu
    );
    Assert.InRange(
      minRow,
      (48 + footprint[1]) * ppu - ppu,
      (48 + footprint[1]) * ppu + ppu
    );
    Assert.InRange(
      maxCol + 1,
      (48 + footprint[2]) * ppu - ppu,
      (48 + footprint[2]) * ppu + ppu
    );
    Assert.InRange(
      maxRow + 1,
      (48 + footprint[3]) * ppu - ppu,
      (48 + footprint[3]) * ppu + ppu
    );
  }

  // The client install the player shape comes from; CI provisions one.
  private static string RequireClientGame() {
    string? game = FixturePath.ClientGame("assets/survival/shapes");
    Skip.If(
      game is null
        && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == null,
      "a client install is absent"
    );
    Assert.NotNull(game);
    return game!;
  }

  [SkippableFact]
  public void Tp_hand_pixel_lies_on_the_grip() {
    ViewsFile dump = ViewsFile.Load(Dump);
    BlockIndex index = BlockIndex.Build([DemoRoot], RequireClientGame());
    (LoadedShape shape, TextureSet textures, _) = ItemViews.Compose(
      ItemViews.ItemOf(dump, index),
      index
    );
    ItemViews.Seraph seraph = ItemViews.LoadSeraph(index)!;
    string clip = ItemViews.HeldClip(
      seraph.Shape,
      dump.Clips?.Idle,
      dump.Clips?.Ready
    )!;
    Renderer.RenderPart[] parts = ItemViews.TpParts(
      shape,
      textures,
      dump.Tp,
      seraph,
      clip
    );
    (double X, double Y, double Z) hand = Renderer
      .WorldMatricesD(seraph.Shape, Poses.PoseAt(seraph.Shape, clip, 0))[
        seraph.Shape.Find("ItemAnchor")!.Path
      ]
      .Mul(0, 0, 0);
    double[] grip = [.. dump.Grip!];
    foreach (View view in ItemViews.TpAngles) {
      Renderer.Projection projection = Renderer.Project(
        parts,
        view,
        ItemViews.TpPpu
      );
      (double gx, double gy, double gz) = parts[1]
        .Model.Mul(grip[0], grip[1], grip[2]);
      (double col, double row) = projection.Screen(gx, gy, gz);
      (double handCol, double handRow) = projection.Screen(
        hand.X,
        hand.Y,
        hand.Z
      );
      Assert.InRange(col, handCol - 1, handCol + 1);
      Assert.InRange(row, handRow - 1, handRow + 1);
    }
  }

  [Fact]
  public void The_tp_view_of_a_dump_without_the_player_shape_is_a_warning() {
    string empty = Scratch("no-game-install");
    Directory.CreateDirectory(empty);
    BlockIndex index = BlockIndex.Build([DemoRoot], empty);
    JObject manifest = ItemViews.WriteDump(
      Dump,
      index,
      Scratch("no-player"),
      24,
      ["tp"]
    );
    Assert.Empty((JArray)manifest["files"]!);
    Assert.Equal(
      ["demo:gearpinion: the player shape is not found; tp not drawn"],
      manifest["warnings"]!.Select(w => (string)w!)
    );
  }

  [Fact]
  public void Command_writes_views_and_manifest() {
    string dir = Scratch("command");
    var stdout = new StringWriter();
    Assert.Equal(
      0,
      Program.RunItemViews(
        [Folder, "--out=" + dir, "--roots=" + DemoRoot],
        stdout
      )
    );
    string[] lines = stdout
      .ToString()
      .Split(
        '\n',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
      );
    Assert.Equal(
      [
        Path.Combine(dir, "demo-gearpinion-gui.png"),
        Path.Combine(dir, "demo-gearpinion-ground.png"),
        Path.Combine(dir, "demo-gearpinion-iso.png"),
        Path.Combine(dir, "demo-gearpinion.json"),
      ],
      lines
    );
    foreach (string png in lines[..3])
      Assert.True(File.Exists(png), png);
    JObject written = JObject.Parse(
      File.ReadAllText(Path.Combine(dir, "demo-gearpinion.json"))
    );
    written["files"] = new JArray(
      written["files"]!.Select(f => (JToken)Path.GetFileName((string)f!))
    );
    JObject expected = JObject.Parse(
      File.ReadAllText(FixturePath.Expected("item-views/demo-gearpinion.json"))
    );
    Assert.True(JToken.DeepEquals(expected, written), written.ToString());
  }

  [Fact]
  public void A_dump_whose_shape_is_not_found_warns_and_draws_nothing() {
    string dir = Scratch("noshape");
    string text = File.ReadAllText(Dump)
      .Replace(
        "demo:item/gearpinion",
        "demo:item/absent",
        StringComparison.Ordinal
      );
    string file = Path.Combine(dir, "x.views.json");
    Directory.CreateDirectory(dir);
    File.WriteAllText(file, text);
    JObject manifest = ItemViews.WriteDump(
      file,
      BlockIndex.Build([DemoRoot]),
      dir,
      24,
      ["gui"]
    );
    Assert.Empty((JArray)manifest["files"]!);
    Assert.Equal(
      "demo:gearpinion: shape demo:item/absent not found; gui not drawn",
      (string)Assert.Single((JArray)manifest["warnings"]!)!
    );
  }
}
