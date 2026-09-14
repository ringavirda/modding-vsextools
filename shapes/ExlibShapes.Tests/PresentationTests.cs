using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>
/// Ties the drawn facing to the camera: a machine is placed facing away from the player, so a
/// picture of the variant's own facing shows the back of it. The fixture the rule is pinned on has
/// an asymmetric footprint, which is what tells a half turn from no turn at all.
/// </summary>
public class PresentationTests {
  private static string DemoRoot => FixturePath.Of("schematic");
  private static string Mega => FixturePath.Of("schematic/mods/demo/assets/demo/blocktypes/mega.json");
  private static string Furnace => FixturePath.Of("schematic/mods/demo/assets/demo/blocktypes/furnace.json");

  private static readonly string[] Sides = ["north", "east", "south", "west"];

  // The four mods' own blocktype trees, and whether each is the published old side of a code the
  // family also declares.
  private static readonly (string Tree, bool Legacy)[] StructureTrees = [
    ("exmods/legacy/smex/assets", true),
    ("exmods/legacy/ppex/assets", true),
    ("exmods/mods/iiex/tests/goldens", false),
    ("exmods/mods/siex/tests/goldens", false),
  ];

  // One step toward a side, stated here rather than read from Presentation so the facts below
  // measure the convention instead of echoing it: Vintage Story puts north at -Z and east at +X.
  private static Offset Step(string side) =>
    side switch {
      "north" => new Offset(0, 0, -1),
      "east" => new Offset(1, 0, 0),
      "south" => new Offset(0, 0, 1),
      "west" => new Offset(-1, 0, 0),
      _ => throw new ArgumentOutOfRangeException(nameof(side), side, "not a horizontal side"),
    };

  // How far a point on the ground lies toward the camera a page's main picture is drawn from:
  // positive in front of the principal cell, negative behind it.
  private static double TowardTheCamera(double x, double z) {
    Vector3 eye = Renderer.Eye(Renderer.NamedViews[Presentation.ViewName]);
    return x * eye.X + z * eye.Z;
  }

  private static double TowardTheCamera(Offset cell) => TowardTheCamera(cell.X, cell.Z);

  // The centre of a layout's footprint, over every cell it reserves.
  private static (double X, double Z) Centre(Layout layout) {
    IReadOnlyList<Offset> cells = Footprint.Reserved(layout);
    return (
      (cells.Min(c => c.X) + cells.Max(c => c.X)) / 2.0,
      (cells.Min(c => c.Z) + cells.Max(c => c.Z)) / 2.0
    );
  }

  // How far a cell reaches toward one side, in cells.
  private static int Along(Offset cell, string side) {
    Offset step = Step(side);
    return cell.X * step.X + cell.Z * step.Z;
  }

  [Fact]
  public void The_drawn_facing_is_the_one_whose_front_leans_furthest_toward_the_camera() {
    double best = double.NegativeInfinity;
    foreach (string side in Sides)
      best = Math.Max(best, TowardTheCamera(Step(Presentation.Front(side)!)));
    Assert.True(best > 0, "no facing presents its front to the camera at all");
    Assert.Equal(best, TowardTheCamera(Step(Presentation.Front(Presentation.Facing)!)), 5);
  }

  [Fact]
  public void A_side_and_its_letter_name_the_same_front() {
    Assert.Equal("south", Presentation.Front("north"));
    Assert.Equal("south", Presentation.Front("n"));
    Assert.Equal("east", Presentation.Front("west"));
    // An axis is not a facing, so it has no front.
    Assert.Null(Presentation.Front("ns"));
    Assert.Null(Presentation.Front(null));
  }

  [Fact]
  public void A_megablock_draws_the_variant_that_reserves_its_body_away_from_the_camera() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    Variant drawn = BlockIndex.Facing(index.VariantsOf(Mega), Presentation.Facing)!;
    Assert.Equal("demo:mega-north", drawn.Code);
    string front = Presentation.FrontOf(drawn)!;
    Assert.Equal("south", front);
    Assert.True(TowardTheCamera(Step(front)) > 0, $"the {front} side is not the camera's");

