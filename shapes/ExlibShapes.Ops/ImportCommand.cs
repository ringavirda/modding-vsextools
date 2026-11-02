using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExpandedLib.Shapes.Core;
using ExpandedLib.Shapes.Generators;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Ops;

/// <summary><c>import DRAWING [--lane DIR --module lathe|shaper|smallflask] [--out DIR]</c>: lists every drawing of the
/// drawing's block, then writes the drawing's record skeleton and a machine file of the lane's casting kinds.</summary>
internal static class ImportCommand {
  public static readonly string[] Values =
  [
    "--lane",
    "--module",
    "--out",
    "--python",
    "--search",
  ];

  public static Outcome Run(Args a, TextWriter output) {
    string drawing = Path.GetFullPath(a.Required(0, "DRAWING"));
    a.AtMost(1);
    string? laneDir = a.Option("--lane");
    string? module = a.Option("--module");
    if ((laneDir == null) != (module == null))
      throw new CliUsageException("--lane and --module go together");
    if (module != null && !LaneTables.Modules.Contains(module))
      throw new CliUsageException(
        $"no such module: {module}; modules: {string.Join(", ", LaneTables.Modules)}"
      );
    string outDir = Path.GetFullPath(a.Option("--out") ?? ".");
    if (!File.Exists(drawing))
      throw new CliUsageException($"no such drawing: {drawing}");

    string searchRoot = a.Option("--search") ?? BlockDrawings.RootOf(drawing);
    List<BlockDrawing> block = BlockDrawings.Find(drawing, searchRoot);
    output.WriteLine($"drawings of block {BlockDrawings.Stem(drawing)}:");
    foreach (BlockDrawing d in block)
      output.WriteLine($"  {d.Kind} {Workspace.Relative(searchRoot, d.Path)}");

    byte[] bytes = File.ReadAllBytes(drawing);
    Shape shape = Reader.Read(
      new System.Text.UTF8Encoding(false).GetString(bytes)
    );
    LaneTables? lane =
      laneDir == null
        ? null
        : LaneTables.Dump(
          a.Option("--python")
            ?? Environment.GetEnvironmentVariable("EXLIB_SHAPES_PYTHON")
            ?? "python3",
          laneDir,
          module!
        );
    string name = Path.GetFileName(drawing);
    string machineName = lane == null ? "" : lane.Module + ".json";
    SkeletonResult sk = Skeleton.Build(
      shape,
      name,
      Provenance.Sha256Of(bytes),
      lane,
      machineName
    );
    Directory.CreateDirectory(outDir);
    string recordPath = Path.Combine(outDir, Workspace.RecordFileOf(name));
    if (File.Exists(recordPath))
      sk = Skeleton.Merge(sk, PartRecord.ReadFile(recordPath));
    sk.Record.WriteFile(recordPath);
    output.WriteLine(
      $"record {Workspace.Relative(outDir, recordPath)}: {sk.Record.Elements.Count} elements, {sk.Record.Features.Count} features"
    );

    int castings = 0;
    if (lane != null) {
      JObject? machine = MachineSkeleton.Build(lane, name);
      if (machine == null)
        output.WriteLine(
          lane.KindsError != null
            ? $"no machine file: {lane.Module} kinds failed: {lane.KindsError}"
            : $"no machine file: {lane.Module} has no casting kinds"
        );
      else {
        castings = ((JArray)machine["castings"]!).Count;
        WriteMachine(outDir, machineName, machine, output);
      }
    }

    string storePath = Path.Combine(outDir, QuestionStore.FileName);
    List<StoredQuestion> kept =
    [
      .. QuestionStore
        .Load(storePath)
        .Where(q => !(q.Part == name && q.Spec == machineName)),
      .. sk.Questions,
    ];
    if (kept.Count > 0 || File.Exists(storePath))
      QuestionStore.Save(storePath, kept);
    foreach (StoredQuestion q in sk.Questions)
      output.WriteLine(
        $"Question: {q.Feature}: {q.Text} [options: {string.Join(", ", q.Options)}; suggested: {q.Suggested}]"
      );
    output.WriteLine($"questions: {sk.Questions.Count}");
    return new Outcome(
      sk.Questions.Count > 0 ? 3 : 0,
      new Dictionary<string, int> {
        ["drawings"] = block.Count,
        ["features"] = sk.Record.Features.Count,
        ["castings"] = castings,
      }
    );
  }

  private static void WriteMachine(
    string outDir,
    string fileName,
    JObject machine,
    TextWriter output
  ) {
    string path = Path.Combine(outDir, fileName);
    var provenance = new Provenance(Path.Combine(outDir, "provenance.jsonl"));
    Ownership.Guard(provenance, path);
    string text = machine.ToString(Newtonsoft.Json.Formatting.Indented) + "\n";
    AtomicFile.WriteAllText(path, text);
    var row = new ProvenanceRow(
      fileName,
      Provenance.Sha256Of(File.ReadAllBytes(path)),
      "import",
      "1",
      Provenance.Sha256Of(text)
    );
    if (provenance.Find(path) != row)
      provenance.Append(row);
    output.WriteLine(
      $"machine file {fileName}: {((JArray)machine["castings"]!).Count} castings"
    );
  }
}
