using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Checks.Tests;

internal static class PourFixtures {
  public const string Runner1 =
    """{ "name": "Runner1", "kind": "runner", "boxes": [[15, 13.5, 2.5, 17, 14, 9]] }""";

  public const string Gate2 =
    """{ "name": "Gate2", "kind": "gate", "boxes": [[15, 13.5, -7.5, 17, 14, -2.5]] }""";

  /// <summary>A translation as the 16 row-major entries a piece's <c>at</c> holds.</summary>
  public static string At(double x, double y, double z) =>
    $"[1, 0, 0, {x}, 0, 1, 0, {y}, 0, 0, 1, {z}, 0, 0, 0, 1]";

  public static Settings Settings() => CastFixtures.Settings();

  /// <summary>The fixtures' settings with the floor station's margin replaced.</summary>
  public static Settings WithFloorMargin(double margin) =>
    Core.Settings.Read(
      Fixtures
        .Text("settings.json")
        .Replace(
          "\"floor\": 2, \"margin\": 1.0, \"footprint\": 0.5,\n      \"runnerX\": [15, 17], \"runnerDepth\": 0.5, \"gateAcross\": 1, \"drop\"",
          $"\"floor\": 2, \"margin\": {margin}, \"footprint\": 0.5,\n      \"runnerX\": [15, 17], \"runnerDepth\": 0.5, \"gateAcross\": 1, \"drop\""
        )
    );

  /// <summary>A filling's record whose pour places a piece at each of <paramref name="at"/> on the floor station.</summary>
  public static PartRecord Record(
    string[] at,
    string[] feeds,
    double perPiece = 360,
    double share = 0,
    string station = "floor",
    string mould = "open"
  ) =>
    CastFixtures.Record(
      $$"""
      "kinds": ["filling"],
      "pour": {
        "station": "{{station}}", "mould": "{{mould}}", "cast": "fill-cast",
        "pieces": [{{string.Join(
        ", ",
        at.Select(a => "{ \"at\": " + a + " }")
      )}}],
        "feeds": [{{string.Join(", ", feeds)}}],
        "units": { "perPiece": {{perPiece}}, "share": {{share}} }
      }
      """
    );

  public static PartRecord One(double perPiece = 360) =>
    Record([At(13, 10, -3)], [Runner1], perPiece);

  public static CheckInput Input(
    Shape filling,
    PartRecord record,
    Shape? cast = null,
    Settings? settings = null
  ) =>
    new(
      filling,
      Record: record,
      Settings: settings ?? Settings(),
      Cast: cast ?? Fixtures.Shape("fill-cast.json")
    );

  public static readonly ICheck[] Gates =
  [
    new Impression(),
    new Flood(),
    new Capacity(),
    new Fit(),
    new Runner(),
  ];

  /// <summary>The ids of the filling gates that fail on <paramref name="input"/>.</summary>
  public static string[] Rejecting(CheckInput input) =>
    [.. Gates.Where(g => g.Run(input).ExitCode == 1).Select(g => g.Id)];

  /// <summary>A cast item of one box <paramref name="x"/> by <paramref name="y"/> by <paramref name="z"/>.</summary>
  public static Shape Cast(double x, double y, double z) =>
    Reader.Read(
      $$"""
      { "textures": { "cast-iron1": "block/metal/cast-iron1" },
        "elements": [ { "name": "Bed", "from": [0,0,0], "to": [0,0,0], "children": [
          { "name": "Body", "from": [0,0,0], "to": [{{x}}, {{y}}, {{z}}],
            "faces": { "up": { "texture": "#cast-iron1" } } } ] } ] }
      """
    );

  /// <summary>A box element turned 45 degrees about y around its centre.</summary>
  public static string Turned(
    string name,
    double x0,
    double y0,
    double z0,
    double x1,
    double y1,
    double z1,
    string texture = "andesite"
  ) =>
    $$"""{ "name": "{{name}}", "from": [{{x0}}, {{y0}}, {{z0}}], "to": [{{x1}}, {{y1}}, {{z1}}], "rotationY": 45, "rotationOrigin": [{{(x0 + x1) / 2}}, {{(y0 + y1) / 2}}, {{(z0 + z1) / 2}}], "faces": { "up": { "texture": "#{{texture}}" } } }""";

  /// <summary>A copy of <paramref name="name"/> with a turned box element added at the top.</summary>
  public static Shape WithTurned(string name, string element) =>
    Fixtures.Edited(
      name,
      raw => ((JArray)raw["elements"]!).Add(JObject.Parse(element))
    );

  /// <summary>The first number in the line of <paramref name="lines"/> that starts with <paramref name="prefix"/>, after the prefix.</summary>
  public static double Number(IEnumerable<string> lines, string prefix) =>
    double.Parse(
      Regex
        .Match(
          lines.First(l => l.StartsWith(prefix, StringComparison.Ordinal))[
            prefix.Length..
          ],
          @"-?\d+(\.\d+)?"
        )
        .Value,
      CultureInfo.InvariantCulture
    );
}
