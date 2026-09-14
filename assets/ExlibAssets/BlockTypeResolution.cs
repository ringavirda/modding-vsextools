using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Util;

namespace ExpandedLib.Assets;

/// <summary>
/// The game's <c>&lt;key&gt;ByType</c> convention (<c>shape</c>/<c>shapeByType</c>,
/// <c>textures</c>/<c>texturesByType</c>, <c>attributes</c>/<c>attributesByType</c>, ...), shared by
/// <c>ExpandedLib.Shapes.BlockIndex</c> (drawing a resolved block) and
/// <c>ExpandedLib.Verify.ShapeTextureChecker</c> (checking one headlessly), so both resolve a
/// variant's entry exactly the way the game does.
/// </summary>
public static class BlockTypeResolution {
  /// <summary>The entry a variant at <paramref name="path"/> (its code without domain) resolves to
  /// for <paramref name="baseKey"/>: the first <c>&lt;baseKey&gt;ByType</c> property whose wildcard
  /// key <see cref="WildcardUtil.Match(string, string)"/> accepts, else the plain
  /// <paramref name="raw"/>[<paramref name="baseKey"/>], else null.</summary>
  public static JToken? ByType(JObject raw, string baseKey, string path) {
    if (GetCi(raw, baseKey + "ByType") is JObject byType)
      foreach (JProperty prop in byType.Properties())
        if (WildcardUtil.Match(prop.Name, path))
          return prop.Value;
    return GetCi(raw, baseKey);
  }

  /// <summary>Case-insensitive property lookup - vanilla and family JSON disagree on the case of a
  /// few keys (<c>shapeByType</c> next to <c>shapebytype</c>).</summary>
  public static JToken? GetCi(JObject raw, string key) {
    if (raw[key] is { } exact)
      return exact;
    string lowered = key.ToLowerInvariant();
    foreach (JProperty prop in raw.Properties())
      if (prop.Name.ToLowerInvariant() == lowered)
        return prop.Value;
    return null;
  }

  /// <summary>Replaces every <c>{code}</c> token in <paramref name="text"/> with
  /// <paramref name="states"/>[<c>code</c>] - a variant's own <c>{group}</c> substitution into a
  /// <c>shapeByType</c>/<c>texturesByType</c> entry's base path, shared by
  /// <c>ExpandedLib.Shapes.BlockIndex</c> and <c>ExpandedLib.Verify.ShapeTextureChecker</c>. A
  /// token naming an axis <paramref name="states"/> does not carry is left untouched.</summary>
  public static string Substitute(string text, IReadOnlyDictionary<string, string> states) {
    foreach ((string code, string state) in states)
      text = text.Replace("{" + code + "}", state);
    return text;
  }
}
