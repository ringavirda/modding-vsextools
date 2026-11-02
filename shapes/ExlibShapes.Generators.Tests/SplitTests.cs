using System.Collections.Generic;
using System.Linq;
using Xunit;
using static ExpandedLib.Shapes.Generators.Tests.FlushFixtures;

namespace ExpandedLib.Shapes.Generators.Tests;

public class SplitTests {
  [Fact]
  public void Split_cuts_an_element_over_sixteen_into_equal_named_parts() {
    List<Leaf> got = Splits.Apply(
      FlushFixtures.Leaves(Bx("Bed", "[0, 0, 0]", "[40, 4, 8]"))
    );
    Assert.Equal(["Bed", "Bed_s2", "Bed_s3"], got.Select(l => l.Name));
    Assert.Equal([0, 13.3333, 26.6667], got.Select(l => l.Element.From![0]));
    Assert.Equal([13.3333, 26.6667, 40], got.Select(l => l.Element.To![0]));
    Assert.All(
      got,
      l => Assert.All(l.Element.Faces, f => Assert.Null(f.Value.Uv))
    );
  }

  [Fact]
  public void Split_makes_cells_on_every_long_axis_and_leaves_short_ones_whole() {
    List<Leaf> got = Splits.Apply(
      FlushFixtures.Leaves(
        Bx("Slab", "[0, 0, 0]", "[20, 4, 33]"),
        Bx("Lug", "[0, 0, 0]", "[16, 4, 8]")
      )
    );
    Assert.Equal(7, got.Count);
    Assert.Equal(6, got.Count(l => l.Name.StartsWith("Slab")));
    Assert.All(
      got.Where(l => l.Name.StartsWith("Slab")),
      l => Assert.True(l.Element.To![0] - l.Element.From![0] <= 16)
    );
    Leaf lug = got.Single(l => l.Name == "Lug");
    Assert.NotNull(lug.Element.Faces[0].Value.Uv);
  }
}
