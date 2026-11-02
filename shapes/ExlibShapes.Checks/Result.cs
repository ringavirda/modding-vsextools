using System;
using System.Collections.Generic;

namespace ExpandedLib.Shapes.Checks;

/// <summary>A check's verdict. There is no third state: a baseline is data in the part record, never a
/// "known failing" result.</summary>
public enum Verdict {
  /// <summary>Everything enumerated held; exit 0.</summary>
  Pass,

  /// <summary>Something enumerated broke, or nothing was enumerated; exit 1.</summary>
  Fail,

  /// <summary>The check needs a ruling it does not have; exit 3.</summary>
  Question,
}

/// <summary>What a check enumerated and found.</summary>
public sealed class CheckResult {
  /// <summary>The check's id.</summary>
  public string Check { get; }

  /// <summary>The verdict.</summary>
  public Verdict Verdict { get; }

  /// <summary>How many things the check enumerated.</summary>
  public int Enumerated { get; }

  /// <summary>The report lines, faults first and the enumerated count last.</summary>
  public IReadOnlyList<string> Lines { get; }

  private CheckResult(
    string check,
    Verdict verdict,
    int enumerated,
    IReadOnlyList<string> lines
  ) {
    Check = check;
    Verdict = verdict;
    Enumerated = enumerated;
    Lines = lines;
  }

  /// <summary>The process exit code: 0 pass, 1 fail, 3 question.</summary>
  public int ExitCode =>
    Verdict switch {
      Verdict.Pass => 0,
      Verdict.Fail => 1,
      _ => 3,
    };

  /// <summary>A result for a check that enumerated <paramref name="enumerated"/> things and found
  /// <paramref name="faults"/> of them wrong. Enumerating nothing fails whatever else was found.</summary>
  /// <param name="check">The check's id.</param>
  /// <param name="enumerated">The count of things enumerated.</param>
  /// <param name="faults">Whether any enumerated thing broke the check's rule.</param>
  /// <param name="lines">The report lines.</param>
  public static CheckResult Of(
    string check,
    int enumerated,
    bool faults,
    IReadOnlyList<string> lines
  ) {
    if (enumerated <= 0)
      return new CheckResult(
        check,
        Verdict.Fail,
        0,
        [.. lines, $"{check}: enumerated 0, nothing was checked"]
      );
    return new CheckResult(
      check,
      faults ? Verdict.Fail : Verdict.Pass,
      enumerated,
      lines
    );
  }

  /// <summary>A result as it was recorded, whatever its verdict.</summary>
  internal static CheckResult Restore(
    string check,
    Verdict verdict,
    int enumerated,
    IReadOnlyList<string> lines
  ) => new(check, verdict, enumerated, lines);

  /// <summary>A result that asks for a ruling.</summary>
  public static CheckResult Ask(
    string check,
    int enumerated,
    IReadOnlyList<string> lines
  ) => new(check, Verdict.Question, enumerated, lines);
}

/// <summary>The input is not one the check can read: a missing shape, a spec row without a key, an axis
/// that is not x, y or z. Exit 2.</summary>
public sealed class CheckUsageException(string message) : Exception(message) {
  /// <summary>The process exit code for a usage error.</summary>
  public const int ExitCode = 2;
}

/// <summary>One finding of a lint.</summary>
/// <param name="Level"><c>error</c> or <c>warn</c>.</param>
/// <param name="Code">The lint's name.</param>
/// <param name="Kind">A short tag within the lint.</param>
/// <param name="Path">The element path, empty when the finding is not about one element.</param>
/// <param name="Message">The finding in words.</param>
public sealed record Finding(
  string Level,
  string Code,
  string Kind,
  string Path,
  string Message
);
