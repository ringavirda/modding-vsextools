using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>Reads the uv table and the allowed changes of a checks file.</summary>
public static class UvTables {
  /// <summary>The table from the text of a checks JSON file: its <c>uv</c> object maps a texture key to
  /// <c>"random"</c>, <c>"position"</c> or <c>[u, v, rotation]</c> where the rotation may be a list; its
  /// optional <c>changes</c> list holds <c>{"element", "face", "reason"}</c> rows.</summary>
  /// <exception cref="CheckUsageException">The text is not JSON, holds no <c>uv</c> object, or a row is malformed.</exception>
  public static UvTable Parse(string json) {
    JObject root;
    try {
      root = JObject.Parse(json);
    } catch (Exception ex) when (ex is Newtonsoft.Json.JsonException) {
      throw new CheckUsageException($"uv table: {ex.Message}");
    }
    if (root["uv"] is not JObject uv)
      throw new CheckUsageException(
        "no UV table: the file has no \"uv\" object"
      );
    var rows = new Dictionary<string, UvRow>();
    foreach ((string key, JToken? mode) in uv) {
      if (mode is { Type: JTokenType.String }) {
        string m = mode.Value<string>()!;
        if (m is not ("random" or "position"))
          throw new CheckUsageException($"uv row '{key}': unknown mode '{m}'");
        rows[key] = new UvRow(m);
      } else if (mode is JArray { Count: 3 } a) {
        IReadOnlyList<double> rot = a[2] is JArray ra
          ? [.. ra.Select(x => (double)x)]
          : [(double)a[2]];
        rows[key] = new UvRow("patterned", (double)a[0], (double)a[1], rot);
      } else
        throw new CheckUsageException(
          $"uv row '{key}': expected \"random\", \"position\" or [u, v, rotation]"
        );
    }
    var changes = new List<FaceChange>();
    if (root["changes"] is JArray list)
      foreach (JToken c in list) {
        string? el = c["element"]?.Value<string>(),
          face = c["face"]?.Value<string>();
        if (el == null || face == null)
          throw new CheckUsageException(
            $"a changes row needs element and face: {c}"
          );
        changes.Add(
          new FaceChange(el, face, c["reason"]?.Value<string>() ?? "")
        );
      }
    return new UvTable(rows, changes);
  }
}
