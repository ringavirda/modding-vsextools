using System;
using System.Collections.Generic;
using System.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>Which checks run for a part: those its kinds name, under the lint profile in force.</summary>
public static class Selection {
  /// <summary>Every check the tool holds.</summary>
  public static IReadOnlyList<ICheck> All { get; } =
  [new Facecheck(), new SizesCheck()];

  /// <summary>Whether <paramref name="check"/> runs under <paramref name="profile"/>: structural checks always,
  /// period checks only under <see cref="Profile.ExpandedPeriod"/>.</summary>
  public static bool Runs(ICheck check, Profile profile) =>
    check.Profile == Profile.Structural || profile == Profile.ExpandedPeriod;

  /// <summary>Parses a settings lint profile name.</summary>
  /// <exception cref="ArgumentException">The name is neither <c>structural</c> nor <c>expanded-period</c>.</exception>
  public static Profile ParseProfile(string name) =>
    name switch {
      "structural" => Profile.Structural,
      "expanded-period" => Profile.ExpandedPeriod,
      _ => throw new ArgumentException($"unknown lint profile '{name}'"),
    };

  /// <summary>The checks of <paramref name="kinds"/>' gates in <paramref name="candidates"/> that run under
  /// <paramref name="profile"/>, in the table's order.</summary>
  /// <exception cref="ArgumentException">A kind is unknown, or a gate id has no check in the candidates.</exception>
  public static IReadOnlyList<ICheck> For(
    IEnumerable<string> kinds,
    Profile profile,
    IReadOnlyList<ICheck>? candidates = null
  ) {
    candidates ??= All;
    return
    [
      .. KindTable
        .Gates(kinds)
        .Select(id =>
          candidates.FirstOrDefault(c => c.Id == id)
          ?? throw new ArgumentException($"no check '{id}'")
        )
        .Where(c => Runs(c, profile)),
    ];
  }
}
