using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Generators.Tests;

public class CastGenRunTests {
  private static string Cube(string name, string from, string to) =>
    $$"""{ "name": "{{name}}", "from": {{from}}, "to": {{to}}, "faces": { {{string.Join(
      ", ",
      new[] { "north", "east", "south", "west", "up", "down" }.Select(f => $"\"{f}\": {{ \"texture\": \"#cast-iron1\" }}")
    )}} } }""";

  private static readonly string FormDrawing = $$"""
    { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
      {{Cube("Base", "[0, 0, 0]", "[16, 4, 16]")}}
    ] } ] }
    """;

  private const string FormRecord = """
    { "schema": 1, "elements": { "Base": { "stage": "cast", "role": "cast" } },
      "features": { "ways": { "elements": ["Base"], "origin": "cast", "faces": ["Base/up"], "step": "plane" } } }
    """;

  private static readonly string Drawing = $$"""
    { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
      {{Cube("Base", "[0, 0, 0]", "[16, 4, 16]")}},
      {{Cube("Way", "[2, 3, 2]", "[14, 4, 14]")}},
      {{Cube("Cav", "[6, 0, 6]", "[10, 4, 10]")}}
    ] } ] }
    """;

  private const string Record = """
    { "schema": 1,
      "elements": { "Base": { "stage": "cast", "role": "cast" }, "Way": { "stage": "worked", "role": "cast" },
                    "Cav": { "stage": "cast", "role": "cast" } },
      "features": { "vways": { "elements": ["Way"], "origin": "planed", "faces": ["Way/up"], "step": "plane" },
                    "cav": { "elements": ["Cav"], "origin": "cored" } },
      "steps": [ { "id": "plane", "faces": ["Base/up"] } ] }
    """;

  private const string FloorLie =
    """{ "x": { "axis": "x", "offset": 4 }, "y": { "axis": "y", "offset": 10 }, "z": { "axis": "z", "offset": -8 } }""";

  private static readonly string SmallFormDrawing = FormDrawing.Replace(
    "[16, 4, 16]",
    "[8, 4, 8]"
  );

  private static GenContext Ctx(
    string drawing = "",
    string form = "",
    string record = "",
    string formRecord = ""
  ) =>
    new(
      Fx.Settings,
      (shape, sha) => {
        (string s, string r) = shape switch {
          "b.json" => (
            form.Length > 0 ? form : FormDrawing,
            formRecord.Length > 0 ? formRecord : FormRecord
          ),
          "m.json" => (
            drawing.Length > 0 ? drawing : Drawing,
            record.Length > 0 ? record : Record
          ),
          _ => throw new InvalidOperationException(shape),
        };
        return new ResolvedSource(
          Reader.Read(s),
          PartRecord.Read(r),
          Provenance.Sha256Of(s)
        );
      },
      null,
      null,
      code => JObject.Parse(BlankFile(code))
    );

  private static string BlankFile(string code) =>
    $$"""
      { "blank": "{{code}}", "size": [16, 4, 16], "cutFaces": "from-record",
        "form": { "shape": "b.json", "sha": "0000000", "groups": ["Root"] } }
      """;

  private static JObject BlankEntry(
    string station,
    string lie,
    string extra = ""
  ) =>
    JObject.Parse(
      $$"""
      { "blank": "castbed", "size": [16, 4, 16], "cutFaces": "from-record",
        "form": { "shape": "b.json", "sha": "0000000", "groups": ["Root"] },
        "station": "{{station}}", "lie": {{lie}} {{extra}} }
      """
    );

  private static JObject MachineEntry(
    string castings = "",
    string users = ""
  ) =>
    JObject.Parse(
      $$"""
      { "machine": "planer", "source": { "shape": "m.json", "sha": "0000000" },
        "users": [ {{users}} ], "castings": [ {{castings}} ] }
      """
    );

  private const string User = """
    { "blank": "castbed", "groups": ["Root"],
      "worked": { "item": "planer-bed", "from": "castbed",
                  "steps": [ { "step": "plane", "cuts": ["ways"], "features": ["vways"] } ] } }
    """;

  private static string Casting(
    string station,
    string lie,
    string extra = ""
  ) =>
    $$"""
      { "item": "cast-planer-bed", "source": { "groups": ["Root"] },
        "station": "{{station}}", "lie": {{lie}} {{extra}} }
      """;