    // The body reaches one cell past the principal, so the near end of the picture is the
    // principal's own cell - the front. A half turn either way puts the body in front of it.
    Layout placed = Footprint.Placed(Layout.Load(Mega, drawn.Path), index);
    Offset filler = Assert.Single(placed.Fillers);
    Assert.True(Along(filler, front) < 0, $"the reserved cell {filler} stands between the body and the camera");
  }

  [Fact]
  public void A_structure_with_no_facing_data_turns_its_anchor_onto_the_camera_facing_edge() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    Layout layout = Footprint.Placed(Layout.Load(Furnace), index);
    // Authored with the body south of the starter block, which is the back of it.
    Assert.Equal([0, 1, 2], layout.Cells.Select(c => c.Z));

    Presentation.Staged staged = Presentation.Stage(layout, null);
    Assert.Equal(180, staged.Angle);
    Assert.Equal("south", staged.Front);
    Assert.Equal([0, -1, -2], staged.Layout.Cells.Select(c => c.Z));
    (double cx, double cz) = Centre(staged.Layout);
    Assert.True(
      TowardTheCamera(staged.Layout.Anchor) > TowardTheCamera(cx, cz),
      "the anchor cell is not nearer the camera than the footprint's centre"
    );
  }

  [Fact]
  public void An_anchor_walled_in_on_both_axes_keeps_the_variant_s_own_front() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    // The kiln's starter block stands in the middle of a 3x3, so no turn puts it on an edge.
    Layout kiln = Layout.Load(FixturePath.Of("schematic/kiln.json"));
    Assert.Equal((0, null), Presentation.AnchorTurn(kiln));

    Variant mega = BlockIndex.Facing(index.VariantsOf(Mega), Presentation.Facing)!;
    Presentation.Staged staged = Presentation.Stage(kiln, mega);
    Assert.Equal(0, staged.Angle);
    Assert.Equal(Presentation.FrontOf(mega), staged.Front);
  }

  [SkippableFact]
  public void Every_structure_of_the_four_mods_stands_its_anchor_nearest_the_camera() {
    string? exmods = FixturePath.Workspace("exmods");
    Skip.If(exmods is null, "the sibling exmods checkout is absent");

    var failures = new List<string>();
    var report = new System.Text.StringBuilder();
    var behind = new List<string>();
    int checkedCount = 0;
    foreach ((string tree, bool legacy) in StructureTrees) {
      string root = Path.Combine(Path.GetDirectoryName(exmods!)!, tree.Replace('/', Path.DirectorySeparatorChar));
      Skip.If(!Directory.Exists(root), $"{tree} is absent");
      BlockIndex index = BlockIndex.Build(BlockIndex.DefaultRoots(root), null, legacy);

      foreach (string file in Structures(root)) {
        IReadOnlyList<Variant> variants = index.VariantsOf(file);
        Variant? drawn = BlockIndex.Facing(variants, Presentation.Facing) ?? variants.FirstOrDefault();
        Presentation.Staged staged =
          Presentation.Stage(Footprint.Placed(Layout.Load(file, drawn?.Path), index), drawn);
        (double cx, double cz) = Centre(staged.Layout);
        double anchor = TowardTheCamera(staged.Layout.Anchor);
        double centre = TowardTheCamera(cx, cz);
        string code = drawn?.Code ?? Path.GetFileNameWithoutExtension(file);
        checkedCount++;
        bool walled = Presentation.AnchorTurn(staged.Layout).Side == null;
        report.AppendLine(
          $"{code} turn={staged.Angle} front={staged.Front ?? "-"} anchor={anchor:0.###} "
            + $"centre={centre:0.###}{(walled ? " walled-in" : "")}"
        );
        if (anchor > centre)
          continue;
        if (walled)
          behind.Add(code);
        else
          failures.Add($"{code} turns {staged.Angle}deg and still stands its anchor behind its own centre");
      }
    }

    string actualDir = Path.Combine(AppContext.BaseDirectory, "actual");
    Directory.CreateDirectory(actualDir);
    File.WriteAllText(Path.Combine(actualDir, "structure-fronts.txt"), report.ToString());

    Assert.True(checkedCount >= 16, $"only {checkedCount} structures were reached");
    Assert.True(failures.Count == 0, string.Join("\n", failures));
    Assert.Equal<IEnumerable<string>>(AnchorBehind, [.. behind.OrderBy(c => c, StringComparer.Ordinal)]);
  }

  // The one structure whose starter block is walled in on both axes and whose body then leans
  // toward the camera: no turn puts that block in front of its own footprint, so the drawing keeps
  // the variant's own facing. Every other structure stands its starter block nearest the camera.
  private static readonly string[] AnchorBehind = ["iiex:furnace-cupolacore-tier1-n"];

  // Every blocktype file under `root` that carries a structure table.
  private static IEnumerable<string> Structures(string root) {
    foreach (
      string file in Directory
        .EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
        .Where(f => f.Split(Path.DirectorySeparatorChar).Contains("blocktypes"))
        .OrderBy(f => f, StringComparer.Ordinal)
    ) {
      Layout layout;
      try {
        layout = Layout.Load(file);
      } catch (Exception) {
        continue; // not a footprint (LayoutError) or not a blocktype file at all
      }
      if (layout.Cells.Count > 0)
        yield return file;
    }
  }

  [SkippableFact]
  public void The_iiex_cornish_boiler_takes_its_front_from_the_frame_that_places_its_cells() {
    string? file = FixturePath.Workspace("exmods/mods/iiex/tests/goldens/iiex/blocktypes/boiler/cornish.json");
    Skip.If(file is null, "the sibling exmods checkout is absent");
    BlockIndex index = BlockIndex.Build(BlockIndex.DefaultRoots(file!));
    Variant drawn = BlockIndex.Facing(index.VariantsOf(file!), Presentation.Facing)!;
    Assert.Equal("iiex:boilercornish-n", drawn.Code);

    // BlockBoiler's own half turn lands in the frame fit, not in the facing: the facing convention
    // alone answers south while the cells it places run the other way.
    Layout placed = Footprint.Placed(Layout.Load(file!, drawn.Path), index);
    Assert.Equal("south", Presentation.FrontOf(drawn));
    Assert.All(placed.Fillers, f => Assert.True(f.Z >= 0, $"the body reaches {f}, south of the anchor"));

    Presentation.Staged staged = Presentation.Stage(placed, drawn);
    Assert.Equal(180, staged.Angle);
    Assert.Equal("south", staged.Front);
    // Drawn that way round the body lies wholly behind the firebox end, which is the picture.
    Assert.All(staged.Layout.Fillers, f => Assert.True(f.Z <= 0, $"the body reaches {f}, in front of the anchor"));
  }

  [SkippableFact]
  public void The_cornish_boiler_draws_its_firebox_toward_the_camera() {
    string? file = FixturePath.Workspace("exmods/legacy/ppex/assets/ppex/blocktypes/boiler/cornish.json");
    Skip.If(file is null, "the sibling exmods checkout is absent");
    BlockIndex index = BlockIndex.Build(BlockIndex.DefaultRoots(file!), null, BlockIndex.UnderLegacyTree(file!));
    Variant drawn = BlockIndex.Facing(index.VariantsOf(file!), Presentation.Facing)!;
    Assert.Equal("ppex:boilercornish-north", drawn.Code);
    string front = Presentation.FrontOf(drawn)!;
    Assert.True(TowardTheCamera(Step(front)) > 0, $"the {front} side is not the camera's");

    // fuelOffset names the firebox cell in the frame the file declares; the drawing turns that
    // frame onto the drawn mesh, which is the half turn BlockBoiler makes in C#.
    Layout layout = Layout.Load(file!, drawn.Path);
    JToken fuel = drawn.Raw["attributes"]!["fuelOffset"]!;
    var declared = new Offset((int)fuel["x"]!, (int)fuel["y"]!, (int)fuel["z"]!);
    Offset firebox = Layout.RotateOffset(declared, Footprint.FrameAngle(layout, index));

    // The body lies wholly behind the firebox, so the picture is of the firebox end, not the flue.
    Layout placed = Footprint.Placed(layout, index);
    Assert.All(
      placed.Fillers,
      f => Assert.True(Along(f, front) < Along(firebox, front), $"the body reaches {f}, past the firebox")
    );
  }
}
