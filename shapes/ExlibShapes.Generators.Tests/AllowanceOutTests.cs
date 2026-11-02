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
}