  private static GenResult Run(GenContext ctx, JObject spec) =>
    new CastGen().Run(ctx, spec);

  private static string Bytes(GenResult r) =>
    string.Join(
      "\n",
      r.Outputs.Select(o =>
        $"{o.Name}|{string.Join(",", o.Kinds)}|{CanonicalWriter.Write(o.Shape)}|{o.Record.Write()}|{o.InputHash}"
      )
    );

  private static double MaxX(Element e) =>
    e.Children.Count > 0 ? e.Children.Max(MaxX) : e.To![0];

  [Fact]
  public void Blank_file_runs_to_its_named_outputs_with_kinds() {
    JObject spec = BlankEntry(
      "floor",
      FloorLie,
      """, "worked": [ { "item": "castbed-planed", "steps": [ { "step": "plane", "cuts": ["ways"] } ] } ]"""
    );

    GenResult r = Run(Ctx(), spec);

    Assert.Empty(r.Questions);
    Assert.Equal(
      [
        "castbed",
        "pattern-castbed",
        "floorflask-filling-castbed",
        "castbed-planed",
      ],
      r.Outputs.Select(o => o.Name)
    );
    Assert.Equal(["casting"], r.Outputs[0].Kinds);
    Assert.Equal(["pattern"], r.Outputs[1].Kinds);
    Assert.Equal(["filling"], r.Outputs[2].Kinds);
    Assert.Equal(["worked", "item"], r.Outputs[3].Kinds);
    Assert.Equal(
      "floor",
      (string?)r.Outputs[2].Record.Carried["pour"]!["station"]
    );
  }

  [Fact]
  public void Machine_file_runs_to_its_named_outputs_with_kinds() {
    JObject spec = MachineEntry(
      Casting(
        "floor",
        FloorLie,
        """, "cores": [ { "feature": "cav", "print": 2 } ]"""
      ),
      User
    );

    GenResult r = Run(Ctx(), spec);

    Assert.Empty(r.Questions);
    Assert.Equal(
      [
        "planer-bed",
        "drysandcore-cast-planer-bed",
        "corebox-cast-planer-bed",
        "cast-planer-bed",
        "pattern-cast-planer-bed",
        "floorflask-filling-cast-planer-bed",
      ],
      r.Outputs.Select(o => o.Name)
    );
    Assert.Equal(
      [
        ["worked", "item"],
        ["core"],
        ["corebox"],
        ["casting"],
        ["pattern"],
        ["filling"],
      ],
      r.Outputs.Select(o => o.Kinds.ToArray())
    );
    GenOutput pour = r.Outputs[5];
    JToken core = Assert.Single(pour.Record.Carried["pour"]!["cores"]!);
    Assert.Equal("drysandcore-cast-planer-bed", (string?)core["item"]);
    Assert.Contains(
      r.Outputs[4].Shape.Elements,
      e => e.Name is "Print1" or "Print2"
    );
  }

  [Fact]
  public void Two_runs_give_identical_serialized_bytes() {
    JObject spec = MachineEntry(Casting("floor", FloorLie), User);

    string a = Bytes(Run(Ctx(), spec));
    string b = Bytes(Run(Ctx(), spec));

    Assert.NotEmpty(a);
    Assert.Equal(a, b);
  }

  [Fact]
  public void Filling_is_at_the_entrys_station() {
    JObject spec = BlankEntry(
      "pit",
      """{ "x": { "axis": "x", "offset": 4 }, "y": { "axis": "y", "offset": 10 }, "z": { "axis": "z", "offset": -8 } }"""
    );

    GenResult r = Run(Ctx(), spec);

    GenOutput filling = r.Outputs.Single(o => o.Kinds.Contains("filling"));
    Assert.Equal("pitflask-filling-castbed", filling.Name);
    Assert.Equal("pit", (string?)filling.Record.Carried["pour"]!["station"]);
  }

  [Fact]
  public void A_closed_pour_has_its_cope() {
    JObject spec = BlankEntry(
      "long",
      """{ "x": { "axis": "x", "offset": 8 }, "y": { "axis": "y", "offset": 4 }, "z": { "axis": "z", "offset": -30 } }"""
    );

    GenResult r = Run(Ctx(), spec);

    Assert.Contains(r.Outputs, o => o.Name == "longflask-filling-castbed");
    GenOutput cope = r.Outputs.Single(o =>
      o.Name == "longflask-cope-filling-castbed"
    );
    Assert.Equal("cope", (string?)cope.Record.Carried["pour"]!["half"]);
  }

