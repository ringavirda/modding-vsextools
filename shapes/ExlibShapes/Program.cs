using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ExpandedLib.Assets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Vintagestory.API.Common;

namespace ExpandedLib.Shapes;

// exlib-shapes render|schematic|block|tree|measure FILE [options] - renders a shape file to
// textured views/animation frames, a multiblock/megablock blocktype file to a build schematic, one
// blocktype variant to the views a wiki page shows, or prints a shape's own element tree or its
// measured extents; see README.md for the full option list of each. Exit codes: 0 success, 1 a
// resolved run-time error (a bad shape, no such clip), 2 usage.

/// <summary>Malformed command-line usage - reported on stderr with exit code 2, not a stack
/// trace.</summary>
internal sealed class UsageException : Exception {
  public UsageException(string message) : base(message) { }
}

internal static class Program {
  private static int Main(string[] args) {
    if (args.Length == 0) {
      Console.Error.WriteLine(Usage);
      return 2;
    }
    string[] rest = args[1..];

    // The tool ships with VintagestoryAPI as a compile-only reference (Private=false - a global
    // tool must never bundle a copy of the game's own assembly), so it is resolved at run time
    // from whichever install --game/$VINTAGE_STORY actually names, the same way exlib-verify does.
    // Newtonsoft.Json, protobuf-net and SkiaSharp are Private=true and need no such handling.
    string? gamePath = null;
    try {
      gamePath = GameInstall.Resolve(OptOf(rest, "--game"));
    } catch (DirectoryNotFoundException) {
      // tree/measure never touch a game install; let them run without one and let render/schematic
      // fail with their own, more specific message once they actually need it.
    }
    ResolveEventHandler? resolver = null;
    if (gamePath != null) {
      string game = gamePath;
      resolver = (_, resolveArgs) => {
        string? name = new AssemblyName(resolveArgs.Name).Name;
        if (name == null)
          return null;
        string path = Path.Combine(game, name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
      };
      AppDomain.CurrentDomain.AssemblyResolve += resolver;
    }

    try {
      return args[0] switch {
        "render" => RunRender(rest),
        "schematic" => RunSchematic(rest),
        "block" => RunBlock(rest),
        "tree" => RunTree(rest),
        "measure" => RunMeasure(rest),
        _ => Unknown(args[0]),
      };
    } catch (UsageException e) {
      Console.Error.WriteLine(e.Message);
      return 2;
    } catch (Exception e) {
      Console.Error.WriteLine(e.Message);
      return 1;
    } finally {
      if (resolver != null)
        AppDomain.CurrentDomain.AssemblyResolve -= resolver;
    }
  }

  private const string Usage = "usage: exlib-shapes {render,schematic,block,tree,measure} FILE [options]";

  private static int Unknown(string command) {
    Console.Error.WriteLine($"exlib-shapes: no such command: {command}");
    Console.Error.WriteLine(Usage);
    return 2;
  }

  // --name VALUE or --name=VALUE, first occurrence; null when absent. The `=` form is accepted
  // alongside the space form (not just for its own sake: PowerShell's parameter binder treats a
  // bare `--out` token forwarded through `exmod render/schematic` as an ambiguous prefix of its
  // own `-OutVariable`/`-OutBuffer` common parameters and refuses it outright, so `exmod`'s own
  // help text for these two commands tells a caller to write `--out=DIR` instead).
  private static string? OptOf(string[] args, string name) {
    string prefix = name + "=";
    for (int i = 0; i < args.Length; i++) {
      if (args[i].StartsWith(prefix, StringComparison.Ordinal))
        return args[i][prefix.Length..];
      if (args[i] == name)
        return i + 1 < args.Length ? args[i + 1] : throw new UsageException($"{name} needs a value");
    }
    return null;
  }

  // --name VALUE or --name=VALUE, every occurrence, in order; a bare `--name` also swallows every
  // following token up to the next `--flag` or the end (the help text's `--roots PATH...`), so
  // `--roots a b --game x` names two roots rather than silently dropping `b`.
  private static List<string> OptAllOf(string[] args, string name) {
    string prefix = name + "=";
    var result = new List<string>();
    for (int i = 0; i < args.Length; i++) {
      if (args[i].StartsWith(prefix, StringComparison.Ordinal)) {
        result.Add(args[i][prefix.Length..]);
        continue;
      }
      if (args[i] != name)
        continue;
      int j = i + 1;
      if (j >= args.Length || args[j].StartsWith("--", StringComparison.Ordinal))
        throw new UsageException($"{name} needs a value");
      while (j < args.Length && !args[j].StartsWith("--", StringComparison.Ordinal))
        result.Add(args[j++]);
      i = j - 1;
    }
    return result;
  }

  private static bool FlagOf(string[] args, string name) => args.Contains(name);

  private static (string File, string[] Flags) FileAndFlags(string[] args, string usage) {
    if (args.Length == 0)
      throw new UsageException(usage);
    return (args[0], args[1..]);
  }

  private static void SavePng(SKBitmap bitmap, string path) {
    using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
    using FileStream file = File.OpenWrite(path);
    data.SaveTo(file);
  }

  // An integral frame prints without a decimal point; a fractional one prints with the
  // fewest digits that round-trip.
  private static string FormatFrame(double frame) =>
    frame == Math.Floor(frame)
      ? ((long)frame).ToString(CultureInfo.InvariantCulture)
      : frame.ToString("G6", CultureInfo.InvariantCulture);

  private static int RunRender(string[] args) {
    (string file, string[] flags) = FileAndFlags(
      args,
      "usage: exlib-shapes render FILE --out DIR [--views a,b] [--ppu N] [--anim CLIP --frames N] "
        + "[--only PATH...] [--highlight PATH...] [--no-grid] [--no-edges] [--game PATH] [--repo PATH]"
    );
    string outDir = OptOf(flags, "--out") ?? throw new UsageException("--out is required");
    string views = OptOf(flags, "--views") ?? "iso";
    int ppu = int.Parse(OptOf(flags, "--ppu") ?? "24", CultureInfo.InvariantCulture);
    string? anim = OptOf(flags, "--anim");
    string framesArg = OptOf(flags, "--frames") ?? "0";
    List<string> only = OptAllOf(flags, "--only");
    List<string> highlight = OptAllOf(flags, "--highlight");
    bool noGrid = FlagOf(flags, "--no-grid");
    bool noEdges = FlagOf(flags, "--no-edges");
    string? game = OptOf(flags, "--game");
    string? repo = OptOf(flags, "--repo");

    LoadedShape shape = ShapeFile.Load(file);
    TextureSet textures = TextureSet.ForShape(shape, TextureRoots.Build(game, repo, file));
    Directory.CreateDirectory(outDir);
    string stem = Path.GetFileNameWithoutExtension(file);

    string[] viewNames = views.Split(',');
    double[] frames = anim != null ? [.. framesArg.Split(',').Select(f => double.Parse(f, CultureInfo.InvariantCulture))] : [0.0];
    HashSet<string>? highlightSet = highlight.Count > 0 ? [.. highlight] : null;
    HashSet<string>? onlySet = only.Count > 0 ? [.. only] : null;

    var written = new List<string>();
    foreach (string viewName in viewNames) {
      View view = Renderer.NamedViews[viewName];
      foreach (double frame in frames) {
        Dictionary<string, Pose>? poses = anim != null ? Poses.PoseAt(shape, anim, frame) : null;
        using SKBitmap img = Renderer.Render(
          shape,
          view,
          ppu: ppu,
          poses: poses,
          highlight: highlightSet,
          only: onlySet,
          textures: textures,
          grid: !noGrid,
          edges: !noEdges
        );
        string suffix = anim != null ? $"-f{FormatFrame(frame)}" : "";
        string outPath = Path.Combine(outDir, $"{stem}-{viewName}{suffix}.png");
        SavePng(img, outPath);
        written.Add(outPath);
      }
    }

    foreach (string p in written)
      Console.WriteLine(p);
    Console.WriteLine("missing textures: [" + string.Join(", ", textures.Missing.OrderBy(m => m, StringComparer.Ordinal)) + "]");
    return 0;
  }

  private static int RunSchematic(string[] args) {
    (string file, string[] flags) = FileAndFlags(
      args,
      "usage: exlib-shapes schematic FILE --out DIR [--views plan,iso] [--angle N] [--layer N|all] "
        + "[--ppu N] [--roots PATH...] [--game PATH]"
    );
    string outDir = OptOf(flags, "--out") ?? throw new UsageException("--out is required");
    string views = OptOf(flags, "--views") ?? "plan,iso";
    int angle = int.Parse(OptOf(flags, "--angle") ?? "0", CultureInfo.InvariantCulture);
    string? layer = OptOf(flags, "--layer");
    int ppu = int.Parse(OptOf(flags, "--ppu") ?? "8", CultureInfo.InvariantCulture);
    List<string> extraRoots = OptAllOf(flags, "--roots");
    string? game = OptOf(flags, "--game");

    List<string> roots = [.. extraRoots, .. BlockIndex.DefaultRoots(file)];
    BlockIndex index = BlockIndex.Build(roots, game, BlockIndex.UnderLegacyTree(file));
    Variant? drawn = DrawnVariant(index, file, null);
    Layout layout = Footprint.Placed(Layout.Load(file, drawn?.Path), index);
    string? front = drawn == null ? null : Presentation.FrontOf(drawn);
    if (angle != 0) {
      layout = layout.Rotated(angle);
      front = front == null ? null : Layout.RotateSideWord(front, angle);
    }

    HashSet<string> viewSet = [.. views.Split(',')];
    Directory.CreateDirectory(outDir);
    string stem = Path.GetFileNameWithoutExtension(file);

    Dictionary<int, LegendEntry> legend = Schematic.LegendColors(layout);
    foreach ((int n, string selector) in layout.Numbers) {
      ResolvedBlock? block = index.Resolve(selector);
      legend[n].Representative = block?.Code;
      legend[n].Optional = index.Optional(selector);
    }

    var files = new List<string>();
    var plans = new List<(string File, int Layer)>();
    if (viewSet.Contains("plan"))
      foreach (int y in layout.Layers()) {
        string path = Path.Combine(outDir, $"{stem}-plan-y{y}.svg");
        File.WriteAllText(path, Schematic.PlanSvg(layout, y, legend));
        files.Add(path);
        plans.Add((path, y));
      }

    if (viewSet.Contains("iso")) {
      string path = Path.Combine(outDir, $"{stem}-iso.png");
      using (SKBitmap img = Schematic.IsoPng(layout, index, ppu))
        SavePng(img, path);
      files.Add(path);

      List<int> cutLayers = layer switch {
        "all" => [.. layout.Layers()],
        null => [],
        _ => [int.Parse(layer, CultureInfo.InvariantCulture)],
      };
      foreach (int y in cutLayers) {
        string cutPath = Path.Combine(outDir, $"{stem}-iso-y{y}.png");
        using (SKBitmap img = Schematic.IsoPng(layout, index, ppu, y))
          SavePng(img, cutPath);
        files.Add(cutPath);
      }
    }

    JObject manifest = Schematic.Manifest(
      layout,
      legend,
      files,
      index.Ambiguities,
      Schematic.MissingTextures(layout, index),
      index.ParseWarnings,
      plans,
      front
    );
    string manifestPath = Path.Combine(outDir, $"{stem}.json");
    File.WriteAllText(manifestPath, manifest.ToString(Formatting.Indented));

    foreach (string f in files)
      Console.WriteLine(f);
    Console.WriteLine(manifestPath);
    // A malformed source file is reported here too, not only in the manifest: a caller watching
    // the run should see it without opening the JSON.
    foreach (string line in index.ParseWarnings)
      Console.Error.WriteLine($"exlib-shapes: {line}");
    var warnings = (JArray)manifest["warnings"]!;
    if (warnings.Count > 0)
      Console.WriteLine("warnings: [" + string.Join(", ", warnings.Select(w => (string)w!)) + "]");
    return 0;
  }

  private static int RunBlock(string[] args) {
    (string file, string[] flags) = FileAndFlags(
      args,
      "usage: exlib-shapes block FILE --out DIR [--variant CODE] "
        + "[--views iso,north,east,south,west,up] [--ppu N] [--roots PATH...] [--game PATH]"
    );
    string outDir = OptOf(flags, "--out") ?? throw new UsageException("--out is required");
    string? wanted = OptOf(flags, "--variant");
    IReadOnlyList<string>? views = NamedViews(OptOf(flags, "--views"));
    int ppu = int.Parse(OptOf(flags, "--ppu") ?? "24", CultureInfo.InvariantCulture);
    List<string> extraRoots = OptAllOf(flags, "--roots");
    string? game = OptOf(flags, "--game");

    List<string> roots = [.. extraRoots, .. BlockIndex.DefaultRoots(file)];
    BlockIndex index = BlockIndex.Build(roots, game, BlockIndex.UnderLegacyTree(file));
    Variant variant =
      DrawnVariant(index, file, wanted) ?? throw new UsageException($"{file}: no blocktype in the index came from it");

    JObject manifest = BlockViews.Write(
      file,
      variant,
      index,
      outDir,
      views,
      ppu
    );

    foreach (JToken written in (JArray)manifest["files"]!)
      Console.WriteLine((string)written!);
    Console.WriteLine(Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + ".json"));
    Console.WriteLine("variant: " + (string)manifest["variant"]!);
    Console.WriteLine(
      "missing textures: [" + string.Join(", ", ((JArray)manifest["missingTextures"]!).Select(t => (string)t!)) + "]"
    );
    foreach (JToken warning in (JArray)manifest["warnings"]!)
      Console.Error.WriteLine($"exlib-shapes: {(string)warning!}");
    return 0;
  }

