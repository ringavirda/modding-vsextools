using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Generators.Tests;

public class WorkedTests {
  private static string Cube(string name, string from, string to) =>
    $$"""{ "name": "{{name}}", "from": {{from}}, "to": {{to}}, "faces": { {{string.Join(
      ", ",
      new[] { "north", "east", "south", "west", "up", "down" }.Select(f => $"\"{f}\": {{ \"texture\": \"#cast-iron1\" }}")
    )}} } }""";

  private static string Drawing(string baseTo, string extra = "") =>
    $$"""
      { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
        {{Cube("Base", "[0, 0, 0]", baseTo)}},
        {{Cube("Way", "[2, 3, 2]", "[14, 4, 14]")}}{{extra}}
      ] } ] }
      """;

  private const string FormDrawing = """
    { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
      { "name": "Base", "from": [0, 0, 0], "to": [16, 4, 16], "faces": { "up": { "texture": "#cast-iron1" }, "east": { "texture": "#cast-iron1" }, "south": { "texture": "#cast-iron1" }, "west": { "texture": "#cast-iron1" }, "north": { "texture": "#cast-iron1" }, "down": { "texture": "#cast-iron1" } } }
    ] } ] }
    """;

  private const string FormRecord = """
    { "schema": 1, "elements": { "Base": { "stage": "cast", "role": "cast" } },
      "features": { "ways": { "elements": ["Base"], "origin": "cast", "faces": ["Base/up"], "step": "plane" } } }
    """;

  private static string UserRecord(string extra = "", string features = "") =>
    $$"""
      { "schema": 1,
        "elements": { "Base": { "stage": "cast", "role": "cast" }, "Way": { "stage": "worked", "role": "cast" }{{extra}} },
        "features": { "vways": { "elements": ["Way"], "origin": "planed", "faces": ["Way/up"], "step": "plane" }{{features}} },
        "steps": [ { "id": "plane", "faces": ["Base/up"] } ] }
      """;

  private static MachineSpec Machine(string step) =>
    Spec.ReadMachine(
      JObject.Parse(
        $$"""
        { "machine": "planer", "source": { "shape": "m.json", "sha": "0000000" },
          "users": [ { "blank": "castbed", "groups": ["Root"],
                       "worked": { "item": "planer-bed", "from": "castbed",
                                   "steps": [ {{{step}}} ] } } ] }
        """
      )
    );

  private const string Plane =
    """ "step": "plane", "cuts": ["ways"], "features": ["vways"] """;

  private static BlankSpec BlankSpecOf() =>
    Spec.ReadBlank(
      JObject.Parse(
        """
        { "blank": "castbed", "size": [16, 4, 16], "cutFaces": "from-record",
          "form": { "shape": "b.json", "sha": "0000000", "groups": ["Root"] } }
        """
      )
    );

  private static GenContext Ctx(
    string drawing,
    string record,
    IReadOnlyList<Answer>? answers = null
  ) =>
    Fx.Ctx(
      new Dictionary<string, (string, string)> {
        ["b.json"] = (FormDrawing, FormRecord),
        ["m.json"] = (drawing, record),
      },
      answers
    );

  private static WorkedResult Run(
    string drawing,
    string record,
    string step = Plane,
    IReadOnlyList<Answer>? answers = null
  ) {
    GenContext ctx = Ctx(drawing, record, answers);
    BlankResult blank = Blank.Build(ctx, BlankSpecOf());
    MachineSpec machine = Machine(step);
    return Worked.ForUser(ctx, machine, machine.Users[0], blank);
  }

  private static string Texture(GenOutput o, string element, string face) =>
    o
      .Shape.Elements.Single(e => e.Name == element)
      .Faces.Single(kv => kv.Key == face)
      .Value.Texture!;

  [Fact]
  public void Worked_steps_record_machine_and_hand() {
    WorkedResult r = Run(Drawing("[16, 4, 16]"), UserRecord());
    WorkedResult named = Run(
      Drawing("[16, 4, 16]"),
      UserRecord(),
      """ "step": "plane", "machine": "shaper", "cuts": ["ways"] """
    );

    GenOutput o = Assert.Single(r.Outputs);
    Step step = Assert.Single(o.Record.Steps);
    Assert.Equal("planer", step.Tool);
    Assert.Equal("chipfile", step.Hand);
    Assert.Equal("shaper", Assert.Single(named.Outputs).Record.Steps[0].Tool);
    Assert.Equal(["worked", "item"], o.Kinds);
    Assert.Equal("castbed", (string?)o.Record.Carried["blank"]);
    Assert.Equal("#cast-iron-turned1", Texture(o, "Base", "up"));
    Assert.Equal("#cast-iron1", Texture(o, "Base", "east"));
    Assert.Equal("#cast-iron-turned1", Texture(o, "Way", "up"));
    Assert.Equal("worked", o.Record.Elements["Way"].Stage);
    Assert.Equal(["Base/up", "Way/up"], step.Faces);
  }

