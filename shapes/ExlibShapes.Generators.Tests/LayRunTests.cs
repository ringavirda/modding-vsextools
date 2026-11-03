using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Generators.Tests;

internal static class LayFx {
  public static string Cube(string name, string from, string to) =>
    $$"""{ "name": "{{name}}", "from": {{from}}, "to": {{to}}, "faces": { {{string.Join(
      ", ",
      new[] { "north", "east", "south", "west", "up", "down" }.Select(f => $"\"{f}\": {{ \"texture\": \"#cast-iron1\" }}")
    )}} } }""";

  public const string FormDrawing = """
    { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
      { "name": "Base", "from": [0, 0, 0], "to": [16, 4, 16], "faces": { "up": { "texture": "#cast-iron1" }, "east": { "texture": "#cast-iron1" }, "south": { "texture": "#cast-iron1" }, "west": { "texture": "#cast-iron1" }, "north": { "texture": "#cast-iron1" }, "down": { "texture": "#cast-iron1" } } }
    ] } ] }
    """;

  public const string FormRecord = """
    { "schema": 1, "elements": { "Base": { "stage": "cast", "role": "cast" } } }
    """;

  public static GenContext Ctx(string drawing, string record) =>
    new(
      Fx.Settings,
      (shape, sha) => {
        (string s, string r) = shape switch {
          "b.json" => (FormDrawing, FormRecord),
          "m.json" => (drawing, record),
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
      code =>
        JObject.Parse(
          $$"""
          { "blank": "{{code}}", "size": [16, 4, 16], "cutFaces": [],
            "form": { "shape": "b.json", "sha": "0000000", "groups": ["Root"] } }
          """
        )
    );

  public static JObject Machine(
    string castings = "",
    string users = "",
    string joined = ""
  ) =>
    JObject.Parse(
      $$"""
      { "machine": "bed", "source": { "shape": "m.json", "sha": "0000000" },
        "users": [ {{users}} ], "joined": [ {{joined}} ], "castings": [ {{castings}} ] }
      """
    );

  public static string Lie(
    string x,
    double ox,
    string y,
    double oy,
    string z,
    double oz
  ) =>
    $$"""{ "x": { "axis": "{{x}}", "offset": {{ox}} }, "y": { "axis": "{{y}}", "offset": {{oy}} }, "z": { "axis": "{{z}}", "offset": {{oz}} } }""";

  public static readonly string Floor = Lie("x", 13, "y", 10, "z", -3);

  public static Element Named(GenOutput o, string name) =>
    o.Shape.Elements.SingleOrDefault(e => e.Name == name)
    ?? throw new InvalidOperationException(
      $"{name} not in {string.Join(", ", o.Shape.Elements.Select(e => e.Name))}"
    );
}

public class JoinedRunTests {
  private static readonly string Drawing = $$"""
    { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
      {{LayFx.Cube("Sec1", "[0, 0, 0]", "[16, 4, 16]")}},
      {{LayFx.Cube("Sec2", "[16, 0, 0]", "[32, 4, 16]")}},
      {{LayFx.Cube("Sec3", "[32, 0, 0]", "[48, 4, 16]")}},
      {{LayFx.Cube("Sec4", "[48, 0, 0]", "[64, 4, 16]")}},
      {{LayFx.Cube("Sec5", "[64, 0, 0]", "[80, 4, 16]")}},
      {{LayFx.Cube("Seat", "[2, 3, 2]", "[14, 4, 14]")}},
      {{LayFx.Cube("Holes", "[20, 0, 4]", "[24, 4, 8]")}}
    ] } ] }
    """;

  private const string Record = """
    { "schema": 1,
      "elements": { "Sec1": { "stage": "cast" }, "Sec2": { "stage": "cast" }, "Sec3": { "stage": "cast" },
                    "Sec4": { "stage": "cast" }, "Sec5": { "stage": "cast" },
                    "Seat": { "stage": "worked" }, "Holes": { "stage": "worked" } },
      "features": { "seat": { "elements": ["Seat"], "origin": "planed", "faces": ["Seat/up"], "step": "plane" },
                    "holes": { "elements": ["Holes"], "origin": "drilled", "faces": ["Holes/up"], "step": "drill",
                                 "hole": { "axis": "y", "centre": [22, 4, 6], "depth": -4, "radius": 1.5 } } } }
    """;

  private static string User(int i) =>
    $$"""
      { "blank": "castbedsection", "groups": ["Root"], "window": [{{16
        * i}}, 0, 0, {{16 * i + 16}}, 4, 16],
        "at": { "offset": [{{-16 * i}}, 0, 0] },
        "worked": { "item": "bed-sec{{i + 1}}", "steps": ["plane"] } }
      """;

  private static GenResult Run(string joined) =>
    new CastGen().Run(
      LayFx.Ctx(Drawing, Record),
      LayFx.Machine(
        users: string.Join(",", Enumerable.Range(0, 5).Select(User)),
        joined: joined
      )
    );

  private const string Bed = """
    { "item": "bed-joined", "from": ["bed-sec1", "bed-sec2", "bed-sec3", "bed-sec4", "bed-sec5"],
      "steps": [ { "step": "plane", "features": ["seat"] }, { "step": "drill", "features": ["holes"] } ] }
    """;

  [Fact]
  public void Joined_bed_has_its_seat_work() {
    GenResult r = Run(Bed);

    Assert.Empty(r.Questions);
    GenOutput bed = r.Outputs.Single(o => o.Name == "bed-joined");
    Assert.Equal(["worked", "item"], bed.Kinds);
    Assert.Equal(["plane", "drill"], bed.Record.Steps.Select(s => s.Id));
    Assert.Equal("planer", bed.Record.Steps[0].Tool);
    Assert.Equal("drillpress", bed.Record.Steps[1].Tool);
    Assert.Equal("worked", bed.Record.Elements["Seat"].Stage);
    Assert.Equal(
      "#cast-iron-turned1",
      LayFx.Named(bed, "Seat").Faces.Single(kv => kv.Key == "up").Value.Texture
    );
    Assert.Equal("worked", bed.Record.Elements["Holes"].Stage);
    Box bounds = BlankResult
      .Union(
        bed.Shape.Elements.Select(e => new Box(
          new Vec3(e.From![0], e.From[1], e.From[2]),
          new Vec3(e.To![0], e.To[1], e.To[2])
        ))
      )!
      .Value;
    Assert.Equal(80, bounds.Hi.X - bounds.Lo.X, 1e-9);
    Assert.Contains(r.Outputs, o => o.Name == "bed-sec3");
  }

  [Fact]
  public void A_joined_section_with_an_open_question_gives_no_item() {
    string untagged = Drawing.Replace(
      LayFx.Cube("Holes", "[20, 0, 4]", "[24, 4, 8]"),
      LayFx.Cube("Holes", "[20, 0, 4]", "[24, 4, 8]")
        + ", "
        + LayFx.Cube("Mystery", "[1, 1, 1]", "[2, 2, 2]")
    );
    GenResult r = new CastGen().Run(
      LayFx.Ctx(untagged, Record),
      LayFx.Machine(
        users: string.Join(",", Enumerable.Range(0, 5).Select(User)),
        joined: Bed
      )
    );

    Assert.Contains(r.Questions, q => q.Feature == "Mystery");
    Assert.DoesNotContain(r.Outputs, o => o.Name == "bed-joined");
  }
}

public class PourRunTests {
  private static readonly string Drawing = $$"""
    { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
      {{LayFx.Cube("Bit", "[0, 0, 0]", "[6, 4, 6]")}},
      {{LayFx.Cube("Cav", "[2, 0, 2]", "[4, 4, 4]")}}
    ] } ] }
    """;

  private const string Record = """
    { "schema": 1,
      "elements": { "Bit": { "stage": "cast" }, "Cav": { "stage": "cast" } },
      "features": { "cav": { "elements": ["Cav"], "origin": "cored" } } }
    """;

  private static string Casting(int count, string station, string extra = "") =>
    $$"""
      { "item": "cast-bed-bit", "source": { "groups": ["Root/Bit"] }, "count": {{count}},
        "station": "{{station}}", "lie": {{LayFx.Floor}} {{extra}} }
      """;

  private static GenResult Run(string casting, string groups = "Root/Bit") =>
    new CastGen().Run(
      LayFx.Ctx(Drawing, Record),
      LayFx.Machine(casting.Replace("Root/Bit", groups))
    );

  private static readonly ICheck[] Gates =
  [
    new Impression(),
    new Flood(),
    new Capacity(),
    new Fit(),
    new Runner(),
    new Pieces(),
    new Feeding(),
    new Pit(),
    new Parting(),
  ];

  private static List<string> Failing(GenResult r) {
    GenOutput filling = r.Outputs.Single(o => o.Kinds.Contains("filling"));
    var input = new CheckInput(
      filling.Shape,
      Record: filling.Record,
      Settings: Fx.Settings,
      Cast: r.Outputs.Single(o => o.Name == "cast-bed-bit").Shape,
      Pattern: r.Outputs.Single(o => o.Name == "pattern-cast-bed-bit").Shape
    );
    return
    [
      .. Gates
        .Where(g => Selection.Applies(g, input))
        .Select(g => (g, res: g.Run(input)))
        .Where(x => x.res.ExitCode != 0)
        .Select(x => $"{x.g.Id}: {string.Join(" | ", x.res.Lines.Take(4))}"),
    ];
  }

  [Fact]
  public void A_pour_lays_every_piece() {
    GenResult r = Run(Casting(2, "floor"));

    GenOutput filling = r.Outputs.Single(o => o.Kinds.Contains("filling"));
    JToken pour = filling.Record.Carried["pour"]!;
    Assert.Equal(2, ((JArray)pour["pieces"]!).Count);
    Assert.Equal("south", (string?)pour["order"]);
    Assert.Contains(filling.Shape.Elements, e => e.Name == "Piece1");
    Assert.Contains(filling.Shape.Elements, e => e.Name == "Piece2");
    Assert.Empty(Failing(r));
    GenInput draw = Assert.Single(r.Inputs, i => i.Name == "cast-bed-bit-draw");
    Assert.Equal(2, draw.Shape.Elements.Count);
  }

  [Fact]
  public void A_pour_of_one_piece_has_no_piece_groups() {
    GenResult r = Run(Casting(1, "floor"));

    GenOutput filling = r.Outputs.Single(o => o.Kinds.Contains("filling"));
    Assert.Single((JArray)filling.Record.Carried["pour"]!["pieces"]!);
    Assert.Equal("one", (string?)filling.Record.Carried["pour"]!["order"]);
    Assert.DoesNotContain(filling.Shape.Elements, e => e.Name == "Piece1");
  }

  [Fact]
  public void A_pour_of_two_pieces_with_cores_is_refused() {
    var e = Assert.Throws<CastGenException>(() =>
      Run(
        Casting(
          2,
          "floor",
          """, "cores": [ { "feature": "cav", "print": 2 } ]"""
        ),
        "Root/Bit\", \"Root/Cav"
      )
    );
    Assert.Contains("cores", e.Message);
  }

  [Fact]
  public void Pieces_that_overrun_the_station_are_refused() {
    var e = Assert.Throws<CastGenException>(() => Run(Casting(4, "floor")));
    Assert.Contains("Piece", e.Message);
  }
}

public class KitLieRunTests {
  private static readonly string Drawing = $$"""
    { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
      {{LayFx.Cube("A", "[0, 0, 0]", "[6, 4, 2]")}},
      {{LayFx.Cube("B", "[8, 0, 0]", "[16, 4, 2]")}},
      {{LayFx.Cube("Cav", "[1, 1, 0]", "[2, 2, 2]")}}
    ] } ] }
    """;

  private const string Record = """
    { "schema": 1,
      "elements": { "A": { "stage": "cast" }, "B": { "stage": "cast" }, "Cav": { "stage": "cast" } },
      "features": { "cav": { "elements": ["Cav"], "origin": "cored" } } }
    """;

  private static readonly string Turned = LayFx.Lie("z", 13, "y", 10, "-x", 15);

  private static string Kit(string extra = "", string a = "Root/A") =>
    $$"""
      { "item": "cast-bed-kit", "station": "floor", {{extra}}
        "groups": [ { "groups": ["{{a}}"], "lie": {{LayFx.Floor}} }, { "groups": ["Root/B"], "lie": {{Turned}} } ] }
      """;

  [Fact]
  public void Kit_groups_keep_their_own_lie() {
    GenResult r = new CastGen().Run(
      LayFx.Ctx(Drawing, Record),
      LayFx.Machine(Kit())
    );

    GenOutput filling = r.Outputs.Single(o =>
      o.Name == "floorflask-filling-cast-bed-kit"
    );
    bool Sand(double x, double y, double z) =>
      filling.Shape.Elements.Any(e =>
        e.Name!.StartsWith("Sand")
        && x > e.From![0]
        && x < e.To![0]
        && y > e.From[1]
        && y < e.To[1]
        && z > e.From[2]
        && z < e.To[2]
      );
    Assert.False(Sand(16, 12, -2));
    Assert.False(Sand(14, 12, 3));
    Assert.True(Sand(16, 12, 3));
  }

  [Fact]
  public void A_kit_of_groups_that_lie_differently_with_cores_is_refused() {
    var e = Assert.Throws<CastGenException>(() =>
      new CastGen().Run(
        LayFx.Ctx(Drawing, Record),
        LayFx.Machine(
          Kit(""" "cores": [ { "feature": "cav", "print": 2 } ], """, "Root/A")
            .Replace("[\"Root/A\"]", "[\"Root/A\", \"Root/Cav\"]")
        )
      )
    );
    Assert.Contains("share one lie", e.Message);
  }
}
