using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Ported from <c>vsshape/tests/test_layout.py</c>, same fixture and expected numbers,
/// plus the megablock filler-by-type case the Python leaves open (T6).</summary>
public class LayoutTests {
  private static string Fixture => FixturePath.Of("schematic/kiln.json");

  // The family workspace's own exmods checkout - dev machine only, same as BlocksTests' own
  // workspace-dependent facts; the two facts naming it skip (an early return) when it is absent.
  private static string? Flywheel =>
    FixturePath.Workspace("exmods/mods/iiex/tests/goldens/iiex/blocktypes/mpenergy/flywheel.json");

  [Fact]
  public void Load_reads_cells_numbers_fillers_and_anchor() {
    Layout layout = Layout.Load(Fixture);
    Assert.Equal(18, layout.Cells.Count);
    Assert.Equal(
      new Dictionary<int, string> { [1] = "game:claybricks-fire-*", [2] = "game:brickslabs-fire-south-free" },
      layout.Numbers
    );
    Assert.Equal(
      new HashSet<Offset> { new(2, 0, 0), new(-2, 0, 0) },
      new HashSet<Offset>(layout.Fillers)
    );
    Assert.Equal(new Offset(0, 0, 0), layout.Anchor);
    Cell anchorCell = layout.Cells.First(c => new Offset(c.X, c.Y, c.Z) == layout.Anchor);
    Assert.Equal(2, anchorCell.Number);
  }

  [Fact]
  public void Load_reads_roles() {
    Layout layout = Layout.Load(Fixture);
    Assert.Equal(
      new Dictionary<string, IReadOnlyList<Offset>> { ["chamber"] = [new(0, 1, 1)] },
      layout.Roles
    );
  }

  [Fact]
  public void Rotated_turns_every_cell_offset_by_the_game_convention() {
    Layout layout = Layout.Load(Fixture);
    Layout rotated = layout.Rotated(90);
    // north 0, west 90, south 180, east 270: (x, y, z) -> 90:(z, y, -x); the fixture's 3x3 layer
    // is rotation-invariant as a set, so pin each cell by index instead of checking membership.
    for (int i = 0; i < layout.Cells.Count; i++) {
      Cell original = layout.Cells[i];
      Cell turned = rotated.Cells[i];
      Assert.Equal((original.Z, original.Y, -original.X), (turned.X, turned.Y, turned.Z));
      Assert.Equal(original.Number, turned.Number);
    }
  }

  [Fact]
  public void Rotated_turns_the_oriented_selector_south_to_east() {
    Layout layout = Layout.Load(Fixture);
    Layout rotated = layout.Rotated(90);
    Assert.Equal("game:brickslabs-fire-east-free", rotated.Numbers[2]);
    Assert.Equal("game:claybricks-fire-*", rotated.Numbers[1]);
  }

  [Fact]
  public void Rotated_twice_composes_like_one_rotation_by_the_sum() {
    Layout layout = Layout.Load(Fixture);
    Layout twice = layout.Rotated(90).Rotated(90);
    Layout once = layout.Rotated(180);
    Assert.Equal("game:brickslabs-fire-north-free", twice.Numbers[2]);
    Assert.Equal("game:brickslabs-fire-north-free", once.Numbers[2]);
  }

  [Fact]
  public void Rotated_turns_a_connector_key_and_its_offsets() {
    Layout layout = Layout.Load(Fixture);
    Layout rotated = layout.Rotated(90);
    Assert.Equal(["w"], rotated.Connectors.Keys);
    Assert.Equal(new Offset[] { new(-2, 0, 0) }, rotated.Connectors["w"]);
  }

  [Fact]
  public void Rotated_turns_filler_offsets_too() {
    Layout layout = Layout.Load(Fixture);
    Layout rotated = layout.Rotated(90);
    // pinned by index, not just as a set, so a 90/270 branch swap (which would only permute which
    // filler maps where, not the resulting set) still fails this test
    Assert.Equal([new Offset(0, 0, -2), new Offset(0, 0, 2)], rotated.Fillers);
  }

  [Fact]
  public void Rotated_turns_role_offsets_too() {
    Layout layout = Layout.Load(Fixture);
    Layout rotated = layout.Rotated(90);
    Assert.Equal(
      new Dictionary<string, IReadOnlyList<Offset>> { ["chamber"] = [new(1, 1, 0)] },
      rotated.Roles
    );
  }

  [Fact]
  public void Load_without_the_attribute_or_fillers_raises_layout_error() {
    string bad = Path.Combine(Path.GetTempPath(), "nostructure-" + Path.GetRandomFileName() + ".json");
    File.WriteAllText(bad, "{\"code\": \"empty\", \"attributes\": {}}");
    try {
      LayoutError ex = Assert.Throws<LayoutError>(() => Layout.Load(bad));
      Assert.Contains(bad, ex.Message);
    } finally {
      File.Delete(bad);
    }
  }

  [Fact]
  public void Load_of_a_megablock_with_no_structure_table_uses_the_first_attributesByType_filler() {
    if (Flywheel is not { } flywheel)
      return; // needs the family workspace's own exmods checkout, dev machine only
    // flywheel.json carries no multiblockStructure at all - just an attributesByType fillerOffsets
    // per size variant (IFillerHost) - so the default (no variant named) picks the first declared
    // entry, "*-normal-*", whose footprint is 8 cells.
    Layout layout = Layout.Load(flywheel);
    Assert.Empty(layout.Cells);
    Assert.Empty(layout.Numbers);
    Assert.Equal(8, layout.Fillers.Count);
  }

  [Fact]
  public void Load_of_a_megablock_picks_the_attributesByType_entry_the_named_variant_matches() {
    if (Flywheel is not { } flywheel)
      return; // needs the family workspace's own exmods checkout, dev machine only
    Layout normal = Layout.Load(flywheel, "mpenergy-flywheel-normal-ns");
    Layout large = Layout.Load(flywheel, "mpenergy-flywheel-large-we");
    Assert.Equal(8, normal.Fillers.Count);
    Assert.Equal(49, large.Fillers.Count);
  }

  [Fact]
  public void Load_of_a_megablock_derives_layers_and_bounds_from_fillers_alone() {
    if (Flywheel is not { } flywheel)
      return; // needs the family workspace's own exmods checkout, dev machine only
    // Layers()/Bounds() must not depend on Cells: a filler-only megablock has none at all, and the
    // schematic CLI's plan/cut loops iterate Layers() to decide what to write.
    Layout layout = Layout.Load(flywheel);
    Assert.Equal([0, 1, 2], layout.Layers());
    (Offset lo, Offset hi) = layout.Bounds();
    Assert.Equal(new Offset(-1, 0, 0), lo);
    Assert.Equal(new Offset(1, 2, 0), hi);
  }
}