  [Fact]
  public void A_turned_lie_writes_the_cast_item_in_its_frame() {
    const string turnedLie =
      """{ "x": { "axis": "z", "offset": 8 }, "y": { "axis": "y", "offset": 4 }, "z": { "axis": "-x", "offset": -14 } }""";
    const string plainLie =
      """{ "x": { "axis": "x", "offset": 8 }, "y": { "axis": "y", "offset": 4 }, "z": { "axis": "z", "offset": -30 } }""";

    GenResult turned = Run(Ctx(), BlankEntry("long", turnedLie));
    GenResult plain = Run(Ctx(), BlankEntry("long", plainLie));

    GenOutput item = turned.Outputs.Single(o => o.Name == "castbed");
    GenOutput itemPlain = plain.Outputs.Single(o => o.Name == "castbed");
    Assert.Equal(
      CanonicalWriter.Write(itemPlain.Shape),
      CanonicalWriter.Write(item.Shape)
    );
    JToken at = turned
      .Outputs.Single(o => o.Name == "longflask-filling-castbed")
      .Record.Carried["pour"]!["pieces"]![0]!["at"]!;
    Assert.Equal(
      [0, 0, 1, 8, 0, 1, 0, 4, -1, 0, 0, -14, 0, 0, 0, 1],
      at.Select(v => (double)v)
    );
  }

  [Fact]
  public void A_closed_pour_takes_a_side_lie() {
    const string sideLie =
      """{ "x": { "axis": "-y", "offset": 10 }, "y": { "axis": "x", "offset": 4 }, "z": { "axis": "z", "offset": -30 } }""";

    GenResult r = Run(Ctx(form: SmallFormDrawing), BlankEntry("long", sideLie));

    Assert.Contains(r.Outputs, o => o.Name == "longflask-filling-castbed");
    Assert.Contains(r.Outputs, o => o.Name == "longflask-cope-filling-castbed");
    GenOutput item = r.Outputs.Single(o => o.Name == "castbed");
    Assert.All(item.Shape.Elements, e => Assert.True(MaxX(e) <= 9));
    GenOutput pattern = r.Outputs.Single(o => o.Name == "pattern-castbed");
    Assert.All(pattern.Shape.Elements, e => Assert.True(MaxX(e) <= 11));
  }

  [Fact]
  public void A_sunk_leaf_is_drawn_unmoved_where_that_keeps_the_pattern_whole() {
    string form = $$"""
      { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
        {{Cube("Arm", "[6, 1, 6]", "[10, 4, 10]")}},
        {{Cube("Base", "[0, 0, 0]", "[8, 4, 8]")}}
      ] } ] }
      """;
    const string formRecord = """
      { "schema": 1,
        "elements": { "Arm": { "stage": "cast", "role": "cast" }, "Base": { "stage": "cast", "role": "cast" } },
        "features": { "ways": { "elements": ["Base"], "origin": "cast", "faces": ["Base/south"], "step": "plane" } } }
      """;
    JObject open = JObject.Parse(
      """
      { "blank": "castarm", "size": [10, 4, 10], "cutFaces": "from-record",
        "form": { "shape": "b.json", "sha": "0000000", "groups": ["Root"] } }
      """
    );

    GenResult r = Run(Ctx(form: form, formRecord: formRecord), open);

    GenOutput item = r.Outputs.Single(o => o.Name == "castarm");
    Assert.Equal(
      [("Arm", "sink")],
      FlushMoves.Read(item.Record).Select(m => (m.Element, m.Rule))
    );
    Assert.DoesNotContain(r.Outputs, o => o.Name.Contains("-loose"));
  }

