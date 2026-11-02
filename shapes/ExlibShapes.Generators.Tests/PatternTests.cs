using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;
using Xunit;
using static ExpandedLib.Shapes.Generators.Tests.FlushFixtures;

namespace ExpandedLib.Shapes.Generators.Tests;

public class PatternTests {
  private static readonly GenContext Ctx = Fx.Ctx(
    """{ "elements": [] }""",
    """{ "schema": 1 }"""
  );

  private static PatternResult Make(
    double? parting,
    IReadOnlyList<PatternPrint>? prints,
    params Box3[] boxes
  ) =>
    Patterns.Make(
      Ctx,
      "pattern-castthing",
      Leaves(boxes),
      prints ?? [],
      parting,
      "spec"
    );

  private static Box3 Base => Bx("Base", "[0, 2, 0]", "[8, 8, 8]");

  private static Box3 Lug => Bx("Lug", "[8, 3, 0]", "[12, 5, 8]");

  private static IEnumerable<string> Names(GenOutput o) =>
    Walk(o.Shape.Elements).Select(e => e.Name!);

  private static IEnumerable<Element> Walk(IEnumerable<Element> es) =>
    es.SelectMany(e => e.Faces.Count > 0 ? [e] : Walk(e.Children));

  private static CheckResult Draws(GenOutput o, double parting) =>
    new Draw().Run(new CheckInput(o.Shape, Parting: parting));

  private static CheckResult Roles(GenOutput o) =>
    new Role().Run(
      new CheckInput(o.Shape, Record: o.Record, Settings: Fx.Settings)
    );

  [Fact]
  public void Pattern_draws_with_a_loose_piece() {
    Assert.Equal(
      1,
      new Draw()
        .Run(new CheckInput(ItemShape(Leaves(Base, Lug)), Parting: 8))
        .ExitCode
    );

    PatternResult r = Make(8, null, Base, Lug);

    Assert.Equal(0, Draws(r.Pattern, 8).ExitCode);
    Assert.Equal(["Base"], Names(r.Pattern));
    GenOutput loose = Assert.Single(r.Loose);
    Assert.Equal("pattern-castthing-loose1", loose.Name);
    Assert.Equal(["Lug"], Names(loose));
  }

  [Fact]
  public void Touching_loose_leaves_make_one_piece_and_apart_ones_two() {
    PatternResult touching = Make(
      8,
      null,
      Base,
      Bx("LugA", "[8, 3, 0]", "[10, 5, 8]"),
      Bx("LugB", "[10, 3, 0]", "[12, 5, 8]")
    );
    PatternResult apart = Make(
      8,
      null,
      Base,
      Bx("LugA", "[8, 3, 0]", "[10, 5, 8]"),
      Bx("LugB", "[-4, 3, 0]", "[-2, 5, 8]")
    );

    Assert.Equal(["LugA", "LugB"], Names(Assert.Single(touching.Loose)));
    Assert.Equal(
      ["pattern-castthing-loose1", "pattern-castthing-loose2"],
      apart.Loose.Select(o => o.Name)
    );
  }

  [Fact]
  public void Columns_no_single_leaf_frees_are_refused() {
    Assert.Throws<CastGenException>(() =>
      Make(
        8,
        null,
        Base,
        Bx("LugA", "[8, 3, 0]", "[12, 4, 8]"),
        Bx("LugB", "[8, 5, 0]", "[12, 6, 8]")
      )
    );
  }

  [Fact]
  public void Every_face_takes_the_pattern_role() {
    PatternResult r = Make(
      8,
      [new PatternPrint("Print", [2, 8, 2], [6, 10, 6])],
      Base,
      Lug
    );

    Assert.NotEmpty(r.Loose);
    foreach (GenOutput o in r.Loose.Prepend(r.Pattern)) {
      CheckResult role = Roles(o);
      Assert.Equal(0, role.ExitCode);
      Assert.Equal(["pattern"], o.Kinds);
    }
    Assert.All(
      Walk(r.Pattern.Shape.Elements).SelectMany(e => e.Faces),
      f => Assert.Equal("#wood", f.Value.Texture)
    );
    Assert.Contains(
      new KeyValuePair<string, string>(
        "wood",
        Fx.Settings.EditablePath("wood")
      ),
      r.Pattern.Shape.Textures
    );
  }

  [Fact]
  public void A_closed_pattern_is_cut_at_the_parting_into_drag_and_cope() {
    PatternResult r = Make(
      8,
      [new PatternPrint("Print", [2, 14, 2], [6, 16, 6])],
      Bx("Base", "[0, 2, 0]", "[8, 14, 8]")
    );

    Assert.Equal(
      ["Drag", "Cope"],
      r.Pattern.Shape.Elements.Select(e => e.Name)
    );
    Element drag = r.Pattern.Shape.Elements[0],
      cope = r.Pattern.Shape.Elements[1];
    Assert.Equal(["Base"], drag.Children.Select(e => e.Name));
    Assert.Equal(["Base_cope", "Print"], cope.Children.Select(e => e.Name));
    Assert.Equal(8, drag.Children[0].To![1]);
    Assert.Equal(8, cope.Children[0].From![1]);
    Assert.Equal(0, Draws(r.Pattern, 8).ExitCode);
    Assert.Equal(0, Roles(r.Pattern).ExitCode);
  }

  [Fact]
  public void An_open_pattern_is_flat_and_drawn_up() {
    PatternResult r = Make(null, null, Base);

    Assert.Equal(["Base"], r.Pattern.Shape.Elements.Select(e => e.Name));
    Assert.Empty(r.Pattern.Shape.Elements[0].Children);
    Assert.Equal(0, Draws(r.Pattern, 8).ExitCode);
  }

  [Fact]
  public void A_turned_leaf_crossing_the_parting_is_refused() {
    List<Leaf> leaves = Leaves(Bx("Ring", "[0, 2, 0]", "[8, 14, 8]"));
    leaves[0].Element.RotationY = 45;
    leaves[0].Element.RotationOrigin = [4, 8, 4];

    Assert.Throws<CastGenException>(() =>
      Patterns.Make(Ctx, "pattern-castring", leaves, [], 8, "spec")
    );
  }

  [Fact]
  public void An_element_over_sixteen_is_cut_into_parts() {
    PatternResult r = Make(null, null, Bx("Bed", "[0, 2, 0]", "[40, 4, 8]"));

    Assert.Equal(["Bed", "Bed_s2", "Bed_s3"], Names(r.Pattern));
    Assert.Equal(0, Draws(r.Pattern, 4).ExitCode);
    Assert.Equal(0, Roles(r.Pattern).ExitCode);
  }

  [Fact]
  public void The_input_hash_is_the_same_in_every_culture() {
    PatternPrint[] prints =
    [
      new PatternPrint("P", [1.5, 4, 1.5], [2.5, 5, 2.5]),
    ];
    CultureInfo was = CultureInfo.CurrentCulture;
    try {
      CultureInfo.CurrentCulture = new CultureInfo("en-US");
      string en = Make(2.5, prints, Base).Pattern.InputHash;
      CultureInfo.CurrentCulture = new CultureInfo("uk-UA");
      string uk = Make(2.5, prints, Base).Pattern.InputHash;
      Assert.Equal(en, uk);
    } finally {
      CultureInfo.CurrentCulture = was;
    }
  }
}