  [Fact]
  public void A_machine_outside_the_steps_row_is_refused() {
    Assert.Throws<CastGenException>(() =>
      Run(
        Drawing("[16, 4, 16]"),
        UserRecord(),
        """ "step": "plane", "machine": "lathe", "cuts": ["ways"] """
      )
    );
  }

  [Fact]
  public void Worked_item_refuses_an_unmatched_user() {
    string boss = ",\n" + Cube("Boss", "[4, 4, 4]", "[8, 6, 8]");
    WorkedResult r = Run(
      Drawing("[16, 4, 16]", boss),
      UserRecord(
        extra: ", \"Boss\": { \"stage\": \"cast\", \"role\": \"cast\" }",
        features: ", \"boss\": { \"elements\": [\"Boss\"], \"origin\": \"cast\", \"form\": \"boss\" }"
      )
    );

    Assert.Empty(r.Outputs);
    Assert.Empty(r.Questions);
    Assert.Equal("form 'boss': 1, the blank has 0", Assert.Single(r.Failures));
  }

  [Fact]
  public void Redraw_row_names_the_difference() {
    WorkedResult r = Run(Drawing("[16, 6, 16]"), UserRecord());

    Assert.Equal(["redraw: planer-bed y drawn 6 blank 4"], r.Redraws);
    Assert.Empty(r.Outputs);
    Question q = Assert.Single(r.Questions);
    Assert.Equal("blankmatch/y", q.Feature);
  }

  [Fact]
  public void An_answered_out_of_band_user_gets_its_item() {
    var answer = new Answer {
      Feature = "blankmatch/y",
      FeatureSha = Provenance.Sha256Of(
        "y: spans 6 of 4 (1.5), the band is 0.75 to 1"
      ),
      Text = "accept",
    };

    WorkedResult r = Run(
      Drawing("[16, 6, 16]"),
      UserRecord(),
      answers: [answer]
    );

    Assert.Single(r.Outputs);
    Assert.Empty(r.Questions);
    Assert.Equal(["redraw: planer-bed y drawn 6 blank 4"], r.Redraws);
  }

  [Fact]
  public void Two_users_of_one_blank_make_two_worked_items() {
    GenContext ctx = Ctx(Drawing("[16, 4, 16]"), UserRecord());
    BlankResult blank = Blank.Build(ctx, BlankSpecOf());
    MachineSpec withWay = Machine(Plane);
    MachineSpec plain = Machine(""" "step": "plane", "cuts": ["ways"] """);

    GenOutput a = Assert.Single(
      Worked.ForUser(ctx, withWay, withWay.Users[0], blank).Outputs
    );
    GenOutput b = Assert.Single(
      Worked.ForUser(ctx, plain, plain.Users[0], blank).Outputs
    );

    Assert.Contains(a.Shape.Elements, e => e.Name == "Way");
    Assert.DoesNotContain(b.Shape.Elements, e => e.Name == "Way");
    Assert.NotEqual(a.InputHash, b.InputHash);
  }

  [Fact]
  public void A_blank_worked_item_cuts_its_features_and_chains_from_an_earlier_one() {
    GenContext ctx = Ctx(Drawing("[16, 4, 16]"), UserRecord());
    BlankResult blank = Blank.Build(ctx, BlankSpecOf());
    var first = new WorkedSpec(
      "castbed-planed",
      null,
      [new StepSpec("plane", null, ["ways"], [])]
    );
    var second = new WorkedSpec(
      "castbed-drilled",
      "castbed-planed",
      [new StepSpec("drill", null, [], [])]
    );

    GenOutput one = Worked.ForBlank(ctx, blank, first);
    GenOutput two = Worked.ForBlank(ctx, blank, second, [one]);

    Assert.Equal("#cast-iron-turned1", Texture(one, "Base", "up"));
    Assert.Equal("#cast-iron-turned1", Texture(two, "Base", "up"));
    Assert.Equal("drillpress", two.Record.Steps.Single().Tool);
    Assert.Throws<CastGenException>(() => Worked.ForBlank(ctx, blank, second));
  }
}
