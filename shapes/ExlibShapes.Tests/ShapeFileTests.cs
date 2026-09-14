using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>
/// Ported from <c>vsshape/tests/test_shape.py</c>'s loading facts; <c>edit_number</c> and
/// <c>save</c> are Model Creator editing aids the shapes tool has no interface for, so they are
/// not ported.
/// </summary>
public class ShapeFileTests {
  [Fact]
  public void Load_owner_cylinder_tree() {
    LoadedShape shape = ShapeFile.Load(FixturePath.Of("items/machined/item-lathed-cylinder.json"));
    Assert.Equal(8, shape.Leaves().Count);
    Assert.Equal("Cylinder/Cube4/Cube2", shape.Find("Cube2")!.Path);
    Assert.Equal("Cube2", shape.Find("Cylinder/Cube4/Cube2")!.Name);
    Assert.EndsWith(
      "workbench/textures/materials/cast-iron1",
      shape.Textures["cast-iron1"].ToString()
    );
  }
}
