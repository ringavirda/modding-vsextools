using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Ops;

/// <summary>A machine file of a lane's casting kinds, one casting entry each.</summary>
internal static class MachineSkeleton {
  /// <summary>The machine file for <paramref name="lane"/>; null when the lane module has no casting kinds.</summary>
  /// <param name="lane">The lane's tables, kinds and press entries.</param>
  /// <param name="drawingName">The drawing's file name, the source shape when the lane names none.</param>
  public static JObject? Build(LaneTables lane, string drawingName) {
    if (lane.Kinds == null || lane.Kinds.Count == 0)
      return null;
    string shape = (string?)lane.Tables["COPY"] ?? drawingName;
    string sha = (string?)lane.Tables["COPY_COMMIT"] ?? "HEAD";
    var castings = new List<(string Item, JObject Entry)>();
    foreach (JProperty kind in lane.Kinds.Properties()) {
      var k = (JObject)kind.Value;
      var entry = new JObject { ["item"] = (string)k["item"]! };
      if (k["pieces"] is JObject pieces)
        entry["source"] = new JObject {
          ["groups"] = new JArray(
            pieces
              .Properties()
              .Select(p => p.Name)
              .Order(StringComparer.Ordinal)
          ),
        };
      if (Lie(k["lie"]) is { } lie)
        entry["lie"] = lie;
      castings.Add(((string)entry["item"]!, entry));
    }
    foreach (JProperty p in lane.Press?.Properties() ?? [])
      castings.Add(
        (
          Path.GetFileNameWithoutExtension((string)p.Value["item"]!),
          new JObject {
            ["item"] = Path.GetFileNameWithoutExtension(
              (string)p.Value["item"]!
            ),
            ["source"] = new JObject { ["groups"] = new JArray("CrossSlide") },
          }
        )
      );
    var machine = new JObject {
      ["machine"] = lane.Module,
      ["source"] = new JObject { ["shape"] = shape, ["sha"] = sha },
    };
    if (lane.Tables["A"] is { Type: JTokenType.Float or JTokenType.Integer } a)
      machine["allowance"] = a;
    machine["castings"] = new JArray(
      castings.OrderBy(c => c.Item, StringComparer.Ordinal).Select(c => c.Entry)
    );
    return machine;
  }

  /// <summary>The per-flask-axis lie of a 4 x 4 matrix taking an item point to the flask; null when a row is not a
  /// signed axis plus an offset.</summary>
  public static JObject? Lie(JToken? matrix) {
    if (matrix is not JArray rows || rows.Count < 3)
      return null;
    var lie = new JObject();
    for (int r = 0; r < 3; r++) {
      if (rows[r] is not JArray row || row.Count != 4)
        return null;
      int[] hit = [.. Enumerable.Range(0, 3).Where(c => (double)row[c] != 0)];
      if (hit.Length != 1 || Math.Abs((double)row[hit[0]]) != 1)
        return null;
      lie["xyz"[r].ToString()] = new JObject {
        ["axis"] = ((double)row[hit[0]] < 0 ? "-" : "") + "xyz"[hit[0]],
        ["offset"] = row[3],
      };
    }
    return lie;
  }
}
