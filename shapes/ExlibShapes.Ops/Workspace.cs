using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ExpandedLib.Shapes.Core;
using ExpandedLib.Shapes.Generators;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Ops;

/// <summary>Where the commands find their files.</summary>
internal static class Workspace {
  /// <summary>The first <paramref name="names"/> file found in <paramref name="startDir"/> or an ancestor; null when none.</summary>
  public static string? FindUp(string startDir, params string[] names) {
    for (
      string? d = Path.GetFullPath(startDir);
      d != null;
      d = Path.GetDirectoryName(d)
    )
      foreach (string name in names) {
        string path = Path.Combine(d, name);
        if (File.Exists(path))
          return path;
      }
    return null;
  }

  /// <summary>The settings named by <paramref name="option"/>, else <c>shapetool.json</c> or <c>workbench/shapetool.json</c>
  /// of <paramref name="startDir"/> or an ancestor.</summary>
  /// <exception cref="CliUsageException">None is named or found.</exception>
  public static Settings LoadSettings(string? option, string startDir) {
    string path =
      option
      ?? FindUp(
        startDir,
        "shapetool.json",
        Path.Combine("workbench", "shapetool.json")
      )
      ?? throw new CliUsageException(
        "no settings: pass --settings FILE or keep shapetool.json above the input"
      );
    return Settings.ReadFile(path);
  }

  /// <summary>The shape path of a shape file or its record path.</summary>
  public static string ShapeFileOf(string path) =>
    path.EndsWith(".part.json", StringComparison.Ordinal)
      ? path[..^".part.json".Length] + ".json"
      : path;

  /// <summary>The record path beside the shape file <paramref name="shapePath"/>.</summary>
  public static string RecordFileOf(string shapePath) =>
    ShapeFileOf(shapePath)[..^".json".Length] + ".part.json";

  /// <summary><paramref name="path"/> relative to <paramref name="dir"/>, with forward slashes.</summary>
  public static string Relative(string dir, string path) =>
    Path.GetRelativePath(dir, Path.GetFullPath(path)).Replace('\\', '/');
}

/// <summary>Reads drawings at a pinned commit with <c>git show</c> and their records from the working tree beside them.</summary>
/// <param name="dir">The directory the spec's shape paths are relative to; it lies inside the git checkout.</param>
internal sealed class GitSources(string dir) {
  private readonly Dictionary<(string, string), ResolvedSource> cache = [];

  /// <summary>The drawing <paramref name="shape"/> at <paramref name="sha"/> with its record.</summary>
  /// <exception cref="InvalidOperationException">git cannot read the drawing at the pin.</exception>
  /// <exception cref="ReconcileException">The record is for another drawing than the pinned bytes.</exception>
  public ResolvedSource Resolve(string shape, string sha) {
    if (cache.TryGetValue((shape, sha), out ResolvedSource? hit))
      return hit;
    byte[] bytes = Show(sha, shape);
    string shapeSha = Provenance.Sha256Of(bytes);
    Shape drawing = Reader.Read(new UTF8Encoding(false).GetString(bytes));
    string recordPath = Path.Combine(dir, Workspace.RecordFileOf(shape));
    PartRecord record;
    if (File.Exists(recordPath)) {
      record = PartRecord.ReadFile(recordPath);
      if (!Reconcile.Check(record, drawing, shapeSha))
        throw new ReconcileException(
          $"{shape}: its record is for another drawing (shapeSha {record.ShapeSha}); the pinned bytes are {shapeSha}",
          [],
          []
        );
    } else
      record = new PartRecord { Shape = shape, ShapeSha = shapeSha };
    return cache[(shape, sha)] = new ResolvedSource(
      drawing,
      record,
      shapeSha,
      shape
    );
  }

  private byte[] Show(string sha, string shape) {
    var psi = new ProcessStartInfo("git") {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
    };
    foreach (string a in new[] { "-C", dir, "show", $"{sha}:./{shape}" })
      psi.ArgumentList.Add(a);
    using Process p =
      Process.Start(psi)
      ?? throw new InvalidOperationException("git not started");
    Task<string> err = p.StandardError.ReadToEndAsync();
    using var ms = new MemoryStream();
    p.StandardOutput.BaseStream.CopyTo(ms);
    p.WaitForExit();
    if (p.ExitCode != 0)
      throw new InvalidOperationException(
        $"cannot read {shape} at {sha}: {err.Result.Trim()}"
      );
    return ms.ToArray();
  }
}

/// <summary>One open question of a <c>gen</c> run, kept in <c>questions.json</c> under the output root until answered.</summary>
internal sealed record StoredQuestion(
  string Spec,
  string Part,
  string Feature,
  string Text,
  IReadOnlyList<string> Options,
  string Suggested,
  string FeatureSha
);

/// <summary>The <c>questions.json</c> file of an output root.</summary>
internal static class QuestionStore {
  public const string FileName = "questions.json";

  public static List<StoredQuestion> Load(string path) {
    if (!File.Exists(path))
      return [];
    return
    [
      .. JArray
        .Parse(File.ReadAllText(path))
        .Select(t => new StoredQuestion(
          (string)t["spec"]!,
          (string)t["part"]!,
          (string)t["feature"]!,
          (string)t["text"]!,
          [.. t["options"]!.Select(o => (string)o!)],
          (string)t["suggested"]!,
          (string)t["featureSha"]!
        )),
    ];
  }

  public static void Save(string path, IEnumerable<StoredQuestion> questions) =>
    AtomicFile.WriteAllText(
      path,
      new JArray(
        questions.Select(q => new JObject {
          ["spec"] = q.Spec,
          ["part"] = q.Part,
          ["feature"] = q.Feature,
          ["text"] = q.Text,
          ["options"] = new JArray(q.Options),
          ["suggested"] = q.Suggested,
          ["featureSha"] = q.FeatureSha,
        })
      ).ToString(Newtonsoft.Json.Formatting.Indented) + "\n"
    );
}
