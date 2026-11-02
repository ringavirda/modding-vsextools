using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>The run log: one JSON line per command in <c>runs.jsonl</c>.</summary>
public static class Timing {
  /// <summary><c>$XDG_STATE_HOME/exlib-shapes/runs.jsonl</c>, <c>~/.local/state</c> standing in for an unset or
  /// empty <c>XDG_STATE_HOME</c>.</summary>
  /// <param name="env">Reads an environment variable.</param>
  public static string DefaultPath(Func<string, string?> env) {
    string? state = env("XDG_STATE_HOME");
    if (string.IsNullOrEmpty(state))
      state = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local",
        "state"
      );
    return Path.Combine(state, "exlib-shapes", "runs.jsonl");
  }

  /// <summary>Appends one line to <paramref name="path"/>: the command, the sha256 of its arguments, the wall time,
  /// the count each check enumerated, the exit code and the UTC time.</summary>
  /// <param name="path">The log; its directory is created.</param>
  /// <param name="command">The verb run.</param>
  /// <param name="args">Its arguments.</param>
  /// <param name="wallMs">Wall time in milliseconds.</param>
  /// <param name="enumerated">Per check id, the count it enumerated, summed over the run.</param>
  /// <param name="exit">The exit code.</param>
  /// <exception cref="IOException">The log cannot be appended to.</exception>
  public static void Append(
    string path,
    string command,
    IReadOnlyList<string> args,
    long wallMs,
    IReadOnlyDictionary<string, int> enumerated,
    int exit
  ) {
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    string line = JsonConvert.SerializeObject(
      new JObject {
        ["time"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["command"] = command,
        ["args"] = Provenance.Sha256Of(string.Join("\0", args)),
        ["wallMs"] = wallMs,
        ["enumerated"] = new JObject(
          enumerated
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new JProperty(kv.Key, kv.Value))
        ),
        ["exit"] = exit,
      },
      Formatting.None
    );
    using var fs = new FileStream(
      path,
      FileMode.Append,
      FileAccess.Write,
      FileShare.ReadWrite
    );
    byte[] bytes = new UTF8Encoding(false).GetBytes(line + "\n");
    fs.Write(bytes, 0, bytes.Length);
  }
}
