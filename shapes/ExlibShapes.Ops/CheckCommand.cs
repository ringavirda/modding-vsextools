using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;
using ExpandedLib.Shapes.Generators;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Ops;

/// <summary><c>check PATH...</c>: runs the gates of each shape's kinds through the cache and the shared slots.</summary>
internal static class CheckCommand {
  public static readonly string[] Values =
  [
    "--settings",
    "--cache",
    "--source",
  ];
  public static readonly string[] Flags = ["--catalogue", "--mutate"];

  public static Outcome Run(Args a, TextWriter output) {
    if (a.Flag("--mutate"))
      throw new CliUsageException(
        "check --mutate is not available: operator sampling is milestone M3"
      );
    if (a.Flag("--catalogue")) {
      foreach (string line in Catalogue.Lines())
        output.WriteLine(line);
      return new Outcome(0, new Dictionary<string, int>());
    }
    if (a.Positional.Count == 0)
      throw new CliUsageException("check needs PATH...");
    List<string> files = [.. a.Positional.SelectMany(Expand)];
    if (files.Count == 0)
      throw new CliUsageException(
        "no shape with a part record in the given paths"
      );
    Settings settings = Workspace.LoadSettings(
      a.Option("--settings"),
      Path.GetDirectoryName(Path.GetFullPath(files[0]))!
    );
    return Report(
      Gates(files, settings, a.Option("--source"), a.Option("--cache")),
      output
    );
  }

  public static Outcome Report(
    IReadOnlyList<JobResult> results,
    TextWriter output
  ) {
    var enumerated = new Dictionary<string, int>();
    foreach (JobResult r in results) {
      CheckResult c = r.Result;
      enumerated[c.Check] =
        enumerated.GetValueOrDefault(c.Check) + c.Enumerated;
      output.WriteLine(
        $"{c.Verdict.ToString().ToUpperInvariant()} {c.Check} {r.Job.Output} (enumerated {c.Enumerated}{(r.Cached ? ", cached" : "")})"
      );
      if (c.Verdict != Verdict.Pass)
        foreach (string line in c.Lines)
          output.WriteLine("  " + line);
    }
    int fail = results.Count(r => r.Result.Verdict == Verdict.Fail);
    int ask = results.Count(r => r.Result.Verdict == Verdict.Question);
    foreach (
      JobResult r in results.Where(r => r.Result.Verdict == Verdict.Question)
    )
      foreach (string line in r.Result.Lines)
        output.WriteLine($"Question: {r.Result.Check}: {line}");
    output.WriteLine(
      $"{results.Count} gates: {results.Count - fail - ask} pass, {fail} fail, {ask} question"
    );
    return new Outcome(
      fail > 0 ? 1
        : ask > 0 ? 3
        : 0,
      enumerated
    );
  }

  /// <summary>The gates of every shape in <paramref name="files"/>, run in parallel. A gate takes its inputs from the
  /// record's slots (<c>source</c>, <c>blankform</c>, <c>draw</c>, <c>core.source</c>) and the generator's
  /// <c>inputs</c> folder beside <c>provenance.jsonl</c>; <paramref name="sourcePath"/> replaces the source drawing.</summary>
  /// <exception cref="StaleOutputException">An output was made by another generator version.</exception>
  /// <exception cref="CheckUsageException">A gate lacks an input.</exception>
  /// <exception cref="CliUsageException">A record names an input that is not there.</exception>
  public static IReadOnlyList<JobResult> Gates(
    IReadOnlyList<string> files,
    Settings settings,
    string? sourcePath,
    string? cacheDir
  ) {
    Profile profile = Selection.ParseProfile(settings.LintProfile);
    string version = new CastGen().Version;
    var jobs = new List<CheckJob>();
    foreach (string file in files) {
      string full = Path.GetFullPath(file);
      CheckInput input = InputOf(full, settings, sourcePath);
      Func<string?>? stale = StaleOf(full, version);
      foreach (ICheck c in Selection.For(input.Record!.Kinds, profile))
        if (Selection.Applies(c, input))
          jobs.Add(
            new CheckJob(
              full,
              c,
              c.Id switch {
                "blankmatch" => PlacedFor(input, full),
                "draw" => DrawnFor(input, full),
                _ => input,
              },
              stale
            )
          );
    }
    var cache = new ResultCache(ResultCache.DefaultDir(files[0], cacheDir));
    return new CheckRunner(Slots.Shared(), cache).Run(jobs);
  }

  private static IEnumerable<string> Expand(string path) {
    if (!Directory.Exists(path))
      return [path];
    return Directory
      .EnumerateFiles(path, "*.json", SearchOption.AllDirectories)
      .Where(f =>
        !Path.GetRelativePath(path, f)
          .StartsWith(
            "inputs" + Path.DirectorySeparatorChar,
            StringComparison.Ordinal
          )
        && !f.EndsWith(".part.json", StringComparison.Ordinal)
        && File.Exists(Workspace.RecordFileOf(f))
      )
      .Order(StringComparer.Ordinal);
  }

  private static Func<string?>? StaleOf(string file, string version) {
    string? file0 = Workspace.FindUp(
      Path.GetDirectoryName(file)!,
      "provenance.jsonl"
    );
    if (file0 == null)
      return null;
    var provenance = new Provenance(file0);
    return () =>
      Staleness.Reason(
        provenance,
        file,
        "castgen",
        version,
        provenance.Find(file)?.InputHash ?? ""
      );
  }

