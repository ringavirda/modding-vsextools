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
  /// <summary>The game install (holding <c>assets/survival/textures</c>).</summary>
  public string GamePath { get; }

  /// <summary>The mod repository (holding <c>mods/*</c> and <c>legacy/*</c>), or null when none
  /// could be found - a <c>domain:</c> reference other than <c>game:</c> then never resolves.</summary>
  public string? RepoPath { get; }

  private TextureRoots(string gamePath, string? repoPath) {
    GamePath = gamePath;
    RepoPath = repoPath;
  }

  /// <summary>Builds the roots explicitly - the pair a test or a caller that already knows both
  /// paths passes, bypassing <see cref="GameInstall.Resolve"/> and the ancestor search.</summary>
  public static TextureRoots From(string gamePath, string? repoPath) => new(gamePath, repoPath);

  /// <summary>
  /// Resolves <paramref name="game"/> through <see cref="GameInstall.Resolve"/> and
  /// <paramref name="repo"/> to the nearest ancestor of <paramref name="shapePath"/> holding
  /// <c>workbench/</c>, <c>mods/</c> or <c>.game/</c> (the current directory when none of those
  /// exist, or when <paramref name="shapePath"/> is null).
  /// </summary>
  public static TextureRoots Build(string? game, string? repo, string? shapePath) {
    string gamePath = GameInstall.Resolve(game);
    string repoPath = repo ?? FindRepoRoot(shapePath);
    return new TextureRoots(gamePath, repoPath);
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
    foreach ((string key, Vintagestory.API.Common.AssetLocation value) in shape.Textures) {
      // AssetLocation.ToString() always prints a domain, defaulting an undomained value to
      // "game:" - the shape's own textures map never had that prefix, so a bare or WSL path is
      // reconstructed from Path alone rather than losing its shape to a fabricated domain.
      string raw = value.HasDomain() ? $"{value.Domain}:{value.Path}" : value.Path;
      string? path = Textures.Resolve(raw, shape.Path, roots);
      if (path == null) {
        missing.Add(key);
        arrays[key] = MissingImage;
      } else {
        arrays[key] = Decode(path);
      }
    }
    return new TextureSet(arrays, missing);
  }

  /// <summary>The decoded <c>[height, width, 4]</c> RGBA array for <paramref name="key"/>, or
  /// <see cref="MissingImage"/> when the key is not in this set at all.</summary>
  public byte[,,] Get(string key) => _arrays.TryGetValue(key, out byte[,,]? v) ? v : MissingImage;

  private static byte[,,] Decode(string path) {
    using SKBitmap raw =
      SKBitmap.Decode(path) ?? throw new IOException($"{path}: not a decodable image");
    using SKBitmap rgba =
      raw.Copy(SKColorType.Rgba8888) ?? throw new IOException($"{path}: cannot convert to RGBA");
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
