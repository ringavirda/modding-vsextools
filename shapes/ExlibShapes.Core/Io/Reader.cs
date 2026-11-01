using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Core;

/// <summary>Reads shape JSON as the game does: comments, trailing commas, unquoted keys and
/// single-quoted strings are accepted.</summary>
public static class Reader {
  /// <summary>Reads the shape file at <paramref name="path"/>.</summary>
  /// <exception cref="FileNotFoundException"><paramref name="path"/> does not exist.</exception>
  /// <exception cref="JsonException">The text is not a shape document.</exception>
  public static Shape ReadFile(string path) {
    if (!File.Exists(path))
      throw new FileNotFoundException($"No such shape file: {path}", path);
    try {
      return Read(File.ReadAllText(path));
    } catch (JsonException e) {
      throw new JsonException($"{path}: {e.Message}", e);
    }
  }

  /// <summary>Reads a shape from its JSON text.</summary>
  /// <exception cref="JsonException">The text is not a JSON object or a value has the wrong type.</exception>
  public static Shape Read(string text) {
    using var reader = new JsonTextReader(new StringReader(text));
    JToken root = JToken.ReadFrom(reader, LoadSettings);
    if (root is not JObject obj)
      throw new JsonException("not a shape (the document is not an object)");
    return ReadShape(obj);
  }

  private static readonly JsonLoadSettings LoadSettings = new() {
    CommentHandling = CommentHandling.Ignore,
    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Replace,
  };

  private static Shape ReadShape(JObject obj) {
    var shape = new Shape();
    foreach (JProperty p in obj.Properties()) {
      shape.KeyOrder.Add(p.Name);
      switch (p.Name) {
        case "textures":
          foreach (JProperty t in Obj(p).Properties())
            shape.Textures.Add(new(t.Name, Str(t.Value, t.Name)));
          break;
        case "textureSizes":
          foreach (JProperty t in Obj(p).Properties())
            shape.TextureSizes.Add(new(t.Name, Nums(t.Value, t.Name)!));
          break;
        case "textureWidth":
          shape.TextureWidth = Num(p.Value, p.Name);
          break;
        case "textureHeight":
          shape.TextureHeight = Num(p.Value, p.Name);
          break;
        case "editor":
          shape.Editor =
            p.Value as JObject
            ?? throw new JsonException("\"editor\" is not an object");
          break;
        case "elements":
          foreach (JToken e in Arr(p))
            shape.Elements.Add(ReadElement(e));
          break;
        case "animations":
          foreach (JToken c in Arr(p))
            shape.Animations.Add(ReadClip(c));
          break;
        default:
          shape.Unknown.Add(p.Name, p.Value);
          break;
      }
    }
    return shape;
  }

  private static Element ReadElement(JToken token) {
    JObject obj = AsObj(token, "element");
    var el = new Element();
    foreach (JProperty p in obj.Properties()) {
      el.KeyOrder.Add(p.Name);
      switch (p.Name) {
        case "name":
          el.Name = Str(p.Value, p.Name);
          break;
        case "from":
          el.From = Nums(p.Value, p.Name);
          break;
        case "to":
          el.To = Nums(p.Value, p.Name);
          break;
        case "rotationOrigin":
          el.RotationOrigin = Nums(p.Value, p.Name);
          break;
        case "rotationX":
          el.RotationX = Num(p.Value, p.Name);
          break;
        case "rotationY":
          el.RotationY = Num(p.Value, p.Name);
          break;
        case "rotationZ":
          el.RotationZ = Num(p.Value, p.Name);
          break;
        case "scaleX":
          el.ScaleX = Num(p.Value, p.Name);
          break;
        case "scaleY":
          el.ScaleY = Num(p.Value, p.Name);
          break;
        case "scaleZ":
          el.ScaleZ = Num(p.Value, p.Name);
          break;
        case "faces":
          foreach (JProperty f in Obj(p).Properties())
            el.Faces.Add(new(f.Name, ReadFace(f.Value)));
          break;
        case "children":
          foreach (JToken c in Arr(p))
            el.Children.Add(ReadElement(c));
          break;
        default:
          el.Unknown.Add(p.Name, p.Value);
          break;
      }
    }
    return el;
  }

