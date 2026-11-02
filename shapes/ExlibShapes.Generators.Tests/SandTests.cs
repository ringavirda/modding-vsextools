using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Generators.Tests;

public class SandTests {
  private static Box B(
    double x0,
    double y0,
    double z0,
    double x1,
    double y1,
    double z1
  ) => new(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1));

  private static double Volume(IEnumerable<Box> boxes) =>
    boxes.Sum(b => (b.Hi.X - b.Lo.X) * (b.Hi.Y - b.Lo.Y) * (b.Hi.Z - b.Lo.Z));

  private static bool In(IEnumerable<Box> boxes, Vec3 p) =>
    boxes.Any(b =>
      Enumerable.Range(0, 3).All(a => p[a] > b.Lo[a] && p[a] < b.Hi[a])
    );

  private static Leaf Slab(string name, double cx, double cz, double angle) {
    var e = Reader
      .Read(
        $$"""
        { "elements": [ { "name": "{{name}}", "from": [{{cx - 3}}, 0, {{cz
          - 0.5}}], "to": [{{cx + 3}}, 4, {{cz + 0.5}}],
          "rotationY": {{angle}}, "rotationOrigin": [{{cx}}, 0, {{cz}}], "faces": { "up": { "texture": "#c" } } } ] }
        """
      )
      .Elements[0];
    return new Leaf(name, name, name, e, "cast", "cast");
  }

  internal static List<Leaf> Ring() =>
    [
      Slab("A", 11, 11, 45),
      Slab("B", 5, 5, 45),
      Slab("C", 5, 11, -45),
      Slab("D", 11, 5, -45),
    ];

  [Fact]
  public void Sand_and_metal_fill_the_region() {
    var occ = new Occupancy();
    occ.Add(B(2, 1, 2, 6, 3, 9));
    occ.Add(B(5, 0, 4, 12, 2, 7));
    var region = B(0, 0, 0, 20, 4, 20);

    List<Box> sand = Sands.Fill(region, occ);

    Assert.Equal(
      20 * 4 * 20 - Sands.Volume(occ, Sands.Step),
      Volume(sand),
      1e-9
    );
    Assert.False(In(sand, new Vec3(3, 2, 3)));
    Assert.True(In(sand, new Vec3(15, 2, 15)));
  }

  [Fact]
  public void Sand_boxes_are_at_most_16_on_every_axis() {
    List<Box> sand = Sands.Fill(B(0, 0, 0, 40, 20, 33), new Occupancy());

    Assert.Equal(40 * 20 * 33, Volume(sand), 1e-9);
    Assert.All(
      sand,
      b =>
        Assert.All(
          Enumerable.Range(0, 3),
          a => Assert.True(b.Hi[a] - b.Lo[a] <= 16 + 1e-9)
        )
    );
  }

  [Fact]
  public void Sand_joins_free_cells_into_maximal_boxes() {
    var occ = new Occupancy();
    occ.Add(B(4, 0, 0, 6, 4, 4));

    List<Box> sand = Sands.Fill(B(0, 0, 0, 10, 4, 4), occ);

    Assert.Equal(2, sand.Count);
  }

  [Fact]
  public void Sand_stays_out_of_a_round_bore() {
    var occ = Occupancy.Of(Ring());
    var region = B(0, 0, 0, 16, 4, 16);

    List<Box> sand = Sands.Fill(region, occ);

    Assert.Equal(4, occ.Prisms.Count);
    Assert.True(In(sand, new Vec3(8, 2, 8)));
    foreach (
      (double x, double z) in new[] { (11, 11), (5, 5), (5, 11), (11, 5) }
    )
      Assert.False(
        In(sand, new Vec3(x, 2, z)),
        $"sand in the ring wall at {x}, {z}"
      );
    Assert.Equal(
      16 * 4 * 16 - Sands.Volume(occ, Sands.Step),
      Volume(sand),
      1e-6
    );
  }

  [Fact]
  public void Occupancy_refuses_a_leaf_turned_on_two_axes() {
    Mat4d m = Mat4d.RotateByXyz(30, 30, 0);

    Assert.Throws<CastGenException>(() =>
      new Occupancy().Add(m, new Vec3(2, 2, 2), "Odd")
    );
  }

  [Fact]
  public void Volume_of_nothing_is_zero() =>
    Assert.Equal(0, Sands.Volume(new Occupancy(), 1.0 / 32));

  [Fact]
  public void Volume_counts_overlap_once() {
    var occ = new Occupancy();
    occ.Add(B(0, 0, 0, 4, 4, 4));
    occ.Add(B(2, 0, 0, 6, 4, 4));

    Assert.Equal(96, Sands.Volume(occ, Sands.Step), 1e-9);
  }
}