  // The --views list split and checked against the views Renderer names, so a misspelt one is a
  // usage error rather than a missing-key crash. Null for a null list, which draws the defaults.
  private static IReadOnlyList<string>? NamedViews(string? views) {
    if (views == null)
      return null;
    string[] names = views.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (string name in names)
      if (!Renderer.NamedViews.ContainsKey(name))
        throw new UsageException(
          $"no such view: {name} (one of {string.Join(", ", Renderer.NamedViews.Keys)})"
        );
    if (names.Length == 0)
      throw new UsageException($"--views names no view (one of {string.Join(", ", Renderer.NamedViews.Keys)})");
    return names;
  }

  // The variant a picture of FILE's family is drawn for: the one `wanted` names (a full code or a
  // bare path), else the variant facing Presentation.Facing, else its first. Null when the index
  // holds no variant from that file; throws when `wanted` names none of them.
  private static Variant? DrawnVariant(BlockIndex index, string file, string? wanted) {
    IReadOnlyList<Variant> variants = index.VariantsOf(file);
    if (wanted == null)
      return BlockIndex.Facing(variants, Presentation.Facing) ?? variants.FirstOrDefault();
    foreach (Variant v in variants)
      if (v.Code == wanted || v.Path == wanted)
        return v;
    throw new UsageException(
      $"no such variant: {wanted} ({(variants.Count == 0 ? "the file expanded to none" : string.Join(", ", variants.Select(v => v.Path)))})"
    );
  }

