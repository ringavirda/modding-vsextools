using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class DrawFieldTests {
  private static Shape CopeGap() =>
    Fixtures.Edited(
      "pattern-ok.json",
      raw => {
        JObject body = CastFixtures.Find(raw, "CopeBody");
        JObject upper = (JObject)body.DeepClone();
        upper["name"] = "CopeHigh";
        CastFixtures.Move(body, "to", 1, 8.53125);
        CastFixtures.Move(upper, "from", 1, 8.59375);
        ((JArray)raw["elements"]![1]!["children"]!).Add(upper);
      }
    );

  [Fact]
  public void The_broken_area_is_the_plan_area_of_the_columns_draw_names() {
    Assert.Equal(16, new DrawField(CopeGap(), 8).Broken, 1e-9);
    Assert.Equal(
      0,
      new DrawField(Fixtures.Shape("pattern-ok.json"), 8).Broken,
      1e-9
    );
  }

  [Fact]
  public void Without_reads_the_shape_less_one_leaf_and_changes_nothing() {
    var field = new DrawField(CopeGap(), 8);

    Assert.Equal(0, field.Without("CopeHigh"), 1e-9);
    Assert.Equal(16, field.Without("CopeBody"), 1e-9);
    Assert.Equal(16, field.Broken, 1e-9);
  }

  [Fact]
  public void A_removed_leaf_stays_out_of_every_later_reading() {
    var field = new DrawField(CopeGap(), 8);
    Assert.Equal(16, field.Without("CopeBody"), 1e-9);

    field.Remove("CopeHigh");

    Assert.Equal(0, field.Broken, 1e-9);
    Assert.Equal(0, field.Without("CopeBody"), 1e-9);
  }
}