  [Fact]
  public void Cast_item_keeps_its_groups() {
    string grouped = $$"""
      { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
        { "name": "Left", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
          {{Cube("Base", "[0, 0, 0]", "[16, 4, 16]")}} ] },
        { "name": "Right", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
          {{Cube("Cap", "[2, 4, 2]", "[14, 6, 14]")}},
          {{Cube("Lug", "[6, 6, 6]", "[10, 8, 10]")}} ] }
      ] } ] }
      """;
    const string groupedRecord = """
      { "schema": 1, "elements": { "Base": { "stage": "cast", "role": "cast" },
        "Cap": { "stage": "cast", "role": "cast" }, "Lug": { "stage": "cast", "role": "cast" } } }
      """;
    JObject spec = MachineEntry(
      """{ "item": "cast-kit", "groups": [ { "groups": ["Root/Left"] }, { "groups": ["Root/Right"] } ] }"""
    );

    GenResult r = Run(Ctx(grouped, record: groupedRecord), spec);

    Shape item = r.Outputs.Single(o => o.Name == "cast-kit").Shape;
    Assert.Equal(["Left", "Right"], item.Elements.Select(e => e.Name));
    Assert.Equal(["Base"], item.Elements[0].Children.Select(e => e.Name));
    Assert.NotEmpty(item.Textures);
    Assert.Equal(["Cap", "Lug"], item.Elements[1].Children.Select(e => e.Name));
  }

  [Fact]
  public void A_group_laid_apart_by_its_lie_is_recorded_and_passes_allowance() {
    string grouped = $$"""
      { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
        { "name": "Left", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
          {{Cube("Base", "[0, 0, 0]", "[16, 4, 16]")}} ] },
        { "name": "Right", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
          {{Cube("Cap", "[2, 4, 2]", "[14, 6, 14]")}} ] }
      ] } ] }
      """;
    const string groupedRecord = """
      { "schema": 1, "elements": { "Base": { "stage": "cast", "role": "cast" },
        "Cap": { "stage": "cast", "role": "cast" } } }
      """;
    JObject spec = MachineEntry(
      """
      { "item": "cast-kit", "groups": [ { "groups": ["Root/Left"], "lie": { "x": { "axis": "x", "offset": 0 }, "y": { "axis": "y", "offset": 0 }, "z": { "axis": "z", "offset": 0 } } },
        { "groups": ["Root/Right"], "lie": { "x": { "axis": "x", "offset": 3 }, "y": { "axis": "y", "offset": -1 }, "z": { "axis": "z", "offset": -30 } } } ] }
      """
    );
    GenContext ctx = Ctx(grouped, record: groupedRecord);

    GenResult r = Run(ctx, spec);

    GenOutput item = r.Outputs.Single(o => o.Name == "cast-kit");
    Assert.Equal(
      new Vec3(3, -1, -30),
      Assert.Single(Lays.Read(item.Record), k => k.Key == "Cap").Value
    );
    Assert.DoesNotContain("Base", Lays.Read(item.Record).Keys);
    ResolvedSource src = ctx.Resolve("m.json", "0000000");
    CheckResult got = new Allowance().Run(
      new CheckInput(
        item.Shape,
        Record: item.Record,
        Source: src.Shape,
        SourceRecord: src.Record
      )
    );
    Assert.Equal(0, got.ExitCode);
  }

  [Fact]
  public void A_cut_face_naming_a_group_gets_its_allowance_and_its_coplanar_pair_is_left() {
    string ribbed = $$"""
      { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
        { "name": "Rib", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
          {{Cube("R1", "[0, 0, 0]", "[16, 4, 16]")}},
          {{Cube("R2", "[0, 4, 0]", "[16, 6, 16]")}},
          {{Cube("R3", "[4, 2, 4]", "[8, 6, 8]")}} ] }
      ] } ] }
      """;
    const string ribRecord = """
      { "schema": 1,
        "elements": { "R1": { "stage": "cast", "role": "cast" }, "R2": { "stage": "cast", "role": "cast" },
                      "R3": { "stage": "cast", "role": "cast" } },
        "features": { "ways": { "elements": ["Rib"], "origin": "planed", "faces": ["Rib/up"], "step": "plane" } },
        "steps": [ { "id": "plane", "faces": ["Rib/up"] } ] }
      """;
    JObject spec = MachineEntry(
      Casting(
        "floor",
        FloorLie,
        """, "worked": [ { "item": "rib-planed", "steps": [ { "step": "plane", "cuts": ["ways"] } ] } ]"""
      )
    );
    GenContext ctx = Ctx(ribbed, record: ribRecord);

    GenResult r = Run(ctx, spec);

    GenOutput item = r.Outputs.Single(o => o.Name == "cast-planer-bed");
    Dictionary<string, Element> by = item
      .Shape.Elements.SelectMany(Flat)
      .ToDictionary(e => e.Name!);
    Assert.Equal(4, by["R1"].To![1], 6);
    Assert.Equal(6.125, by["R2"].To![1], 6);
    Assert.Equal(6.125, by["R3"].To![1], 6);
    ResolvedSource src = ctx.Resolve("m.json", "0000000");
    CheckResult got = new Allowance().Run(
      new CheckInput(
        item.Shape,
        Record: item.Record,
        Source: src.Shape,
        SourceRecord: src.Record
      )
    );
    Assert.Equal(0, got.ExitCode);
  }

