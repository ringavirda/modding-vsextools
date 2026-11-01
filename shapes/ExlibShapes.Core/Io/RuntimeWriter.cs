using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Core;

/// <summary>Turns an editable shape into the shape a mod ships. Texture paths take the registry's
/// runtime path for the mod's domain, <c>editor</c> and <c>textureSizes</c> go, a top-level
/// <c>Root</c> group is lifted, the <c>Items</c> group is dropped on request, and clip end flags
/// come from the caller; geometry is untouched.</summary>
public static class RuntimeWriter {
  private const double UnwindDegrees = 180;

  /// <param name="shape">The editable shape; not modified.</param>
  /// <param name="settings">The texture registry.</param>
  /// <param name="domain">The mod domain whose runtime paths are written.</param>
  /// <param name="clipEnds"><c>onAnimationEnd</c> by clip code (the name when a clip has no code);
  /// a clip not listed keeps the value it was read with. A clip that ends on <c>Repeat</c> has its
  /// wrap closed.</param>
  /// <param name="dropItems">Removes every <c>Items</c> group and the keyframe entries naming
  /// its elements.</param>
  /// <returns>The text, indent of one space, LF, ending in a line feed.</returns>
  /// <exception cref="ShapeWriteException">A texture key has no runtime path for the domain; the
  /// message names every such key and nothing was produced.</exception>
  public static string Write(
    Shape shape,
    Settings settings,
    string domain,
    IReadOnlyDictionary<string, string>? clipEnds = null,
    bool dropItems = true
  ) {
    var paths = new Dictionary<string, string>();
    var unmapped = new List<string>();
    foreach (var (key, _) in shape.Textures)
      try {
        paths[key] = settings.RuntimePath(key, domain);
      } catch (SettingsException) {
        unmapped.Add(key);
      }
    if (unmapped.Count > 0)
      throw new ShapeWriteException(
        $"no runtime texture path for domain \"{domain}\": {string.Join(", ", unmapped)}"
      );

    JObject o = ShapeJson.Build(
      shape,
      asRead: true,
      runtime: true,
      textures: paths
    );
    if (Prop(o, "elements")?.Value is JArray elements) {
      UnwrapRoot(elements);
      if (dropItems)
        DropItems(o, elements);
    }
    if (Prop(o, "animations")?.Value is JArray clips)
      for (int i = 0; i < clips.Count; i++) {
        var entry = (JObject)clips[i];
        Clip clip = shape.Animations[i];
        string? code = clip.Code ?? clip.Name;
        if (
          code != null
          && clipEnds != null
          && clipEnds.TryGetValue(code, out string? end)
        )
          entry["onAnimationEnd"] = end;
        if ((string?)Prop(entry, "onAnimationEnd")?.Value == "Repeat")
          CloseLoop(entry);
      }
    return JsonLayout.Python.Print(o) + "\n";
  }

  /// <summary>Writes the runtime shape to <paramref name="path"/> through <see cref="AtomicFile"/>;
  /// the arguments and exceptions are <see cref="Write"/>'s, and no file is touched when it throws.</summary>
  /// <exception cref="System.IO.IOException">The file cannot be written.</exception>
  public static void WriteFile(
    string path,
    Shape shape,
    Settings settings,
    string domain,
    IReadOnlyDictionary<string, string>? clipEnds = null,
    bool dropItems = true
  ) =>
    AtomicFile.WriteAllText(
      path,
      Write(shape, settings, domain, clipEnds, dropItems)
    );

  private static JProperty? Prop(JObject o, string name) =>
    o.Properties()
      .FirstOrDefault(p =>
        p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
      );

  private static string? NameOf(JToken el) =>
    el is JObject o ? (string?)Prop(o, "name")?.Value : null;

