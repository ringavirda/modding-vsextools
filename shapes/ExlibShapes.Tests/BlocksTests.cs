using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Covers BlockIndex's resolution of a selector to its blocktype file, including the
/// flywheel's per-variant fillerOffsets and the deterministic, warned resolution of a selector
/// matching more than one source file.</summary>
public class BlocksTests {
  private static string DemoRoot => FixturePath.Of("schematic");

  // The family workspace's own sibling checkouts - present only on a contributor's machine that
  // has cloned exlib/exmods next to this repo, absent from a bare checkout of this repo alone.
  // A test naming one of these skips (an early return, xunit 2 having no built-in
  // skip-with-reason) rather than failing.
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
      return; // skips when the sibling exlib checkout is absent
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
    // "game:cobblestone-*" matches variants from both. Resolve must pick the same file every run:
    // stone/cobble/cobblestone.json, the file whose own base code equals the selector's text
    // before the "*", not the coral variant a first-match rule would give; and record the
    // ambiguity naming both files.
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
  public void Legacy_and_sample_trees_contribute_blocktypes_and_their_own_assets() {
    // The published old mods live under legacy/<mod>/assets and a single-mod repo's samples under
    // samples/<project>/ with their goldens - the trees the wiki's first figures found missing.
    BlockIndex index = BlockIndex.Build([DemoRoot]);

    ResolvedBlock? gate = index.Resolve("old:gate-shut");
    Assert.NotNull(gate);
    Assert.EndsWith(
      Path.Combine("legacy", "old", "assets", "old", "shapes", "block", "gate.json"),
      gate!.ShapePath
    );

    ResolvedBlock? post = index.Resolve("sample:post-*");
    Assert.NotNull(post);
    Assert.Equal("sample:post-short", post!.Code);
    Assert.EndsWith(
      Path.Combine("samples", "Post", "assets", "sample", "shapes", "block", "post.json"),
      post.ShapePath
    );
  }

  [Fact]
  public void Malformed_blocktype_file_warns_naming_its_path_instead_of_vanishing() {
    string root = Path.Combine(Path.GetTempPath(), "exlib-shapes-" + Guid.NewGuid().ToString("N"));
    string blocktypesDir = Path.Combine(root, "mods", "broken", "assets", "broken", "blocktypes");
    Directory.CreateDirectory(blocktypesDir);
    string brokenFile = Path.Combine(blocktypesDir, "broken.json");
    File.WriteAllText(brokenFile, "{ this is not json");
    try {
      BlockIndex index = BlockIndex.Build([root]);
      Assert.Null(index.Resolve("broken:anything"));
      Assert.Contains(index.ParseWarnings, w => w.Contains(brokenFile));
    } finally {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public void Two_roots_linking_one_install_index_its_files_once() {
    // exlib and exmods each link .game to the workspace's one install; a selector matching a
    // vanilla file must not come out ambiguous between that file's two spellings.
    string linked = Path.Combine(Path.GetTempPath(), "exlib-shapes-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(linked);
    Directory.CreateSymbolicLink(Path.Combine(linked, ".game"), Path.Combine(GameRoot, ".game"));
    try {
      BlockIndex index = BlockIndex.Build([GameRoot, linked]);
      Assert.NotNull(index.Resolve("game:cobblestone-andesite"));
      Assert.Empty(index.Ambiguities);

      index.Resolve("game:cobblestone-*");
      Assert.Equal(2, index.Ambiguities["game:cobblestone-*"].Count);
    } finally {
      Directory.Delete(linked, true);
    }
  }

  [Fact]
  public void A_code_declared_on_both_sides_of_legacy_follows_the_side_the_index_serves() {
    // legacy/old/assets/demo/blocktypes/wall.json declares the same "wall" as the current
    // mods/demo tree with another shape: two versions of one mod, not an ambiguity to warn about.
    BlockIndex current = BlockIndex.Build([DemoRoot]);
    ResolvedBlock? wall = current.Resolve("demo:wall-north");
    Assert.NotNull(wall);
    Assert.EndsWith(Path.Combine("demo", "shapes", "block", "wall.json"), wall!.ShapePath);
    Assert.Empty(current.Ambiguities);

    BlockIndex legacy = BlockIndex.Build([DemoRoot], legacyFirst: true);
    ResolvedBlock? old = legacy.Resolve("demo:wall-north");
    Assert.NotNull(old);
    Assert.EndsWith(Path.Combine("old", "shapes", "block", "gate.json"), old!.ShapePath);
    Assert.Empty(legacy.Ambiguities);

    Assert.True(BlockIndex.UnderLegacyTree(Path.Combine(DemoRoot, "legacy", "old", "assets", "demo", "blocktypes", "wall.json")));
    Assert.False(BlockIndex.UnderLegacyTree(Path.Combine(DemoRoot, "mods", "demo", "assets", "demo", "blocktypes", "wall.json")));
  }

  [Fact]
  public void Air_admitting_and_monolithic_multiblock_selectors_are_empty_space() {
    // ppex's boilers fill with "game:air*", smex's blast furnace door names the block the game
    // creates in code for a door's upper cell; neither has a blocktype file to find.
    BlockIndex index = BlockIndex.Build([GameRoot]);
    foreach (string selector in new[] { "game:air*", "game:multiblock-monolithic-0-p1-0", "air" }) {
      Assert.Null(index.Representative(selector));
      Assert.True(index.Optional(selector));
    }
    // Vanilla's stone coffin names the door first: the resolving alternative is a real block,
    // whose orientation axis comes from a variant group naming only its worldproperties file.
    string coffinDoor = "game:@(irondoor-.*-up-.*|multiblock-monolithic-0-p1-0)";
    Assert.Equal("game:irondoor-north-up-closed-left", index.Representative(coffinDoor)!.Code);
    Assert.False(index.Optional(coffinDoor));
    Assert.Empty(index.Ambiguities);
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
      return; // skips when the sibling exmods checkout is absent
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
