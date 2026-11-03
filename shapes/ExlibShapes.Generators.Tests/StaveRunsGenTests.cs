using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Generators.Tests;

public class StaveRunsGenTests {
  private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);

  private static string Stave(int k) {
    double x0 = 0.01 * (k % 4),
      len = 1.0 - 0.02 * (k % 4);
    return $$"""
      { "name": "Stave{{k}}", "from": [{{F(x0)}}, 5, -0.255], "to": [{{F(
        x0 + len
      )}}, 5.3, 0.255],
        "rotationX": {{F(
        -90 + 22.5 * k
      )}}, "rotationOrigin": [0, 4, 0], "faces": { "up": { "texture": "#c" } } }
      """;
  }

  private static List<Leaf> Ring() =>
    [
      .. Select
        .Groups(
          Reader.Read(
            $$"""
            { "elements": [ {{string.Join(
              ",",
              Enumerable.Range(0, 9).Select(Stave)
            )}},
              { "name": "Bore", "from": [0, 4, -1], "to": [1, 5, 1], "faces": { "up": { "texture": "#c" } } },
              { "name": "Block", "from": [0, 3, -1.3], "to": [1, 4.2, 1.3], "faces": { "up": { "texture": "#c" } } } ] }
            """
          ),
          [.. Enumerable.Range(0, 9).Select(k => $"Stave{k}"), "Bore", "Block"]
        )
        .Select(l => l with { Stage = "cast", Role = "cast" }),
    ];

  [Fact]
  public void The_staves_of_a_run_occupy_the_polygons_they_span_over_the_run_s_full_extent() {
    Occupancy occ = Occupancy.Of(Ring());

    Assert.Equal(11, occ.Parts.Count);
    Assert.Equal(2, occ.Boxes.Count);
    Assert.NotEmpty(occ.Prisms);
    Assert.All(
      occ.Prisms,
      p => {
        Assert.Equal(0, p.Axis);
        Assert.Equal(0.0, p.Lo, 1e-9);
        Assert.Equal(1.0, p.Hi, 1e-9);
      }
    );
  }

  [Fact]
  public void A_run_placed_by_a_turn_keeps_its_polygons_on_the_turned_axis() {
    Occupancy occ = Occupancy.Of(Ring(), Mat4d.RotateByXyz(0, 0, 90));

    Assert.All(occ.Prisms, p => Assert.Equal(1, p.Axis));
  }
}
