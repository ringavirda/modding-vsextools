using System;
using System.Collections.Generic;

namespace ExpandedLib.Shapes.Ops;

/// <summary>Malformed command-line usage; the command exits 2.</summary>
public sealed class CliUsageException(string message) : Exception(message);

/// <summary>A verb's arguments: positionals, <c>--name VALUE</c> or <c>--name=VALUE</c> options and bare flags.</summary>
internal sealed class Args {
  private readonly Dictionary<string, string> options = [];
  private readonly HashSet<string> flags = [];

  /// <summary>The arguments that are not options, in order.</summary>
  public List<string> Positional { get; } = [];

  /// <summary>Splits <paramref name="argv"/>.</summary>
  /// <param name="argv">The arguments after the verb.</param>
  /// <param name="valueNames">The options that take a value.</param>
  /// <param name="flagNames">The options that take none.</param>
  /// <exception cref="CliUsageException">An option is unknown, repeated or lacks its value.</exception>
  public static Args Parse(
    IReadOnlyList<string> argv,
    ISet<string> valueNames,
    ISet<string> flagNames
  ) {
    var a = new Args();
    for (int i = 0; i < argv.Count; i++) {
      string arg = argv[i];
      if (!arg.StartsWith("--", StringComparison.Ordinal)) {
        a.Positional.Add(arg);
        continue;
      }
      int eq = arg.IndexOf('=');
      string name = eq < 0 ? arg : arg[..eq];
      if (flagNames.Contains(name)) {
        a.flags.Add(name);
        continue;
      }
      if (!valueNames.Contains(name))
        throw new CliUsageException($"unknown option {name}");
      string value;
      if (eq >= 0)
        value = arg[(eq + 1)..];
      else if (i + 1 < argv.Count)
        value = argv[++i];
      else
        throw new CliUsageException($"{name} needs a value");
      if (!a.options.TryAdd(name, value))
        throw new CliUsageException($"{name} given twice");
    }
    return a;
  }

  /// <summary>The value of option <paramref name="name"/>, null when absent.</summary>
  public string? Option(string name) => options.GetValueOrDefault(name);

  /// <summary>Whether flag <paramref name="name"/> was given.</summary>
  public bool Flag(string name) => flags.Contains(name);

  /// <summary>The positional at <paramref name="index"/>.</summary>
  /// <exception cref="CliUsageException">There are fewer positionals.</exception>
  public string Required(int index, string what) =>
    index < Positional.Count
      ? Positional[index]
      : throw new CliUsageException($"missing {what}");

  /// <summary>Fails when more than <paramref name="count"/> positionals were given.</summary>
  /// <exception cref="CliUsageException">There are more.</exception>
  public void AtMost(int count) {
    if (Positional.Count > count)
      throw new CliUsageException($"unexpected argument {Positional[count]}");
  }
}
