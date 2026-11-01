using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Core;

/// <summary>One line of <c>provenance.jsonl</c>: a shape file the tool generated and what made it.</summary>
/// <param name="Path">The file, relative to the provenance file's directory, with <c>/</c> separators.</param>
/// <param name="Sha256">Lowercase hex sha256 of the file bytes as generated.</param>
/// <param name="Generator">The generator id.</param>
/// <param name="Version">The generator version.</param>
/// <param name="InputHash">See <see cref="Provenance.InputHash"/>.</param>
public sealed record ProvenanceRow(
  string Path,
  string Sha256,
  string Generator,
  string Version,
  string InputHash
);

/// <summary>The provenance rows of one directory tree.</summary>
public sealed class Provenance {
  private readonly List<ProvenanceRow> rows = [];

  /// <summary>The provenance file.</summary>
  public string FilePath { get; }

  /// <summary>The directory row paths are relative to.</summary>
  public string BaseDir => System.IO.Path.GetDirectoryName(FilePath)!;

  /// <summary>Opens the rows in <paramref name="filePath"/>; a missing file is an empty store.</summary>
  /// <exception cref="JsonException">A line is not a row.</exception>
  public Provenance(string filePath) {
    FilePath = System.IO.Path.GetFullPath(filePath);
    if (!File.Exists(FilePath))
      return;
    foreach (string line in File.ReadAllLines(FilePath)) {
      if (line.Trim().Length == 0)
        continue;
      using var reader = new JsonTextReader(new StringReader(line)) {
        DateParseHandling = DateParseHandling.None,
      };
      JObject o = JObject.Load(reader);
      rows.Add(
        new ProvenanceRow(
          Field(o, "path"),
          Field(o, "sha256"),
          Field(o, "generator"),
          Field(o, "version"),
          Field(o, "inputHash")
        )
      );
    }
  }

  /// <summary>The rows in file order.</summary>
  public IReadOnlyList<ProvenanceRow> Rows => rows;

  /// <summary>The newest row for <paramref name="path"/> (absolute or relative to <see cref="BaseDir"/>);
  /// null when none.</summary>
  public ProvenanceRow? Find(string path) {
    string rel = Relative(path);
    return rows.LastOrDefault(r => r.Path == rel);
  }

  /// <summary>Appends <paramref name="row"/> to the file and the store. Its path is made relative.</summary>
  /// <exception cref="IOException">The file cannot be appended to.</exception>
  public void Append(ProvenanceRow row) {
    ProvenanceRow r = row with { Path = Relative(row.Path) };
    string line = JsonConvert.SerializeObject(
      new JObject {
        ["path"] = r.Path,
        ["sha256"] = r.Sha256,
        ["generator"] = r.Generator,
        ["version"] = r.Version,
        ["inputHash"] = r.InputHash,
      },
      Formatting.None
    );
    Directory.CreateDirectory(BaseDir);
    File.AppendAllText(FilePath, line + "\n", new UTF8Encoding(false));
    rows.Add(r);
  }

  /// <summary>The path as stored in a row: relative to <see cref="BaseDir"/>, <c>/</c> separated.</summary>
  public string Relative(string path) =>
    System
      .IO.Path.GetRelativePath(
        BaseDir,
        System.IO.Path.GetFullPath(path, BaseDir)
      )
      .Replace('\\', '/');

  /// <summary>Lowercase hex sha256 of <paramref name="bytes"/>.</summary>
  public static string Sha256Of(byte[] bytes) =>
    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

  /// <summary>Lowercase hex sha256 of <paramref name="text"/> as UTF-8.</summary>
  public static string Sha256Of(string text) =>
    Sha256Of(Encoding.UTF8.GetBytes(text));

  /// <summary>The input hash of an output: sha256 over the spec entry text, the source shape sha, the
  /// record sha and the settings sha, each followed by a line feed. Equal inputs give an equal hash.</summary>
  public static string InputHash(
    string specEntry,
    string sourceShapeSha,
    string recordSha,
    string settingsSha
  ) => Sha256Of($"{specEntry}\n{sourceShapeSha}\n{recordSha}\n{settingsSha}\n");

  private static string Field(JObject o, string key) =>
    o.Value<string>(key)
    ?? throw new JsonException($"provenance row has no \"{key}\"");
}
