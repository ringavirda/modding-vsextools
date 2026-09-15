using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Tests;

public class SelectiveElementsTests {
  private const string Shape = """
    {
      "textures": { "a": "block/a" },
      "elements": [
        { "name": "Base", "from": [0,0,0], "to": [16,4,16], "faces": {},
          "children": [ { "name": "Cube1", "from": [0,0,0], "to": [4,4,4], "faces": {} } ] },
        { "name": "Fillings", "from": [0,4,0], "to": [16,8,16], "faces": {},
          "children": [ { "name": "Coal", "from": [0,0,0], "to": [4,4,4], "faces": {} } ] }
      ]
    }
    """;

  private static string Write() {
    string path = Path.Combine(Path.GetTempPath(), $"selective-{Guid.NewGuid():N}.json");
    File.WriteAllText(path, Shape);
    return path;
  }

  [Fact]
  public void A_subtree_pattern_keeps_the_element_and_everything_under_it() {
    LoadedShape shape = ShapeFile.Load(Write(), ["Base/*"]);
    Assert.Equal(["Base"], shape.Elements.Select(e => e.Name));
    Assert.Equal(["Cube1"], shape.Elements[0].Children.Select(e => e.Name));
  }

  [Fact]
  public void A_bare_pattern_keeps_the_element_alone() {
    LoadedShape shape = ShapeFile.Load(Write(), ["Base"]);
    Assert.Equal(["Base"], shape.Elements.Select(e => e.Name));
    Assert.Empty(shape.Elements[0].Children);
  }

  // render's own --selective is a comma-separated list split into exactly this kind of patterns
  // list before it reaches ShapeFile.Load; two of them together keep the union of what each names.
  [Fact]
  public void Two_patterns_together_keep_the_union() {
    LoadedShape shape = ShapeFile.Load(Write(), ["Base", "Fillings/*"]);
    Assert.Equal(["Base", "Fillings"], shape.Elements.Select(e => e.Name));
    Assert.Empty(shape.Elements[0].Children);
    Assert.Equal(["Coal"], shape.Elements[1].Children.Select(e => e.Name));
  }

  [Fact]
  public void No_pattern_keeps_the_whole_shape() {
    LoadedShape shape = ShapeFile.Load(Write(), []);
    Assert.Equal(["Base", "Fillings"], shape.Elements.Select(e => e.Name));
  }

  [Fact]
  public void The_json_tree_is_pruned_the_same_way() {
    JArray elements = (JArray)JObject.Parse(Shape)["elements"]!;
    JArray kept = SelectiveElements.Prune(elements, ["Fillings/*"]);
    Assert.Single(kept);
    Assert.Equal("Fillings", (string?)kept[0]["name"]);
    Assert.Equal("Coal", (string?)kept[0]["children"]![0]!["name"]);
    Assert.True(SelectiveElements.Keeps(["Base/*"], "Base/Cube1/Sub"));
    Assert.False(SelectiveElements.Keeps(["Base"], "Base/Cube1"));
    Assert.True(SelectiveElements.Keeps(["Cube*"], "Cube12"));
    Assert.True(SelectiveElements.Keeps(["Root/Base/*"], "Root"));
    Assert.True(SelectiveElements.Keeps(["Root/Base/*"], "Root/Base/Cube1"));
    Assert.False(SelectiveElements.Keeps(["Root/Base/*"], "Root/Fillings"));
  }
}
