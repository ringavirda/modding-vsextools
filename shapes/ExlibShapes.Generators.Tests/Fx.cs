using System;
using System.Collections.Generic;
using System.IO;
using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Generators.Tests;

internal static class Fx {
  public static readonly Settings Settings = Settings.ReadFile(
    Path.Combine(AppContext.BaseDirectory, "fixtures", "settings.json")
  );

  public const string Pin = "0000000";

  public static GenContext Ctx(
    IReadOnlyDictionary<string, (string Shape, string Record)> files,
    IReadOnlyList<Answer>? answers = null
  ) =>
    new(
      Settings,
      (shape, sha) => {
        (string s, string r) = files[shape];
        return new ResolvedSource(
          Reader.Read(s),
          PartRecord.Read(r),
          Provenance.Sha256Of(s)
        );
      },
      answers
    );

  public static GenContext Ctx(
    string shape,
    string record,
    IReadOnlyList<Answer>? answers = null
  ) =>
    Ctx(
      new Dictionary<string, (string, string)> { ["d.json"] = (shape, record) },
      answers
    );

  public static string Faces =>
    """
      "faces": { "north": { "texture": "#c" }, "east": { "texture": "#c" }, "south": { "texture": "#c" }, "west": { "texture": "#c" }, "up": { "texture": "#c" }, "down": { "texture": "#c" } }
      """;

  public static string Cube(
    string name,
    string from,
    string to,
    string extra = ""
  ) =>
    $$"""{ "name": "{{name}}", "from": {{from}}, "to": {{to}}, {{extra}} {{Faces}} }""";

  public static void Near(double expected, double actual, double tol = 1e-9) =>
    Assert.InRange(actual, expected - tol, expected + tol);

  public static void Near(Vec3 expected, Vec3 actual, double tol = 1e-9) {
    Near(expected.X, actual.X, tol);
    Near(expected.Y, actual.Y, tol);
    Near(expected.Z, actual.Z, tol);
  }

  public static SourceRef Src(params string[] groups) =>
    new("d.json", Pin, groups);
}
