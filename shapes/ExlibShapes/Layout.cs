using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Util;

namespace ExpandedLib.Shapes;

/// <summary>Raised when a blocktype file carries neither a <c>multiblockStructure</c> nor any
/// <c>fillerOffsets</c> - there is nothing a <see cref="Layout"/> could represent.</summary>
public sealed class LayoutError : Exception {
  /// <summary>Wraps <paramref name="message"/> naming the file and which of the two the file
  /// carries neither of.</summary>
  public LayoutError(string message) : base(message) { }
}

/// <summary>One structure cell: a grid offset and the number its selector resolves through
/// <see cref="Layout.Numbers"/>.</summary>
public sealed record Cell(int X, int Y, int Z, int Number);

/// <summary>An (x, y, z) grid offset, used for filler cells, connector faces and role cells - every
/// place the JSON carries a bare offset with no number attached.</summary>
public readonly record struct Offset(int X, int Y, int Z);

/// <summary>
/// A blocktype JSON's multiblock footprint: the structure table
/// (<c>attributes.multiblockStructure</c>, the game's own <see cref="Vintagestory.API.Common.MultiblockStructure"/>
/// fields), oriented selectors, filler cells, connector faces and named roles - and, for a
/// filler-only megablock with no structure table at all (<c>ExpandedLib.Structures.IFillerHost</c>,
/// read through <c>attributes.fillerOffsets</c> or a per-variant <c>attributesByType</c> entry), just
/// the fillers around a single principal at the anchor. <see cref="Rotated"/> turns all of it by a
/// structure angle, matching the game's own <c>ExOrientation</c>
/// (<c>exlib/src/ExpandedLib/Helpers/ExOrientation.cs</c>) exactly: north 0, west 90, south 180,
/// east 270, and the same segment-swap <c>MultiblockFacings.Rotate</c> performs on an oriented code's
/// dash-segments.
/// </summary>
public sealed class Layout {
  /// <summary>Every structure cell, in file order. Empty for a filler-only megablock.</summary>
  public IReadOnlyList<Cell> Cells { get; }

  /// <summary>Cell number to the selector it resolves (the structure's <c>blockNumbers</c>, inverted).
  /// Empty for a filler-only megablock.</summary>
  public IReadOnlyDictionary<int, string> Numbers { get; }

  /// <summary>Cells drawn as an invisible <c>BlockStructureFiller</c>/<c>IFillerHost</c> footprint
  /// box rather than a numbered cell.</summary>
  public IReadOnlyList<Offset> Fillers { get; }

  /// <summary>Selector to the dash-segment indices of its code that name a facing
  /// (<c>attributes.multiblockFacings</c>), re-keyed by each selector's own rotated form so a second
  /// <see cref="Rotated"/> call still finds the segments to keep turning.</summary>
  public IReadOnlyDictionary<string, IReadOnlyList<int>> Facings { get; }

  /// <summary>Connector side (a word, a letter, or a non-directional key) to the offsets that face
  /// that way (<c>attributes.multiblockConnectors</c>).</summary>
  public IReadOnlyDictionary<string, IReadOnlyList<Offset>> Connectors { get; }

  /// <summary>Named role to the offsets carrying it (<c>attributes.multiblockRoles</c>).</summary>
  public IReadOnlyDictionary<string, IReadOnlyList<Offset>> Roles { get; }

  /// <summary>The principal's own cell - the origin every offset in this class is relative to.
  /// Always <c>(0, 0, 0)</c>: neither the structure table nor a megablock's footprint ever author a
  /// different one.</summary>
  public Offset Anchor { get; }

  /// <summary>
  /// The selector of the block the file itself declares, drawn at <see cref="Anchor"/> - the
  /// megablock's own body, which no <see cref="Cells"/> entry names. Widened to a <c>*</c> wildcard
  /// when <see cref="Load"/> was given no variant for a file that has <c>variantgroups</c>, and null
  /// when the path names no domain or the file no code.
  /// </summary>
  public string? Principal { get; }

  internal Layout(
    IReadOnlyList<Cell> cells,
    IReadOnlyDictionary<int, string> numbers,
    IReadOnlyList<Offset> fillers,
    IReadOnlyDictionary<string, IReadOnlyList<int>> facings,
    IReadOnlyDictionary<string, IReadOnlyList<Offset>> connectors,
    IReadOnlyDictionary<string, IReadOnlyList<Offset>> roles,
    Offset anchor = default,
    string? principal = null
  ) {
    Cells = cells;
    Numbers = numbers;
    Fillers = fillers;
    Facings = facings;
    Connectors = connectors;
    Roles = roles;
    Anchor = anchor;
    Principal = principal;
  }

