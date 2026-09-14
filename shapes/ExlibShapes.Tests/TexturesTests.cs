using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>
/// Covers texture resolution against a tiny self-contained fixture tree
/// (<c>fixtures/textures/repo/</c>) rather than the real workspace: a real game install's server
/// build ships no <c>assets/survival/textures</c> at all, so the "game:" and bare-path cases need
/// their own fixture "game" directory regardless.
/// </summary>
public class TexturesTests {
  private static string Repo => FixturePath.Of("textures/repo");
  private static string Game => Path.Combine(Repo, "game");
  private static string ShapePath => Path.Combine(Repo, "workbench", "shapes", "synthetic.json");
  private static TextureRoots Roots => TextureRoots.From(Game, Repo);

  [Fact]
  public void Resolve_the_wsl_path() {
    string absolute = Path.Combine(Repo, "workbench", "shapes", "wsl-target");
    string value = "//wsl.localhost/testdistro" + absolute;
    string? hit = Textures.Resolve(value, ShapePath, Roots);
    Assert.Equal("wsl-target.png", Path.GetFileName(hit));
  }

  [Fact]
  public void Resolve_a_bare_vanilla_path() {
    string? hit = Textures.Resolve("block/metal/sheet-plain/iron5", ShapePath, Roots);
    Assert.Equal("iron5.png", Path.GetFileName(hit));
  }

  [Fact]
  public void Resolve_the_game_domain() {
    string? hit = Textures.Resolve("game:block/metal/sheet-plain/iron5", ShapePath, Roots);
    Assert.Equal("iron5.png", Path.GetFileName(hit));
  }

  [Fact]
  public void Resolve_a_mod_domain() {
    string? hit = Textures.Resolve("demo:block/thing", ShapePath, Roots);
    Assert.Equal("thing.png", Path.GetFileName(hit));
  }

  [Fact]
  public void Resolve_a_legacy_domain() {
    string? hit = Textures.Resolve("olddomain:block/relic", ShapePath, Roots);
    Assert.Equal("relic.png", Path.GetFileName(hit));
  }

  [Fact]
  public void Windows_drive_paths_never_resolve() {
    Assert.Null(Textures.Resolve("F:/repos/dead/cast-iron1", ShapePath, Roots));
  }

  [Fact]
  public void A_missing_path_resolves_to_null() {
    Assert.Null(Textures.Resolve("block/does/not/exist", ShapePath, Roots));
  }

  [Fact]
  public void Texture_set_loads_arrays_and_flags_missing() {
    LoadedShape shape = ShapeFile.Load(FixturePath.Of("items/machined/item-lathed-cylinder.json"));
    // cast-iron1 resolves under the "test" domain the fixture shape carries
    // (fixtures/mods/test/assets/test/textures/materials/cast-iron1.png), not the
    // textures/repo tree the domain-resolution facts above use.
    TextureRoots roots = TextureRoots.From(Game, FixturePath.Of(""));
    TextureSet ts = TextureSet.ForShape(shape, roots);
    Assert.Equal(4, ts.Get("cast-iron1").GetLength(2));
    Assert.False(ts.Missing.Contains("cast-iron1"));
  }

  [Fact]
  public void An_unresolved_key_gets_the_magenta_placeholder() {
    LoadedShape shape = ShapeFile.Load(FixturePath.Of("items/machined/item-lathed-cylinder.json"));
    // Injects a key into the shape's own textures map that names a texture nothing can resolve,
    // rather than querying a key the shape never had (which only exercises TextureSet.Get's
    // fallback, not ForShape's own Missing bookkeeping).
    var textures = new Dictionary<string, string>(shape.Textures) { ["ghost"] = "block/no/such/texture" };
    Shape raw = JsonConvert.DeserializeObject<Shape>(
      File.ReadAllText(FixturePath.Of("items/machined/item-lathed-cylinder.json"))
    )!;
    LoadedShape ghosted = ShapeFile.FromRaw(raw, shape.Path, textures);
    TextureRoots roots = TextureRoots.From(Game, Repo);
    TextureSet ts = TextureSet.ForShape(ghosted, roots);
    Assert.Contains("ghost", ts.Missing);
    Assert.Equal(255, ts.Get("ghost")[0, 0, 0]);
    Assert.Equal(0, ts.Get("ghost")[0, 0, 1]);
    Assert.Equal(255, ts.Get("ghost")[0, 0, 2]);
  }
  [Fact]
  public void The_repository_root_outranks_a_nested_game_install() {
    string root = Path.Combine(Path.GetTempPath(), "exlib-shapes-" + Guid.NewGuid().ToString("N"));
    string shapeDir = Path.Combine(root, "legacy", "old", "assets", "old", "shapes");
    Directory.CreateDirectory(shapeDir);
    Directory.CreateDirectory(Path.Combine(root, "legacy", ".game", ".cache"));
    Directory.CreateDirectory(Path.Combine(root, "mods"));
    File.WriteAllText(Path.Combine(root, ".git"), "gitdir: elsewhere");
    try {
      TextureRoots roots = TextureRoots.Build(null, null, Path.Combine(shapeDir, "thing.json"));
      Assert.Equal(root, roots.RepoPath);
    } finally {
      Directory.Delete(root, true);
    }
  }
}
