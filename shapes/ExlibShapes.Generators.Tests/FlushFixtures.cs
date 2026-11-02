using System.Collections.Generic;
using System.Linq;
using System.Text;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Generators.Tests;

internal static class FlushFixtures {
  public sealed record Box3(
    string Name,
    string From,
    string To,
    string Role = "cast"
  );

  public static Box3 Bx(
    string name,
    string from,
    string to,
    string role = "cast"
  ) => new(name, from, to, role);

  private static string Cube(Box3 b) =>
    $$"""{ "name": "{{b.Name}}", "from": {{b.From}}, "to": {{b.To}}, "faces": { {{string.Join(
      ", ",
      new[] { "north", "east", "south", "west", "up", "down" }.Select(f => $"\"{f}\": {{ \"texture\": \"#cast-iron1\", \"uv\": [0, 0, 4, 4] }}")
    )}} } }""";

  public static Shape Drawn(params Box3[] boxes) =>
    Reader.Read(
      $$"""{ "elements": [ {{string.Join(", ", boxes.Select(Cube))}} ] }"""
    );

  public static List<Leaf> Leaves(params Box3[] boxes) {
    Dictionary<string, string> role = boxes.ToDictionary(
      b => b.Name,
      b => b.Role
    );
    return
    [
      .. Select
        .Groups(Drawn(boxes), [.. boxes.Select(b => b.Name)])
        .Select(l => l with { Stage = "cast", Role = role[l.Name] }),
    ];
  }

  public static Shape ItemShape(IEnumerable<Leaf> leaves) {
    var shape = new Shape();
    foreach (Leaf l in leaves) {
      Element e = Select.Clone(l.Element, false);
      e.Name = l.Name;
      shape.Elements.Add(e);
    }
    return shape;
  }

  public static PartRecord SourceRecord(string steps, params string[] names) {
    var sb = new StringBuilder(
      "{\"schema\": 1, \"kinds\": [\"casting\"], \"elements\": {"
    );
    sb.Append(
      string.Join(
        ", ",
        names.Select(n =>
          $"\"{n}\": {{ \"stage\": \"cast\", \"role\": \"cast\" }}"
        )
      )
    );
    sb.Append("}, \"steps\": [").Append(steps).Append("] }");
    return PartRecord.Read(sb.ToString());
  }
}
