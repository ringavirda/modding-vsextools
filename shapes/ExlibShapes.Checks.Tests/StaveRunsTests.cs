using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class StaveRunsTests {
  private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);

  private static string Stave(int k, double angle, double radial, double cross) {
    double x0 = 0.01 * (k % 4),
      len = 1.0 - 0.02 * (k % 4);
    return $$"""
      { "name": "Stave{{k}}", "from": [{{F(x0)}}, {{F(4 + radial)}}, {{F(
        -cross / 2
      )}}],
        "to": [{{F(x0 + len)}}, {{F(4 + radial + 0.3)}}, {{F(cross / 2)}}],
        "rotationX": {{F(
        angle
      )}}, "rotationOrigin": [0, 4, 0], "faces": { "up": { "texture": "#c" } } }
      """;
  }

  private static string Box(string name, string from, string to) =>
    $$"""{ "name": "{{name}}", "from": {{from}}, "to": {{to}}, "faces": { "up": { "texture": "#c" } } }""";

  private static Shape HalfRing(params string[] extra) =>
    Reader.Read(
      $$"""
      { "elements": [ {{string.Join(
        ",",
        Enumerable
          .Range(0, 9)
          .Select(k => Stave(k, -90 + 22.5 * k, 1.0, 0.51))
          .Concat(extra)
      )}} ] }
      """
    );

  private static List<StaveRun> Runs(Shape shape) =>
    StaveRuns.Find(new Tree(shape), new Frames(shape));

  [Fact]
  public void The_staves_of_a_staggered_ring_are_one_run_over_their_full_extent() {
    StaveRun run = Assert.Single(Runs(HalfRing()));

    Assert.Equal(0, run.Axis);
    Assert.Equal(9, run.Members.Count);
    Assert.Equal(0.0, run.Lo, 1e-9);
    Assert.Equal(1.0, run.Hi, 1e-9);
  }

  [Fact]
  public void A_box_of_another_cross_section_beside_the_ring_is_not_a_stave() {
    Shape shape = HalfRing(Stave(9, 0, 1.0, 0.8).Replace("Stave9", "Fat"));

    StaveRun run = Assert.Single(Runs(shape));

    Assert.DoesNotContain(run.Members, m => m.Name == "Fat");
  }

  [Fact]
  public void A_ring_of_staves_staggered_along_the_axis_draws_from_a_parting_through_its_centre() {
    Shape shape = HalfRing(
      Box("Bore", "[0, 4, -1]", "[1, 5, 1]"),
      Box("Block", "[0, 3, -1.3]", "[1, 4.2, 1.3]")
    );

    CheckResult r = new Draw().Run(new CheckInput(shape, Parting: 4));

    Assert.Equal(0, r.ExitCode);
  }

  [Fact]
  public void The_staves_of_a_run_leave_a_draw_field_together() {
    var field = new DrawField(
      HalfRing(Box("Bore", "[0, 4, -1]", "[1, 5, 1]")),
      4
    );

    IReadOnlyList<string> together = field.Together("Stave3");

    Assert.Equal(9, together.Count);
    Assert.Contains("Stave0", together);
    Assert.Equal(["Bore"], field.Together("Bore"));
  }

  [Fact]
  public void The_cells_of_a_run_are_cut_back_to_the_bounds_of_its_section() {
    Shape shape = HalfRing();
    foreach (Element e in shape.Elements) {
      e.From![1] += 0.01;
      e.To![1] += 0.01;
      e.RotationOrigin![1] += 0.01;
    }
    StaveRun run = Assert.Single(Runs(shape));

    List<Box> cells = StaveRuns.Solid(run, Lattice.Fine);

    Assert.Equal(
      run.Polygons.Min(p => p.Min(q => q.U)),
      cells.Min(b => b.Lo.Y),
      1e-9
    );
    Assert.Equal(
      run.Polygons.Max(p => p.Max(q => q.U)),
      cells.Max(b => b.Hi.Y),
      1e-9
    );
  }
}