  /// <summary>
  /// Reads <paramref name="path"/>'s <c>attributes.multiblockStructure</c> (blockNumbers, offsets)
  /// plus the exlib sibling attributes (<c>multiblockFacings</c>, <c>multiblockConnectors</c>,
  /// <c>multiblockRoles</c>, <c>fillerOffsets</c>); or, when there is no structure table at all, the
  /// filler-only footprint of an <see cref="ExpandedLib.Structures.IFillerHost"/> megablock (the
  /// family's flywheel among them): <c>attributes.fillerOffsets</c> directly, or - when the file
  /// instead varies its footprint per variant - the <c>attributesByType</c> entry whose wildcard key
  /// (the game's own <c>WildcardUtil.Match</c>) matches <paramref name="variant"/> (the block's own
  /// path, domain and <c>#</c>-segments stripped), defaulting to the first declared entry when
  /// <paramref name="variant"/> is null.
  /// </summary>
  /// <exception cref="LayoutError">The file has neither a structure table nor any fillers.</exception>
  public static Layout Load(string path, string? variant = null) {
    JObject raw = (JObject)JToken.Parse(File.ReadAllText(path));
    JObject? attrs = raw["attributes"] as JObject;
    JObject? structure = attrs?["multiblockStructure"] as JObject;
    List<Offset> fillers = ReadFillerOffsets(attrs, raw, variant);

    if (structure == null) {
      if (fillers.Count == 0)
        throw new LayoutError($"{path}: no attributes.multiblockStructure and no fillerOffsets");
      return new Layout(
        [],
        new Dictionary<int, string>(),
        fillers,
        new Dictionary<string, IReadOnlyList<int>>(),
        new Dictionary<string, IReadOnlyList<Offset>>(),
        new Dictionary<string, IReadOnlyList<Offset>>(),
        default,
        PrincipalOf(path, raw, variant)
      );
    }

    var numbers = new Dictionary<int, string>();
    if (structure["blockNumbers"] is JObject blockNumbers)
      foreach (JProperty prop in blockNumbers.Properties())
        numbers[(int)prop.Value!] = prop.Name;

    var cells = new List<Cell>();
    if (structure["offsets"] is JArray offsets)
      foreach (JToken o in offsets)
        cells.Add(new Cell((int)o["x"]!, (int)o["y"]!, (int)o["z"]!, (int)o["w"]!));

    var facings = new Dictionary<string, IReadOnlyList<int>>();
    if (attrs?["multiblockFacings"] is JObject facingsJson)
      foreach (JProperty prop in facingsJson.Properties())
        facings[prop.Name] =
          prop.Value is JArray segArray
            ? [.. segArray.Select(t => (int)t)]
            : [(int)prop.Value!];

    var connectors = new Dictionary<string, IReadOnlyList<Offset>>();
    if (attrs?["multiblockConnectors"] is JObject connectorsJson)
      foreach (JProperty prop in connectorsJson.Properties())
        connectors[prop.Name] = ReadOffsets((JArray)prop.Value!);

    var roles = new Dictionary<string, IReadOnlyList<Offset>>();
    if (attrs?["multiblockRoles"] is JObject rolesJson)
      foreach (JProperty prop in rolesJson.Properties())
        roles[prop.Name] = ReadOffsets((JArray)prop.Value!);

    return new Layout(cells, numbers, fillers, facings, connectors, roles, default, PrincipalOf(path, raw, variant));
  }

  // The file's own top-level attributesByType entry (a sibling of "attributes", the same
  // shape/shapeByType and textures/texturesByType keep) whose wildcard key the game's
  // WildcardUtil.Match accepts for `variant`, or the first declared entry that carries
  // fillerOffsets when `variant` is null - IFillerHost's own footprint-by-variant convention (the
  // flywheel's normal/large sizes); else attributes.fillerOffsets. Checked in that order, matching
  // the game's own <key>ByType convention, where a matching ByType entry REPLACES the plain key
  // for that variant rather than being a fallback for it.
  private static List<Offset> ReadFillerOffsets(JObject? attrs, JObject raw, string? variant) {
    if (raw["attributesByType"] is JObject byType) {
      JProperty? chosen = null;
      foreach (JProperty prop in byType.Properties()) {
        if ((prop.Value as JObject)?["fillerOffsets"] is not JArray)
          continue;
        chosen ??= prop;
        if (variant != null && WildcardUtil.Match(prop.Name, variant)) {
          chosen = prop;
          break;
        }
      }
      if (chosen != null)
        return ReadOffsets((JArray)((JObject)chosen.Value)["fillerOffsets"]!);
    }

    if (attrs?["fillerOffsets"] is JArray direct)
      return ReadOffsets(direct);

    return [];
  }

