using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Util;

namespace ExpandedLib.Shapes;

/// <summary>
/// One concrete block/item code a blocktype file's <c>variantgroups</c> expands to (every state
/// axis joined by <c>-</c>), still carrying the file's own raw JSON (shared by every variant of
/// that file) and the state each axis contributed - <see cref="BlockIndex.ByType"/> needs both to
/// pick a <c>shapeByType</c>/<c>texturesByType</c> entry and substitute its <c>{group}</c> tokens.
/// </summary>
public sealed record Variant(
  string Code,
  string Domain,
  JObject Raw,
  IReadOnlyDictionary<string, string> States,
  string SourceFile,
  bool Legacy = false
) {
  /// <summary>The code without its domain prefix.</summary>
  public string Path => Code[(Code.IndexOf(':') + 1)..];
}

/// <summary>One selector resolved to a concrete block: its shape file, the rotation its
/// <c>shapeByType</c> entry carries for that variant, and its texture map.</summary>
public sealed record ResolvedBlock(
  string Code,
  string? ShapePath,
  double RotateX,
  double RotateY,
  double RotateZ,
  IReadOnlyDictionary<string, string> Textures
);

/// <summary>
/// Finds a blocktype JSON by code across the game install and every mod repo, expands its
/// <c>variantgroups</c> to concrete codes, and resolves a cell's selector (a full code, a
/// <c>*</c> wildcard, or the <c>domain:@(a|b|c)</c> regex form the family's megablock fillers and
/// vanilla ore piles use) to one representative block.
/// <para>
/// A <c>shapeByType</c>/<c>texturesByType</c> entry is picked with the game's own
/// <see cref="WildcardUtil.Match(string, string)"/>, not a hand-rolled glob, so this index resolves
/// exactly the entry the running game would (<see cref="Layout"/>'s filler-only megablock case
/// resolves its <c>attributesByType</c> entry the same way).
/// </para>
/// <para>
/// A selector whose match spans more than one source file (two vanilla blocktype files can declare
/// the same base <c>code</c> - <c>game:cobblestone-*</c> among them, one of them a texture reskin
/// of the other under an unrelated file name) resolves deterministically: the file with an
/// expanded code exactly equal to the selector wins; else, for a wildcard selector, the file whose
/// own name (without <c>.json</c>) equals the selector's text before the first <c>*</c> with a
/// trailing dash trimmed; else the file that sorts first by path. Either way the ambiguity is
/// recorded in <see cref="Ambiguities"/> for a caller (the schematic manifest) to warn about,
/// naming every file involved. A match spanning a <c>legacy/</c> file and a current one is not an
/// ambiguity but two versions of one mod, settled by the side the index was built for (see
/// <see cref="Build"/>) and never recorded.
/// </para>
/// <para>
/// Two kinds of cell have no blocktype file to resolve to and are empty space in a schematic,
/// never warned about: <c>air</c> (block id 0), also when a wildcard or regex alternative admits
/// it (<c>air*</c>), and <c>multiblock-monolithic-&lt;dx&gt;-&lt;dy&gt;-&lt;dz&gt;</c>, the blocks the
/// game creates in code for the cells a door or a coffin section occupies beside its own.
/// </para>
/// </summary>
public sealed class BlockIndex {
  // Every blocktype file's own convention: the family's per-mod mods/<mod>/ and the published old
  // mods kept under legacy/<mod>/, or a single-mod repo's src/<project>/ and samples/<project>/
  // (shipped) and tests/<project>/goldens/ (code-first).
  private static readonly (string WildcardDir, string[] Literal)[] BlocktypeTrees = [
    ("mods", ["assets"]),
    ("mods", ["tests", "goldens"]),
    ("legacy", ["assets"]),
    ("legacy", ["tests", "goldens"]),
    ("src", ["assets"]),
    ("samples", ["assets"]),
    ("samples", ["tests", "goldens"]),
    ("tests", ["goldens"]),
  ];

  // A domain's real assets - shapes, textures, worldproperties - live only under one of these,
  // never under tests/*/goldens/: exlib ships framework-only blocks there with no shipped shapes
  // of their own domain elsewhere, so a blocktype found in goldens still resolves its shape here.
  // A domain can have a root in more than one tree (the old exlib under legacy/ declares the same
  // domain as the framework's src/), looked up in this order: the current tree answers first and
  // the old one only for a file it alone holds.
  private static readonly string[] AssetRootTrees = ["mods", "src", "samples", "legacy"];
  private static readonly string[] LegacyFirstAssetRootTrees = ["legacy", "mods", "src", "samples"];

