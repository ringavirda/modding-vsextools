using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes;

/// <summary>The shape a views dump draws from.</summary>
/// <param name="Base">The shape code, <c>domain:path</c>.</param>
/// <param name="SelectiveElements">The element patterns the shape entry keeps; null when it draws
/// the whole shape.</param>
/// <param name="RotateX">Degrees, the shape entry's own turn about x.</param>
/// <param name="RotateY">Degrees about y.</param>
/// <param name="RotateZ">Degrees about z.</param>
internal sealed record ViewsShape(
  string Base,
  IReadOnlyList<string>? SelectiveElements,
  double RotateX,
  double RotateY,
  double RotateZ
);

/// <summary>The held clips a views dump names; each null where the item sets none.</summary>
/// <param name="Idle">The third-person idle clip.</param>
/// <param name="Ready">The ready clip.</param>
/// <param name="Hit">The third-person hit clip.</param>
/// <param name="Use">The third-person use clip.</param>
internal sealed record ViewsClips(
  string? Idle,
  string? Ready,
  string? Hit,
  string? Use
);

/// <summary>One gate of a views dump.</summary>
/// <param name="Gate">The gate's id, such as <c>G7</c>.</param>
/// <param name="Name">The measurement's name, such as <c>hand.size</c>.</param>
/// <param name="Target">gui, tp, fp or ground.</param>
/// <param name="Pass">Whether the value is inside the gate's band.</param>
internal sealed record ViewsGate(
  string Gate,
  string Name,
  string Target,
  bool Pass
);

/// <summary>
/// A views dump as the guard writes it: the shape and textures of one group of collectibles, the
/// transforms the game resolves for it and what its gates measured. The format is
/// <c>item-views/1</c>. Keys the pictures and the manifest do not read are not kept.
/// </summary>
/// <param name="Game">The game version the dump was made on; null when absent.</param>
/// <param name="View">The group's first code, or <c>&lt;code&gt;@&lt;family&gt;/&lt;element&gt;</c>.</param>
/// <param name="Codes">Every code in the group.</param>
/// <param name="Type">item or block.</param>
/// <param name="Kind">The kind the group boxes as.</param>
/// <param name="Shape">The shape the group is drawn from.</param>
/// <param name="Textures">Texture key to <c>domain:path</c>.</param>
/// <param name="Gui">The resolved inventory transform; translation in screen pixels.</param>
/// <param name="Tp">The resolved third-person hand transform.</param>
/// <param name="Ground">The resolved ground transform.</param>
/// <param name="Footprint">The item's slot footprint [x0, y0, x1, y1] in pixels at GUI scale 1,
/// from the slot's top left, y down; null when the dump carries none.</param>
/// <param name="Grip">The item's point [x, y, z] in voxels that lies on the hand; null when the
/// dump carries none.</param>
/// <param name="Gates">Every gate, in file order.</param>
/// <param name="Clips">The held clips; null when the dump carries none.</param>
internal sealed record ViewsFile(
  string? Game,
  string View,
  IReadOnlyList<string> Codes,
  string Type,
  string Kind,
  ViewsShape Shape,
  IReadOnlyDictionary<string, string> Textures,
  ItemTransform Gui,
  ItemTransform Tp,
  ItemTransform Ground,
  IReadOnlyList<double>? Footprint,
  IReadOnlyList<double>? Grip,
  IReadOnlyList<ViewsGate> Gates,
  ViewsClips? Clips = null
) {
  /// <summary>The value the file's <c>format</c> must hold.</summary>
  public const string Format = "item-views/1";

  /// <summary>Reads a views dump.</summary>
  /// <param name="path">A <c>.views.json</c> file.</param>
  /// <exception cref="InvalidDataException">The file is not JSON, its format is not
  /// <see cref="Format"/>, or a key the pictures need is missing or of the wrong type.</exception>
  /// <exception cref="IOException">The file cannot be read.</exception>
  public static ViewsFile Load(string path) => Parse(File.ReadAllText(path));

  /// <summary>Reads a views dump from its JSON text; see <see cref="Load"/>.</summary>
  public static ViewsFile Parse(string json) {
    JObject root;
    try {
      root = JObject.Parse(json);
    } catch (Newtonsoft.Json.JsonException e) {
      throw new InvalidDataException($"not a views file: {e.Message}", e);
    }
    string? format = (string?)root["format"];
    if (format != Format)
      throw new InvalidDataException(
        $"not a views file: format {format ?? "(none)"} is not {Format}"
      );
    try {
      JObject shape = Object(root, "shape");
      return new ViewsFile(
        (string?)root["game"],
        Text(root, "view"),
        [.. Items(root, "codes").Select(c => (string)c!)],
        Text(root, "type"),
        Text(root, "kind"),
        new ViewsShape(
          Text(shape, "base"),
          shape["selectiveElements"] is JArray selective
            ? [.. selective.Select(s => (string)s!)]
            : null,
          Number(shape, "rotateX"),
          Number(shape, "rotateY"),
          Number(shape, "rotateZ")
        ),
        Object(root, "textures")
          .Properties()
          .ToDictionary(p => p.Name, p => (string)p.Value!),
        Transform(root, "gui"),
        Transform(root, "tp"),
        Transform(root, "ground"),
        root["footprint"] is JArray { Count: 4 } box
          ? [.. box.Select(n => (double)n)]
          : null,
        root["grip"] is JArray { Count: 3 } grip
          ? [.. grip.Select(n => (double)n)]
          : null,
        [.. Items(root, "gates").Select(Gate)],
        root["clips"] is JObject clips
          ? new ViewsClips(
            (string?)clips["idle"],
            (string?)clips["ready"],
            (string?)clips["hit"],
            (string?)clips["use"]
          )
          : null
      );
    } catch (Exception e)
        when (e is InvalidCastException or ArgumentException or FormatException) {
      throw new InvalidDataException($"not a views file: {e.Message}", e);
    }
  }

  private static ViewsGate Gate(JToken gate) =>
    new(
      Text(gate, "gate"),
      Text(gate, "name"),
      Text(gate, "target"),
      gate["pass"] is { Type: JTokenType.Boolean } pass
        ? (bool)pass
        : throw new InvalidDataException("not a views file: gate lacks pass")
    );

  private static ItemTransform Transform(JObject root, string key) {
    JObject t = Object(root, key);
    return new ItemTransform(
      Vector(t, "translation"),
      Vector(t, "rotation"),
      Vector(t, "origin"),
      Vector(t, "scaleXyz"),
      t["rotate"] is { Type: JTokenType.Boolean } rotate
        ? (bool)rotate
        : throw new InvalidDataException(
          $"not a views file: {key} lacks rotate"
        )
    );
  }

  private static (double X, double Y, double Z) Vector(JObject t, string key) {
    JObject v = Object(t, key);
    return (Number(v, "x"), Number(v, "y"), Number(v, "z"));
  }

  private static JObject Object(JToken parent, string key) =>
    parent[key] as JObject
    ?? throw new InvalidDataException($"not a views file: no {key} object");

  private static IEnumerable<JToken> Items(JObject root, string key) =>
    root[key] as JArray
    ?? throw new InvalidDataException($"not a views file: no {key} array");

  private static string Text(JToken parent, string key) =>
    parent[key] is { Type: JTokenType.String } s
      ? (string)s!
      : throw new InvalidDataException($"not a views file: no {key} string");

  private static double Number(JToken parent, string key) =>
    parent[key] is { Type: JTokenType.Integer or JTokenType.Float } n
      ? Convert.ToDouble(n, CultureInfo.InvariantCulture)
      : throw new InvalidDataException($"not a views file: no {key} number");
}
