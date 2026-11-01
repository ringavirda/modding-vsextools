using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Core;

/// <summary>The model as a JSON tree, ready for a layout. Integer literals the file wrote stay
/// integers and every other number is a float.</summary>
internal static class ShapeJson {
  /// <param name="shape">The shape to convert; not modified.</param>
  /// <param name="asRead">Keys keep the file's order and spelling, known keys the file lacked
  /// follow; false gives the fixed order and canonical spelling.</param>
  /// <param name="runtime">Leaves out <c>editor</c> and <c>textureSizes</c>.</param>
  /// <param name="textures">Replaces the <c>textures</c> values by key when given.</param>
  public static JObject Build(
    Shape shape,
    bool asRead,
    bool runtime = false,
    IReadOnlyDictionary<string, string>? textures = null
  ) {
    HashSet<string> present = asRead ? Lower(shape.KeyOrder) : [];
    return Ordered(
      shape.KeyOrder,
      asRead,
      [
        "editor",
        "textureWidth",
        "textureHeight",
        "textureSizes",
        "textures",
        "elements",
        "animations",
      ],
      shape.Unknown,
      key => {
        switch (key) {
          case "editor":
            return runtime ? null : shape.Editor?.DeepClone();
          case "texturewidth":
            return Whole(shape.TextureWidth);
          case "textureheight":
            return Whole(shape.TextureHeight);
          case "texturesizes":
            if (
              runtime
              || (shape.TextureSizes.Count == 0 && !present.Contains(key))
            )
              return null;
            var sizes = new JObject();
            foreach (var (k, v) in shape.TextureSizes)
              sizes[k] = new JArray(v.Select(d => Number(d, true)));
            return sizes;
          case "textures":
            if (shape.Textures.Count == 0 && !present.Contains(key))
              return null;
            var map = new JObject();
            foreach (var (k, v) in shape.Textures)
              map[k] =
                textures != null && textures.TryGetValue(k, out string? r)
                  ? r
                  : v;
            return map;
          case "elements":
            return shape.Elements.Count == 0 && !present.Contains(key)
              ? null
              : new JArray(shape.Elements.Select(e => Element(e, asRead)));
          default:
            return shape.Animations.Count == 0 && !present.Contains(key)
              ? null
              : new JArray(shape.Animations.Select(c => Clip(c, asRead)));
        }
      }
    );
  }

  private static JObject Element(Element e, bool asRead) {
    HashSet<string> present = asRead ? Lower(e.KeyOrder) : [];
    return Ordered(
      e.KeyOrder,
      asRead,
      [
        "name",
        "from",
        "to",
        "rotationOrigin",
        "rotationX",
        "rotationY",
        "rotationZ",
        "scaleX",
        "scaleY",
        "scaleZ",
        "faces",
        "children",
      ],
      e.Unknown,
      key =>
        key switch {
          "name" => e.Name == null ? null : new JValue(e.Name),
          "from" => Nums(e.From, e.IntegerForms, key),
          "to" => Nums(e.To, e.IntegerForms, key),
          "rotationorigin" => Nums(e.RotationOrigin, e.IntegerForms, key),
          "rotationx" => Scalar(e.RotationX, e.IntegerForms, key),
          "rotationy" => Scalar(e.RotationY, e.IntegerForms, key),
          "rotationz" => Scalar(e.RotationZ, e.IntegerForms, key),
          "scalex" => Scalar(e.ScaleX, e.IntegerForms, key),
          "scaley" => Scalar(e.ScaleY, e.IntegerForms, key),
          "scalez" => Scalar(e.ScaleZ, e.IntegerForms, key),
          "faces" => e.Faces.Count == 0 && !present.Contains(key)
            ? null
            : Faces(e.Faces, asRead),
          _ => e.Children.Count == 0 && !present.Contains(key)
            ? null
            : new JArray(e.Children.Select(c => Element(c, asRead))),
        }
    );
  }

