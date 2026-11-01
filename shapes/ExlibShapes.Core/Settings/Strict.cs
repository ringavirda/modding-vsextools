using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Core;

/// <summary>A settings value with its key path, read so that an unknown or missing key stops with
/// its name.</summary>
internal sealed class Strict(JToken token, string path) {
  public bool IsString => token.Type == JTokenType.String;

  public void Allow(params string[] keys) {
    foreach (JProperty p in Object().Properties())
      if (!keys.Contains(p.Name))
        throw new SettingsException($"unknown key \"{p.Name}\" in {path}");
  }

  public Strict Required(string key) =>
    Optional(key)
    ?? throw new SettingsException($"missing key \"{key}\" in {path}");

  public Strict? Optional(string key) =>
    Object().TryGetValue(key, out JToken? v)
      ? new Strict(v, $"{path}.{key}")
      : null;

  public Dictionary<string, T> Map<T>(Func<string, Strict, T> read) {
    var map = new Dictionary<string, T>();
    foreach (JProperty p in Object().Properties())
      map[p.Name] = read(p.Name, new Strict(p.Value, $"{path}.{p.Name}"));
    return map;
  }

  public List<T> List<T>(Func<Strict, T> read) {
    if (token is not JArray a)
      throw new SettingsException($"{path} is not an array");
    return [.. a.Select((v, i) => read(new Strict(v, $"{path}[{i}]")))];
  }

  public string Text() =>
    token.Type == JTokenType.String
      ? token.Value<string>()!
      : throw new SettingsException($"{path} is not a string");

  public double Number() =>
    token.Type is JTokenType.Integer or JTokenType.Float
      ? token.Value<double>()
      : throw new SettingsException($"{path} is not a number");

  public double[] Numbers(int count) {
    double[] v = [.. List(x => x.Number())];
    return v.Length == count
      ? v
      : throw new SettingsException(
        $"{path} needs {count} numbers, has {v.Length}"
      );
  }

  private JObject Object() =>
    token as JObject ?? throw new SettingsException($"{path} is not an object");
}
