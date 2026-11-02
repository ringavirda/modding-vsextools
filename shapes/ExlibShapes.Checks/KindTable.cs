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
      "facecheck",
      "flushcheck",
      "octcheck",
      "uvwrap",
      "octregular",
    ],
    ["pattern"] = ["role", "draw", "split", "facecheck"],
    ["worked"] = ["role", "split", "facecheck", "blankmatch"],
    ["filling"] =
    [
      "impression",
      "draw",
      "flood",
      "capacity",
      "fit",
      "runner",
      "split",
      "facecheck",
      "pieces",
      "feeding",
      "pit",
      "parting",
      "corefit",
    ],
    ["core"] = ["facecheck", "corefit"],
    ["corebox"] = ["facecheck", "corefit"],
    ["machine"] = ["facecheck"],
    ["item"] = ["facecheck"],
    ["fitting"] = ["facecheck"],
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
