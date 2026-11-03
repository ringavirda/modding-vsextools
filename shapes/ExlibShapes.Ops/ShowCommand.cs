using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;
using ExpandedLib.Shapes.Generators;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Ops;

/// <summary><c>show PART</c>: a part's record, gate status and open questions, or a spec file's bill, in under 40 lines.</summary>
internal static class ShowCommand {
  public static readonly string[] Values =
  [
    "--settings",
    "--cache",
    "--source",
    "--root",
  ];
  private const int MaxLines = 39;

  public static Outcome Run(Args a, TextWriter output) {
    string path = Path.GetFullPath(a.Required(0, "PART"));
    a.AtMost(1);
    var lines = new List<string>();
    JObject? spec = null;
    if (
      !path.EndsWith(".part.json", StringComparison.Ordinal)
      && File.Exists(path)
    )
      spec = JObject.Parse(File.ReadAllText(path));
    if (
      spec != null
      && (spec.ContainsKey("machine") || spec.ContainsKey("blank"))
    )
      Bill(spec, lines);
    else
      PartLines(a, Workspace.ShapeFileOf(path), lines);
    if (lines.Count > MaxLines) {
      int more = lines.Count - (MaxLines - 1);
      lines.RemoveRange(MaxLines - 1, more);
      lines.Add($"... {more} more lines");
    }
    foreach (string l in lines)
      output.WriteLine(l);
    return new Outcome(0, new Dictionary<string, int>());
  }

  private static void Bill(JObject spec, List<string> lines) {
    if (spec.ContainsKey("blank")) {
      BlankSpec b = Spec.ReadBlank(spec);
      lines.Add(
        $"blank {b.Blank}: size {string.Join(" x ", b.Size)}, station {b.Station ?? "none"}"
      );
      foreach (WorkedSpec w in b.Worked)
        lines.Add(
          $"  worked {w.Item}: {string.Join(", ", w.Steps.Select(s => s.Step))}"
        );
      return;
    }
    MachineSpec m = Spec.ReadMachine(spec);
    lines.Add(
      $"machine {m.Machine}: source {m.Source.Shape} at {m.Source.Sha}"
    );
    lines.Add($"own castings: {m.Castings.Count}");
    foreach (CastingSpec c in m.Castings)
      lines.Add(
        $"  {c.Item} x{c.Count} ({c.PerPour} a pour), station {c.Station ?? "none"}"
      );
    lines.Add($"blank users: {m.Users.Count}");
    foreach (UserSpec u in m.Users)
      lines.Add($"  {u.Worked.Item} on blank {u.Blank} x{u.Count}");
    if (m.Joined.Count > 0) {
      lines.Add($"joined: {m.Joined.Count}");
      foreach (JoinedSpec j in m.Joined)
        lines.Add($"  {j.Item} from {string.Join(", ", j.From)}");
    }
    lines.Add(
      m.Set == null
        ? "set: none"
        : $"set: {m.Set.Item}, station {m.Set.Station ?? "none"}"
    );
    lines.Add("standard items:");
    foreach (StandardSpec s in m.Standard)
      lines.Add($"  {s.Item} x{s.Count}");
  }

  private static void PartLines(Args a, string shapePath, List<string> lines) {
    string recordPath = Workspace.RecordFileOf(shapePath);
    PartRecord record = PartRecord.ReadFile(recordPath);
    lines.Add(
      $"{Path.GetFileName(shapePath)} [{string.Join(", ", record.Kinds)}]{(record.Owner == null ? "" : ", owner " + record.Owner)}"
    );
    PartCount n = record.Count();
    lines.Add(
      $"parts {n.Parts}, fasteners {n.Fasteners}, rods {n.Rods}; features {record.Features.Count}; steps {string.Join(", ", record.Steps.Select(s => s.Id))}"
    );
    foreach (KeyValuePair<string, int> kv in n.ByStageRole)
      lines.Add($"  {kv.Key}: {kv.Value}");
    lines.Add($"answers {record.Answers.Count}");

    if (record.Kinds.Count > 0 && File.Exists(shapePath)) {
      Settings settings;
      try {
        settings = Workspace.LoadSettings(
          a.Option("--settings"),
          Path.GetDirectoryName(shapePath)!
        );
      } catch (CliUsageException e) {
        lines.Add($"gates: not run ({e.Message})");
        settings = null!;
      }
      if (settings != null)
        try {
          IReadOnlyList<JobResult> r = CheckCommand.Gates(
            [shapePath],
            settings,
            a.Option("--source"),
            a.Option("--cache")
          );
          lines.Add(
            $"gates: {r.Count(x => x.Result.Verdict == Verdict.Pass)} pass, {r.Count(x => x.Result.Verdict == Verdict.Fail)} fail, {r.Count(x => x.Result.Verdict == Verdict.Question)} question"
          );
          foreach (
            JobResult x in r.Where(x => x.Result.Verdict != Verdict.Pass)
          )
            lines.Add(
              $"  {x.Result.Verdict.ToString().ToLowerInvariant()} {x.Result.Check}"
            );
        } catch (Exception e)
            when (e is StaleOutputException or CheckUsageException) {
          lines.Add($"gates: not run ({e.Message.Split('\n')[0]})");
        }
    }

    string? root = a.Option("--root");
    if (
      root == null
      && Workspace.FindUp(
        Path.GetDirectoryName(shapePath)!,
        QuestionStore.FileName
      )
        is { } found
    )
      root = Path.GetDirectoryName(found);
    if (root == null)
      return;
    string part = Workspace.Relative(root, shapePath);
    foreach (
      StoredQuestion q in QuestionStore
        .Load(Path.Combine(root, QuestionStore.FileName))
        .Where(q => q.Part == part)
    )
      lines.Add(
        $"open question {q.Feature}: {q.Text} [options: {string.Join(", ", q.Options)}; suggested: {q.Suggested}]"
      );
  }
}