  private static Face ReadFace(JToken token) {
    JObject obj = AsObj(token, "face");
    var face = new Face();
    foreach (JProperty p in obj.Properties()) {
      face.KeyOrder.Add(p.Name);
      switch (p.Name) {
        case "texture":
          face.Texture = Str(p.Value, p.Name);
          break;
        case "uv":
          face.Uv = Nums(p.Value, p.Name);
          break;
        case "rotation":
          face.Rotation = Num(p.Value, p.Name);
          break;
        case "enabled":
          face.Enabled =
            p.Value.Type == JTokenType.Boolean
              ? (bool)p.Value
              : throw new JsonException($"\"{p.Name}\" is not a boolean");
          break;
        default:
          face.Unknown.Add(p.Name, p.Value);
          break;
      }
    }
    return face;
  }

  private static Clip ReadClip(JToken token) {
    JObject obj = AsObj(token, "animation");
    var clip = new Clip();
    foreach (JProperty p in obj.Properties()) {
      clip.KeyOrder.Add(p.Name);
      switch (p.Name) {
        case "code":
          clip.Code = Str(p.Value, p.Name);
          break;
        case "version":
          clip.Version = Int(p.Value, p.Name);
          break;
        case "quantityframes":
          clip.Quantity = Int(p.Value, p.Name);
          break;
        case "onAnimationEnd":
          clip.OnAnimationEnd = Str(p.Value, p.Name);
          break;
        case "keyframes":
          foreach (JToken k in Arr(p))
            clip.Keyframes.Add(ReadKeyframe(k));
          break;
        default:
          clip.Unknown.Add(p.Name, p.Value);
          break;
      }
    }
    return clip;
  }

  private static Keyframe ReadKeyframe(JToken token) {
    JObject obj = AsObj(token, "keyframe");
    var key = new Keyframe();
    foreach (JProperty p in obj.Properties()) {
      key.KeyOrder.Add(p.Name);
      switch (p.Name) {
        case "frame":
          key.Frame = Int(p.Value, p.Name);
          break;
        case "elements":
          foreach (JProperty e in Obj(p).Properties())
            key.Elements.Add(new(e.Name, e.Value));
          break;
        default:
          key.Unknown.Add(p.Name, p.Value);
          break;
      }
    }
    return key;
  }

  private static JObject AsObj(JToken t, string what) =>
    t as JObject ?? throw new JsonException($"a {what} is not an object");

  private static JObject Obj(JProperty p) =>
    p.Value as JObject
    ?? throw new JsonException($"\"{p.Name}\" is not an object");

  private static JArray Arr(JProperty p) =>
    p.Value as JArray
    ?? throw new JsonException($"\"{p.Name}\" is not an array");

  private static string Str(JToken t, string key) =>
    t.Type == JTokenType.String
      ? (string)t!
      : throw new JsonException($"\"{key}\" is not a string");

  private static double Num(JToken t, string key) =>
    t.Type is JTokenType.Integer or JTokenType.Float
      ? (double)t
      : throw new JsonException($"\"{key}\" is not a number");

  private static int Int(JToken t, string key) =>
    t.Type is JTokenType.Integer or JTokenType.Float
      ? (int)(double)t
      : throw new JsonException($"\"{key}\" is not a number");

  private static double[]? Nums(JToken t, string key) {
    if (t is not JArray arr)
      throw new JsonException($"\"{key}\" is not an array of numbers");
    var list = new List<double>(arr.Count);
    foreach (JToken v in arr) {
      if (v.Type == JTokenType.Undefined)
        continue;
      list.Add(Num(v, key));
    }
    return [.. list];
  }
}
