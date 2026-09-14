using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ExpandedLib.Assets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Verify;

/// <summary>
/// The whole <c>exlib-verify</c> run, factored out of <c>Program.cs</c> so
/// <c>ExlibVerify.Tests</c> can drive it in-process against a fixture folder and assert on its
/// findings and exit code, the same call a real invocation makes.
/// </summary>
public static class Runner {
  /// <summary>
  /// Parses <paramref name="args"/>, checks the named mod, writes the report to
  /// <paramref name="stdout"/>/<paramref name="stderr"/>, and returns the process exit code: 0
  /// clean (or informational-only without <c>--strict</c>), 1 an error was found (or
  /// informational with <c>--strict</c>), 2 a usage or load failure.
  /// </summary>
  public static int Run(string[] args, TextWriter stdout, TextWriter stderr) =>
    Run(args, stdout, stderr, out _);

  /// <summary>As <see cref="Run(string[],TextWriter,TextWriter)"/>, also handing back every
  /// <see cref="Finding"/> raised - what a test asserts against, rather than re-parsing the
  /// printed report.</summary>
  public static int Run(
    string[] args,
    TextWriter stdout,
    TextWriter stderr,
    out List<Finding> findings
  ) {
    findings = [];
    string? modPath = null;
    string? gamePath = null;
    var modsDirs = new List<string>();
    bool json = false;
    bool strict = false;

    for (int i = 0; i < args.Length; i++) {
      switch (args[i]) {
        case "--game":
          gamePath = args[++i];
          break;
        case "--mods":
          while (
            i + 1 < args.Length
            && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
          )
            modsDirs.Add(args[++i]);
          break;
        case "--json":
          json = true;
          break;
        case "--strict":
          strict = true;
          break;
        default:
          modPath ??= args[i];
          break;
      }
    }

    if (modPath == null) {
      stderr.WriteLine(
        "usage: exlib-verify <modpath> [--game <install>] [--mods <dir>...] [--json] [--strict]"
      );
      return 2;
    }

    try {
      ModSource primary = ModSource.Load(modPath);
      string game = GameInstall.Resolve(gamePath);
      // VintagestoryAPI and Tavis.JsonPatch are compile-only (Private=false - a global tool must
      // never bundle a copy of the game's own assemblies) and resolved at runtime from whichever
      // install --game/$VINTAGE_STORY actually named.
      string[] probeDirs =
      [
        game,
        Path.Combine(game, "Lib"),
        Path.Combine(game, "Mods"),
      ];
      ResolveEventHandler resolver = (_, resolveArgs) => {
        string? name = new AssemblyName(resolveArgs.Name).Name;
        if (name == null)
          return null;
        foreach (string dir in probeDirs) {
          string path = Path.Combine(dir, name + ".dll");
          if (File.Exists(path))
            return Assembly.LoadFrom(path);
        }
        return null;
      };
      AppDomain.CurrentDomain.AssemblyResolve += resolver;
      try {
        RunChecks(primary, game, modsDirs, findings);
      } finally {
        AppDomain.CurrentDomain.AssemblyResolve -= resolver;
      }
    } catch (Exception e) {
      stderr.WriteLine($"exlib-verify: {e.Message}");
      return 2;
    }

    Report(findings, json, stdout);

    bool hasErrors = findings.Any(f => f.Level == FindingLevel.Error);
    bool hasInfo = findings.Any(f => f.Level == FindingLevel.Info);
    return hasErrors || (strict && hasInfo) ? 1 : 0;
  }

  private static void RunChecks(
    ModSource primary,
    string game,
    List<string> modsDirs,
    List<Finding> findings
  ) {
    List<ModSource> extras = [.. modsDirs.Select(ModSource.Load)];

    var store = new AssetStore();
    store.AddRoot(
      game,
      remapDomain: folder =>
        folder is "survival" or "creative" ? "game" : folder
    );
    foreach (ModSource extra in extras)
      store.AddRoot(extra.RootDir);
    // Loaded last, and with parse errors reported: the mod under test is what this run is
    // checking, so a syntax mistake in its own JSON is always a finding, while one in a game
    // file or a dependency mod is not this run's business to report.
    store.AddRoot(
      primary.RootDir,
      onParseError: (file, message, line, col) =>
        findings.Add(
          new Finding(
            FindingLevel.Error,
            "JsonParse",
            Path.GetRelativePath(primary.RootDir, file),
            line,
            $"{message} (column {col})"
          )
        )
    );

    var loadedModIds = new HashSet<string>(StringComparer.Ordinal)
    {
      primary.ModId.ToLowerInvariant(),
      "game",
      "survival",
      "creative",
    };
    foreach (ModSource extra in extras)
      loadedModIds.Add(extra.ModId.ToLowerInvariant());

    // A mod's own asset domain (the folder name under its assets/) is not guaranteed to match its
    // modid - so every check scoped to "the mod under test" runs over every domain its own
    // assets/ folder actually carries, not over ModId as a string.
    string[] primaryDomains = AssetDomains(primary);
    string[] patchDomains =
    [
      .. primaryDomains,
      .. extras.SelectMany(AssetDomains),
    ];
    // A mod that ships an assembly can register assets that never exist on disk (exlib's code-first
    // definitions inject one per definition), so patches aimed into its domain cannot be resolved
    // here. Naming those domains is what keeps that an informational note rather than a false error.
    // A domain's own tests/goldens tree - the same fixtures its assembly's definitions are built
    // from - is what the game actually sees there, so it is merged into the store as that domain's
    // own content, the tree ExpandedLib.Shapes.BlockIndex already resolves a golden's shape
    // through.
    var codeDomains = new HashSet<string>(StringComparer.Ordinal);
    foreach (ModSource mod in extras.Prepend(primary))
      if (mod.ShipsCode) {
        string? goldens = GoldensDir(mod.RootDir);
        foreach (string domain in AssetDomains(mod)) {
          codeDomains.Add(domain);
          if (goldens != null)
            store.AddDomainRoot(domain, Path.Combine(goldens, domain));
        }
      }

    findings.AddRange(
      PatchChecker.Run(store, patchDomains, loadedModIds, codeDomains)
    );

    BlockItemCatalogue catalogue = BlockItemCatalogue.Build(store);
    foreach (string domain in primaryDomains)
      foreach (
        string prefix in catalogue.UnresolvedPrefixes.GetValueOrDefault(
          domain,
          []
        )
      )
        findings.Add(
          new Finding(
            FindingLevel.Info,
            "VariantExpansion",
            null,
            null,
            $"{domain}:'{prefix}' declares a variantgroup this tool cannot expand "
              + "(loadFromProperties) - references under it are assumed to resolve"
          )
        );

    foreach (string domain in primaryDomains) {
      findings.AddRange(
        RecipeCodeChecker.Run(
          catalogue,
          domain,
          OwnFiles(primary, store, domain, "recipes/")
        )
      );
      findings.AddRange(
        HandbookLangChecker.Run(
          store,
          domain,
          OwnFiles(primary, store, domain, "config/handbook/")
        )
      );
      findings.AddRange(
        LangParityChecker.Run(domain, OwnFiles(primary, store, domain, "lang/"))
      );
      findings.AddRange(
        ShapeTextureChecker.Run(
          store,
          domain,
          OwnFiles(primary, store, domain, "blocktypes/"),
          isBlock: true
        )
      );
      findings.AddRange(
        ShapeTextureChecker.Run(
          store,
          domain,
          OwnFiles(primary, store, domain, "itemtypes/"),
          isBlock: false
        )
      );
    }
  }