  private static CheckInput InputOf(
    string file,
    Settings settings,
    string? sourcePath
  ) {
    if (!File.Exists(Workspace.RecordFileOf(file)))
      throw new CliUsageException(
        $"{file}: no part record beside it ({Path.GetFileName(Workspace.RecordFileOf(file))})"
      );
    PartRecord record = PartRecord.ReadFile(Workspace.RecordFileOf(file));
    if (record.Kinds.Count == 0)
      throw new CliUsageException($"{file}: its record names no kind");
    string dir = Path.GetDirectoryName(file)!;
    Shape? Sibling(string? item) {
      string? path = SiblingPath(dir, item);
      return path == null ? null : Reader.ReadFile(path);
    }
    (Shape Shape, PartRecord Record)? Input(string? name) =>
      ReadInput(file, name);
    Shape? cast = null,
      pattern = null,
      pair = null;
    List<Shape> cores = [];
    List<PartRecord> coreRecords = [];
    if (record.Carried["pour"] is JObject pour) {
      cast = Sibling((string?)pour["cast"]);
      pattern = Sibling((string?)pour["pattern"]);
      pair = Sibling((string?)pour["pair"]);
      foreach (JToken c in pour["cores"] as JArray ?? [])
        if (SiblingPath(dir, (string?)c["item"]) is { } p) {
          cores.Add(Reader.ReadFile(p));
          coreRecords.Add(PartRecord.ReadFile(Workspace.RecordFileOf(p)));
        }
    }
    if (
      record.Carried["corebox"] is JObject box
      && SiblingPath(dir, (string?)box["core"]) is { } corePath
    ) {
      cores.Add(Reader.ReadFile(corePath));
      coreRecords.Add(PartRecord.ReadFile(Workspace.RecordFileOf(corePath)));
    }

    Shape? source = null;
    PartRecord? sourceRecord = null;
    double allowance = new CheckInput(null).Allowance;
    IReadOnlyList<string>? cutFaces = null;
    if (record.Carried["source"] is JObject src) {
      if (Input((string?)src["input"]) is { } got)
        (source, sourceRecord) = got;
      allowance = (double?)src["allowance"] ?? allowance;
    } else if (record.Carried["blankform"] is JObject form) {
      if (Input((string?)form["input"]) is { } got)
        (source, sourceRecord) = got;
      cutFaces =
      [
        .. (form["cutFeatures"] as JArray ?? []).Select(t => (string)t!),
      ];
    } else if (
        record.Carried["core"] is JObject core
        && Input((string?)core["source"]) is { } fed
      )
      (source, sourceRecord) = fed;
    if (sourcePath != null) {
      source = Reader.ReadFile(sourcePath);
      sourceRecord = File.Exists(Workspace.RecordFileOf(sourcePath))
        ? PartRecord.ReadFile(Workspace.RecordFileOf(sourcePath))
        : null;
    }
    double? parting = (double?)(record.Carried["draw"] as JObject)?["parting"];
    return new CheckInput(
      Reader.ReadFile(file),
      Record: record,
      Settings: settings,
      Source: source,
      SourceRecord: sourceRecord,
      Allowance: allowance,
      Parting: parting,
      CutFaces: cutFaces,
      Cast: cast,
      Pattern: pattern,
      Pair: pair,
      Cores: cores.Count > 0 ? cores : null,
      CoreRecords: coreRecords.Count > 0 ? coreRecords : null
    );
  }

  private static (Shape Shape, PartRecord Record)? ReadInput(
    string file,
    string? name
  ) {
    if (string.IsNullOrEmpty(name))
      return null;
    string dir = Path.GetDirectoryName(file)!;
    string? provenance = Workspace.FindUp(dir, "provenance.jsonl");
    string path = Path.Combine(
      provenance == null ? dir : Path.GetDirectoryName(provenance)!,
      "inputs",
      name + ".json"
    );
    if (!File.Exists(path) || !File.Exists(Workspace.RecordFileOf(path)))
      throw new CliUsageException(
        $"{file}: its input {name} is not in {Path.GetDirectoryName(path)}; run gen again"
      );
    return (
      Reader.ReadFile(path),
      PartRecord.ReadFile(Workspace.RecordFileOf(path))
    );
  }

  private static CheckInput PlacedFor(CheckInput input, string file) =>
    ReadInput(
      file,
      (string?)(input.Record!.Carried["blankform"] as JObject)?["placed"]
    )
      is { } placed
      ? input with {
        Shape = placed.Shape,
        Record = placed.Record,
      }
      : input;

  private static CheckInput DrawnFor(CheckInput input, string file) =>
    ReadInput(
      file,
      (string?)(input.Record!.Carried["draw"] as JObject)?["input"]
    )
      is { } drawn
      ? input with {
        Shape = drawn.Shape,
      }
      : input;

  private static string? SiblingPath(string dir, string? item) {
    if (string.IsNullOrEmpty(item))
      return null;
    foreach (string name in new[] { item + "-proposed.json", item + ".json" }) {
      string path = Path.Combine(dir, name);
      if (File.Exists(path))
        return path;
    }
    throw new CliUsageException($"{item}: no such shape beside the output");
  }
}
