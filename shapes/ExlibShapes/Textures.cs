using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ExpandedLib.Assets;
using SkiaSharp;

namespace ExpandedLib.Shapes;

/// <summary>
/// The game install and mod repository a shape's texture references resolve against.
/// </summary>
public sealed class TextureRoots {
  private readonly Lazy<string> _gamePath;

  /// <summary>The game install (holding <c>assets/survival/textures</c>) - resolved on first
  /// access, not when these roots are built: a shape whose textures are all absolute or WSL
  /// paths never touches this and so needs no install at all, matching <c>textures.py</c>, which
  /// only opens <c>.game/</c> when a <c>game:</c> or bare reference actually asks for it.</summary>
  /// <exception cref="System.IO.DirectoryNotFoundException">No install could be found, and a
  /// <c>game:</c> or bare-path texture reference asked to resolve against one.</exception>
  public string GamePath => _gamePath.Value;

  /// <summary>The mod repository (holding <c>mods/*</c> and <c>legacy/*</c>), or null when none
  /// could be found - a <c>domain:</c> reference other than <c>game:</c> then never resolves.</summary>
  public string? RepoPath { get; }

  private TextureRoots(Lazy<string> gamePath, string? repoPath) {
    _gamePath = gamePath;
    RepoPath = repoPath;
  }

  /// <summary>Builds the roots explicitly - the pair a test or a caller that already knows both
  /// paths passes, bypassing <see cref="GameInstall.Resolve"/> and the ancestor search.</summary>
  public static TextureRoots From(string gamePath, string? repoPath) =>
    new(new Lazy<string>(gamePath), repoPath);

  /// <summary>
  /// Resolves <paramref name="repo"/> to the nearest ancestor of <paramref name="shapePath"/>
  /// holding <c>workbench/</c>, <c>mods/</c> or <c>.game/</c> (the current directory when none of
  /// those exist, or when <paramref name="shapePath"/> is null); <paramref name="game"/> is
  /// resolved through <see cref="GameInstall.Resolve"/> lazily, the first time a texture
  /// reference actually needs <see cref="GamePath"/>.
  /// </summary>
  public static TextureRoots Build(string? game, string? repo, string? shapePath) {
    string repoPath = repo ?? FindRepoRoot(shapePath);
    return new TextureRoots(new Lazy<string>(() => GameInstall.Resolve(game)), repoPath);
  }

  private static string FindRepoRoot(string? shapePath) {
    string start =
      shapePath != null
        ? Path.GetDirectoryName(Path.GetFullPath(shapePath)) ?? Directory.GetCurrentDirectory()
        : Directory.GetCurrentDirectory();
    for (DirectoryInfo? dir = new(start); dir != null; dir = dir.Parent)
      if (
        Directory.Exists(Path.Combine(dir.FullName, "workbench"))
        || Directory.Exists(Path.Combine(dir.FullName, "mods"))
        || Directory.Exists(Path.Combine(dir.FullName, ".game"))
      )
        return dir.FullName;
    return Directory.GetCurrentDirectory();
  }
}

/// <summary>
/// Texture path resolution for the spellings a shape can carry: a WSL UNC path, a Windows drive
/// path (never resolvable here), an absolute POSIX path, a <c>domain:path</c> reference, or a bare
/// vanilla-relative path.
/// </summary>
public static class Textures {
  private static readonly Regex WslPath = new(@"^//wsl\.localhost/[^/]+(/.*)$");
  private static readonly Regex WindowsDrive = new(@"^[A-Za-z]:/");
  private static readonly Regex DomainPath = new(@"^([a-z0-9_]+):(.+)$");

