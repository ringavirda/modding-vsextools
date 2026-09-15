using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes;

/// <summary>The game's <c>selectiveElements</c> rule for a blocktype's shape entry: an element is
/// drawn only when a pattern names its path (element names joined by <c>/</c>). <c>Base/*</c> names
/// Base and everything under it, a bare <c>Base</c> that element alone, and <c>*</c> stands for any
/// run of characters. An empty list names everything.</summary>
public static class SelectiveElements {
  private static readonly ConcurrentDictionary<string, Regex> Cache = new();

  /// <summary>Whether an element at <paramref name="path"/> is drawn under <paramref name="patterns"/>:
  /// a pattern is matched segment by segment down the path, so an element on the way to a named
  /// one is kept for its children to be reached, and a pattern ending in <c>*</c> takes the whole
  /// subtree under the segments before it.</summary>
  public static bool Keeps(IReadOnlyList<string> patterns, string path) {
    if (patterns.Count == 0)
      return true;
    string[] parts = path.Split('/');
    foreach (string pattern in patterns) {
      string[] segments = pattern.Split('/');
      int depth = Math.Min(parts.Length, segments.Length);
      bool matched = true;
      for (int i = 0; i < depth && matched; i++)
        matched = ToRegex(segments[i]).IsMatch(parts[i]);
      if (matched && (parts.Length <= segments.Length || segments[^1] == "*"))
        return true;
    }
    return false;
  }

  /// <summary>The JSON element tree with every element the patterns do not name left out; a child
  /// is checked under its parent's path, so a kept parent still loses the children no pattern
  /// names.</summary>
  public static JArray Prune(JArray elements, IReadOnlyList<string> patterns, string prefix = "") {
    if (patterns.Count == 0)
      return elements;
    var kept = new JArray();
    foreach (JToken token in elements) {
      if (token is not JObject element)
        continue;
      string path = prefix + ((string?)element["name"] ?? "");
      if (!Keeps(patterns, path))
        continue;
      var copy = (JObject)element.DeepClone();
      if (copy["children"] is JArray children)
        copy["children"] = Prune(children, patterns, path + "/");
      kept.Add(copy);
    }
    return kept;
  }

  // One path segment as a regex, `*` standing for any run of characters.
  private static Regex ToRegex(string segment) =>
    Cache.GetOrAdd(segment, p =>
      new Regex("^" + string.Join(".*", p.Split('*').Select(Regex.Escape)) + "$", RegexOptions.CultureInvariant));
}
