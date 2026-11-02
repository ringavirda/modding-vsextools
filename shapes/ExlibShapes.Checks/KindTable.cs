using System;
using System.Collections.Generic;
using System.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>The kinds a part declares and the gates each kind gets. A row lists the gates the kind can have;
/// <see cref="Selection.Applies"/> drops the ones the part gives them nothing to read.</summary>
public static class KindTable {
  /// <summary>Every kind a part may declare.</summary>
  public static readonly IReadOnlyList<string> Kinds =
  [
    "casting",
    "pattern",
    "worked",
    "filling",
    "core",
    "corebox",
    "machine",
    "item",
    "fitting",
  ];

  private static readonly Dictionary<string, string[]> Rows = new() {
    ["casting"] =
    [
      "castonly",
      "allowance",
      "split",
      "role",
      "uvwrap",
      "octregular",
    ],
    ["pattern"] = ["role", "draw"],
    ["worked"] = ["role", "blankmatch"],
    ["filling"] =
    [
      "impression",
      "flood",
      "capacity",
      "fit",
      "runner",
      "pieces",
      "feeding",
      "pit",
      "parting",
    ],
    ["core"] = [],
    ["corebox"] = [],
    ["machine"] = [],
    ["item"] = [],
    ["fitting"] = [],
  };

  /// <summary>The gate ids of one kind, in run order.</summary>
  /// <exception cref="ArgumentException">The kind is not one of <see cref="Kinds"/>.</exception>
  public static IReadOnlyList<string> Gates(string kind) =>
    Rows.TryGetValue(kind, out string[]? gates)
      ? gates
      : throw new ArgumentException(
        $"unknown kind '{kind}'; kinds: {string.Join(", ", Kinds)}"
      );

  /// <summary>The gate ids of every kind in <paramref name="kinds"/>, each once, in first-seen order.</summary>
  /// <exception cref="ArgumentException">A kind is not one of <see cref="Kinds"/>.</exception>
  public static IReadOnlyList<string> Gates(IEnumerable<string> kinds) =>
    [.. kinds.SelectMany(Gates).Distinct()];
}