  [Fact]
  public void A_group_step_face_is_written_to_the_flat_form_as_its_leaves_and_the_item_checks_against_it() {
    string ribbed = $$"""
      { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
        { "name": "Rib", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
          {{Cube("R1", "[0, 0, 0]", "[16, 4, 16]")}},
          {{Cube("R2", "[0, 4, 0]", "[16, 6, 16]")}},
          {{Cube("R3", "[4, 2, 4]", "[8, 6, 8]")}} ] }
      ] } ] }
      """;
    const string ribRecord = """
      { "schema": 1,
        "elements": { "R1": { "stage": "cast", "role": "cast" }, "R2": { "stage": "cast", "role": "cast" },
                      "R3": { "stage": "cast", "role": "cast" } },
        "features": { "ways": { "elements": ["Rib"], "origin": "planed", "faces": ["Rib/up"], "step": "plane" } },
        "steps": [ { "id": "plane", "faces": ["Rib/up"] } ] }
      """;
    JObject spec = MachineEntry(
      Casting(
        "floor",
        FloorLie,
        """, "worked": [ { "item": "rib-planed", "steps": [ { "step": "plane", "cuts": ["ways"] } ] } ]"""
      )
    );

    GenResult r = Run(Ctx(ribbed, record: ribRecord), spec);

    GenOutput item = r.Outputs.Single(o => o.Name == "cast-planer-bed");
    GenInput form = r.Inputs.Single(i => i.Name == "cast-planer-bed-form");
    Assert.Equal(
      ["R2/up", "R3/up"],
      form.Record.Steps.Single().Faces.OrderBy(f => f, StringComparer.Ordinal)
    );
    CheckResult got = new Allowance().Run(
      new CheckInput(
        item.Shape,
        Record: item.Record,
        Source: form.Shape,
        SourceRecord: form.Record
      )
    );
    Assert.Equal(0, got.ExitCode);
  }

  private static IEnumerable<Element> Flat(Element e) =>
    e.Children.Count == 0 ? [e] : e.Children.SelectMany(Flat);

  [Fact]
  public void A_group_whose_leaf_has_the_groups_name_is_still_written_as_a_group() {
    string grouped = $$"""
      { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
        { "name": "Base", "from": [0, 0, 0], "to": [16, 4, 16], "faces": { "up": { "texture": "#cast-iron1" } }, "children": [
          {{Cube("Foot", "[2, 4, 2]", "[14, 6, 14]")}} ] },
        { "name": "Side", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
          {{Cube("Lug", "[6, 6, 6]", "[10, 8, 10]")}} ] }
      ] } ] }
      """;
    const string groupedRecord = """
      { "schema": 1, "elements": { "Base": { "stage": "cast", "role": "cast" },
        "Foot": { "stage": "cast", "role": "cast" }, "Lug": { "stage": "cast", "role": "cast" } } }
      """;
    JObject spec = MachineEntry(
      """{ "item": "cast-kit", "groups": [ { "groups": ["Root/Base"] }, { "groups": ["Root/Side"] } ] }"""
    );

    GenResult r = Run(Ctx(grouped, record: groupedRecord), spec);

    Shape item = r.Outputs.Single(o => o.Name == "cast-kit").Shape;
    Assert.Equal(["BaseGroup", "Side"], item.Elements.Select(e => e.Name));
    Assert.Equal(
      ["Base", "Foot"],
      item.Elements[0].Children.Select(e => e.Name)
    );
  }

