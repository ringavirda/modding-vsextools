using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Ported from <c>vsshape/tests/test_blocks.py</c>, plus the two rulings the Python leaves
/// open (T6): the flywheel's per-variant fillerOffsets and the deterministic, warned resolution of
/// a selector matching more than one source file.</summary>
public class BlocksTests {
  private static string DemoRoot => FixturePath.Of("schematic");

  // The family workspace's own sibling checkouts - present only on a contributor's machine that
  // has cloned exlib/exmods next to this repo, never in extools' own CI (a bare checkout of this
  // repo alone). A test naming one of these skips (an early return, xunit 2 having no built-in
  // skip-with-reason) rather than failing, the same as the Python's own pytest.skip guard.
  private static string? ExlibRoot => FixturePath.Workspace("exlib");
  private static string? BlastcoreGolden =>
    FixturePath.Workspace("exmods/mods/iiex/tests/goldens/iiex/blocktypes/furnace/blastcore.json");

  // This checkout's own .game, provisioned by every contributor and by CI alike - unlike the
  // family workspace above, its absence is a real failure, not something to skip past.
  private static string GameRoot => FixturePath.RepoRoot;

  [Fact]
  public void Demo_wall_north_resolves_with_its_shapeByType_rotation() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    ResolvedBlock? block = index.Resolve("demo:wall-north");
    Assert.NotNull(block);
    Assert.Equal("demo:wall-north", block!.Code);
    Assert.Equal(0, block.RotateY);
    Assert.NotNull(block.ShapePath);
    Assert.True(File.Exists(block.ShapePath));
  }

  [Fact]
  public void Demo_wall_east_carries_its_own_rotation() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    ResolvedBlock? block = index.Resolve("demo:wall-east");
    Assert.Equal(270, block!.RotateY);
  }

  [Fact]
  public void Wildcard_resolves_to_the_first_declared_variant() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    ResolvedBlock? block = index.Resolve("demo:wall-*");
    Assert.Equal("demo:wall-north", block!.Code);
  }

  [Fact]
  public void Air_alternative_wins_first_and_is_optional() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    const string selector = "*:@(air|demo:wall-south)";
    Assert.Null(index.Resolve(selector));
    Assert.True(index.Optional(selector));
  }

  [Fact]
  public void Unknown_code_returns_null_and_is_not_optional() {
    BlockIndex index = BlockIndex.Build([DemoRoot]);
    Assert.Null(index.Resolve("demo:nosuchblock"));
    Assert.False(index.Optional("demo:nosuchblock"));
  }

  [Fact]
  public void Structurefiller_resolves_through_the_exlib_checkout() {
    if (ExlibRoot is not { } exlibRoot)
      return; // needs the family workspace's own exlib checkout, dev machine only
    BlockIndex index = BlockIndex.Build([exlibRoot]);
    ResolvedBlock? block = index.Resolve("exlib:structurefiller");
    Assert.NotNull(block);
    Assert.NotNull(block!.ShapePath);
    Assert.True(File.Exists(block.ShapePath));
  }

  [Fact]
  public void A_vanilla_code_resolves_when_the_game_root_is_present() {
    Assert.True(Directory.Exists(GameRoot), "the workspace always carries .game; this must fail, not skip");
    BlockIndex index = BlockIndex.Build([GameRoot]);
    // A plain cube block ships no shape file of its own (the engine draws a default unit cube), so
    // this only pins that the code resolves and carries a texture, not a shape_path.
    ResolvedBlock? block = index.Resolve("game:cobblestone-andesite");
    Assert.NotNull(block);
    Assert.True(block!.Textures.ContainsKey("all"));
  }

  [Fact]
  public void SkipVariants_drops_the_listed_state_from_the_game_index() {
    Assert.True(Directory.Exists(GameRoot), "the workspace always carries .game; this must fail, not skip");
    BlockIndex index = BlockIndex.Build([GameRoot]);
    // mudbrickslab.json declares skipVariants: ["*-up-snow"]; the engine never registers it.
    Assert.Null(index.Resolve("game:mudbrickslab-dark-up-snow"));
    Assert.NotNull(index.Resolve("game:mudbrickslab-dark-up-free"));
  }

  [Fact]
  public void Wildcard_selector_spanning_two_files_is_deterministic_and_warns() {
    // aquatic/cobble-coral.json and stone/cobble/cobblestone.json both declare code "cobblestone";
    // "game:cobblestone-*" matches variants from both. Resolve must pick the same file every run,
    // pick stone/cobble/cobblestone.json (the file whose own base code equals the selector's text
    // before the "*" - the tie-break T6 adds, not the coral variant a first-match rule would give),
    // and record the ambiguity naming both files.
    BlockIndex index = BlockIndex.Build([GameRoot]);
    ResolvedBlock? first = index.Resolve("game:cobblestone-*");
    Assert.NotNull(first);
    Assert.StartsWith("game:cobblestone-", first!.Code);
    Assert.DoesNotContain("coral", first.Code);

    BlockIndex second = BlockIndex.Build([GameRoot]);
    ResolvedBlock? repeat = second.Resolve("game:cobblestone-*");
    Assert.Equal(first.Code, repeat!.Code);

    Assert.True(index.Ambiguities.TryGetValue("game:cobblestone-*", out IReadOnlyList<string>? files));
    Assert.Equal(2, files!.Count);
    Assert.Contains(files, f => f.EndsWith("cobble-coral.json"));
    Assert.Contains(files, f => f.EndsWith("cobble/cobblestone.json"));
  }

  [Fact]
  public void An_exact_selector_that_is_not_ambiguous_records_no_warning() {
    BlockIndex index = BlockIndex.Build([GameRoot]);
    index.Resolve("game:cobblestone-andesite");
    Assert.Empty(index.Ambiguities);
  }

  [Fact]
  public void Resolve_agrees_with_every_selector_of_the_blastcore_golden() {
    if (BlastcoreGolden is not { } golden)
      return; // needs the family workspace's own exmods checkout, dev machine only
    // The blastcore golden's own blockNumbers cover a full code, a `*` wildcard, and the
    // `@(air|...)` regex form - every selector shape BlockIndex.Resolve supports.
    IReadOnlyList<string> roots = BlockIndex.DefaultRoots(golden);
    Assert.NotEmpty(roots);
    BlockIndex index = BlockIndex.Build(roots);
    Layout layout = Layout.Load(golden);

    foreach (string selector in layout.Numbers.Values) {
      bool optional = index.Optional(selector);
      ResolvedBlock? block = index.Resolve(selector);
      // Every selector this golden carries names a real block or is explicitly optional (air) -
      // nothing in it is a dangling reference this index cannot find at all.
      Assert.True(block != null || optional, $"{selector}: neither resolved nor optional");
    }
  }
}
