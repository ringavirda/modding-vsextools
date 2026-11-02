using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Generators.Tests;

public class UvTests {
  private static string Faces(string key) =>
    string.Join(
      ", ",
      new[] { "north", "east", "south", "west", "up", "down" }.Select(f =>
        $"\"{f}\": {{ \"texture\": \"#{key}\" }}"
      )
    );

  private static string Cube(
    string name,
    string from,
    string to,
    string key = "cast-iron1",
    string extra = ""
  ) =>
    $$"""{ "name": "{{name}}", "from": {{from}}, "to": {{to}}, {{extra}} "faces": { {{Faces(key)}} } }""";

  private static List<Leaf> Leaves(params string[] cubes) =>
    Select.Groups(
      Reader.Read(
        $$"""{ "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [ {{string.Join(", ", cubes)}} ] } ] }"""
      ),
      ["Root"]
    );

  private static GenContext Ctx() => Fx.Ctx("{}", "{}");

  private static double[] Rect(Leaf l, string face) =>
    l.Element.Faces.Single(kv => kv.Key == face).Value.Uv!;

  [Fact]
  public void Assign_sizes_every_face_and_seeds_plain_origins() {
    List<Leaf> leaves = Leaves(Cube("Body", "[0, 0, 0]", "[4, 2, 6]"));

    List<Leaf> a = Uvs.Assign(Ctx(), "item", leaves);
    List<Leaf> b = Uvs.Assign(Ctx(), "item", leaves);

    foreach (
      (string face, double u, double v) in new[]
      {
        ("north", 4.0, 2.0),
        ("east", 6.0, 2.0),
        ("up", 4.0, 6.0),
      }
    ) {
      double[] r = Rect(a[0], face);
      Fx.Near(u, r[2] - r[0]);
      Fx.Near(v, r[3] - r[1]);
      Assert.Equal(0, r[0] * 2 % 1);
      Assert.Equal(0, r[1] * 2 % 1);
      Assert.True(r[2] <= 16 && r[3] <= 16);
      Assert.NotEqual([0, 0], [r[0], r[1]]);
      Assert.Equal(Rect(b[0], face), r);
    }
  }

  [Fact]
  public void Assign_keeps_a_rect_of_the_face_size_and_places_by_class() {
    Leaf kept = Leaves(Cube("Body", "[0, 0, 0]", "[4, 2, 6]"))[0];
    kept.Element.Faces.Single(kv => kv.Key == "north").Value.Uv = [5, 6, 9, 8];
    Leaf wood = Leaves(Cube("Plank", "[0, 0, 0]", "[2, 6, 1]", "wood"))[0];
    Leaf pat = Leaves(Cube("Slab", "[0, 0, 0]", "[4, 2, 6]", "burned"))[0];

    List<Leaf> got = Uvs.Assign(Ctx(), "item", [kept, wood, pat]);

    Assert.Equal([5, 6, 9, 8], Rect(got[0], "north"));
    Face tall = got[1].Element.Faces.Single(kv => kv.Key == "north").Value;
    Assert.Equal([0, 0, 6, 2], tall.Uv!);
    Assert.Equal(90, tall.Rotation);
    Face flat = got[1].Element.Faces.Single(kv => kv.Key == "up").Value;
    Assert.Equal([0, 0, 2, 1], flat.Uv!);
    Assert.Null(flat.Rotation);
    Assert.Equal([0, 0, 4, 2], Rect(got[2], "north"));
  }

  private static string Bars(
    int n,
    double apothem,
    double thick,
    double y0,
    double y1,
    double turn
  ) {
    double s = 2 * apothem * Math.Tan(Math.PI / n);
    return string.Join(
      ", ",
      Enumerable
        .Range(0, n)
        .Select(k =>
          Cube(
            $"Bar{y0}_{k}",
            $"[{8 - s / 2:F6}, {y0}, {8 + apothem - thick:F6}]",
            $"[{8 + s / 2:F6}, {y1}, {8 + apothem:F6}]",
            "cast-iron1",
            $"\"rotationOrigin\": [8, 0, 8], \"rotationY\": {k * turn:F6},"
          )
        )
    );
  }

  [Fact]
  public void Round_run_runs_on() {
    List<Leaf> leaves = Leaves(
      Bars(8, 4, 1.5, 0, 8, 45),
      Bars(16, 6, 2, 3, 5, 22.5)
    );
    List<Leaf> assigned = Uvs.Assign(Ctx(), "shaft", leaves);
    var wrap = new Uvwrap();

    CheckResult before = wrap.Run(
      new CheckInput(FlushFixtures.ItemShape(assigned), Directed: true)
    );
    List<Leaf> rolled = Uvs.Unroll(Ctx(), "shaft", assigned);
    CheckResult after = wrap.Run(
      new CheckInput(FlushFixtures.ItemShape(rolled), Directed: true)
    );

    Assert.Equal(1, before.ExitCode);
    Assert.Equal(2, after.Enumerated);
    Assert.Equal(0, after.ExitCode);
  }
}