  // The megablock's own selector: its file's domain (the folder above `blocktypes`) and either the
  // variant the caller named or, with none, the file's `code` - widened with a `*` when the file
  // expands to variants at all, since no one of them is the block. Null when the path names no
  // domain or the file no code.
  private static string? PrincipalOf(string path, JObject raw, string? variant) {
    string? domain = DomainOf(path);
    string? code = (string?)raw["code"];
    if (domain == null || code == null)
      return null;
    if (variant != null)
      return $"{domain}:{variant}";
    return raw["variantgroups"] is JArray ? $"{domain}:{code}*" : $"{domain}:{code}";
  }

  // <...>/<domain>/blocktypes/**/<file>.json - the asset domain every code in that file carries.
  private static string? DomainOf(string path) {
    string[] parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar);
    for (int i = parts.Length - 1; i > 0; i--)
      if (parts[i] == "blocktypes")
        return parts[i - 1];
    return null;
  }

  private static List<Offset> ReadOffsets(JArray array) =>
    [.. array.Select(o => new Offset((int)o["x"]!, (int)o["y"]!, (int)o["z"]!))];

  /// <summary>
  /// This layout turned by <paramref name="angle"/> (0, 90, 180 or 270): every cell, filler,
  /// connector and role offset rotated by <see cref="RotateOffset"/>, a connector's own side key
  /// rotated by <see cref="RotateSideWord"/>, and each numbered selector that
  /// <see cref="Facings"/> marks as oriented rewritten through its facing segments
  /// (<c>MultiblockFacings.RotateSegments</c>) - re-keyed into the returned layout's own
  /// <see cref="Facings"/> so a further rotation still finds them.
  /// <para>
  /// <see cref="Principal"/> passes through untouched: it names the one variant a drawing uses, and
  /// the turn places that variant's footprint around it rather than choosing another variant.
  /// </para>
  /// </summary>
  public Layout Rotated(int angle) {
    List<Cell> cells = [.. Cells.Select(c => {
      Offset o = RotateOffset(new Offset(c.X, c.Y, c.Z), angle);
      return new Cell(o.X, o.Y, o.Z, c.Number);
    })];
    List<Offset> fillers = [.. Fillers.Select(o => RotateOffset(o, angle))];
    var connectors = new Dictionary<string, IReadOnlyList<Offset>>();
    foreach ((string side, IReadOnlyList<Offset> offsets) in Connectors)
      connectors[RotateSideWord(side, angle)] = [.. offsets.Select(o => RotateOffset(o, angle))];
    var roles = new Dictionary<string, IReadOnlyList<Offset>>();
    foreach ((string name, IReadOnlyList<Offset> offsets) in Roles)
      roles[name] = [.. offsets.Select(o => RotateOffset(o, angle))];

    var numbers = new Dictionary<int, string>(Numbers);
    var facings = new Dictionary<string, IReadOnlyList<int>>();
    foreach ((int number, string selector) in Numbers) {
      if (!Facings.TryGetValue(selector, out IReadOnlyList<int>? segments))
        continue;
      string? rotated = RotateSegments(selector, segments, angle);
      string newSelector = rotated ?? selector;
      numbers[number] = newSelector;
      facings[newSelector] = segments;
    }

    return new Layout(cells, numbers, fillers, facings, connectors, roles, Anchor, Principal);
  }

  /// <summary>(lo, hi), inclusive, over every cell and filler offset - a filler-only megablock has
  /// no <see cref="Cells"/> at all, so <see cref="Fillers"/> alone still bounds it.</summary>
  public (Offset Lo, Offset Hi) Bounds() {
    List<int> xs = [.. Cells.Select(c => c.X), .. Fillers.Select(f => f.X)];
    List<int> ys = [.. Cells.Select(c => c.Y), .. Fillers.Select(f => f.Y)];
    List<int> zs = [.. Cells.Select(c => c.Z), .. Fillers.Select(f => f.Z)];
    return (new Offset(xs.Min(), ys.Min(), zs.Min()), new Offset(xs.Max(), ys.Max(), zs.Max()));
  }

  /// <summary>Every distinct Y layer among <see cref="Cells"/> and <see cref="Fillers"/>, ascending
  /// - a filler-only megablock has no <see cref="Cells"/> at all, so its layers come from
  /// <see cref="Fillers"/> alone.</summary>
  public IReadOnlyList<int> Layers() => [
    .. Cells.Select(c => c.Y).Concat(Fillers.Select(f => f.Y)).Distinct().OrderBy(y => y),
  ];

  // ExOrientation.RotateOffset: (x, z) turns 90:(z,-x) 180:(-x,-z) 270:(-z,x); y is untouched.
  internal static Offset RotateOffset(Offset offset, int angle) {
    int a = ((angle % 360) + 360) % 360;
    (int x, int z) = (offset.X, offset.Z);
    (int dx, int dz) = a switch {
      90 => (z, -x),
      180 => (-x, -z),
      270 => (-z, x),
      _ => (x, z),
    };
    return new Offset(dx, offset.Y, dz);
  }

  private static readonly Dictionary<string, int> AngleFromSide = new(StringComparer.Ordinal) {
    ["north"] = 0, ["n"] = 0,
    ["west"] = 90, ["w"] = 90,
    ["south"] = 180, ["s"] = 180,
    ["east"] = 270, ["e"] = 270,
  };
  private static readonly Dictionary<int, (string Word, string Letter)> SideFromAngle = new() {
    [0] = ("north", "n"),
    [90] = ("west", "w"),
    [180] = ("south", "s"),
    [270] = ("east", "e"),
  };

  private static bool IsHorizontalSideWord(string token) => AngleFromSide.ContainsKey(token);

  private static string SideFromAngleValue(int angle, bool asLetter) {
    (string word, string letter) = SideFromAngle[((angle % 360) + 360) % 360];
    return asLetter ? letter : word;
  }

  // ExOrientation.RotateSideWord: a word stays a word, a letter stays a letter; vertical and
  // unrecognised tokens (a role name, a connector's own key when it is not a side) pass through.
  internal static string RotateSideWord(string side, int angle) {
    if (!IsHorizontalSideWord(side))
      return side;
    bool asLetter = side.Length == 1;
    return SideFromAngleValue(AngleFromSide[side] + angle, asLetter);
  }

  private static readonly HashSet<string> AxisPairs = ["ns", "sn", "we", "ew", "ud", "du"];
  private static readonly Dictionary<char, char> AxisOf = new() {
    ['n'] = 'n', ['s'] = 'n',
    ['w'] = 'w', ['e'] = 'w',
    ['u'] = 'u', ['d'] = 'u',
  };
  private static readonly Dictionary<char, string> CanonicalPair = new() {
    ['n'] = "ns", ['w'] = "we", ['u'] = "ud",
  };

  // ExOrientation.IsOrientationToken: a single side, up/down/u/d, or a concatenation of whole axis
  // pairs each used at most once.
  internal static bool IsOrientationToken(string token) {
    if (token.Length == 0)
      return false;
    if (IsHorizontalSideWord(token) || token is "up" or "down" or "u" or "d")
      return true;
    if (token.Length % 2 != 0 || token.Length > 6)
      return false;
    var axesSeen = new HashSet<char>();
    for (int i = 0; i < token.Length; i += 2) {
      string pair = token.Substring(i, 2);
      if (!AxisPairs.Contains(pair))
        return false;
      char axis = AxisOf[pair[0]];
      if (!axesSeen.Add(axis))
        return false;
    }
    return true;
  }

  private static char RotateLetter(char direction, int angle) =>
    direction is 'u' or 'd' ? direction : RotateSideWord(direction.ToString(), angle)[0];

  // ExOrientation.RotateOrientationToken: a network node's multi-direction token rotates letter by
  // letter and comes back in canonical axis order (ns, we, ud), discarding direction; a single side
  // or up/down rotates as a word.
  internal static string RotateOrientationToken(string token, int angle) {
    if (!IsOrientationToken(token))
      return token;
    if (token.Length > 1 && !IsHorizontalSideWord(token) && token is not ("up" or "down")) {
      var axes = new List<char>();
      foreach (char c in token) {
        char axis = AxisOf[RotateLetter(c, angle)];
        if (!axes.Contains(axis))
          axes.Add(axis);
      }
      return string.Concat("nwu".Where(axes.Contains).Select(axis => CanonicalPair[axis]));
    }
    return RotateSideWord(token, angle);
  }

  // MultiblockFacings.RotateSegments: swaps every listed dash-segment of `path` (domain stripped)
  // for its rotated orientation token; null when a segment is out of range or not an orientation
  // token, so the caller keeps the authored selector.
  internal static string? RotateSegments(string path, IReadOnlyList<int> segments, int angle) {
    int colon = path.IndexOf(':');
    string domain = colon >= 0 ? path[..colon] : "";
    string rest = colon >= 0 ? path[(colon + 1)..] : path;
    string prefix = colon >= 0 ? domain + ":" : "";
    string[] parts = rest.Split('-');
    foreach (int segment in segments)
      if (segment < 0 || segment >= parts.Length || !IsOrientationToken(parts[segment]))
        return null;
    foreach (int segment in segments)
      parts[segment] = RotateOrientationToken(parts[segment], angle);
    return prefix + string.Join('-', parts);
  }
}