  private static int RunTree(string[] args) {
    (string file, string[] flags) = FileAndFlags(args, "usage: exlib-shapes tree FILE [--group PREFIX]");
    string? group = OptOf(flags, "--group");
    LoadedShape shape = ShapeFile.Load(file);

    Console.WriteLine("textures: " + string.Join(", ", shape.Textures.Select(kv => $"{kv.Key}={kv.Value}")));

    void Walk(Node el) {
      if (group == null || el.Path.StartsWith(group, StringComparison.Ordinal)) {
        int depth = el.Path.Count(c => c == '/');
        string rot = el.IsRotated
          ? $" rot({G(el.Rotation.X)},{G(el.Rotation.Y)},{G(el.Rotation.Z)}) org({G(el.Origin.X)},{G(el.Origin.Y)},{G(el.Origin.Z)})"
          : "";
        string tex = string.Join(
          ",",
          el.Faces.Values.Select(f => f.Texture ?? "").Distinct().OrderBy(t => t, StringComparer.Ordinal)
        );
        Console.WriteLine(
          $"{new string(' ', depth * 2)}{el.Name} ({G(el.From.X)},{G(el.From.Y)},{G(el.From.Z)})"
            + $"->({G(el.To.X)},{G(el.To.Y)},{G(el.To.Z)}){rot} {tex}"
        );
      }
      foreach (Node c in el.Children)
        Walk(c);
    }
    foreach (Node root in shape.Elements)
      Walk(root);

    foreach (Animation a in shape.Animations)
      Console.WriteLine(
        $"anim {a.Code ?? a.Name}: {a.QuantityFrames}f {a.OnAnimationEnd} keyframes={a.KeyFrames?.Length ?? 0}"
      );
    return 0;
  }