  private static void UnwrapRoot(JArray elements) {
    var lifted = new List<JToken>();
    foreach (JToken el in elements) {
      if (
        NameOf(el) == "Root"
        && Prop((JObject)el, "children")?.Value
          is JArray { Count: > 0 } children
      ) {
        JArray origin =
          Prop((JObject)el, "from")?.Value as JArray ?? [0L, 0L, 0L];
        foreach (JObject child in children) {
          Translate(child, "from", origin);
          Translate(child, "to", origin);
          Translate(child, "rotationOrigin", origin);
          lifted.Add(child);
        }
      } else
        lifted.Add(el);
    }
    elements.Clear();
    foreach (JToken t in lifted)
      elements.Add(t);
  }

  private static void Translate(JObject child, string key, JArray by) {
    if (Prop(child, key)?.Value is not JArray { Count: 3 } v)
      return;
    for (int i = 0; i < 3; i++) {
      var a = (JValue)v[i];
      var b = (JValue)by[i];
      v[i] =
        a.Type == JTokenType.Integer && b.Type == JTokenType.Integer
          ? new JValue((long)a + (long)b)
          : new JValue((double)a + (double)b);
    }
  }

  private static void DropItems(JObject shape, JArray elements) {
    var dropped = new HashSet<string>();
    Strip(elements, dropped);
    if (Prop(shape, "animations")?.Value is not JArray clips)
      return;
    foreach (JObject clip in clips.OfType<JObject>())
      if (Prop(clip, "keyframes")?.Value is JArray keyframes)
        foreach (JObject kf in keyframes.OfType<JObject>())
          if (Prop(kf, "elements")?.Value is JObject posed)
            foreach (
              string name in posed.Properties().Select(p => p.Name).ToList()
            )
              if (dropped.Contains(name))
                posed.Remove(name);
  }

  private static void Strip(JArray elements, HashSet<string> dropped) {
    foreach (JToken el in elements.ToList()) {
      if (NameOf(el) == "Items") {
        Names(el, dropped);
        el.Remove();
      } else if (Prop((JObject)el, "children")?.Value is JArray children)
        Strip(children, dropped);
    }
  }

  private static void Names(JToken el, HashSet<string> names) {
    if (NameOf(el) is { } n)
      names.Add(n);
    if (Prop((JObject)el, "children")?.Value is JArray children)
      foreach (JToken c in children)
        Names(c, names);
  }

  private static void CloseLoop(JObject clip) {
    if (Prop(clip, "keyframes")?.Value is not JArray keyframes)
      return;
    var frames = keyframes
      .OfType<JObject>()
      .Where(k => Prop(k, "frame") != null)
      .OrderBy(k => (double)Prop(k, "frame")!.Value)
      .ToList();
    if (frames.Count < 2)
      return;
    JObject first = Poses(frames[0]);
    JObject last = Poses(frames[^1]);

    var rest = new List<(string Name, JObject Pose)>();
    foreach (JObject kf in frames)
      if (Prop(kf, "elements")?.Value is JObject poses)
        foreach (JProperty p in poses.Properties())
          if (rest.All(r => r.Name != p.Name) && p.Value is JObject pose)
            rest.Add((p.Name, pose));

    foreach (var (name, pose) in rest) {
      if (!first.ContainsKey(name))
        first[name] = pose.DeepClone();
      if (last[name] is not JObject lastPose) {
        last[name] = pose.DeepClone();
        continue;
      }
      foreach (string axis in new[] { "X", "Y", "Z" }) {
        string key = "rotation" + axis;
        if (
          pose.TryGetValue(key, out JToken? start)
          && lastPose.TryGetValue(key, out JToken? end)
          && Math.Abs((double)end - (double)start) >= UnwindDegrees
        )
          lastPose["rotShortestDistance" + axis] = true;
      }
    }
  }

  private static JObject Poses(JObject keyframe) {
    if (Prop(keyframe, "elements")?.Value is JObject poses)
      return poses;
    var made = new JObject();
    keyframe["elements"] = made;
    return made;
  }
}
