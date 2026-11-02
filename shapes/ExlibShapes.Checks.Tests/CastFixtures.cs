using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Checks.Tests;

internal static class CastFixtures {
  public const string CastRecord = """
    "kinds": ["casting"],
    "elements": { "Bed": { "stage": "cast", "role": "cast" } }
    """;

  public const string PlaneWay = """
    "steps": [ { "id": "plane", "tool": "planer", "faces": ["Way/up"] } ]
    """;

  public static Settings Settings() =>
    Core.Settings.Read(Fixtures.Text("settings.json"));

  public static PartRecord Record(string body) =>
    PartRecord.Read("{\"schema\": 1, " + body + "}");

  public static readonly ICheck[] Gates =
  [
    new Castonly(),
    new Allowance(),
    new Split(),
    new Role(),
    new Draw(),
  ];

  /// <summary>The ids of the gates that fail on <paramref name="input"/>; a gate the input lacks a part for is not run.</summary>
  public static string[] Rejecting(CheckInput input) =>
    [
      .. Gates
        .Where(g =>
        {
          try
          {
            return g.Run(input).ExitCode == 1;
          }
          catch (CheckUsageException)
          {
            return false;
          }
        })
        .Select(g => g.Id),
    ];

  /// <summary>The element names a gate's fault lines start with, last path segment, face dropped.</summary>
  public static string[] Names(CheckResult r, params string[] skip) =>
    [
      .. Fixtures
        .FaultedNames(r, skip)
        .Select(n => n.Split(' ')[0])
        .Select(n => n[(n.LastIndexOf('/') + 1)..])
        .Distinct()
        .Order(StringComparer.Ordinal),
    ];

  /// <summary>The element called <paramref name="name"/> at any depth of a shape's JSON.</summary>
  public static JObject Find(JObject raw, string name) =>
    raw.SelectTokens("$..[?(@.name == '" + name + "')]")
      .OfType<JObject>()
      .First();

  public static void Move(JObject el, string end, int axis, double to) =>
    el[end] = new JArray(
      ((JArray)el[end]!).Select((v, i) => i == axis ? (JToken)to : v)
    );
}
