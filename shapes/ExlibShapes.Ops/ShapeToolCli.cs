using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;
using ExpandedLib.Shapes.Generators;

namespace ExpandedLib.Shapes.Ops;

/// <summary>The exit code of a verb and what its checks enumerated, for the timing log.</summary>
/// <param name="Exit">0 pass, 1 a gate failed or a run-time error, 2 usage, 3 an open question.</param>
/// <param name="Enumerated">Counts by name; empty when the verb enumerates nothing.</param>
internal sealed record Outcome(
  int Exit,
  IReadOnlyDictionary<string, int> Enumerated
);

/// <summary>The shape tool's verbs <c>gen</c>, <c>check</c>, <c>show</c> and <c>answer</c>.</summary>
public static class ShapeToolCli {
  /// <summary>The verbs <see cref="Run"/> takes.</summary>
  public static IReadOnlyList<string> Verbs { get; } =
  ["gen", "check", "show", "answer"];

  /// <summary>Runs one verb. Exit 0 passes; 1 is a failed gate or a resolved run-time error (a stale output, a bad
  /// drawing, a spec that cannot be made); 2 is usage; 3 is an open question, printed as <c>Question:</c> lines.
  /// One line is appended to the timing log of <paramref name="env"/>'s <c>XDG_STATE_HOME</c>.</summary>
  /// <param name="args">The verb and its arguments.</param>
  /// <param name="output">Where results go.</param>
  /// <param name="error">Where errors go.</param>
  /// <param name="env">Reads an environment variable; null when unset.</param>
  public static int Run(
    string[] args,
    TextWriter output,
    TextWriter error,
    Func<string, string?> env
  ) {
    string verb = args.Length > 0 ? args[0] : "";
    var watch = Stopwatch.StartNew();
    Outcome outcome;
    try {
      outcome = Dispatch(verb, args.Skip(1).ToArray(), output);
    } catch (Exception e) when (e is CliUsageException or CheckUsageException) {
      error.WriteLine(e.Message);
      outcome = new Outcome(2, new Dictionary<string, int>());
    } catch (Exception e)
        when (e
            is StaleOutputException
              or SpecException
              or CastGenException
              or ReconcileException
              or ShapeWriteException
              or SettingsException
              or InvalidOperationException
              or IOException
              or Newtonsoft.Json.JsonException
        ) {
      error.WriteLine(e.Message);
      outcome = new Outcome(1, new Dictionary<string, int>());
    }
    try {
      Timing.Append(
        Timing.DefaultPath(env),
        verb,
        args.Skip(1).ToArray(),
        watch.ElapsedMilliseconds,
        outcome.Enumerated,
        outcome.Exit
      );
    } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
      error.WriteLine($"timing log not written: {e.Message}");
    }
    return outcome.Exit;
  }

  private static Outcome Dispatch(
    string verb,
    string[] rest,
    TextWriter output
  ) =>
    verb switch {
      "gen" => GenCommand.Run(
        Args.Parse(rest, Set(GenCommand.Values), Set()),
        output
      ),
      "check" => CheckCommand.Run(
        Args.Parse(rest, Set(CheckCommand.Values), Set(CheckCommand.Flags)),
        output
      ),
      "show" => ShowCommand.Run(
        Args.Parse(rest, Set(ShowCommand.Values), Set()),
        output
      ),
      "answer" => AnswerCommand.Run(
        Args.Parse(rest, Set(AnswerCommand.Values), Set()),
        output
      ),
      _ => throw new CliUsageException(
        $"no such verb: {verb}; verbs: {string.Join(", ", Verbs)}"
      ),
    };

  private static HashSet<string> Set(params string[] names) => [.. names];
}