  private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

  // One leaf element's world-space axis-aligned bounds, path to (lo, hi).
  private static Dictionary<string, (Vector3 Lo, Vector3 Hi)> ElementBoxes(LoadedShape shape) {
    Dictionary<string, System.Numerics.Matrix4x4> mats = Geometry.WorldMatrices(shape);
    var result = new Dictionary<string, (Vector3, Vector3)>();
    foreach (Node leaf in shape.Leaves()) {
      Vector3[] corners = Geometry.Corners(mats[leaf.Path], (Vector3)leaf.Size);
      result[leaf.Path] = Geometry.Aabb(corners);
    }
    return result;
  }

  // The shared volume of two axis-aligned boxes, 0 when they do not overlap on some axis.
  private static double OverlapVolume((Vector3 Lo, Vector3 Hi) a, (Vector3 Lo, Vector3 Hi) b) {
    double ox = Math.Max(0, Math.Min(a.Hi.X, b.Hi.X) - Math.Max(a.Lo.X, b.Lo.X));
    double oy = Math.Max(0, Math.Min(a.Hi.Y, b.Hi.Y) - Math.Max(a.Lo.Y, b.Lo.Y));
    double oz = Math.Max(0, Math.Min(a.Hi.Z, b.Hi.Z) - Math.Max(a.Lo.Z, b.Lo.Z));
    return ox * oy * oz;
  }