  /// <summary>
  /// Resolves <paramref name="value"/> (one of a shape's <c>textures</c> values) to an actual PNG
  /// file, or null when it cannot be found:
  /// <list type="bullet">
  /// <item><c>//wsl.localhost/&lt;distro&gt;/&lt;rest&gt;</c> (backslashes normalised first) maps
  /// to <c>/&lt;rest&gt;</c>.</item>
  /// <item>a Windows drive path (<c>C:/...</c>) is never resolvable from this (Linux) tool.</item>
  /// <item>an absolute POSIX path is used as is.</item>
  /// <item><c>domain:path</c>: <c>game:</c> resolves under
  /// <paramref name="roots"/>.<see cref="TextureRoots.GamePath"/>'s <c>assets/survival/textures</c>;
  /// any other domain under the first of <paramref name="roots"/>.<see cref="TextureRoots.RepoPath"/>'s
  /// <c>mods/*/assets/&lt;domain&gt;/textures</c> then <c>legacy/*/assets/&lt;domain&gt;/textures</c>
  /// to carry that path.</item>
  /// <item>a bare path resolves under the game's own vanilla textures.</item>
  /// </list>
  /// A path missing its <c>.png</c> extension has one appended before the existence check, the
  /// same convenience Model Creator's own <c>texturePath</c> field allows.
  /// </summary>
  public static string? Resolve(string value, string? shapePath, TextureRoots roots) {
    string v = value.Replace('\\', '/');

    Match wsl = WslPath.Match(v);
    if (wsl.Success)
      return Png(wsl.Groups[1].Value);

    if (WindowsDrive.IsMatch(v))
      return null;

    if (v.StartsWith('/'))
      return Png(v);

    Match domain = DomainPath.Match(v);
    if (domain.Success) {
      string name = domain.Groups[1].Value;
      string rel = domain.Groups[2].Value;
      if (name == "game")
        return Png(Path.Combine(roots.GamePath, "assets", "survival", "textures", rel));
      if (roots.RepoPath == null)
        return null;
      foreach (string tree in new[] { "mods", "legacy" })
        foreach (string texturesDir in DomainTextureDirs(roots.RepoPath, tree, name)) {
          string? hit = Png(Path.Combine(texturesDir, rel));
          if (hit != null)
            return hit;
        }
      return null;
    }

    return Png(Path.Combine(roots.GamePath, "assets", "survival", "textures", v));
  }

  // <repo>/<treeRoot>/*/assets/<domain>/textures, in directory-name order - the same tie-break
  // "last loaded wins" would use if two mods shipped the same domain, made deterministic here
  // since only the first hit is taken.
  private static IEnumerable<string> DomainTextureDirs(string repo, string treeRoot, string domain) {
    string parent = Path.Combine(repo, treeRoot);
    if (!Directory.Exists(parent))
      yield break;
    foreach (
      string modDir in Directory.EnumerateDirectories(parent).OrderBy(d => d, StringComparer.Ordinal)
    ) {
      string textures = Path.Combine(modDir, "assets", domain, "textures");
      if (Directory.Exists(textures))
        yield return textures;
    }
  }

  private static string? Png(string path) {
    if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
      return File.Exists(path) ? path : null;
    string withExt = path + ".png";
    return File.Exists(withExt) ? withExt : null;
  }
}

/// <summary>
/// Every texture a shape needs, decoded to RGBA - key to a <c>[height, width, 4]</c> byte array,
/// unresolved keys mapped to a 16x16 magenta placeholder and listed in <see cref="Missing"/>.
/// </summary>
public sealed class TextureSet {
  /// <summary>16x16 solid magenta - stands in for any texture key this set could not resolve.</summary>
  public static readonly byte[,,] MissingImage = BuildMissing();

  private static byte[,,] BuildMissing() {
    var img = new byte[16, 16, 4];
    for (int y = 0; y < 16; y++)
      for (int x = 0; x < 16; x++) {
        img[y, x, 0] = 255;
        img[y, x, 1] = 0;
        img[y, x, 2] = 255;
        img[y, x, 3] = 255;
      }
    return img;
  }

