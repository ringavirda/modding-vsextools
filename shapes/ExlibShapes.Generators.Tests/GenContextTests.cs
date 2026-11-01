using System.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Generators.Tests;

public class GenContextTests {
  private static int[] Draw(GenContext c, string face) {
    var r = c.Rng("castgen", "planer-bed", "Rail", face);
    return [.. Enumerable.Range(0, 5).Select(_ => r.Next(1000))];
  }

  [Fact]
  public void Rng_repeats_for_the_same_four_parts_and_differs_by_each() {
    GenContext c = Fx.Ctx(
      new System.Collections.Generic.Dictionary<string, (string, string)>()
    );

    Assert.Equal(Draw(c, "up"), Draw(c, "up"));
    Assert.NotEqual(Draw(c, "up"), Draw(c, "down"));
    Assert.NotEqual(
      Draw(c, "up"),
      [
        .. Enumerable
          .Range(0, 5)
          .Select(_ =>
            c.Rng("castgen", "planer-bed", "Rail2", "up").Next(1000)
          ),
      ]
    );
  }
}
