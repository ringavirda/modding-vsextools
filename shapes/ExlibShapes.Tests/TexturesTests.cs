using System;
using System.IO;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>
/// Ported from <c>vsshape/tests/test_textures.py</c>, against a tiny self-contained fixture tree
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
    TextureSet ts = TextureSet.ForShape(shape, Roots);
    Assert.Equal(4, ts.Get("cast-iron1").GetLength(2));
    Assert.False(ts.Missing.Contains("cast-iron1"));
  }

  [Fact]
  public void An_unresolved_key_gets_the_magenta_placeholder() {
    LoadedShape shape = ShapeFile.Load(FixturePath.Of("items/machined/item-lathed-cylinder.json"));
    TextureRoots roots = TextureRoots.From(Game, Repo);
    TextureSet ts = TextureSet.ForShape(shape, roots);
    // cast-iron1 is a real wsl path in the fixture shape, resolvable against no repo at all here -
    // force a miss the same way the Python test does, by resolving a key the shape never had.
    Assert.True(ts.Get("no-such-key").GetLength(0) == 16 && ts.Get("no-such-key").GetLength(1) == 16);
    Assert.Equal(255, ts.Get("no-such-key")[0, 0, 0]);
    Assert.Equal(0, ts.Get("no-such-key")[0, 0, 1]);
    Assert.Equal(255, ts.Get("no-such-key")[0, 0, 2]);
  }
}
