using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks.Tests;

internal static class StageFixtures {
  public const string Sprue =
    """{ "name": "Sprue", "kind": "sprue", "boxes": [[14, 9, 0, 16, 14, 2]] }""";

  public const string Riser =
    """{ "name": "Riser", "kind": "riser", "boxes": [[17, 9, -2, 18, 14, -1]] }""";

  public static readonly ICheck[] Gates =
  [
    new Pieces(),
    new Feeding(),
    new Pit(),
    new Parting(),
  ];

  /// <summary>The ids of the stage gates that apply to <paramref name="input"/> and fail on it.</summary>
  public static string[] Rejecting(CheckInput input) =>
    [
      .. Gates
        .Where(g => Selection.Applies(g, input))
        .Where(g => g.Run(input).ExitCode == 1)
        .Select(g => g.Id),
    ];

  public static PartRecord Record(
    string station,
    string[] at,
    string[] feeds,
    string mould = "open",
    string? order = null,
    string[]? cores = null
  ) =>
    CastFixtures.Record(
      $$"""
      "kinds": ["filling"],
      "pour": {
        "station": "{{station}}", "mould": "{{mould}}", "cast": "fill-cast",
        {{(mould == "closed" ? "\"pair\": \"closed-cope\"," : "")}}
        {{(order == null ? "" : $"\"order\": \"{order}\",")}}
        "pieces": [{{string.Join(
        ", ",
        at.Select(a => "{ \"at\": " + a + " }")
      )}}],
        "cores": [{{string.Join(
        ", ",
        (cores ?? []).Select(c => "{ \"item\": \"" + c + "\", \"at\": " + PourFixtures.At(0, 0, 0) + " }")
      )}}],
        "feeds": [{{string.Join(", ", feeds)}}],
        "units": { "perPiece": 360, "share": 0 }
      }
      """
    );

  public static CheckInput Input(
    Shape filling,
    PartRecord record,
    Shape? cast = null,
    Shape? pair = null,
    Shape? pattern = null
  ) =>
    new(
      filling,
      Record: record,
      Settings: PourFixtures.Settings(),
      Cast: cast ?? Fixtures.Shape("fill-cast.json"),
      Pair: pair,
      Pattern: pattern
    );

  public static CheckInput Closed(
    string drag = "closed-drag.json",
    string cope = "closed-cope.json",
    string at = "",
    Shape? cast = null,
    Shape? pattern = null,
    string[]? feeds = null
  ) =>
    Input(
      Fixtures.Shape(drag),
      Record(
        "long",
        [at == "" ? PourFixtures.At(13, 5, -3) : at],
        feeds ?? [Sprue, Riser],
        "closed"
      ),
      cast,
      Fixtures.Shape(cope),
      pattern
    );

  public static string Box(
    string name,
    double x0,
    double y0,
    double z0,
    double x1,
    double y1,
    double z1,
    string texture = "andesite"
  ) =>
    $$"""{ "name": "{{name}}", "from": [{{x0}}, {{y0}}, {{z0}}], "to": [{{x1}}, {{y1}}, {{z1}}], "faces": { "up": { "texture": "#{{texture}}" } } }""";

  public static string Group(string name, params string[] children) =>
    $$"""{ "name": "{{name}}", "from": [0, 0, 0], "to": [0, 0, 0], "children": [{{string.Join(", ", children)}}] }""";

  public static Shape Sand(params string[] elements) =>
    Reader.Read(
      $$"""{ "textures": { "andesite": "block/stone/sand/andesite" }, "elements": [{{string.Join(", ", elements)}}] }"""
    );

  /// <summary>A cast of the given elements, boxes of the cast-iron texture under one zero-sized parent.</summary>
  public static Shape CastOf(params string[] boxes) =>
    Reader.Read(
      $$"""
      { "textures": { "cast-iron1": "block/metal/cast-iron1" },
        "elements": [ { "name": "Bed", "from": [0,0,0], "to": [0,0,0], "children": [{{string.Join(
        ", ",
        boxes
      )}}] } ] }
      """
    );

  /// <summary>A pattern of the given root elements, each a zero-sized group of cast-iron boxes.</summary>
  public static Shape Roots(params string[] roots) =>
    Reader.Read(
      $$"""
      { "textures": { "cast-iron1": "block/metal/cast-iron1" },
        "elements": [{{string.Join(", ", roots)}}] }
      """
    );

  /// <summary>One piece's <c>Piece&lt;k&gt;</c> group over the footprint x0..x1 by z0..z1 (default -3..3): a cavity to y 10 and a level to <paramref name="levelTop"/>.</summary>
  public static string Piece(
    int k,
    double x0,
    double x1,
    double levelTop = 14,
    double levelX1 = double.NaN,
    double z0 = -3,
    double z1 = 3
  ) =>
    Group(
      "Piece" + k,
      Group("Bedded", Box("Cavity", x0, 2, z0, x1, 10, z1)),
      Group(
        "Level",
        Box(
          "Cover",
          x0,
          10,
          z0,
          double.IsNaN(levelX1) ? x1 : levelX1,
          levelTop,
          z1
        )
      )
    );

  /// <summary>Sand from the floor station's floor to its top over its whole plan up to <paramref name="zTo"/>.</summary>
  public static string Slab(double zTo = 14) =>
    Box("Slab", 2, 2, -14, 30, 14, zTo);
}
