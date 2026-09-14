using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Assets;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Verify;

/// <summary>
/// Checks that every texture code a blocktype's or itemtype's shape actually uses (a face's
/// <c>texture</c> is <c>#code</c>) is covered by either the shape's own <c>textures</c> map or the
/// definition's own <c>textures</c>/<c>texturesByType</c> resolved for that concrete variant - the
/// same lookup the client's shape tesselator performs, <c>all</c>/<c>sides</c>/<c>horizontals</c>/
/// <c>verticals</c> shorthands included (see <c>ExpandedLib.Shapes.Schematic.Shorthands</c>, whose
/// same table fixed the slab-lined furnace cores rendering magenta). A code neither covers logs
/// "Missing mapping for texture code #code during shape tesselation of block ..." and draws the
/// face untextured; a block finding is an error and an item finding is informational - the owner's
/// ruling, not a difference in the client's own logging (it logs for an item exactly as for a
/// block): vanilla itself ships this defect, its own metalbit mapping only <c>#ore</c> against
/// <c>game:item/nugget</c>'s <c>#granite</c>, so an item finding stays a note rather than failing a
/// mod's run for a defect the mod inherited from the game.
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

      // A group naming loadFromProperties cannot be expanded headlessly; the type is skipped here
      // rather than guessed at, the same way BlockItemCatalogue.UnresolvedPrefixes already reports
      // it to the user once, in Runner.
      List<Variant>? variants = BlockItemCatalogue.TryExpand(raw, code!);
      if (variants == null)
        continue;
      foreach (Variant variant in variants)
        CheckVariant(store, domain, path, raw, variant, isBlock, findings);
    }
    return findings;
  }

  private static void CheckVariant(
    AssetStore store,
    string domain,
    string path,
    JObject raw,
    Variant variant,
    bool isBlock,
    List<Finding> findings
  ) {
    JObject? declaredTextures =
      BlockTypeResolution.ByType(raw, "textures", variant.Code) as JObject;
    var declaredCodes = new HashSet<string>(
      declaredTextures?.Properties().Select(p => p.Name) ?? [],
      StringComparer.Ordinal
    );

    foreach ((string shapeDomain, string shapePath) in ShapeRefs(raw, variant)) {
      if (
        store.TryGet(shapeDomain, $"shapes/{shapePath}.json")
        is not JObject shape
      )
        continue; // A dangling shape reference is not this check's business to report.
      if (shape["elements"] is not JArray elements)
        continue;

      var shapeCodes = new HashSet<string>(
        (shape["textures"] as JObject)?.Properties().Select(p => p.Name) ?? [],
        StringComparer.Ordinal
      );

      foreach (
        string faceCode in FaceCodes(elements).Distinct(StringComparer.Ordinal)
      ) {
        if (
          shapeCodes.Contains(faceCode)
          || declaredCodes.Contains(faceCode)
          || CoveredByShorthand(faceCode, shapeCodes)
          || CoveredByShorthand(faceCode, declaredCodes)
        )
          continue;
        findings.Add(
          new Finding(
            isBlock ? FindingLevel.Error : FindingLevel.Info,
            "ShapeTexture",
            $"{domain}:{path}",
            null,
            $"Missing mapping for texture code #{faceCode} in shape {shapeDomain}:{shapePath} "
              + $"for {domain}:{variant.Code}"
          )
        );
      }
    }
  }

  private static readonly string[] Horizontals =
  [
    "north",
    "east",
    "south",
    "west",
  ];
  private static readonly string[] Verticals = ["up", "down"];
  private static readonly string[] Sides =
  [
    "north",
    "east",
    "south",
    "west",
    "up",
    "down",
  ];
  private static readonly string[] WestEast = ["west", "east"];
  private static readonly string[] NorthSouth = ["north", "south"];

  // "all" stands in for any face code at all; "sides" for the six cardinal directions (west, east,
  // north, south, up, down - not "any code", TextureAtlasManager.ResolveTextureDict's own rule);
  // "horizontals"/"verticals" for the four side faces and the two vertical ones; "westeast"/
  // "northsouth" for their own opposing pair - the game's own convention, matching
  // ExpandedLib.Shapes.Schematic.Shorthands.
  private static bool CoveredByShorthand(
    string faceCode,
    HashSet<string> codes
  ) =>
    codes.Contains("all")
    || (Sides.Contains(faceCode) && codes.Contains("sides"))
    || (Horizontals.Contains(faceCode) && codes.Contains("horizontals"))
    || (Verticals.Contains(faceCode) && codes.Contains("verticals"))
    || (WestEast.Contains(faceCode) && codes.Contains("westeast"))
    || (NorthSouth.Contains(faceCode) && codes.Contains("northsouth"));

  // Every shape a variant can draw: its own shape/shapeByType base, plus every alternates[].base
  // beside it - each is a distinct shape file the client can tesselate for that block. A base
  // path's own {group} tokens are substituted from the variant's states first, the same rule
  // ExpandedLib.Shapes.BlockIndex resolves a shape file with.
  private static IEnumerable<(string Domain, string Path)> ShapeRefs(
    JObject raw,
    Variant variant
  ) {
    JToken? entry = BlockTypeResolution.ByType(raw, "shape", variant.Code);
    if (SplitShapeRef(entry, variant.States) is { } baseRef)
      yield return baseRef;
    if (entry is JObject obj && obj["alternates"] is JArray alternates)
      foreach (JToken alt in alternates)
        if (SplitShapeRef(alt, variant.States) is { } altRef)
          yield return altRef;
  }

  // A shape entry's own "base" (or the entry itself when it is a bare string) split into its
  // domain and path - a bare path defaults to "game", the same default AssetLocation gives one.
  // The variant's own states substitute the base's {group} tokens first (a slab's own
  // "block/basic/slab/slab-{rot}", say), the same rule ExpandedLib.Shapes.BlockIndex resolves a
  // shape file with, or a shape reached only through such a token is never looked up.
  private static (string Domain, string Path)? SplitShapeRef(
    JToken? entry,
    IReadOnlyDictionary<string, string> states
  ) {
    string? value = entry is JObject obj
      ? (string?)obj["base"]
      : (string?)entry;
    if (string.IsNullOrEmpty(value))
      return null;
    value = BlockTypeResolution.Substitute(value, states);
    int colon = value.IndexOf(':');
    return colon < 0 ? ("game", value) : (value[..colon], value[(colon + 1)..]);
  }

  // Every `#code` a shape's elements (children included) name on a face still resolved for
  // tesselation - `#null` excepted, Model Creator's own marker for a face with no texture at all,
  // and a face carrying `"enabled": false` excepted too, ShapeElement.TrimTextureNamesAndResolveFaces'
  // own rule: a disabled face never enters FacesResolved, so the tesselator never requests its code.
  private static IEnumerable<string> FaceCodes(JArray elements) {
    foreach (JToken el in elements) {
      if (el["faces"] is JObject faces)
        foreach (JProperty face in faces.Properties())
          if (
            (string?)face.Value["texture"] is { } tex
            && tex.StartsWith('#')
            && tex != "#null"
            && (bool?)face.Value["enabled"] != false
          )
            yield return tex[1..];
      if (el["children"] is JArray children)
        foreach (string code in FaceCodes(children))
          yield return code;
    }
  }
}