  // Every top-level folder under a mod's own assets/ - normally just its modid, but a mod is free
  // to name its asset domain differently (or ship more than one, e.g. an overlay into "game").
  private static string[] AssetDomains(ModSource mod) {
    string assetsDir = Path.Combine(mod.RootDir, "assets");
    return Directory.Exists(assetsDir)
      ?
      [
        .. Directory
          .EnumerateDirectories(assetsDir)
          .Select(Path.GetFileName)
          .OfType<string>(),
      ]
      : [mod.ModId];
  }

  // The files the mod itself ships under a domain/prefix, read back through the (possibly
  // patched) store - never a merged domain's vanilla or --mods content, which sits in the same
  // store under the same domain when a mod overlays into e.g. "game", but is not this run's mod
  // to check.
  private static IEnumerable<(string Path, JToken Json)> OwnFiles(
    ModSource mod,
    AssetStore store,
    string domain,
    string prefix
  ) {
    foreach (string domainDir in OwnDomainDirs(mod, domain))
      foreach (
        string file in Directory.EnumerateFiles(
          domainDir,
          "*.json",
          SearchOption.AllDirectories
        )
      ) {
        string relPath = Path.GetRelativePath(domainDir, file)
          .Replace(Path.DirectorySeparatorChar, '/')
          .ToLowerInvariant();
        if (!relPath.StartsWith(prefix, StringComparison.Ordinal))
          continue;
        JToken? json = store.TryGet(domain, relPath);
        if (json != null)
          yield return (relPath, json);
      }
  }

  // The directories a domain's own files are read from: assets/<domain>, plus - for a mod that
  // ships an assembly - a sibling tests/goldens/<domain>, since a code-first mod's definitions can
  // live only there. Either can be absent alone: a code-first domain often carries shapes/lang/
  // config under assets/ but no blocktypes/itemtypes JSON at all.
  private static IEnumerable<string> OwnDomainDirs(ModSource mod, string domain) {
    string assetsDir = Path.Combine(mod.RootDir, "assets", domain);
    if (Directory.Exists(assetsDir))
      yield return assetsDir;
    if (mod.ShipsCode && GoldensDir(mod.RootDir) is { } goldens) {
      string goldensDir = Path.Combine(goldens, domain);
      if (Directory.Exists(goldensDir))
        yield return goldensDir;
    }
  }

  // A ShipsCode mod's own tests/goldens: the folder passed in may be the source project directly
  // (modinfo.json beside tests/) or a built copy several levels below it
  // (bin/<config>/Mods/mod, this repository's own convention) - every ancestor up to six levels is
  // tried, the first carrying a tests/goldens folder wins. Null when neither is a code-first mod's
  // own layout, e.g. an ordinary --mods dependency with no source tree beside it.
  private static string? GoldensDir(string rootDir) {
    DirectoryInfo? dir = new DirectoryInfo(rootDir);
    for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent) {
      string candidate = Path.Combine(dir.FullName, "tests", "goldens");
      if (Directory.Exists(candidate))
        return candidate;
    }
    return null;
  }

  private static void Report(
    List<Finding> findings,
    bool asJson,
    TextWriter stdout
  ) {
    if (asJson) {
      var payload = findings.Select(f => new {
        level = f.Level == FindingLevel.Error ? "error" : "info",
        check = f.Check,
        file = f.File,
        line = f.Line,
        message = f.Message,
      });
      stdout.WriteLine(
        JsonConvert.SerializeObject(payload, Formatting.Indented)
      );
      return;
    }

    foreach (Finding finding in findings)
      stdout.WriteLine(finding.ToString());

    int errors = findings.Count(f => f.Level == FindingLevel.Error);
    int infos = findings.Count(f => f.Level == FindingLevel.Info);
    stdout.WriteLine($"{errors} error(s), {infos} informational finding(s)");
  }
}
