using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;
using static ExpandedLib.Shapes.Generators.Tests.FlushFixtures;

namespace ExpandedLib.Shapes.Generators.Tests;

public class LieTests {
  private static JToken Bed =>
    JToken.Parse(
      """{ "x": { "axis": "-x", "offset": 15 }, "y": { "axis": "-y", "offset": 14 }, "z": { "axis": "z", "offset": -20.5 } }"""
    );

  private static string Lie(string x, string y, string z) =>
    $$"""{ "x": { "axis": "{{x}}", "offset": 0 }, "y": { "axis": "{{y}}", "offset": 0 }, "z": { "axis": "{{z}}", "offset": 0 } }""";

  [Fact]
  public void Lie_lays_a_leaf_by_sign_and_offset() {
    LieSpec lie = Lies.Read(Bed, "bed");
    List<Leaf> got = Lies.Apply(
      Leaves(Bx("Wall", "[0, 0, 0]", "[4, 2, 6]")),
      lie
    );

    Box b = Assert.Single(got).Box;
    Fx.Near(new Vec3(11, 12, -20.5), b.Lo, 1e-6);
    Fx.Near(new Vec3(15, 14, -14.5), b.Hi, 1e-6);
  }

  [Fact]
  public void Lie_swaps_axes() {
    LieSpec lie = Lies.Read(JToken.Parse(Lie("y", "-x", "z")), "cover");
    Box b = Assert
      .Single(Lies.Apply(Leaves(Bx("Wall", "[0, 0, 0]", "[4, 2, 6]")), lie))
      .Box;

    Fx.Near(new Vec3(0, -4, 0), b.Lo, 1e-6);
    Fx.Near(new Vec3(2, 0, 6), b.Hi, 1e-6);
  }

  [Fact]
  public void Lie_auto_names_the_later_version() {
    SpecException ex = Assert.Throws<SpecException>(() =>
      Lies.Read(JValue.CreateString("auto"), "bed")
    );
    Assert.Contains("M2", ex.Message);
  }

  [Fact]
  public void Lie_refuses_the_bare_axis_form() {
    SpecException ex = Assert.Throws<SpecException>(() =>
      Lies.Read(JToken.Parse("""{ "axis": "-z", "offset": 3 }"""), "bed")
    );
    Assert.Contains("per-flask-axis", ex.Message);
  }

  [Fact]
  public void Lie_refuses_a_repeated_item_axis() {
    SpecException ex = Assert.Throws<SpecException>(() =>
      Lies.Read(JToken.Parse(Lie("x", "-x", "z")), "bed")
    );
    Assert.Contains("not a signed permutation", ex.Message);
  }

  [Fact]
  public void Lie_refuses_a_mirror() {
    SpecException ex = Assert.Throws<SpecException>(() =>
      Lies.Read(JToken.Parse(Lie("-x", "y", "z")), "bed")
    );
    Assert.Contains("mirrors", ex.Message);
  }

  [Theory]
  [InlineData(
    """{ "x": { "axis": "-x", "offset": 0 }, "y": { "axis": "-y", "offset": 0 } }"""
  )]
  [InlineData(
    """{ "x": { "axis": "w", "offset": 0 }, "y": { "axis": "y", "offset": 0 }, "z": { "axis": "z", "offset": 0 } }"""
  )]
  [InlineData(
    """{ "x": { "axis": "x" }, "y": { "axis": "y", "offset": 0 }, "z": { "axis": "z", "offset": 0 } }"""
  )]
  [InlineData(
    """{ "x": { "axis": "--y", "offset": 0 }, "y": { "axis": "x", "offset": 0 }, "z": { "axis": "z", "offset": 0 } }"""
  )]
  [InlineData(
    """{ "x": { "axis": "x", "offset": 0, "turn": 1 }, "y": { "axis": "y", "offset": 0 }, "z": { "axis": "z", "offset": 0 } }"""
  )]
  public void Lie_refuses_a_malformed_axis(string text) {
    Assert.Throws<SpecException>(() => Lies.Read(JToken.Parse(text), "bed"));
  }

  [Fact]
  public void Lie_without_a_value_is_refused() {
    Assert.Throws<SpecException>(() => Lies.Read(null, "bed"));
  }

  [Fact]
  public void A_group_lie_wins_over_the_entrys() {
    var group = new GroupSpec(
      ["Wall"],
      null,
      JToken.Parse(Lie("x", "z", "-y"))
    );

    LieSpec got = Lies.Of(group, Bed, "kit");

    Assert.Equal(
      [(0, 1), (2, 1), (1, -1)],
      got.Axes.Select(a => (a.Item, a.Sign))
    );
    Assert.Equal(
      -1,
      Lies.Of(new GroupSpec(["Wall"], null, null), Bed, "kit").Axes[0].Sign
    );
  }
}