  private readonly Dictionary<string, List<Variant>> _byCode = new(StringComparer.Ordinal);
  private readonly List<string> _order = [];
  private readonly Dictionary<string, List<string>> _domainRoots;
  private readonly bool _legacyFirst;
  private readonly Dictionary<string, List<string>> _ambiguities = new(StringComparer.Ordinal);

  /// <summary>Selector to every distinct source file its match spanned, sorted - populated as
  /// <see cref="Resolve"/>/<see cref="Representative"/>/<see cref="Optional"/> encounter an
  /// ambiguous selector, so a caller reads this only after resolving every selector it cares
  /// about.</summary>
  public IReadOnlyDictionary<string, IReadOnlyList<string>> Ambiguities =>
    _ambiguities.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);

  private BlockIndex(List<Variant> variants, Dictionary<string, List<string>> domainRoots, bool legacyFirst) {
    _domainRoots = domainRoots;
    _legacyFirst = legacyFirst;
    foreach (Variant v in variants) {
      if (!_byCode.TryGetValue(v.Code, out List<Variant>? list)) {
        list = [];
        _byCode[v.Code] = list;
        _order.Add(v.Code);
      }
      list.Add(v);
    }
  }

  /// <summary>
  /// Builds the index from every blocktype file under <paramref name="roots"/> (each root's
  /// <c>mods/*/assets/*/blocktypes/**</c>, <c>mods/*/tests/goldens/*/blocktypes/**</c>, the same
  /// two under <c>legacy/*</c>, <c>src/*/assets/*/blocktypes/**</c>,
  /// <c>samples/*/assets/*/blocktypes/**</c>, <c>samples/*/tests/goldens/*/blocktypes/**</c> and
  /// <c>tests/*/goldens/*/blocktypes/**</c>), plus the game install's own
  /// <c>assets/survival/blocktypes/**</c> under each root's <c>.game/&lt;version&gt;</c> (the
  /// latest version present). Symbolic links along a <c>.game</c> path are resolved first, so
  /// two roots linking the same install contribute its files once.
  /// </summary>
  /// <param name="roots">Repository checkouts to scan; see <see cref="DefaultRoots"/>.</param>
  /// <param name="gamePath">A game install directory that replaces every root's own
  /// <c>.game/&lt;version&gt;</c>, or null to discover one per root.</param>
  /// <param name="legacyFirst">True when the index serves a block under a <c>legacy/</c> tree
  /// (<see cref="UnderLegacyTree"/>): a code declared both there and in a current tree then
  /// resolves to the legacy file, and a domain's assets are looked up in the legacy tree first.
  /// False resolves both toward the current trees.</param>
  public static BlockIndex Build(IReadOnlyList<string> roots, string? gamePath = null, bool legacyFirst = false) {
    // An explicit `--game` (a game install directory, the same one GameInstall.Resolve returns)
    // overrides every root's own `.game/<version>` discovery, for both the domain root and the
    // blocktype files it contributes - a caller pointing this index at a different install than
    // whichever one a root's own checkout carries.
    string? explicitSurvival =
      gamePath != null ? RealPath(Path.Combine(gamePath, "assets", "survival")) : null;

    var domainRoots = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    foreach (string wildcardDir in legacyFirst ? LegacyFirstAssetRootTrees : AssetRootTrees)
      foreach (string root in roots)
        foreach ((string domain, string dir) in GlobAssetRoots(root, wildcardDir))
          if (domain != "game")
            AddDomainRoot(domainRoots, domain, dir);
    foreach (string root in roots) {
      string? survivalForRoots = explicitSurvival ?? GameSurvival(root);
      if (survivalForRoots != null)
        AddDomainRoot(domainRoots, "game", survivalForRoots);
    }

    var variants = new List<Variant>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (string root in roots) {
      foreach ((string wildcardDir, string[] literal) in BlocktypeTrees)
        foreach ((string domain, string file) in GlobBlocktypes(root, wildcardDir, literal)) {
          if (!seen.Add(file))
            continue;
          variants.AddRange(
            Expand(file, domain, domainRoots.GetValueOrDefault(domain) ?? [], wildcardDir == "legacy")
          );
        }

      string? survival = explicitSurvival ?? GameSurvival(root);
      if (survival == null)
        continue;
      string blocktypesDir = Path.Combine(survival, "blocktypes");
      if (!Directory.Exists(blocktypesDir))
        continue;
      foreach (
        string file in Directory
          .EnumerateFiles(blocktypesDir, "*.json", SearchOption.AllDirectories)
          .OrderBy(f => f, StringComparer.Ordinal)
      ) {
        if (!seen.Add(file))
          continue;
        variants.AddRange(Expand(file, "game", [survival]));
      }
    }

    return new BlockIndex(variants, domainRoots, legacyFirst);
  }

  /// <summary>True when <paramref name="file"/> sits under a <c>legacy/</c> directory - the
  /// published old mods' tree, whose blocks resolve their ties toward that tree (the
  /// <c>legacyFirst</c> argument of <see cref="Build"/>).</summary>
  public static bool UnderLegacyTree(string file) =>
    Path.GetFullPath(file).Split(Path.DirectorySeparatorChar).Contains("legacy");

  private static void AddDomainRoot(Dictionary<string, List<string>> domainRoots, string domain, string dir) {
    if (!domainRoots.TryGetValue(domain, out List<string>? dirs)) {
      dirs = [];
      domainRoots[domain] = dirs;
    }
    if (!dirs.Contains(dir))
      dirs.Add(dir);
  }

  /// <summary>The repository <paramref name="file"/> sits in (the nearest ancestor holding
  /// <c>workbench/</c>, <c>mods/</c> or <c>.game/</c>), plus its sibling <c>exlib</c> checkout when
  /// present (family blocks resolve framework-only selectors, <c>exlib:structurefiller</c> among
  /// them, only there) - the roots a schematic CLI passes to <see cref="Build"/> when the caller
  /// gave none explicitly.</summary>
  public static IReadOnlyList<string> DefaultRoots(string file) {
    string? root = TextureRoots.Build(null, null, file).RepoPath;
    if (root == null)
      return [];
    List<string> roots = [root];
    string sibling = Path.Combine(Path.GetDirectoryName(root) ?? root, "exlib");
    if (Directory.Exists(sibling) && !string.Equals(sibling, root, StringComparison.Ordinal))
      roots.Add(sibling);
    return roots;
  }

  /// <summary>
  /// The variant a selector resolves to, or null when it is unresolved or its first-resolving
  /// alternative is empty space (drawn as such by a caller, not this method's concern - see
  /// <see cref="Optional"/>).
  /// </summary>
  public Variant? Representative(string selector) {
    foreach ((string domain, string path, bool isRegex) in Alternatives(selector)) {
      bool empty = EmptySpace(domain, path, isRegex);
      if (empty && !isRegex && !path.Contains('*'))
        return null;
      Variant? v = Find(domain, path, isRegex, selector);
      if (v != null)
        return v;
      if (empty)
        return null;
    }
    return null;
  }

  /// <summary>True when the selector's first-resolving alternative is empty space - drawn as
  /// such, not warned about when nothing else matches.</summary>
  public bool Optional(string selector) {
    foreach ((string domain, string path, bool isRegex) in Alternatives(selector)) {
      bool empty = EmptySpace(domain, path, isRegex);
      if (empty && !isRegex && !path.Contains('*'))
        return true;
      if (Find(domain, path, isRegex, selector) != null)
        return false;
      if (empty)
        return true;
    }
    return false;
  }

  // The two cells the game fills without a blocktype file (the class remarks): a plain `air`
  // or `multiblock-monolithic-*` path, or a wildcard or regex path that admits `air`.
  private static bool EmptySpace(string domain, string path, bool isRegex) {
    if (domain != "*" && domain != "game")
      return false;
    if (path == "air" || path.StartsWith("multiblock-monolithic-", StringComparison.Ordinal))
      return true;
    Regex? pattern = isRegex ? new Regex("^(?:" + path + ")$") : path.Contains('*') ? GlobRegex(path) : null;
    return pattern != null && pattern.IsMatch("air");
  }

  /// <summary>The selector resolved to a block's shape, rotation and textures, or null when
  /// unresolved.</summary>
  public ResolvedBlock? Resolve(string selector) {
    Variant? v = Representative(selector);
    return v == null ? null : ToBlock(v);
  }

  /// <summary>
  /// A texture value from a resolved block's <see cref="ResolvedBlock.Textures"/> map (bare, or
  /// <c>domain:path</c>) to its PNG file, using this index's own domain roots rather than a shape
  /// file's own ancestry. A <c>*</c> in the value (a block that lets any of a set of variant
  /// textures stand in) picks the first match in directory order.
  /// </summary>
  public string? ResolveTexture(string value) {
    int colon = value.IndexOf(':');
    string domain = colon >= 0 ? value[..colon] : "game";
    string rel = colon >= 0 ? value[(colon + 1)..] : value;
    if (!_domainRoots.TryGetValue(domain, out List<string>? roots))
      return null;
    foreach (string root in roots) {
      string texturesRoot = Path.Combine(root, "textures");
      string? found = rel.Contains('*')
        ? GlobOne(texturesRoot, rel + ".png")
        : ExistingFile(texturesRoot, rel + ".png");
      if (found != null)
        return found;
    }
    return null;
  }

  private string? ShapePath(string baseCode) {
    int colon = baseCode.IndexOf(':');
    string domain = colon >= 0 ? baseCode[..colon] : "game";
    string rel = colon >= 0 ? baseCode[(colon + 1)..] : baseCode;
    if (!_domainRoots.TryGetValue(domain, out List<string>? roots))
      return null;
    foreach (string root in roots) {
      string p = Path.Combine(root, "shapes", rel + ".json");
      if (File.Exists(p))
        return p;
    }
    return null;
  }

  private Variant? Find(string domain, string path, bool isRegex, string selectorText) {
    if (domain != "*" && !isRegex && !path.Contains('*'))
      return _byCode.TryGetValue($"{domain}:{path}", out List<Variant>? exact) ? Disambiguate(exact, selectorText) : null;

    Regex pattern = isRegex ? new Regex("^(?:" + path + ")$") : GlobRegex(path);
    var matches = new List<Variant>();
    foreach (string code in _order)
      foreach (Variant v in _byCode[code]) {
        if (domain != "*" && v.Domain != domain)
          continue;
        if (pattern.IsMatch(v.Path))
          matches.Add(v);
      }
    return matches.Count == 0 ? null : Disambiguate(matches, selectorText);
  }

  // More than one source file among `matches`: a match spanning the legacy and the current trees
  // keeps only the side the index was built for; then a deterministic tie-break (a file with an
  // expanded code exactly equal to the selector text; else, for a wildcard selector, the file
  // whose own name equals the selector's text before the first `*` with a trailing dash trimmed;
  // else the file that sorts first by path), and the ambiguity is recorded once per selector for
  // the caller to warn about.
  private Variant Disambiguate(List<Variant> matches, string selectorText) {
    if (matches.Count == 1)
      return matches[0];
    if (matches.Any(v => v.Legacy) && matches.Any(v => !v.Legacy))
      matches = [.. matches.Where(v => v.Legacy == _legacyFirst)];
    List<string> files = [
      .. matches.Select(v => v.SourceFile).Distinct().OrderBy(f => f, StringComparer.Ordinal),
    ];
    if (files.Count == 1)
      return matches[0];

    if (!_ambiguities.ContainsKey(selectorText))
      _ambiguities[selectorText] = files;

    List<Variant> exact = [.. matches.Where(v => v.Code == selectorText).OrderBy(v => v.SourceFile, StringComparer.Ordinal)];
    if (exact.Count > 0)
      return exact[0];

    int star = selectorText.IndexOf('*');
    if (star >= 0) {
      string baseCode = selectorText[..star].TrimEnd('-');
      int colon = baseCode.IndexOf(':');
      // The declared "code" alone is not enough to name one file: two vanilla files can declare
      // the identical code (aquatic/cobble-coral.json and stone/cobble/cobblestone.json both say
      // "cobblestone") for what is really a texture reskin of the other, so the file itself - its
      // own name, stripped of ".json" - is what the selector's base code is actually naming.
      string baseName = colon >= 0 ? baseCode[(colon + 1)..] : baseCode;
      List<Variant> baseMatch = [
        .. matches
          .Where(v => Path.GetFileNameWithoutExtension(v.SourceFile) == baseName)
          .OrderBy(v => v.SourceFile, StringComparer.Ordinal),
      ];
      if (baseMatch.Count > 0)
        return baseMatch[0];
    }

    return matches.First(v => v.SourceFile == files[0]);
  }

  private ResolvedBlock ToBlock(Variant variant) {
    JObject? shapeEntry = ByType(variant.Raw, "shape", variant.Path) as JObject;
    string? shapeBase = (string?)shapeEntry?["base"];
    string? shapePath = shapeBase != null ? ShapePath(Substitute(shapeBase, variant.States)) : null;

    var textures = new Dictionary<string, string>();
    if (ByType(variant.Raw, "textures", variant.Path) is JObject texturesJson)
      foreach (JProperty prop in texturesJson.Properties()) {
        string? value = prop.Value is JObject vo ? (string?)vo["base"] : (string?)prop.Value;
        if (value != null)
          textures[prop.Name] = Substitute(value, variant.States);
      }

    return new ResolvedBlock(
      variant.Code,
      shapePath,
      (double?)shapeEntry?["rotateX"] ?? 0.0,
      (double?)shapeEntry?["rotateY"] ?? 0.0,
      (double?)shapeEntry?["rotateZ"] ?? 0.0,
      textures
    );
  }

  // The game's "<key>ByType" convention: the first entry whose wildcard key WildcardUtil.Match
  // accepts for `path` (domain stripped), else the plain raw[baseKey]. Shared by shape/shapeByType,
  // textures/texturesByType and, from Layout, attributes/attributesByType's fillerOffsets.
  internal static JToken? ByType(JObject raw, string baseKey, string path) {
    if (GetCi(raw, baseKey + "ByType") is JObject byType)
      foreach (JProperty prop in byType.Properties())
        if (WildcardUtil.Match(prop.Name, path))
          return prop.Value;
    return GetCi(raw, baseKey);
  }

  // Vanilla and family JSON disagree on the case of a few keys (shapeByType next to shapebytype); a
  // plain JObject indexer would silently miss one spelling.
  private static JToken? GetCi(JObject raw, string key) {
    if (raw[key] is { } exact)
      return exact;
    string lowered = key.ToLowerInvariant();
    foreach (JProperty prop in raw.Properties())
      if (prop.Name.ToLowerInvariant() == lowered)
        return prop.Value;
    return null;
  }

  private static string Substitute(string text, IReadOnlyDictionary<string, string> states) {
    foreach ((string code, string state) in states)
      text = text.Replace("{" + code + "}", state);
    return text;
  }

  private static readonly Regex AltRe = new(@"@\(([^)]*)\)");

  // A selector as (domain, path, is_regex) candidates, tried in order until one resolves: a plain
  // code or a `*` wildcard is one candidate (glob semantics); a `domain:@(a|b|c)` selector is one
  // candidate per alternative (regex semantics, since an alternative may itself carry a raw
  // fragment like `hearthmetal-.*`); an alternative naming its own `domain:path` overrides the
  // selector's outer domain.
  internal static List<(string Domain, string Path, bool IsRegex)> Alternatives(string selector) {
    int colon = selector.IndexOf(':');
    string domain = colon >= 0 ? selector[..colon] : "*";
    string path = colon >= 0 ? selector[(colon + 1)..] : selector;
    Match m = AltRe.Match(path);
    if (!m.Success)
      return [(domain, path, false)];

    string prefix = path[..m.Index];
    string suffix = path[(m.Index + m.Length)..];
    var outp = new List<(string, string, bool)>();
    foreach (string alt in m.Groups[1].Value.Split('|')) {
      int altColon = alt.IndexOf(':');
      outp.Add(
        altColon >= 0 ? (alt[..altColon], alt[(altColon + 1)..], true) : (domain, prefix + alt + suffix, true)
      );
    }
    return outp;
  }

  // A selector's `*` wildcard as a fullmatch regex; every other character is literal.
  private static Regex GlobRegex(string text) => new("^" + Regex.Escape(text).Replace(@"\*", ".*") + "$");

  private static List<Variant> Expand(
    string path,
    string domain,
    IReadOnlyList<string> domainRoots,
    bool legacy = false
  ) {
    JObject raw;
    try {
      if (JToken.Parse(File.ReadAllText(path)) is not JObject parsed || parsed["code"] == null)
        return [];
      raw = parsed;
    } catch {
      return [];
    }
    string code = (string)raw["code"]!;

    var axes = new List<(string Code, List<string> States)>();
    if (raw["variantgroups"] is JArray groups)
      foreach (JToken g in groups) {
        string? gcode = (string?)g["code"];
        if (g["states"] is JArray states) {
          if (gcode != null)
            axes.Add((gcode, [.. states.Select(s => (string)s!)]));
        } else if ((string?)g["loadFromProperties"] is { } reference) {
          // A group naming only a worldproperties file takes that file's own code as its axis,
          // the way the game does for `{ loadFromProperties: "abstract/horizontalorientation" }`.
          (string? fileCode, List<string> fileStates) = PropertyStates(reference, domainRoots);
          if ((gcode ?? fileCode) is { } axisCode)
            axes.Add((axisCode, fileStates));
        }
      }

    List<Dictionary<string, string>> combos = [[]];
    foreach ((string acode, List<string> states) in axes) {
      if (states.Count == 0)
        continue;
      var next = new List<Dictionary<string, string>>();
      foreach (Dictionary<string, string> c in combos)
        foreach (string s in states)
          next.Add(new Dictionary<string, string>(c) { [acode] = s });
      combos = next;
    }

    List<Regex> skip = [.. ((JArray?)raw["skipVariants"])?.Select(v => GlobRegex((string)v!)) ?? []];
    List<Regex> allowed = [.. ((JArray?)raw["allowedVariants"])?.Select(v => GlobRegex((string)v!)) ?? []];

    var variants = new List<Variant>();
    foreach (Dictionary<string, string> combo in combos) {
      string suffix = string.Join('-', axes.Where(a => combo.ContainsKey(a.Code)).Select(a => combo[a.Code]));
      string fullPath = suffix.Length == 0 ? code : $"{code}-{suffix}";
      if (skip.Any(p => p.IsMatch(fullPath)))
        continue;
      if (allowed.Count > 0 && !allowed.Any(p => p.IsMatch(fullPath)))
        continue;
      variants.Add(new Variant($"{domain}:{fullPath}", domain, raw, combo, path, legacy));
    }
    return variants;
  }

  // A loadFromProperties group's worldproperties file: its own code and the Code of every variant
  // it lists, in file order, from the first domain root holding the file; no code and no states
  // when no root does, so that axis drops out of expansion rather than failing the whole blocktype.
  private static (string? Code, List<string> States) PropertyStates(
    string reference,
    IReadOnlyList<string> domainRoots
  ) {
    string? p = domainRoots
      .Select(root => Path.Combine(root, "worldproperties", reference + ".json"))
      .FirstOrDefault(File.Exists);
    if (p == null)
      return (null, []);
    try {
      if (JToken.Parse(File.ReadAllText(p)) is not JObject data || data["variants"] is not JArray variants)
        return (null, []);
      List<string> outp = [];
      foreach (JToken v in variants) {
        string? code = (string?)v["Code"] ?? (string?)v["code"];
        if (code != null)
          outp.Add(code);
      }
      return ((string?)GetCi(data, "code"), outp);
    } catch {
      return (null, []);
    }
  }

  // <root>/<wildcardDir>/*/assets/* (mods/*/assets/*, src/*/assets/*), "game" excluded - reserved
  // for the real vanilla install (a mod's own assets/game/ holds compat patches, not the domain's
  // full asset tree).
  private static IEnumerable<(string Domain, string Root)> GlobAssetRoots(string root, string wildcardDir) {
    string parent = Path.Combine(root, wildcardDir);
    if (!Directory.Exists(parent))
      yield break;
    foreach (string modDir in Directory.EnumerateDirectories(parent).OrderBy(d => d, StringComparer.Ordinal)) {
      string assetsDir = Path.Combine(modDir, "assets");
      if (!Directory.Exists(assetsDir))
        continue;
      foreach (string domainDir in Directory.EnumerateDirectories(assetsDir).OrderBy(d => d, StringComparer.Ordinal))
        yield return (Path.GetFileName(domainDir), domainDir);
    }
  }

  // <root>/<wildcardDir>/*/<literal...>/*/blocktypes/**/*.json, sorted at every level.
  private static IEnumerable<(string Domain, string File)> GlobBlocktypes(
    string root,
    string wildcardDir,
    string[] literal
  ) {
    string parent = Path.Combine(root, wildcardDir);
    if (!Directory.Exists(parent))
      yield break;
    foreach (string modDir in Directory.EnumerateDirectories(parent).OrderBy(d => d, StringComparer.Ordinal)) {
      string mid = literal.Aggregate(modDir, Path.Combine);
      if (!Directory.Exists(mid))
        continue;
      foreach (string domainDir in Directory.EnumerateDirectories(mid).OrderBy(d => d, StringComparer.Ordinal)) {
        string blocktypesDir = Path.Combine(domainDir, "blocktypes");
        if (!Directory.Exists(blocktypesDir))
          continue;
        string domain = Path.GetFileName(domainDir);
        foreach (
          string file in Directory
            .EnumerateFiles(blocktypesDir, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
        )
          yield return (domain, file);
      }
    }
  }

  // The latest version's assets/survival under <root>/.game, the same root the game's own
  // blocktypes, shapes and worldproperties ship from.
  private static string? GameSurvival(string root) {
    string game = Path.Combine(root, ".game");
    if (!Directory.Exists(game))
      return null;
    game = RealPath(game);
    List<string> versions = [
      .. Directory
        .EnumerateDirectories(game)
        .OrderBy(d => Path.GetFileName(d), Comparer<string>.Create(CompareVersions)),
    ];
    for (int i = versions.Count - 1; i >= 0; i--) {
      string survival = Path.Combine(versions[i], "assets", "survival");
      if (Directory.Exists(survival))
        return survival;
    }
    return null;
  }

  // The path with every symbolic link along it resolved: the family's checkouts each link .game to
  // one shared install, and only the resolved path lets the same vanilla file reached through two
  // roots dedupe as one entry instead of resolving a selector ambiguously against itself.
  private static string RealPath(string path) {
    string full = Path.GetFullPath(path);
    string current = Path.GetPathRoot(full) is { Length: > 0 } root ? root : Path.DirectorySeparatorChar.ToString();
    foreach (
      string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
    ) {
      current = Path.Combine(current, part);
      FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
      if (info.LinkTarget != null && info.ResolveLinkTarget(true) is { } target)
        current = target.FullName;
    }
    return current;
  }

  // Splits a version folder name on '.' and compares part by part numerically, a non-numeric part
  // (the "-server" suffix) sorting as 0 - the same key Python's `int(x) if x.isdigit() else 0` gives.
  private static int CompareVersions(string a, string b) {
    string[] pa = a.Split('.');
    string[] pb = b.Split('.');
    for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++) {
      int va = i < pa.Length && int.TryParse(pa[i], out int na) ? na : 0;
      int vb = i < pb.Length && int.TryParse(pb[i], out int nb) ? nb : 0;
      if (va != vb)
        return va.CompareTo(vb);
    }
    return 0;
  }

  // A relative pattern (segments possibly carrying a `*`, matching within that segment only - the
  // same restriction pathlib's glob applies) resolved under `root`, first hit in sorted order.
  private static string? GlobOne(string root, string relPattern) {
    string[] segments = relPattern.Split('/');
    List<string> current = [root];
    for (int i = 0; i < segments.Length; i++) {
      bool isLast = i == segments.Length - 1;
      string seg = segments[i];
      Regex? rx = seg.Contains('*') ? GlobRegex(seg) : null;
      var next = new List<string>();
      foreach (string dir in current) {
        if (!Directory.Exists(dir))
          continue;
        IEnumerable<string> names = (
          isLast ? Directory.EnumerateFiles(dir) : Directory.EnumerateDirectories(dir)
        )
          .Select(Path.GetFileName)
          .OfType<string>()
          .OrderBy(n => n, StringComparer.Ordinal);
        foreach (string name in names)
          if (rx?.IsMatch(name) ?? name == seg)
            next.Add(Path.Combine(dir, name));
      }
      current = next;
    }
    return current.FirstOrDefault();
  }

  private static string? ExistingFile(string root, string relPath) {
    string p = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
    return File.Exists(p) ? p : null;
  }
}
