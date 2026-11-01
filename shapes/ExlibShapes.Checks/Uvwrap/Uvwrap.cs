using System;
using System.Collections.Generic;
using System.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Whether the uv of every round run of boxes runs on round it, so a texture reads as one unbroken surface. A round
/// run is six or more sibling boxes whose centres circle one line and turn with their place on it, a fan of bars
/// turned about one pivot, an octagon or a plate ring. One line per run: <c>PARENT  N members  KEYS  PASS|FAIL</c>
/// and, on a failure, the first break. Nested runs are all judged unless the input asks for the directed verdict.
/// </summary>
public sealed class Uvwrap : ICheck {
  /// <inheritdoc />
  public string Id => "uvwrap";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    var tree = new Tree(input.Shape);
    var rest = new Frames(input.Shape);
    Func<string, bool> isWood = k =>
      (input.WoodKeys ?? Facecheck.LegacyWood).Contains(k);
    var lines = new List<string>();
    int total = 0,
      failed = 0;
    foreach (RoundRun run in RoundRuns.Find(tree, rest, !input.Directed)) {
      total++;
      (bool ok, string text) = input.Directed
        ? RoundRuns.VerdictDirected(run.Faces, run.Axis, input.Tex, isWood)
        : RoundRuns.Verdict(run.Faces, input.Tex);
      if (!ok)
        failed++;
      string keys = string.Join(
        ",",
        run.Faces.Select(f => "#" + f.Quad.Texture)
          .Distinct()
          .Order(StringComparer.Ordinal)
      );
      lines.Add(
        $"{(run.Parent == "" ? "/" : run.Parent)}  {run.Members.Count} members  {(keys == "" ? "-" : keys)}  {(ok ? "PASS" : "FAIL")}"
          + (text == "" ? "" : "  " + text)
      );
    }
    lines.Add($"{total} round runs; {failed} fail");
    return CheckResult.Of(Id, total, failed > 0, lines);
  }
}
