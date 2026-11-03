using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExpandedLib.Shapes.Core;
using ExpandedLib.Shapes.Generators;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Ops;

/// <summary><c>gen SPEC --root DIR</c>: runs CastGen on a blank file or machine file and writes its outputs.</summary>
internal static class GenCommand {
  public static readonly string[] Values =
  [
    "--root",
    "--runtime",
    "--shapes",
    "--settings",
    "--domain",
  ];

  public static Outcome Run(Args a, TextWriter output) {
    string specPath = Path.GetFullPath(a.Required(0, "SPEC"));
    a.AtMost(1);
    string root = Path.GetFullPath(
      a.Option("--root") ?? throw new CliUsageException("gen needs --root DIR")
    );
    string shapes = Path.GetFullPath(a.Option("--shapes") ?? root);
    string? runtime = a.Option("--runtime");
    Settings settings = Workspace.LoadSettings(
      a.Option("--settings"),
      Path.GetDirectoryName(specPath)!
    );
    var gen = new CastGen();
    GenResult result = Workspace.RunGen(specPath, shapes, settings);

    var provenance = new Provenance(Path.Combine(root, "provenance.jsonl"));
    foreach (GenOutput o in result.Outputs) {
      string shapePath = Path.Combine(root, "items", o.Name + "-proposed.json");
      Ownership.Guard(provenance, shapePath);
      CanonicalWriter.WriteFile(o.Shape, shapePath);
      byte[] bytes = File.ReadAllBytes(shapePath);
      string sha = Provenance.Sha256Of(bytes);
      o.Record.Shape = o.Name + "-proposed.json";
      o.Record.ShapeSha = sha;
      o.Record.Carried["gen"] = new JObject {
        ["spec"] = Workspace.Relative(root, specPath),
        ["shapes"] = Workspace.Relative(root, shapes),
      };
      o.Record.WriteFile(Workspace.RecordFileOf(shapePath));
      var row = new ProvenanceRow(
        Workspace.Relative(root, shapePath),
        sha,
        gen.Id,
        gen.Version,
        o.InputHash
      );
      if (provenance.Find(shapePath) != row)
        provenance.Append(row);
      if (runtime != null)
        RuntimeWriter.WriteFile(
          Path.Combine(runtime, o.Name + ".json"),
          o.Shape,
          settings,
          a.Option("--domain") ?? "iiex"
        );
      output.WriteLine(
        $"{o.Name} [{string.Join(", ", o.Kinds)}] {Workspace.Relative(root, shapePath)}"
      );
    }
    foreach (GenInput input in result.Inputs) {
      string inputPath = Path.Combine(root, "inputs", input.Name + ".json");
      CanonicalWriter.WriteFile(input.Shape, inputPath);
      input.Record.ShapeSha = Provenance.Sha256Of(File.ReadAllBytes(inputPath));
      input.Record.WriteFile(Workspace.RecordFileOf(inputPath));
    }
    foreach (string redraw in result.Redraws)
      output.WriteLine($"redraw: {redraw}");

    string specName = Workspace.Relative(root, specPath);
    string storePath = Path.Combine(root, QuestionStore.FileName);
    List<StoredQuestion> kept =
    [
      .. QuestionStore.Load(storePath).Where(q => q.Spec != specName),
      .. result.Questions.Select(q => new StoredQuestion(
        specName,
        q.Part,
        q.Feature,
        q.Text,
        q.Options,
        q.Suggested,
        q.FeatureSha
      )),
    ];
    if (kept.Count > 0 || File.Exists(storePath))
      QuestionStore.Save(storePath, kept);
    foreach (Question q in result.Questions)
      output.WriteLine(
        $"Question: {q.Feature}: {q.Text} [options: {string.Join(", ", q.Options)}; suggested: {q.Suggested}]"
      );
    return new Outcome(
      result.Questions.Count > 0 ? 3 : 0,
      new Dictionary<string, int> { ["outputs"] = result.Outputs.Count }
    );
  }
}
