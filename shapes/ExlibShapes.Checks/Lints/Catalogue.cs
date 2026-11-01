using System.Collections.Generic;
using System.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>One ruling a script decides.</summary>
/// <param name="Id">The ruling's id.</param>
/// <param name="Date">The date of fallen's ruling, <c>yyyy-mm-dd</c>.</param>
/// <param name="Message">The ruling in one line.</param>
/// <param name="Decider">The id of the check or lint that decides it.</param>
public sealed record RulingLint(
  string Id,
  string Date,
  string Message,
  string Decider
);

/// <summary>One ruling no script decides: a person looks.</summary>
/// <param name="Id">The ruling's id.</param>
/// <param name="Date">The date of fallen's ruling, <c>yyyy-mm-dd</c>.</param>
/// <param name="Message">What to look at, in one line.</param>
public sealed record RulingEye(string Id, string Date, string Message);

/// <summary>The casting rulings the tool holds: those a check or lint decides and those that need an eye.</summary>
public static class Catalogue {
  /// <summary>The id the fastener lint decides under; it is a lint, not a check in <see cref="Selection.All"/>.</summary>
  public const string FastenerDecider = FastenerLint.Id;

  /// <summary>The rulings a check or lint decides, in id order.</summary>
  public static IReadOnlyList<RulingLint> Decided { get; } =
  [
    new(
      "castings-hold-the-pour",
      "2026-10-19",
      "a cast item holds only the iron one pour makes: no bolts, brasses, keys or holes",
      "castonly"
    ),
    new(
      "finishing-allowance",
      "2026-10-20",
      "a casting stands out by the allowance on the faces its steps cut and nowhere else",
      "allowance"
    ),
    new(
      FastenerLint.Id,
      FastenerLint.Ruling,
      FastenerLint.Message,
      FastenerDecider
    ),
    new(
      "pattern-texture",
      "2026-11-01",
      "a pattern and a core box take the one shellacked pine texture",
      "role"
    ),
    new(
      "regular-octagon",
      "2026-10-27",
      "a turned part under 6 across is a regular octagon, all eight faces equal",
      "octregular"
    ),
    new(
      "round-uv",
      "2026-10-27",
      "the outward faces of a round run take one unrolled texture strip",
      "uvwrap"
    ),
    new(
      "shared-blanks",
      "2026-11-01",
      "a machine's casting is a user of a shared blank within 0.75 to 1.0 of it, its cuts on the blank's cut faces",
      "blankmatch"
    ),
  ];

  /// <summary>The rulings that need an eye, in id order.</summary>
  public static IReadOnlyList<RulingEye> Eye { get; } =
  [
    new(
      "finishing-judged-by-use",
      "2026-10-20",
      "which castings need a bench step is judged from where the item is used and the process simulated"
    ),
    new(
      "flask-stations",
      "2026-10-19",
      "a casting gets a filling at a flask only where its pieces fit that flask's sand"
    ),
    new(
      "grouped-castings",
      "2026-10-26",
      "pieces bolted into one assembly are one cast item when they fit one flask; precision pieces cast apart"
    ),
    new(
      "worked-faces",
      "2026-10-28",
      "an item shows its stage: a finished texture only on the faces its step cut"
    ),
  ];

  /// <summary>The lines of <c>check --catalogue</c>: each decided ruling as <c>ID  DATE  MESSAGE  [DECIDER]</c>, then
  /// <c>needs an eye:</c> and each ruling no script decides as <c>ID  DATE  MESSAGE</c>.</summary>
  public static IReadOnlyList<string> Lines() =>
    [
      .. Decided.Select(r => $"{r.Id}  {r.Date}  {r.Message}  [{r.Decider}]"),
      "needs an eye:",
      .. Eye.Select(r => $"{r.Id}  {r.Date}  {r.Message}"),
    ];
}
