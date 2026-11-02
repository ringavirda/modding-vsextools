using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Ops;

/// <summary><c>answer PART FEATURE OPTION</c>: stores the answer to an open question in the part's record.</summary>
internal static class AnswerCommand {
  public static readonly string[] Values = ["--root", "--shapes", "--by"];

  public static Outcome Run(Args a, TextWriter output) {
    string part = a.Required(0, "PART");
    string feature = a.Required(1, "FEATURE");
    string option = a.Required(2, "OPTION");
    a.AtMost(3);
    string root = Path.GetFullPath(
      a.Option("--root")
        ?? Path.GetDirectoryName(
          Workspace.FindUp(
            Path.GetDirectoryName(Path.GetFullPath(part))!,
            QuestionStore.FileName
          ) ?? throw new CliUsageException("answer needs --root DIR")
        )!
    );
    string shapes = Path.GetFullPath(a.Option("--shapes") ?? root);
    string storePath = Path.Combine(root, QuestionStore.FileName);
    string partName = Workspace.Relative(
      shapes,
      Workspace.ShapeFileOf(
        Path.IsPathRooted(part) ? part : Path.Combine(shapes, part)
      )
    );
    List<StoredQuestion> all = QuestionStore.Load(storePath);
    StoredQuestion q =
      all.FirstOrDefault(x => x.Part == partName && x.Feature == feature)
      ?? throw new InvalidOperationException(
        $"no open question \"{feature}\" for {partName}; run gen to ask it"
      );
    if (!q.Options.Contains(option))
      throw new CliUsageException(
        $"\"{option}\" is not one of: {string.Join(", ", q.Options)}"
      );
    string recordPath = Path.Combine(shapes, Workspace.RecordFileOf(partName));
    PartRecord record = PartRecord.ReadFile(recordPath);
    record.Answers.RemoveAll(x => x.Feature == feature);
    record.Answers.Add(
      new Answer {
        Feature = feature,
        Question = q.Text,
        Text = option,
        By = a.Option("--by") ?? "fallen",
        Date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        FeatureSha = q.FeatureSha,
      }
    );
    record.WriteFile(recordPath);
    QuestionStore.Save(storePath, all.Where(x => x != q));
    output.WriteLine($"answered {feature} for {partName}: {option}");
    return new Outcome(0, new Dictionary<string, int>());
  }
}
