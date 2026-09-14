using System;
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

  private static readonly string[] Sides = ["north", "east", "south", "west"];

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

  // How far a cell lies toward the camera a page's main picture is drawn from: positive in front
  // of the principal cell, negative behind it.
  private static double TowardTheCamera(Offset cell) {
    Vector3 eye = Renderer.Eye(Renderer.NamedViews[Presentation.ViewName]);
    return cell.X * eye.X + cell.Z * eye.Z;
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
