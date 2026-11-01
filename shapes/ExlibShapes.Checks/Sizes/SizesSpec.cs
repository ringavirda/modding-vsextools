using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>Reads the <c>sizes</c> section of a per-machine checks file.</summary>
public static class SizesSpec {
  /// <summary>The rows of the <c>sizes</c> list: <c>{"element": PATH, "axis": "x"|"y"|"z", "voxels": N}</c>.</summary>
  /// <param name="json">The checks file's text.</param>
  /// <exception cref="CheckUsageException">The text is not JSON, has no non-empty <c>sizes</c> list, a row lacks
  /// <c>element</c>, <c>axis</c> or <c>voxels</c>, an axis is not x, y or z, or <c>voxels</c> is not a number.</exception>
  public static IReadOnlyList<SizeRow> Parse(string json) {
    JObject root;
    try {
      root = JObject.Parse(json);
    } catch (JsonException ex) {
      throw new CheckUsageException($"spec: {ex.Message}");
    }
    if (root["sizes"] is not JArray { Count: > 0 } rows)
      throw new CheckUsageException("the spec has no `sizes` section");
    var outp = new List<SizeRow>();
    foreach (JToken row in rows) {
      if (
        row is not JObject o
        || o["element"] == null
        || o["axis"] == null
        || o["voxels"] == null
      )
        throw new CheckUsageException(
          $"a sizes row needs element, axis and voxels: {row.ToString(Formatting.None)}"
        );
      string element = o["element"]!.ToString();
      string axis = o["axis"]!.ToString();
      if (axis is not ("x" or "y" or "z"))
        throw new CheckUsageException(
          $"{element}: the axis is x, y or z, not '{axis}'"
        );
      if (o["voxels"]!.Type is not (JTokenType.Integer or JTokenType.Float))
        throw new CheckUsageException(
          $"{element}: voxels is a number, not {o["voxels"]!.ToString(Formatting.None)}"
        );
      outp.Add(new SizeRow(element, axis, (double)o["voxels"]!));
    }
    return outp;
  }
}