  private static int RunMeasure(string[] args) {
    (string file, string[] flags) = FileAndFlags(
      args,
      "usage: exlib-shapes measure FILE [--group PREFIX] [--cells x,y,z;x,y,z]"
    );
    string? group = OptOf(flags, "--group");
    string? cellsArg = OptOf(flags, "--cells");

    LoadedShape shape = ShapeFile.Load(file);
    Dictionary<string, (Vector3 Lo, Vector3 Hi)> boxes = ElementBoxes(shape);
    if (group != null)
      boxes = boxes.Where(kv => kv.Key.StartsWith(group, StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value);
    if (boxes.Count == 0) {
      Console.WriteLine("no elements");
      return 1;
    }

    Vector3 lo = boxes.Values.Select(b => b.Lo).Aggregate((a, b) => Vector3.Min(a, b));
    Vector3 hi = boxes.Values.Select(b => b.Hi).Aggregate((a, b) => Vector3.Max(a, b));
    Console.WriteLine($"leaf cubes: {boxes.Count}");
    Console.WriteLine(
      $"extents x {F2(lo.X)}..{F2(hi.X)}  y {F2(lo.Y)}..{F2(hi.Y)}  z {F2(lo.Z)}..{F2(hi.Z)}  "
        + $"cells {F2((hi.X - lo.X) / 16)} x {F2((hi.Y - lo.Y) / 16)} x {F2((hi.Z - lo.Z) / 16)}"
    );

    var groups = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (string p in boxes.Keys) {
      string g = p.Split('/')[0];
      groups[g] = groups.GetValueOrDefault(g) + 1;
    }
    Console.WriteLine("per top-level group: " + string.Join(", ", groups.Select(kv => $"{kv.Key}={kv.Value}")));

    if (cellsArg != null)
      foreach (string cellStr in cellsArg.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
        int[] c = [.. cellStr.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture))];
        Vector3 clo = new Vector3(c[0], c[1], c[2]) * 16;
        Vector3 chi = clo + new Vector3(16);
        double vol = boxes.Values.Sum(b => OverlapVolume(b, (clo, chi)));
        Console.WriteLine($"cell ({c[0]},{c[1]},{c[2]}): {F1(100 * vol / 4096)}% of volume covered by element boxes");
      }
    return 0;
  }

  private static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
  private static string F1(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
}
