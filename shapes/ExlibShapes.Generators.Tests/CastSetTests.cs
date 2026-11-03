using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Generators.Tests;

public class CastSetTests {
  private static readonly string Drawing = $$"""
    { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
      {{Fx.Cube("Body", "[0, 0, 0]", "[8, 4, 8]")}},
      {{Fx.Cube("Cap", "[0, 4, 0]", "[8, 5, 8]")}},
      {{Fx.Cube("Bolt1", "[1, 5, 1]", "[2, 6, 2]")}},
      {{Fx.Cube("Seat", "[0, 0, 8]", "[8, 1, 10]")}},
      {{Fx.Cube("Hole", "[3, 0, 3]", "[5, 4, 5]")}},
      {{Fx.Cube("Cav", "[10, 0, 0]", "[12, 4, 2]")}}
    ] } ] }
    """;

  private static string Record(
    string hole = DefaultHole,
    string capEntry = DefaultCap,
    string seatStage = "worked"
  ) =>
    $$"""
      { "schema": 1, "shape": "d.json", "kinds": ["casting"],
        "elements": {
          "Body": { "stage": "cast", "role": "cast" },
          {{capEntry}}
          "Bolt1": { "stage": "fastener", "role": "fastener" },
          "Seat": { "stage": "{{seatStage}}", "role": "cast" },
          "Hole": { "stage": "cast", "role": "cast" },
          "Cav": { "stage": "cast", "role": "cast" }
        },
        "features": {
          "seat": { "elements": ["Seat"], "origin": "planed", "faces": ["Seat/up"], "step": "plane" },
          "bore": { "elements": ["Hole"], "origin": "drilled", "hole": {{hole}} },
          "cav": { "elements": ["Cav"], "origin": "cored" }
        } }
      """;

  private const string DefaultHole =
    """{ "axis": "y", "centre": [4, 0, 4], "radius": 1, "depth": 4 }""";
  private const string DefaultCap =
    "\"Cap\": { \"stage\": \"cast\", \"role\": \"cast\" },";

  private static CastSetResult Run(
    string record,
    IReadOnlyList<Answer>? answers = null,
    IReadOnlyList<CoreSpec>? cores = null
  ) =>
    CastSet.Of(
      Fx.Ctx(Drawing, record, answers),
      Fx.Ctx(Drawing, record).Resolve("d.json", Fx.Pin),
      [new GroupSpec(["Root"], null, null)],
      cores ?? []
    );

  [Fact]
  public void A_bolt_tagged_fastener_is_absent() {
    CastSetResult r = Run(Record());

    Assert.Empty(r.Questions);
    Assert.DoesNotContain(r.Cast, l => l.Name == "Bolt1");
    Assert.DoesNotContain(r.Worked, l => l.Name == "Bolt1");
    Assert.Contains(r.Cast, l => l.Name == "Body");
  }

  [Fact]
  public void An_untagged_feature_returns_one_question_and_no_output() {
    CastSetResult r = Run(Record(capEntry: ""));

    Question q = Assert.Single(r.Questions);
    Assert.Equal("Cap", q.Feature);
    Assert.Equal(["cast", "worked", "fastener"], q.Options);
    Assert.Empty(r.Cast);
    Assert.Empty(r.Worked);
    Assert.Empty(r.Cores);
  }

  [Fact]
  public void A_stored_answer_settles_the_question_until_the_feature_changes() {
    string record = Record(capEntry: "");
    Shape drawn = Reader.Read(Drawing);
    string sha = CastSet.FeatureSha(Select.Groups(drawn, ["Root/Cap"]));
    Answer Ans(string featureSha) =>
      PartRecord
        .Read(
          $$"""{ "schema": 1, "answers": [ { "feature": "Cap", "question": "q", "answer": "cast", "by": "fallen", "date": "2026-11-01", "featureSha": "{{featureSha}}" } ] }"""
        )
        .Answers[0];

    CastSetResult settled = Run(record, [Ans(sha)]);
    CastSetResult stale = Run(record, [Ans("0123")]);

    Assert.Empty(settled.Questions);
    Assert.Contains(settled.Cast, l => l.Name == "Cap");
    Assert.Single(stale.Questions);
  }

  [Fact]
  public void A_cap_is_cast_beside_its_body() {
    CastSetResult r = Run(Record());

    Assert.Contains(r.Cast, l => l.Name == "Body");
    Assert.Contains(r.Cast, l => l.Name == "Cap");
  }

  [Fact]
  public void Worked_features_are_set_aside_for_their_step() {
    CastSetResult r = Run(Record());

    Assert.DoesNotContain(r.Cast, l => l.Name == "Seat");
    Assert.Equal("Seat", Assert.Single(r.Worked).Name);
  }

  [Fact]
  public void A_cast_element_of_a_planed_feature_stays_in_the_cast_set() {
    CastSetResult r = Run(Record(seatStage: "cast"));

    Assert.Contains(r.Cast, l => l.Name == "Seat" && l.Role == "cast");
    Assert.Empty(r.Worked);
  }

  [Fact]
  public void A_drilled_feature_is_filled_with_boxes_covering_its_hole() {
    CastSetResult r = Run(Record());

    Assert.DoesNotContain(r.Cast, l => l.Name == "Hole");
    List<Leaf> fill = [.. r.Cast.Where(l => l.Name.StartsWith("bore_fill"))];
    Assert.Equal(3, fill.Count);
    Box u = BlankResult.Union(fill.Select(l => l.Box))!.Value;
    Fx.Near(new Vec3(3, 0, 3), u.Lo);
    Fx.Near(new Vec3(5, 4, 5), u.Hi);
    Assert.All(fill, l => Assert.Equal("cast", l.Role));
    Assert.All(
      fill,
      l => Assert.Equal("#cast-iron1", l.Element.Faces[0].Value.Texture)
    );
  }

