using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Assets;

/// <summary>
/// Every concrete block/item code a domain declares, resolved from its <c>blocktypes/</c> and
/// <c>itemtypes/</c> JSON the same way <c>RegistryObjectTypeLoader</c> does for a
/// <c>variantgroups</c> entry carrying its states inline (<c>{ "code": "type", "states": [...] }</c>).
/// A group that instead names <c>loadFromProperties</c> (a shared <c>config/worldproperties/*.json</c>
/// entry) needs the loader's own <c>ICoreServerAPI</c>-bound world-property resolution this tool does
/// not have, so that type's codes are never enumerated - its base code is recorded as an
/// <see cref="UnresolvedPrefixes"/> entry instead, and any reference under that prefix is treated as
/// resolvable rather than risking a false error.
/// </summary>
public sealed class BlockItemCatalogue {
  /// <summary>domain -> every concrete <c>code-state-state</c> string it registers (no domain
  /// prefix), one set for blocks and one for items.</summary>
  public Dictionary<string, HashSet<string>> Blocks { get; } =
    new(StringComparer.Ordinal);

  /// <inheritdoc cref="Blocks"/>
  public Dictionary<string, HashSet<string>> Items { get; } =
    new(StringComparer.Ordinal);

  /// <summary>domain -> every base code whose <c>variantgroups</c> could not be fully expanded, so a
  /// reference under it is never reported as a dangling code.</summary>
  public Dictionary<string, HashSet<string>> UnresolvedPrefixes { get; } =
    new(StringComparer.Ordinal);

  /// <summary>Builds the catalogue for every domain <paramref name="store"/> holds.</summary>
  public static BlockItemCatalogue Build(AssetStore store) {
    var catalogue = new BlockItemCatalogue();
    foreach (string domain in store.Domains) {
      catalogue.Blocks[domain] = [];
      catalogue.Items[domain] = [];
      catalogue.UnresolvedPrefixes[domain] = [];

      foreach ((string _, JToken json) in store.Under(domain, "blocktypes/"))
        if (json is JObject obj)
          catalogue.Expand(domain, obj, catalogue.Blocks[domain]);

      foreach ((string _, JToken json) in store.Under(domain, "itemtypes/"))
        if (json is JObject obj)
          catalogue.Expand(domain, obj, catalogue.Items[domain]);
    }
    return catalogue;
  }

  private void Expand(string domain, JObject type, HashSet<string> into) {
    string? code = (string?)type["code"];
    if (string.IsNullOrEmpty(code) || (bool?)type["enabled"] == false)
      return;

    List<Variant>? variants = TryExpand(type, code!);
    if (variants == null) {
      // loadFromProperties or some other group shape this tool cannot resolve headlessly -
      // treat the whole type as an unresolved prefix rather than guess at its states.
      UnresolvedPrefixes[domain].Add(code!);
      return;
    }

    foreach (Variant variant in variants)
      into.Add(variant.Code);
  }

  /// <summary>
  /// Expands a definition's own <c>variantgroups</c> into every concrete variant, the same
  /// combinatorial rule <c>RegistryObjectTypeLoader</c> uses for a group carrying its states
  /// inline, filtered by <c>skipVariants</c>/<c>allowedVariants</c>. Returns null when a group
  /// names <c>loadFromProperties</c> instead of inline <c>states</c> - that axis needs the
  /// loader's own world-property resolution this tool does not have, so the type is left
  /// unexpanded rather than guessed at.
  /// </summary>
  public static List<Variant>? TryExpand(JObject type, string code) {
    List<(string Code, Dictionary<string, string> States)> combos = [(code, [])];
    if (type["variantgroups"] is JArray groups)
      foreach (JToken groupToken in groups) {
        if (groupToken is not JObject group)
          continue;
        if (group["states"] is not JArray states)
          return null;
        string? axisCode = (string?)group["code"];
        string[] values = [.. states.Select(s => (string?)s).OfType<string>()];
        combos = [
          .. combos.SelectMany(c =>
            values.Select(v => {
              var next = new Dictionary<string, string>(c.States, StringComparer.Ordinal);
              if (axisCode != null)
                next[axisCode] = v;
              return ($"{c.Code}-{v}", next);
            })
          ),
        ];
      }

    var skip = new HashSet<string>(
      ((JArray?)type["skipVariants"])?.Select(v => (string?)v).OfType<string>() ?? [],
      StringComparer.Ordinal
    );
    List<string>? allow = ((JArray?)type["allowedVariants"])
      ?.Select(v => (string?)v)
      .OfType<string>()
      .ToList();

    return [
      .. combos
        .Where(c => !skip.Contains(c.Code) && (allow is not { Count: > 0 } || allow.Contains(c.Code)))
        .Select(c => new Variant(c.Code, c.States)),
    ];
  }
}

/// <summary>One concrete <c>code-state-state</c> a definition's own <c>variantgroups</c> expand
/// to, and the axis code -> chosen state map that produced it - a caller substituting a
/// <c>shapeByType</c> entry's own <c>{group}</c> tokens needs the map, not just the code.</summary>
public sealed record Variant(string Code, IReadOnlyDictionary<string, string> States);
