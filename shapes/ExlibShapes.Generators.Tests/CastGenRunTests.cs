using System;
using System.Collections.Generic;
using System.Linq;
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

  private static GenContext Ctx(string drawing = "") =>
    new(
      Fx.Settings,
      (shape, sha) => {
        (string s, string r) = shape switch {
          "b.json" => (FormDrawing, FormRecord),
          "m.json" => (drawing.Length > 0 ? drawing : Drawing, Record),
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
}