  [Fact]
  public void Draw_input_holds_the_pattern_halves() {
    const string plainLie =
      """{ "x": { "axis": "x", "offset": 8 }, "y": { "axis": "y", "offset": 4 }, "z": { "axis": "z", "offset": -30 } }""";

    GenResult r = Run(Ctx(), BlankEntry("long", plainLie));

    Shape pattern = r.Outputs.Single(o => o.Name == "pattern-castbed").Shape;
    Shape draw = r.Inputs.Single(i => i.Name == "castbed-draw").Shape;
    Assert.Equal(["Drag", "Cope"], draw.Elements.Select(e => e.Name));
    Assert.Equal(
      pattern.Elements.Select(e => e.Children.Count),
      draw.Elements.Select(e => e.Children.Count)
    );
    Assert.All(draw.Elements, e => Assert.NotEmpty(e.Children));
  }

  [Fact]
  public void An_unanswered_eye_call_omits_only_that_output() {
    string untagged = Drawing.Replace(
      Cube("Cav", "[6, 0, 6]", "[10, 4, 10]"),
      Cube("Cav", "[6, 0, 6]", "[10, 4, 10]")
        + ", "
        + Cube("Mystery", "[1, 1, 1]", "[2, 2, 2]")
    );
    GenContext ctx = Fx.Ctx(
      new Dictionary<string, (string, string)> {
        ["m.json"] = (untagged, Record),
      }
    );
    string second = Casting("floor", FloorLie)
      .Replace("cast-planer-bed", "cast-planer-other")
      .Replace("[\"Root\"]", "[\"Root/Mystery\"]");
    string first = Casting("floor", FloorLie)
      .Replace("[\"Root\"]", "[\"Root/Base\"]");
    JObject spec = MachineEntry(first + ", " + second);

    GenResult r = Run(ctx, spec);

    Question q = Assert.Single(r.Questions);
    Assert.Equal("Mystery", q.Feature);
    Assert.DoesNotContain(r.Outputs, o => o.Name.Contains("other"));
    Assert.Contains(r.Outputs, o => o.Name == "cast-planer-bed");
  }

  [Fact]
  public void A_malformed_entry_throws_SpecException() {
    Assert.Throws<SpecException>(() =>
      Run(Ctx(), JObject.Parse("""{ "item": "x" }"""))
    );
    Assert.Throws<SpecException>(() =>
      Run(Ctx(), BlankEntry("floor", FloorLie, """, "bogus": 1"""))
    );
  }

  [Theory]
  [InlineData("shape", "nosuch.json")]
  [InlineData("sha", "deadbeef")]
  public void A_casting_source_that_names_a_drawing_throws_SpecException(
    string key,
    string value
  ) {
    JObject casting = JObject.Parse(
      $$"""{ "item": "cast-x", "source": { "groups": ["Root"], "{{key}}": "{{value}}" } }"""
    );

    SpecException e = Assert.Throws<SpecException>(() =>
      Spec.ReadMachine(
        JObject.Parse(
          $$"""
          { "machine": "planer", "source": { "shape": "m.json", "sha": "0000000" },
            "castings": [ {{casting}} ] }
          """
        )
      )
    );

    Assert.Contains("cast-x", e.Message);
    Assert.Contains("source." + key, e.Message);
  }

  [SkippableFact]
  public void The_generator_version_is_the_hash_of_its_sources_and_not_of_its_build() {
    DirectoryInfo? shapes = new DirectoryInfo(AppContext.BaseDirectory).Parent;
    while (
      shapes != null
      && !File.Exists(
        Path.Combine(
          shapes.FullName,
          "ExlibShapes.Generators",
          "ExlibShapes.Generators.csproj"
        )
      )
    )
      shapes = shapes.Parent;
    Skip.If(shapes == null, "built outside the source tree");
    using var sha = SHA256.Create();
    foreach (
      string project in new[]
      {
        "ExlibShapes.Generators",
        "ExlibShapes.Checks",
        "ExlibShapes.Core",
      }
    ) {
      string root = Path.Combine(shapes!.FullName, project);
      foreach (
        string rel in Directory
          .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
          .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
          .Where(f => !f.StartsWith("obj/") && !f.StartsWith("bin/"))
          .Order(StringComparer.Ordinal)
      ) {
        byte[] label = Encoding.UTF8.GetBytes(project + "/" + rel + "\n");
        sha.TransformBlock(label, 0, label.Length, null, 0);
        byte[] body = File.ReadAllBytes(Path.Combine(root, rel));
        sha.TransformBlock(body, 0, body.Length, null, 0);
      }
    }
    sha.TransformFinalBlock([], 0, 0);

    Assert.Equal(Convert.ToHexStringLower(sha.Hash!), new CastGen().Version);
  }
}
