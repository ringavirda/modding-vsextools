using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ExpandedLib.Shapes;

/// <summary>
/// Which way an oriented family stands in the pictures a page shows.
/// <para>
/// A large machine is placed facing away from the player, so its front - the side the player
/// stands at, the boiler's firebox, the engine's cylinder end, the furnace's door - is the side
/// opposite the variant's facing. A picture shows that front, so a drawing takes the facing whose
/// front points most nearly at the camera of <see cref="ViewName"/>.
/// </para>
/// </summary>
public static class Presentation {
  /// <summary>The <see cref="Renderer.NamedViews"/> entry a page's main picture is drawn from, and
  /// the camera <see cref="Facing"/> is chosen for.</summary>
  public const string ViewName = "iso";

  // The four horizontal sides with their world normals (x east, z south), north first: the
  // isometric camera stands between two of them, and taking the earlier keeps north up.
  private static readonly (string Side, int X, int Z)[] Horizontals = [
    ("north", 0, -1), ("east", 1, 0), ("south", 0, 1), ("west", -1, 0),
  ];

  private static readonly Dictionary<string, string> Opposite = new(StringComparer.Ordinal) {
    ["north"] = "south", ["n"] = "south",
    ["east"] = "west", ["e"] = "west",
    ["south"] = "north", ["s"] = "north",
    ["west"] = "east", ["w"] = "east",
  };

  /// <summary>The side an oriented family is drawn facing by default, as a side word.</summary>
  public static string Facing => FacingFor(Renderer.NamedViews[ViewName]);

  /// <summary>
  /// The side an oriented family is drawn facing so that its front meets <paramref name="view"/>'s
  /// camera: the facing leaning furthest from the camera, the front being opposite it. Ties - the
  /// isometric camera stands square between two facings - go to the earlier of north, east, south,
  /// west, which keeps north up in the plans.
  /// </summary>
  public static string FacingFor(View view) {
    Vector3 eye = Renderer.Eye(view);
    double Lean((string Side, int X, int Z) side) => side.X * eye.X + side.Z * eye.Z;
    double least = Horizontals.Min(Lean);
    return Horizontals.First(side => Lean(side) <= least + 1e-6).Side;
  }

  /// <summary>
  /// The side the front of a machine facing <paramref name="side"/> looks toward: the opposite one.
  /// </summary>
  /// <param name="side">A side word or its single letter, in either case; null is accepted.</param>
  /// <returns>The side as a word, or null when <paramref name="side"/> names no horizontal side (a
  /// flywheel's <c>ns</c>/<c>we</c> axis names an axis, not a facing).</returns>
  public static string? Front(string? side) =>
    side != null && Opposite.TryGetValue(side, out string? front) ? front : null;

  /// <summary>The side <paramref name="variant"/>'s front looks toward (<see cref="Front"/> of the
  /// facing its <c>side</c> or <c>orientation</c> axis names), or null when its code names no
  /// horizontal facing.</summary>
  public static string? FrontOf(Variant variant) {
    foreach (string axis in new[] { "side", "orientation" })
      if (variant.States.TryGetValue(axis, out string? state) && Front(state) is { } front)
        return front;
    return null;
  }
}
