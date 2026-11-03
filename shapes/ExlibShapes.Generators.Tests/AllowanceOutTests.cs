using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Xunit;
using static ExpandedLib.Shapes.Generators.Tests.FlushFixtures;

namespace ExpandedLib.Shapes.Generators.Tests;

public class AllowanceOutTests {
  private static readonly FlushFixtures.Box3 Block = Bx(
    "Block",
    "[2, 2, 2]",
    "[10, 6, 8]"
  );

  [Fact]
  public void Allowance_moves_each_cut_face_out_and_only_that_face() {
    List<Leaf> got = Allowances.Out(
      FlushFixtures.Leaves(Block),
      ["Block/up", "Block/west", "Block/down", "Ghost/up"],
      0.125
    );
    Leaf l = Assert.Single(got);
    Assert.Equal([1.875, 1.875, 2], l.Element.From!);
    Assert.Equal([10, 6.125, 8], l.Element.To!);
    Assert.Null(l.Element.Faces.First(f => f.Key == "up").Value.Uv);
    Assert.Null(l.Element.Faces.First(f => f.Key == "west").Value.Uv);
    Assert.NotNull(l.Element.Faces.First(f => f.Key == "east").Value.Uv);
  }

  [Fact]
  public void Allowance_on_the_far_faces_moves_to_not_from() {
    Leaf l = Assert.Single(
      Allowances.Out(
        FlushFixtures.Leaves(Block),
        ["Block/east", "Block/south", "Block/north"],
        0.25
      )
    );
    Assert.Equal([2, 2, 1.75], l.Element.From!);
    Assert.Equal([10.25, 6, 8.25], l.Element.To!);
  }

  [Fact]
  public void A_step_face_that_is_not_name_slash_face_is_refused() {
    Assert.Throws<CastGenException>(() =>
      Allowances.Out(FlushFixtures.Leaves(Block), ["Block/top"], 0.125)
    );
    Assert.Throws<CastGenException>(() =>
      Allowances.Out(FlushFixtures.Leaves(Block), ["Block"], 0.125)
    );
  }

  private static List<Leaf> Pile() {
    Shape shape = Reader.Read(
      $$"""
      { "elements": [ { "name": "Pile", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
        {{Cell("A", "[0, 0, 0]", "[4, 4, 4]")}},
        {{Cell("B", "[4, 0, 0]", "[8, 4, 4]")}},
        {{Cell("C", "[2, 0, 0]", "[6, 4, 4]")}}
      ] } ] }
      """
    );
    return [.. Select.Groups(shape, ["Pile"])];
  }

  private static string Cell(string name, string from, string to) =>
    $$"""{ "name": "{{name}}", "from": {{from}}, "to": {{to}}, "faces": { {{string.Join(
      ", ",
      new[] { "north", "east", "south", "west", "up", "down" }.Select(f => $"\"{f}\": {{ \"texture\": \"#cast-iron1\", \"uv\": [0, 0, 4, 4] }}")
    )}} } }""";

  [Fact]
  public void A_group_face_moves_only_the_leaves_on_the_groups_bounding_face() {
    List<Leaf> got = Allowances.Out(
      Pile(),
      ["Pile/east", "Pile/west", "A/west"],
      0.5
    );
    Assert.Equal(
      [-0.5, 0, 0],
      got.Single(l => l.SourceName == "A").Element.From!
    );
    Assert.Equal([4, 0, 0], got.Single(l => l.SourceName == "B").Element.From!);
    Assert.Equal([8.5, 4, 4], got.Single(l => l.SourceName == "B").Element.To!);
    Leaf c = got.Single(l => l.SourceName == "C");
    Assert.Equal([2, 0, 0], c.Element.From!);
    Assert.Equal([6, 4, 4], c.Element.To!);
  }

  [Fact]
  public void An_entry_naming_a_leaf_that_has_leaves_below_it_stays_that_leaf_s() {
    Shape shape = Reader.Read(
      $$"""
      { "elements": [
        {{Cell("Hub", "[0, 0, 0]", "[4, 4, 4]").TrimEnd('}')}}, "children": [
          {{Cell("Cap", "[0, 4, 0]", "[4, 6, 4]")}}
        ] }
      ] }
      """
    );
    List<Leaf> got = Allowances.Out(
      Select.Groups(shape, ["Hub"]),
      ["Hub/up"],
      0.5
    );
    Assert.Equal(
      [4, 4.5, 4],
      got.Single(l => l.SourceName == "Hub").Element.To!
    );
    Assert.Equal([4, 6, 4], got.Single(l => l.SourceName == "Cap").Element.To!);
  }

  [Fact]
  public void A_group_face_skips_a_leaf_that_turns_about_another_axis() {
    Shape shape = Reader.Read(
      $$"""
      { "elements": [ { "name": "Pile", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
        {{Cell("A", "[0, 0, 0]", "[4, 4, 4]").TrimEnd(
        '}'
      )}}, "rotationX": 90, "rotationOrigin": [2, 2, 2] }
      ] } ] }
      """
    );
    Assert.Empty(
      Allowances.Expand([.. Select.Groups(shape, ["Pile"])], ["Pile/up"])
    );
    Assert.Equal(
      ["A/west"],
      Allowances.Expand(Pile(), ["Pile/west", "A/west", "Pile/west"])
    );
    Assert.Equal(
      ["A/up"],
      Allowances.Expand([.. Select.Groups(shape, ["Pile"])], ["A/up"])
    );
  }

  [Fact]
  public void Names_finds_a_leaf_or_a_group_and_nothing_else() {
    List<Leaf> pile = Pile();
    Assert.True(Allowances.Names(pile, "A"));
    Assert.True(Allowances.Names(pile, "Pile"));
    Assert.False(Allowances.Names(pile, "Ghost"));
  }
}