  [Fact]
  public void A_polygon_hole_is_filled_by_the_polygon() {
    string square =
      """{ "axis": "y", "centre": [4, 0, 4], "polygon": [[-1, -1], [1, -1], [1, 1], [-1, 1]], "depth": 2 }""";

    CastSetResult r = Run(Record(hole: square));

    Leaf fill = Assert.Single(r.Cast, l => l.Name.StartsWith("bore_fill"));
    Fx.Near(new Vec3(3, 0, 3), fill.Box.Lo);
    Fx.Near(new Vec3(5, 2, 5), fill.Box.Hi);
  }

  [Fact]
  public void A_hole_with_an_unknown_axis_is_refused() {
    string record = Record()
      .Replace(DefaultHole, "{ \"axis\": \"q\", \"depth\": 1 }");

    var e = Assert.Throws<CastGenException>(() => Run(record));
    Assert.Contains("bore", e.Message);
  }

  [Fact]
  public void A_cored_feature_is_handed_on_with_its_print() {
    CastSetResult r = Run(
      Record(),
      cores: [new CoreSpec("cav", JToken.Parse("""{ "d": 2 }"""))]
    );

    Assert.DoesNotContain(r.Cast, l => l.Name == "Cav");
    CoreHandoff core = Assert.Single(r.Cores);
    Assert.Equal("cav", core.Feature);
    Assert.Equal("Cav", Assert.Single(core.Elements).Name);
    Assert.Equal(2, (int)core.Print!["d"]!);
  }

  [Fact]
  public void A_spec_core_naming_no_feature_is_refused() {
    var e = Assert.Throws<CastGenException>(() =>
      Run(Record(), cores: [new CoreSpec("ghost", null)])
    );
    Assert.Contains("ghost", e.Message);
  }

  [Fact]
  public void Set_groups_make_one_item() {
    GenContext ctx = Fx.Ctx(Drawing, Record());
    var set = new SetSpec(
      "cast-m-brackets",
      "small",
      [
        new GroupSpec(["Root/Body"], null, null),
        new GroupSpec(["Root/Cap"], new Placement([0, 0, 0], [20, 0, 0]), null),
      ]
    );

    CastSetResult r = CastSet.ForSet(ctx, Fx.Src(), set);

    Assert.Equal(["Body", "Cap"], r.Cast.Select(l => l.Name).Order());
    Fx.Near(20, r.Cast.Single(l => l.Name == "Cap").Box.Lo.X);
    Fx.Near(0, r.Cast.Single(l => l.Name == "Body").Box.Lo.X);
  }

  private static readonly string Bed = $$"""
    { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
      {{Fx.Cube("SecA", "[0, 0, 0]", "[10, 4, 8]")}},
      {{Fx.Cube("SecB", "[10, 0, 0]", "[20, 4, 8]")}},
      {{Fx.Cube("SecC", "[20, 0, 0]", "[30, 4, 8]")}}
    ] } ] }
    """;

  private const string BedRecord = """
    { "schema": 1, "elements": {
      "SecA": { "stage": "cast" }, "SecB": { "stage": "cast" }, "SecC": { "stage": "cast" } } }
    """;

  private static MachineSpec Lathe(params string[] from) =>
    Spec.ReadMachine(
      JObject.Parse(
        $$"""
        { "machine": "lathe", "source": { "shape": "d.json", "sha": "0000000" },
          "users": [
            { "blank": "castbedsection", "groups": ["Root/SecA"], "worked": { "item": "lathe-bedsection-head", "steps": ["plane"] } },
            { "blank": "castbedsection", "groups": ["Root/SecB"], "worked": { "item": "lathe-bedsection-mid", "steps": ["plane"] } },
            { "blank": "castbedsection", "groups": ["Root/SecC"], "worked": { "item": "lathe-bedsection-tail", "steps": ["plane"] } } ],
          "joined": [ { "item": "lathe-bed", "from": [{{string.Join(
          ",",
          from.Select(f => $"\"{f}\"")
        )}}],
            "steps": [ { "step": "plane", "features": ["headseat"] }, { "step": "drill", "features": ["holes"] } ] } ] }
        """
      )
    );

  [Fact]
  public void Joined_item_works_the_assembled_sections() {
    MachineSpec m = Lathe(
      "lathe-bedsection-head",
      "lathe-bedsection-mid",
      "lathe-bedsection-tail"
    );
    GenContext ctx = Fx.Ctx(Bed, BedRecord);

    JoinedResult j = CastSet.Joined(ctx, m, m.Joined[0]);

    Assert.Equal("lathe-bed", j.Item);
    Assert.Equal(["SecA", "SecB", "SecC"], j.Cast.Select(l => l.Name));
    Box u = BlankResult.Union(j.Cast.Select(l => l.Box))!.Value;
    Fx.Near(0, u.Lo.X);
    Fx.Near(30, u.Hi.X);
    Assert.Equal(["plane", "drill"], j.Steps.Select(s => s.Step));
    Assert.Equal(["headseat"], j.Steps[0].Features);
  }

  [Fact]
  public void A_joined_section_no_user_makes_is_refused() {
    MachineSpec m = Lathe("lathe-bedsection-head", "lathe-bedsection-gone");
    GenContext ctx = Fx.Ctx(Bed, BedRecord);

    var e = Assert.Throws<CastGenException>(() =>
      CastSet.Joined(ctx, m, m.Joined[0])
    );
    Assert.Contains("lathe-bedsection-gone", e.Message);
  }
}
