using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Covers ShapeFile's loading of a Model Creator tree: leaves, path lookup by name and
/// texture resolution. Editing and saving are Model Creator aids the shapes tool has no
/// interface for and are out of scope.</summary>
public class ShapeFileTests {
  [Fact]
  public void Load_owner_cylinder_tree() {
    LoadedShape shape = ShapeFile.Load(FixturePath.Of("items/machined/item-lathed-cylinder.json"));
    Assert.Equal(8, shape.Leaves().Count);
    Assert.Equal("Cylinder/Cube4/Cube2", shape.Find("Cube2")!.Path);
    Assert.Equal("Cube2", shape.Find("Cylinder/Cube4/Cube2")!.Name);
    Assert.Equal("test:materials/cast-iron1", shape.Textures["cast-iron1"]);
  }

  // Model Creator can save two siblings under one name, the vanilla steam engine's second bank
  // of gear teeth among them: every "Tooth" here keeps its own Name, but a repeat gets a #2, #3,
  // ... on its Path alone, so the geometry's per-path maps (WorldMatrices, ElementBoxes) never
  // collapse two of them onto one transform.
  [Fact]
  public void A_repeated_sibling_name_gets_a_numbered_path() {
    string json = """
      {"elements": [{"name": "Group", "from": [0,0,0], "to": [3,1,1], "faces": {},
        "children": [
          {"name": "Tooth", "from": [0,0,0], "to": [1,1,1], "faces": {"up": {"texture": "#a", "uv": [0,0,1,1]}}},
          {"name": "Tooth", "from": [1,0,0], "to": [2,1,1], "faces": {"up": {"texture": "#a", "uv": [0,0,1,1]}}},
          {"name": "Tooth", "from": [2,0,0], "to": [3,1,1], "faces": {"up": {"texture": "#a", "uv": [0,0,1,1]}}}
        ]}]}
      """;
    Shape raw = JsonConvert.DeserializeObject<Shape>(json)!;
    LoadedShape shape = ShapeFile.FromRaw(raw, null);
    Assert.Equal(
      ["Group/Tooth", "Group/Tooth#2", "Group/Tooth#3"],
      shape.Leaves().Select(el => el.Path)
    );
    Assert.Equal(["Tooth", "Tooth", "Tooth"], shape.Leaves().Select(el => el.Name));
  }
}
