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
}
