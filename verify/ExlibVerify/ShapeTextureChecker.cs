using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using ExpandedLib.Assets;

namespace ExpandedLib.Verify;

/// <summary>
/// Checks that every texture code a blocktype's or itemtype's shape actually uses (a face's
/// <c>texture</c> is <c>#code</c>) is covered by either the shape's own <c>textures</c> map or the
/// definition's own <c>textures</c>/<c>texturesByType</c> resolved for that concrete variant - the
/// same lookup the client's shape tesselator performs. A code neither covers logs "Missing mapping
/// for texture code #code during shape tesselation of block ..." and draws the face untextured; a
/// block finding is an error (the client always logs it), an item finding is a warning (the client
/// is silent there - vanilla's own metalbit/nugget pair leaves <c>#granite</c> unmapped this way).
/// </summary>
public static class ShapeTextureChecker {
  /// <summary>Every missing-mapping finding among <paramref name="definitionFiles"/> - the mod's
  /// own <c>blocktypes/</c> or <c>itemtypes/</c> files, resolved through <paramref name="store"/>
  /// so a shape reached through a patch or a code-first golden resolves the same way the game
  /// would. A definition naming no shape at all is skipped - nothing to tesselate.</summary>
  /// <param name="isBlock">True for a <c>blocktypes/</c> scan (an error), false for
  /// <c>itemtypes/</c> (a warning).</param>
  public static List<Finding> Run(
    AssetStore store,
    string domain,
    IEnumerable<(string Path, JToken Json)> definitionFiles,
    bool isBlock
  ) {
    var findings = new List<Finding>();
    foreach ((string path, JToken json) in definitionFiles) {
      if (json is not JObject raw)
        continue;
      string? code = (string?)raw["code"];
      if (string.IsNullOrEmpty(code) || (bool?)raw["enabled"] == false)
        continue;

      foreach (string variant in ExpandVariants(raw, code!))
        CheckVariant(store, domain, path, raw, variant, isBlock, findings);
    }
    return findings;
  }

  private static void CheckVariant(
    AssetStore store,
    string domain,
    string path,
    JObject raw,
    string variant,
    bool isBlock,
    List<Finding> findings
  ) {
    JObject? declaredTextures =
      BlockTypeResolution.ByType(raw, "textures", variant) as JObject;
    var declaredCodes = new HashSet<string>(
      declaredTextures?.Properties().Select(p => p.Name) ?? [],
      StringComparer.Ordinal
    );

    foreach ((string shapeDomain, string shapePath) in ShapeRefs(raw, variant)) {
      if (store.TryGet(shapeDomain, $"shapes/{shapePath}.json") is not JObject shape)
        continue; // A dangling shape reference is not this check's business to report.
      if (shape["elements"] is not JArray elements)
        continue;

      var shapeCodes = new HashSet<string>(
        (shape["textures"] as JObject)?.Properties().Select(p => p.Name) ?? [],
        StringComparer.Ordinal
      );

      foreach (string faceCode in FaceCodes(elements).Distinct(StringComparer.Ordinal)) {
        if (shapeCodes.Contains(faceCode) || declaredCodes.Contains(faceCode))
          continue;
        findings.Add(
          new Finding(
            isBlock ? FindingLevel.Error : FindingLevel.Info,
            "ShapeTexture",
            $"{domain}:{path}",
            null,
            $"Missing mapping for texture code #{faceCode} in shape {shapeDomain}:{shapePath} "
              + $"for {domain}:{variant}"
          )
        );
      }
    }
  }

  // Every shape a variant can draw: its own shape/shapeByType base, plus every alternates[].base
  // beside it - each is a distinct shape file the client can tesselate for that block.
  private static IEnumerable<(string Domain, string Path)> ShapeRefs(JObject raw, string variant) {
    JToken? entry = BlockTypeResolution.ByType(raw, "shape", variant);
    if (SplitShapeRef(entry) is { } baseRef)
      yield return baseRef;
    if (entry is JObject obj && obj["alternates"] is JArray alternates)
      foreach (JToken alt in alternates)
        if (SplitShapeRef(alt) is { } altRef)
          yield return altRef;
  }

  // A shape entry's own "base" (or the entry itself when it is a bare string) split into its
  // domain and path - a bare path defaults to "game", the same default AssetLocation gives one.
  private static (string Domain, string Path)? SplitShapeRef(JToken? entry) {
    string? value = entry is JObject obj ? (string?)obj["base"] : (string?)entry;
    if (string.IsNullOrEmpty(value))
      return null;
    int colon = value.IndexOf(':');
    return colon < 0
      ? ("game", value)
      : (value[..colon], value[(colon + 1)..]);
  }

  // Every `#code` a shape's elements (children included) name on a face.
  private static IEnumerable<string> FaceCodes(JArray elements) {
    foreach (JToken el in elements) {
      if (el["faces"] is JObject faces)
        foreach (JProperty face in faces.Properties())
          if ((string?)face.Value["texture"] is { } tex && tex.StartsWith('#'))
            yield return tex[1..];
      if (el["children"] is JArray children)
        foreach (string code in FaceCodes(children))
          yield return code;
    }
  }

  // Every concrete "code-state-state" a definition's inline variantgroups expand to, the same
  // combinatorial rule BlockItemCatalogue.Expand uses; a group naming loadFromProperties instead of
  // inline states cannot be resolved here and is dropped from expansion rather than guessed at, so
  // its axis never appears in the returned paths.
  private static List<string> ExpandVariants(JObject raw, string code) {
    List<string> combos = [code];
    if (raw["variantgroups"] is JArray groups)
      foreach (JToken group in groups) {
        if (group["states"] is not JArray states)
          continue;
        string[] values = [.. states.Select(s => (string?)s).OfType<string>()];
        combos = [.. combos.SelectMany(c => values.Select(v => $"{c}-{v}"))];
      }

    var skip = new HashSet<string>(
      ((JArray?)raw["skipVariants"])?.Select(v => (string?)v).OfType<string>() ?? [],
      StringComparer.Ordinal
    );
    List<string>? allow = ((JArray?)raw["allowedVariants"])
      ?.Select(v => (string?)v)
      .OfType<string>()
      .ToList();

    return [
      .. combos.Where(c =>
        !skip.Contains(c) && (allow is not { Count: > 0 } || allow.Contains(c))
      ),
    ];
  }
}
