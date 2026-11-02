using System.IO;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Checks.Tests;

/// <summary>The pour a recorded stage-gate parity case was run as: its shapes, station, placements and feeds.</summary>
internal static class StageParity {
  public static JObject Case(string gate, string name) =>
    (JObject)
      JObject.Parse(File.ReadAllText(Fixtures.Path($"parity/{gate}.json")))[
        name
      ]!["case"]!;

  public static string[] Omits(string gate, string name) =>
    [
      .. ((JArray?)Case(gate, name)["portOmits"] ?? [])
        .Select(t => (string)t!)
        .Order(),
    ];

  private static string At(JToken at) =>
    at.Count() == 3
      ? PourFixtures.At((double)at[0]!, (double)at[1]!, (double)at[2]!)
      : "[" + string.Join(", ", at.Select(t => (double)t!)) + "]";

  public static CheckInput Input(string gate, string name) {
    JObject c = Case(gate, name);
    Shape? Maybe(string key) =>
      c[key] is { } f ? Fixtures.Shape((string)f!) : null;
    PartRecord record = StageFixtures.Record(
      (string)c["station"]!,
      [.. c["at"]!.Select(At)],
      [.. c["feeds"]!.Select(f => f.ToString(Formatting.None))],
      (string?)c["mould"] ?? "open",
      (string?)c["order"]
    );
    return StageFixtures.Input(
      Fixtures.Shape((string)c["shape"]!),
      record,
      Maybe("cast"),
      Maybe("pair"),
      Maybe("pattern")
    );
  }
}
