using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Checks.Tests;

internal sealed record Recorded(
  int Exit,
  int Enumerated,
  string[] Faulted,
  double[] Areas
);

internal static class Fixtures {
  public static string Path(string name) =>
    System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);

  public static Shape Shape(string name) => Reader.ReadFile(Path(name));

  public static Shape Edited(string name, Action<JObject> edit) {
    JObject raw = JObject.Parse(File.ReadAllText(Path(name)));
    edit(raw);
    return Reader.Read(raw.ToString());
  }

  public static JObject Element(JObject raw, string name) =>
    raw.SelectTokens("$..elements[?(@.name == '" + name + "')]")
      .OfType<JObject>()
      .First();

  public static string Text(string name) => File.ReadAllText(Path(name));

  public static UvTable Table() =>
    UvTables.Parse(File.ReadAllText(Path("table.json")));

  public static IReadOnlyDictionary<string, Recorded> Parity(string check) =>
    JObject
      .Parse(File.ReadAllText(Path("parity/" + check + ".json")))
      .Properties()
      .ToDictionary(
        p => p.Name,
        p => new Recorded(
          (int)p.Value["exit"]!,
          (int)p.Value["enumerated"]!,
          [.. p.Value["faulted"]!.Select(t => (string)t!)],
          [.. (p.Value["areas"] ?? new JArray()).Select(t => (double)t!)]
        )
      );

  public static IEnumerable<object[]> ParityNames(string check) =>
    Parity(check).Keys.Select(k => new object[] { k });

  /// <summary>The names a check line starts with, for the lines that open with "NAME: ".</summary>
  public static string[] FaultedNames(CheckResult r, params string[] skip) =>
    [
      .. r
        .Lines.Where(l =>
          l.Contains(": ")
          && !skip.Any(s => l.StartsWith(s, StringComparison.Ordinal))
        )
        .Select(l => l[..l.IndexOf(": ", StringComparison.Ordinal)])
        .Order(StringComparer.Ordinal),
    ];
}