  private static JObject Faces(
    List<KeyValuePair<string, Face>> faces,
    bool asRead
  ) {
    var result = new JObject();
    foreach (var (name, f) in faces)
      result[name] = Ordered(
        f.KeyOrder,
        asRead,
        ["texture", "uv", "rotation", "enabled"],
        f.Unknown,
        key =>
          key switch {
            "texture" => f.Texture == null ? null : new JValue(f.Texture),
            "uv" => Nums(f.Uv, f.IntegerForms, key),
            "rotation" => Scalar(f.Rotation, f.IntegerForms, key),
            _ => f.Enabled == null ? null : new JValue(f.Enabled.Value),
          }
      );
    return result;
  }

  private static JObject Clip(Clip c, bool asRead) {
    HashSet<string> present = asRead ? Lower(c.KeyOrder) : [];
    return Ordered(
      c.KeyOrder,
      asRead,
      [
        "name",
        "code",
        "version",
        "quantityframes",
        "onAnimationEnd",
        "keyframes",
      ],
      c.Unknown,
      key =>
        key switch {
          "name" => c.Name == null ? null : new JValue(c.Name),
          "code" => c.Code == null ? null : new JValue(c.Code),
          "version" => c.Version == null
            ? null
            : new JValue((long)c.Version.Value),
          "quantityframes" => c.Quantity == null
            ? null
            : new JValue((long)c.Quantity.Value),
          "onanimationend" => c.OnAnimationEnd == null
            ? null
            : new JValue(c.OnAnimationEnd),
          _ => c.Keyframes.Count == 0 && !present.Contains(key)
            ? null
            : new JArray(c.Keyframes.Select(k => Keyframe(k, asRead))),
        }
    );
  }

  private static JObject Keyframe(Keyframe k, bool asRead) {
    HashSet<string> present = asRead ? Lower(k.KeyOrder) : [];
    return Ordered(
      k.KeyOrder,
      asRead,
      ["frame", "elements"],
      k.Unknown,
      key => {
        if (key == "frame")
          return k.Frame == null ? null : new JValue((long)k.Frame.Value);
        if (k.Elements.Count == 0 && !present.Contains(key))
          return null;
        var poses = new JObject();
        foreach (var (name, pose) in k.Elements)
          poses[name] = pose.DeepClone();
        return poses;
      }
    );
  }

  private static JObject Ordered(
    IReadOnlyList<string> keyOrder,
    bool asRead,
    string[] canonical,
    JObject unknown,
    Func<string, JToken?> known
  ) {
    var result = new JObject();
    var canonicalKeys = canonical.ToDictionary(c => c.ToLowerInvariant());
    var done = new HashSet<string>();
    if (asRead)
      foreach (string name in keyOrder) {
        string lower = name.ToLowerInvariant();
        if (!canonicalKeys.ContainsKey(lower)) {
          if (unknown.TryGetValue(name, out JToken? u))
            result[name] = u.DeepClone();
        } else if (done.Add(lower) && known(lower) is { } v)
          result[name] = v;
      }
    foreach (var (lower, name) in canonicalKeys)
      if (done.Add(lower) && known(lower) is { } v)
        result[name] = v;
    foreach (JProperty p in unknown.Properties())
      if (!result.ContainsKey(p.Name))
        result[p.Name] = p.Value.DeepClone();
    return result;
  }

  private static HashSet<string> Lower(IEnumerable<string> keys) =>
    [.. keys.Select(k => k.ToLowerInvariant())];

  private static JToken? Scalar(
    double? v,
    Dictionary<string, uint> forms,
    string key
  ) => v == null ? null : Number(v.Value, forms.GetValueOrDefault(key) != 0);

  private static JToken? Nums(
    double[]? v,
    Dictionary<string, uint> forms,
    string key
  ) {
    if (v == null)
      return null;
    uint mask = forms.GetValueOrDefault(key);
    return new JArray(
      v.Select((d, i) => Number(d, i < 32 && (mask >> i & 1) != 0))
    );
  }

  private static JToken? Whole(double? v) =>
    v == null ? null : Number(v.Value, true);

  private static JToken Number(double v, bool whole) =>
    whole && v == Math.Floor(v) && Math.Abs(v) < 1e15
      ? new JValue((long)v)
      : new JValue(v);
}
