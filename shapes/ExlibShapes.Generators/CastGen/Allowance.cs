using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Generators;

/// <summary>The face names as axes: where each lies on its element's local axes.</summary>
internal static class FaceAxes {
  public static readonly string[] Names =
  [
    "north",
    "east",
    "south",
    "west",
    "up",
    "down",
  ];

  /// <summary>The local axis (0 x, 1 y, 2 z) of <paramref name="face"/> and whether it is the <c>to</c> side.</summary>
  /// <exception cref="CastGenException"><paramref name="face"/> is not a face name.</exception>
  public static (int Axis, bool Hi) Of(string face) =>
    face switch {
      "east" => (0, true),
      "west" => (0, false),
      "up" => (1, true),
      "down" => (1, false),
      "south" => (2, true),
      "north" => (2, false),
      _ => throw new CastGenException($"\"{face}\" is not a face name"),
    };

  /// <summary>A copy of <paramref name="leaf"/> whose <paramref name="face"/> moved
  /// <paramref name="delta"/> along its outward normal (inward when negative), in the element's own
  /// frame; the moved face loses its uv.</summary>
  public static Leaf Push(Leaf leaf, string face, double delta) {
    (int axis, bool hi) = Of(face);
    Element e = Select.Clone(leaf.Element, false);
    if (hi)
      e.To![axis] = Round(e.To[axis] + delta);
    else
      e.From![axis] = Round(e.From![axis] - delta);
    foreach (KeyValuePair<string, Face> f in e.Faces)
      if (f.Key == face)
        f.Value.Uv = null;
    return leaf with { Element = e };
  }

  public static double Round(double v) => Math.Round(v, 4);
}

/// <summary>Step 3: the allowance out on the faces a step cuts.</summary>
public static class Allowances {
  /// <summary>The leaves with <paramref name="amount"/> added outward on each face
  /// <paramref name="faces"/> names, by the drawing's element name; the moved face loses its uv.
  /// A name no leaf carries is skipped.</summary>
  /// <param name="leaves">The cast set's leaves.</param>
  /// <param name="faces">Entries <c>NAME/FACE</c>, FACE one of north, east, south, west, up, down.</param>
  /// <param name="amount">The allowance in voxels.</param>
  /// <exception cref="CastGenException">An entry is not <c>NAME/FACE</c> or names an unknown face.</exception>
  public static List<Leaf> Out(
    IEnumerable<Leaf> leaves,
    IEnumerable<string> faces,
    double amount
  ) {
    var cuts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    foreach (string spec in faces) {
      int at = spec.LastIndexOf('/');
      if (at < 0 || !FaceAxes.Names.Contains(spec[(at + 1)..]))
        throw new CastGenException($"\"{spec}\" is not NAME/FACE");
      string name = spec[..at];
      if (!cuts.TryGetValue(name, out List<string>? list))
        cuts[name] = list = [];
      if (!list.Contains(spec[(at + 1)..]))
        list.Add(spec[(at + 1)..]);
    }
    var outp = new List<Leaf>();
    foreach (Leaf leaf in leaves) {
      Leaf moved = leaf;
      if (cuts.TryGetValue(leaf.SourceName, out List<string>? list))
        foreach (string face in list)
          moved = FaceAxes.Push(moved, face, amount);
      outp.Add(moved);
    }
    return outp;
  }
}
