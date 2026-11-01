using System;
using System.IO;
using Xunit;

namespace ExpandedLib.Shapes.Core.Tests;

public class SettingsTests {
  private static readonly string FixturePath = Path.Combine(
    AppContext.BaseDirectory,
    "fixtures",
    "settings.json"
  );

  private static string Text => File.ReadAllText(FixturePath);

  private static Settings Fixture() => Settings.ReadFile(FixturePath);

  [Fact]
  public void Unknown_texture_key_stops() {
    Settings s = Fixture();
    SettingsException e = Assert.Throws<SettingsException>(() =>
      s.EditablePath("no-such-key")
    );
    Assert.Contains("no-such-key", e.Message);

    string bad = Text.Replace(
      "\"cast\": \"cast-iron1\"",
      "\"cast\": \"cast-iron9\""
    );
    e = Assert.Throws<SettingsException>(() => Settings.Read(bad));
    Assert.Contains("cast-iron9", e.Message);
  }

  [Fact]
  public void Unknown_role_stops() {
    string bad = Text.Replace(
      "\"runner\": \"burned\"",
      "\"runner\": \"burned\", \"gleam\": \"iron5\""
    );
    SettingsException e = Assert.Throws<SettingsException>(() =>
      Settings.Read(bad)
    );
    Assert.Contains("gleam", e.Message);
    Assert.Throws<SettingsException>(() =>
      Fixture().RoleKey("finished.polish")
    );
  }

  [Fact]
  public void Unknown_settings_key_stops_with_its_name() {
    string bad = Text.Replace(
      "\"margin\": 1.0, \"ladleUnits\": 2.5, \"risers\": {}",
      "\"margin\": 1.0, \"ladleUnits\": 2.5, \"risers\": {}, \"moat\": 3"
    );
    SettingsException e = Assert.Throws<SettingsException>(() =>
      Settings.Read(bad)
    );
    Assert.Contains("moat", e.Message);
  }

  [Fact]
  public void Step_machine_outside_its_row_stops() {
    Settings s = Fixture();
    s.CheckMachine("drill", "drillpress");
    s.CheckMachine("drill", "handdrill");
    SettingsException e = Assert.Throws<SettingsException>(() =>
      s.CheckMachine("drill", "lathe")
    );
    Assert.Contains("lathe", e.Message);
    Assert.Throws<SettingsException>(() => s.CheckMachine("polish", "lathe"));
  }

  [Fact]
  public void Long_flask_holds_the_code_risers() {
    Station longFlask = Fixture().Stations["long"];
    Assert.Equal([2.0, 2.0], longFlask.Risers["bed"]);
    Assert.Equal([1.5, 1.5], longFlask.Risers["faceplate"]);
    Assert.Equal(8, longFlask.Parting);
    Assert.Equal([2.0, 2, -46], longFlask.SandFrom);
  }

  [Fact]
  public void Runtime_path_follows_the_domain_then_the_wildcard() {
    Settings s = Fixture();
    Assert.Equal(
      "iiex:block/metal/castiron",
      s.RuntimePath("cast-iron1", "iiex")
    );
    Assert.Equal(
      "game:block/metal/cast-iron1",
      s.RuntimePath("cast-iron1", "siex")
    );
    Assert.Equal(
      "iiex:block/metal/castiron-turned",
      s.RuntimePath("cast-iron-turned1", "iiex")
    );
  }

  [Fact]
  public void Wood_variant_is_oak_in_an_editable_and_the_placeholder_in_a_runtime() {
    Settings s = Fixture();
    Assert.Equal("block/wood/planks/oak1", s.EditablePath("wood"));
    Assert.Equal(
      "game:block/wood/planks/{wood}1",
      s.RuntimePath("wood", "iiex")
    );
    Assert.Equal(TextureClass.Wood, s.Texture("wood").Class);
  }

  [Fact]
  public void Roles_resolve_to_keys() {
    Settings s = Fixture();
    Assert.Equal("iron5", s.RoleKey("fastener"));
    Assert.Equal("cast-iron-turned1", s.RoleKey("finished.bore"));
    Assert.Equal("expanded-period", s.LintProfile);
  }
}
