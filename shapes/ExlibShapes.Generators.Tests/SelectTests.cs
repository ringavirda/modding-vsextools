using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Generators.Tests;

public class SelectTests {
  private static readonly string Rotated = $$"""
    { "elements": [
      { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "rotationY": 37, "rotationOrigin": [3, 0, 5], "children": [
        { "name": "Arm", "from": [2, 1, 1], "to": [3, 2, 2], "rotationX": 20, "rotationOrigin": [4, 2, 3], "children": [
          {{Fx.Cube(
      "Leaf",
      "[1, 0, 0]",
      "[6, 3, 2]",
      "\"rotationZ\": 11, \"rotationOrigin\": [2, 1, 0],"
    )}}
        ] } ] } ] }
    """;

  [Fact]
  public void Leaf_under_a_rotated_parent_keeps_its_world_box() {
    Shape drawn = Reader.Read(Rotated);
    Box want = WorldBox.Of(drawn, "Root/Arm/Leaf");
    Dictionary<string, Mat4d> world = Engine.WorldMatrices(drawn);

    List<Leaf> leaves = Select.Groups(drawn, ["Root"]);

    Leaf leaf = Assert.Single(leaves);
    Assert.Equal("Leaf", leaf.Name);
    Fx.Near(want.Lo, leaf.Box.Lo);
    Fx.Near(want.Hi, leaf.Box.Hi);
    Mat4d got = Engine.Local(leaf.Element);
    for (int i = 0; i < 4; i++)
      for (int j = 0; j < 4; j++)
        Fx.Near(world["Root/Arm/Leaf"][i, j], got[i, j]);
  }

  [Fact]
  public void A_uniformly_scaled_parent_is_folded_into_the_leaf_size() {
    Shape drawn = Reader.Read(
      $$"""
      { "elements": [ { "name": "Root", "from": [1, 0, 0], "to": [2, 1, 1], "rotationY": 90, "rotationOrigin": [0, 0, 0],
          "scaleX": 2, "scaleY": 2, "scaleZ": 2, "children": [
          {{Fx.Cube("Leaf", "[1, 1, 1]", "[4, 2, 3]")}} ] } ] }
      """
    );
    Box want = WorldBox.Of(drawn, "Root/Leaf");

    Leaf leaf = Assert.Single(Select.Groups(drawn, ["Root/Leaf"]));

    Fx.Near(want.Lo, leaf.Box.Lo);
    Fx.Near(want.Hi, leaf.Box.Hi);
    Assert.Null(leaf.Element.ScaleX);
  }

  [Fact]
  public void A_sheared_world_matrix_is_refused() {
    Shape drawn = Reader.Read(
      $$"""
      { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "scaleY": 3, "children": [
          {{Fx.Cube(
        "Leaf",
        "[1, 1, 1]",
        "[4, 2, 3]",
        "\"rotationZ\": 30,"
      )}} ] } ] }
      """
    );

    var e = Assert.Throws<CastGenException>(() =>
      Select.Groups(drawn, ["Root/Leaf"])
    );
    Assert.Contains("Root/Leaf", e.Message);
  }

  [Fact]
  public void A_repeated_name_across_selections_gets_a_suffix() {
    Shape drawn = Reader.Read(
      $$"""{ "elements": [ {{Fx.Cube("A", "[0, 0, 0]", "[1, 1, 1]")}} ] }"""
    );
    var taken = new HashSet<string>();

    List<Leaf> first = Select.Groups(drawn, ["A"], null, taken);
    List<Leaf> second = Select.Groups(drawn, ["A"], null, taken);

    Assert.Equal("A", first[0].Name);
    Assert.Equal("A_2", second[0].Name);
    Assert.Equal("A", second[0].SourceName);
  }

  [Fact]
  public void A_group_the_drawing_lacks_is_refused() {
    Shape drawn = Reader.Read(
      $$"""{ "elements": [ {{Fx.Cube("A", "[0, 0, 0]", "[1, 1, 1]")}} ] }"""
    );

    var e = Assert.Throws<CastGenException>(() =>
      Select.Groups(drawn, ["Nope"])
    );
    Assert.Contains("Nope", e.Message);
  }

  [Fact]
  public void Window_keeps_the_inside_and_cuts_the_crossing() {
    Shape drawn = Reader.Read(
      $$"""
      { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
        {{Fx.Cube("In", "[6, 0, 0]", "[10, 2, 2]")}},
        {{Fx.Cube("Out", "[0, 0, 0]", "[4, 2, 2]")}},
        { "name": "Cross", "from": [3, 0, 0], "to": [8, 2, 2], "faces": { "up": { "texture": "#c", "uv": [0, 0, 5, 2] } } } ] } ] }
      """
    );
    List<Leaf> all = Select.Groups(drawn, ["Root"]);

    List<Leaf> got = Select.Window(all, [5, 0, 0, 16, 16, 16]);

    Assert.Equal(["Cross", "In"], got.Select(l => l.Name).Order());
    Leaf cross = got.Single(l => l.Name == "Cross");
    Fx.Near(5, cross.Box.Lo.X);
    Fx.Near(8, cross.Box.Hi.X);
    Assert.Null(cross.Element.Faces[0].Value.Uv);
  }

  [Fact]
  public void Window_refuses_a_rotated_element_it_would_cut() {
    Shape drawn = Reader.Read(
      $$"""{ "elements": [ {{Fx.Cube("R", "[0, 0, 0]", "[8, 2, 2]", "\"rotationZ\": 10, \"rotationOrigin\": [0, 0, 0],")}} ] }"""
    );
    List<Leaf> all = Select.Groups(drawn, ["R"]);

    var e = Assert.Throws<CastGenException>(() =>
      Select.Window(all, [4, -20, -20, 40, 20, 20])
    );
    Assert.Contains("R", e.Message);
  }
}
