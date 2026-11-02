using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>A store of check results keyed on content: the check, its version and a canonical hash of everything
/// the check reads. No path or command line is in a key, so moving a directory reruns nothing.</summary>
/// <param name="dir">The directory the entries live in; created on the first <see cref="Put"/>.</param>
public sealed class ResultCache(string dir) {
  /// <summary>The cache directory for an input: <c>.exmod/shapes-cache</c> under the git root above
  /// <paramref name="inputPath"/>, or under the input's own directory when none holds a <c>.git</c>.</summary>
  /// <param name="inputPath">A file or directory.</param>
  /// <param name="overrideDir">The directory to use instead, when not null.</param>
  public static string DefaultDir(string inputPath, string? overrideDir = null) {
    if (overrideDir != null)
      return Path.GetFullPath(overrideDir);
    string full = Path.GetFullPath(inputPath);
    string start = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
    for (string? d = start; d != null; d = Path.GetDirectoryName(d))
      if (
        Directory.Exists(Path.Combine(d, ".git"))
        || File.Exists(Path.Combine(d, ".git"))
      )
        return Path.Combine(d, ".exmod", "shapes-cache");
    return Path.Combine(start, ".exmod", "shapes-cache");
  }

  /// <summary>The key of a check run: sha256 over the check id, <paramref name="version"/> and
  /// <see cref="InputHash"/>.</summary>
  /// <param name="check">The check.</param>
  /// <param name="version">The check's version, <see cref="CheckVersions.Of"/> for the shipped ones.</param>
  /// <param name="input">What the check reads.</param>
  public static string Key(ICheck check, string version, CheckInput input) =>
    Provenance.Sha256Of($"{check.Id}\n{version}\n{InputHash(input)}\n");

  /// <summary>The canonical content hash of everything in <paramref name="input"/>: shapes as their canonical
  /// text, records as their written text, the rest as key-sorted JSON.</summary>
  public static string InputHash(CheckInput input) {
    var o = new JObject();
    void AddShape(string name, Shape? s) {
      if (s != null)
        o[name] = Provenance.Sha256Of(CanonicalWriter.Write(s));
    }
    void AddRecord(string name, PartRecord? r) {
      if (r != null)
        o[name] = Provenance.Sha256Of(r.Write());
    }
    AddShape("shape", input.Shape);
    AddShape("shipped", input.Shipped);
    AddShape("source", input.Source);
    AddShape("cast", input.Cast);
    AddShape("pattern", input.Pattern);
    AddShape("pair", input.Pair);
    AddRecord("record", input.Record);
    AddRecord("sourceRecord", input.SourceRecord);
    if (input.Cores != null)
      o["cores"] = new JArray(
        input.Cores.Select(c =>
          (JToken)Provenance.Sha256Of(CanonicalWriter.Write(c))
        )
      );
    if (input.CoreRecords != null)
      o["coreRecords"] = new JArray(
        input.CoreRecords.Select(c => (JToken)Provenance.Sha256Of(c.Write()))
      );
    static JToken Plain(object? v) =>
      v == null ? JValue.CreateNull() : JToken.FromObject(v);
    o["table"] = Plain(input.Table);
    o["sizes"] = Plain(input.Sizes);
    o["woodKeys"] = Plain(input.WoodKeys?.Order(StringComparer.Ordinal));
    o["hide"] = Plain(input.Hide);
    o["footprint"] = Plain(input.Footprint);
    o["regular"] = Plain(input.Regular);
    o["directed"] = input.Directed;
    o["tex"] = input.Tex;
    o["settings"] = Plain(input.Settings);
    o["allowance"] = input.Allowance;
    o["parting"] = Plain(input.Parting);
    o["cutFaces"] = Plain(input.CutFaces);
    o["station"] = Plain(input.Station);
    return Provenance.Sha256Of(Staleness.Canonical(o));
  }

  /// <summary>The recorded result under <paramref name="key"/>; false when none, or when the entry is
  /// unreadable.</summary>
  public bool TryGet(string key, out CheckResult result) {
    result = null!;
    string path = EntryPath(key);
    if (!File.Exists(path))
      return false;
    try {
      JObject o = JObject.Parse(File.ReadAllText(path));
      result = CheckResult.Restore(
        (string)o["check"]!,
        Enum.Parse<Verdict>((string)o["verdict"]!),
        (int)o["enumerated"]!,
        [.. o["lines"]!.Select(t => (string)t!)]
      );
      return true;
    } catch (Exception e)
        when (e
            is JsonException
              or InvalidCastException
              or ArgumentException
              or NullReferenceException
        ) {
      return false;
    }
  }

  /// <summary>Records <paramref name="result"/> under <paramref name="key"/>.</summary>
  /// <exception cref="IOException">The entry cannot be written.</exception>
  public void Put(string key, CheckResult result) {
    Directory.CreateDirectory(dir);
    string text = JsonConvert.SerializeObject(
      new JObject {
        ["check"] = result.Check,
        ["verdict"] = result.Verdict.ToString(),
        ["enumerated"] = result.Enumerated,
        ["lines"] = new JArray(result.Lines),
      },
      Formatting.None
    );
    string tmp = Path.Combine(
      dir,
      key + "." + Guid.NewGuid().ToString("N") + ".tmp"
    );
    File.WriteAllText(tmp, text);
    File.Move(tmp, EntryPath(key), true);
  }

  private string EntryPath(string key) => Path.Combine(dir, key + ".json");
}
