using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;
using static ExpandedLib.Shapes.Generators.Tests.FlushFixtures;

namespace ExpandedLib.Shapes.Generators.Tests;

internal static class CoreFixtures {
  public static readonly GenContext Ctx = Fx.Ctx(
    """{ "elements": [] }""",
    """{ "schema": 1 }"""
  );

  public static CoreHandoff Handoff(JToken? print, params Box3[] boxes) =>
    new("slot", Leaves(boxes), print);

  public static PartRecord SourceRecord(params string[] names) =>
    PartRecord.Read(
      $$"""
      { "schema": 1, "kinds": ["casting"],
        "features": { "slot": { "elements": [{{string.Join(
        ", ",
        names.Select(n => $"\"{n}\"")
      )}}], "origin": "cored" } } }
      """
    );

  public static GenOutput Core(JToken? print, params Box3[] boxes) =>
    Cores.Make(
      Ctx,
      "drysandcore-castthing",
      "cast-thing",
      Handoff(print, boxes),
      "spec"
    );

  public static CheckResult Fit(GenOutput core, params Box3[] boxes) =>
    new Corefit().Run(
      new CheckInput(
        core.Shape,
        Record: core.Record,
        Source: Drawn(boxes),
        SourceRecord: SourceRecord([.. boxes.Select(b => b.Name)]),
        Settings: Fx.Settings
      )
    );

  public static IEnumerable<Element> Walk(IEnumerable<Element> es) =>
    es.SelectMany(e => e.Faces.Count > 0 ? [e] : Walk(e.Children));
}

public class CoresTests {
  private static Box3 Slot => Bx("Slot", "[2, 0, 0]", "[14, 2, 3]");

  [Fact]
  public void Core_passes_corefit_with_a_print_at_each_end() {
    GenOutput core = CoreFixtures.Core(null, Slot);

    CheckResult r = CoreFixtures.Fit(core, Slot);

    Assert.Equal(0, r.ExitCode);
    Assert.Equal(5, r.Enumerated);
    Assert.Equal(
      ["Print1", "Print2", "Slot"],
      CoreFixtures.Walk(core.Shape.Elements).Select(e => e.Name!).Order()
    );
    Element lo = core.Shape.Elements.First(e => e.Name == "Print1");
    Assert.Equal([0.0, 0, 0], lo.From);
    Assert.Equal([2.0, 2, 3], lo.To);
  }

  [Fact]
  public void Print_object_names_its_length_and_run() {
    GenOutput core = CoreFixtures.Core(
      JObject.Parse("""{ "length": 3, "run": "z" }"""),
      Slot
    );

    Assert.Equal("z", (string?)core.Record.Carried["core"]!["run"]);
    Element lo = core.Shape.Elements.First(e => e.Name == "Print1");
    Assert.Equal([2.0, 0, -3], lo.From);
    Assert.Equal([14.0, 2, 0], lo.To);
    Assert.Equal(0, CoreFixtures.Fit(core, Slot).ExitCode);
  }

  [Fact]
  public void A_number_is_the_print_length_on_the_longest_axis() {
    GenOutput core = CoreFixtures.Core(JToken.Parse("1.5"), Slot);

    Assert.Equal("x", (string?)core.Record.Carried["core"]!["run"]);
    Element hi = core.Shape.Elements.First(e => e.Name == "Print2");
    Assert.Equal([14.0, 0, 0], hi.From);
    Assert.Equal([15.5, 2, 3], hi.To);
  }

  [Fact]
  public void Core_cuts_elements_over_16_and_keeps_no_gap() {
    Box3 slot = Bx("Slot", "[0, 0, 0]", "[40, 2, 3]");

    GenOutput core = CoreFixtures.Core(null, slot);

    Assert.All(
      CoreFixtures.Walk(core.Shape.Elements),
      e =>
        Assert.All(
          Enumerable.Range(0, 3),
          a => Assert.True(e.To![a] - e.From![a] <= 16 + 1e-9)
        )
    );
    Assert.True(CoreFixtures.Walk(core.Shape.Elements).Count() > 3);
    Assert.Equal(0, CoreFixtures.Fit(core, slot).ExitCode);
  }

  [Fact]
  public void Core_is_sand_to_the_role_gate() {
    GenOutput core = CoreFixtures.Core(null, Slot);

    CheckResult r = new Role().Run(
      new CheckInput(core.Shape, Record: core.Record, Settings: Fx.Settings)
    );

    Assert.Equal(0, r.ExitCode);
    Assert.All(
      core.Shape.Elements.SelectMany(e => e.Faces),
      kv => Assert.Equal("#andesite", kv.Value.Texture)
    );
  }

  [Fact]
  public void Core_refuses_an_element_named_like_a_print() {
    Box3 odd = Bx("Print9", "[0, 0, 0]", "[4, 2, 3]");

    Assert.Throws<CastGenException>(() => CoreFixtures.Core(null, odd));
  }

  [Theory]
  [InlineData("\"long\"")]
  [InlineData("0")]
  [InlineData("""{ "length": 2, "run": "w" }""")]
  [InlineData("""{ "width": 2 }""")]
  public void Core_refuses_a_malformed_print(string print) =>
    Assert.Throws<CastGenException>(() =>
      CoreFixtures.Core(JToken.Parse(print), Slot)
    );
}
