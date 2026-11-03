using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>Raised when outputs were made by another version of their generator, or from other inputs, than the
/// ones now running; nothing is checked until they are regenerated. Exit 1.</summary>
public sealed class StaleOutputException(IReadOnlyList<string> reasons)
  : Exception("stale outputs, regenerate them:\n" + string.Join("\n", reasons)) {
  /// <summary>The process exit code.</summary>
  public const int ExitCode = 1;

  /// <summary>One line per stale output.</summary>
  public IReadOnlyList<string> Reasons { get; } = reasons;
}

/// <summary>Whether a generated output still matches the generator and inputs that would make it now.</summary>
public static class Staleness {
  /// <summary>Why the output at <paramref name="path"/> is stale, null when it is fresh. An output with no row, or
  /// a row of another generator, is not this generator's to judge and is fresh.</summary>
  /// <param name="provenance">The provenance rows of the output's tree.</param>
  /// <param name="path">The output, absolute or relative to the provenance file's directory.</param>
  /// <param name="generator">The running generator's id.</param>
  /// <param name="version">The running generator's version.</param>
  /// <param name="inputHash">The input hash the generator computes for this output now.</param>
  public static string? Reason(
    Provenance provenance,
    string path,
    string generator,
    string version,
    string inputHash
  ) {
    ProvenanceRow? row = provenance.Find(path);
    if (row == null || row.Generator != generator)
      return null;
    if (row.Version != version)
      return $"{row.Path}: made by {generator} {Short(row.Version)}, now {Short(version)}";
    if (row.InputHash != inputHash)
      return $"{row.Path}: its inputs changed since {generator} made it";
    return null;
  }

  /// <summary>Compact JSON with every object's keys in ordinal order.</summary>
  public static string Canonical(JToken token) =>
    JsonConvert.SerializeObject(Sorted(token), Formatting.None);

  private static string Short(string version) =>
    version.Length > 8 ? version[..8] : version;

  private static JToken Sorted(JToken t) =>
    t switch {
      JObject o => new JObject(
        o.Properties()
          .OrderBy(p => p.Name, StringComparer.Ordinal)
          .Select(p => new JProperty(p.Name, Sorted(p.Value)))
      ),
      JArray a => new JArray(a.Select(Sorted)),
      _ => t.DeepClone(),
    };
}