  private readonly Dictionary<string, byte[,,]> _arrays;

  /// <summary>Texture keys that could not be resolved to a real file.</summary>
  public IReadOnlySet<string> Missing { get; }

  private TextureSet(Dictionary<string, byte[,,]> arrays, HashSet<string> missing) {
    _arrays = arrays;
    Missing = missing;
  }

  /// <summary>Resolves and decodes every texture <paramref name="shape"/>'s <c>textures</c> map
  /// names.</summary>
  public static TextureSet ForShape(LoadedShape shape, TextureRoots roots) {
    var arrays = new Dictionary<string, byte[,,]>();
    var missing = new HashSet<string>();
    foreach ((string key, string value) in shape.Textures) {
      string? path = Textures.Resolve(value, shape.Path, roots);
      if (path == null) {
        missing.Add(key);
        arrays[key] = MissingImage;
      } else {
        arrays[key] = Decode(path);
      }
    }
    return new TextureSet(arrays, missing);
  }

  /// <summary>
  /// Builds a set from already-resolved values rather than a shape's own <c>textures</c> map: a
  /// schematic's composite shape resolves its texture values through a <see cref="BlockIndex"/>'s
  /// own domain roots, not a shape file's ancestry, so it calls this directly with
  /// <see cref="BlockIndex.ResolveTexture"/> as <paramref name="resolvePath"/>.
  /// </summary>
  /// <param name="values">Texture key to the raw value string (bare or <c>domain:path</c>) it
  /// names.</param>
  /// <param name="resolvePath">Resolves one raw value string to a PNG path, or null when it cannot
  /// be found.</param>
  /// <param name="extra">Additional key to already-decoded array entries merged in as is (a
  /// schematic's synthetic filler texture, which names no real file at all).</param>
  public static TextureSet FromResolved(
    IReadOnlyDictionary<string, string> values,
    Func<string, string?> resolvePath,
    IReadOnlyDictionary<string, byte[,,]>? extra = null
  ) {
    var arrays = new Dictionary<string, byte[,,]>();
    var missing = new HashSet<string>();
    foreach ((string key, string value) in values) {
      string? path = resolvePath(value);
      if (path == null) {
        missing.Add(key);
        arrays[key] = MissingImage;
      } else {
        arrays[key] = Decode(path);
      }
    }
    if (extra != null)
      foreach ((string key, byte[,,] array) in extra)
        arrays[key] = array;
    return new TextureSet(arrays, missing);
  }

  /// <summary>The decoded <c>[height, width, 4]</c> RGBA array for <paramref name="key"/>, or
  /// <see cref="MissingImage"/> when the key is not in this set at all.</summary>
  public byte[,,] Get(string key) => _arrays.TryGetValue(key, out byte[,,]? v) ? v : MissingImage;

  private static byte[,,] Decode(string path) {
    using SKCodec codec = SKCodec.Create(path) ?? throw new IOException($"{path}: not a decodable image");
    // Decoded straight into unpremultiplied RGBA, matching PIL's convert("RGBA"): SKBitmap.Decode
    // defaults to premultiplied alpha, which darkens every partially transparent texel.
    var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
    using SKBitmap rgba = new(info);
    SKCodecResult result = codec.GetPixels(info, rgba.GetPixels());
    if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
      throw new IOException($"{path}: cannot decode to RGBA ({result})");
    int w = rgba.Width;
    int h = rgba.Height;
    ReadOnlySpan<byte> pixels = rgba.GetPixelSpan();
    var arr = new byte[h, w, 4];
    for (int y = 0; y < h; y++)
      for (int x = 0; x < w; x++) {
        int i = (y * w + x) * 4;
        arr[y, x, 0] = pixels[i];
        arr[y, x, 1] = pixels[i + 1];
        arr[y, x, 2] = pixels[i + 2];
        arr[y, x, 3] = pixels[i + 3];
      }
    return arr;
  }
}
