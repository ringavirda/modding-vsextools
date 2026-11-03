using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>A record face entry that names a group (<c>ScrewBoss/down</c>) read as the faces of the group's outer
/// leaves.</summary>
internal static class GroupFaces {
  private const double Tol = 1e-4;
  private const double OnAxis = 1e-6;

  /// <summary>Which of <paramref name="members"/>, the leaves of one group, carry <paramref name="face"/> on the group's
  /// bounding face: the leaf's world box ends there within 1e-4 voxel, and its own face points the same way in the
  /// world (it turns only about that axis, never flipped), so the face is the one the entry names.</summary>
  /// <param name="members">Each leaf's world matrix and world box.</param>
  /// <param name="face">One of north, east, south, west, up, down.</param>
  /// <returns>Indices into <paramref name="members"/>, ascending; empty when no leaf qualifies.</returns>
  /// <exception cref="ArgumentException"><paramref name="face"/> is not a face name.</exception>
  public static List<int> OnBound(
    IReadOnlyList<(Mat4d World, Box Box)> members,
    string face
  ) {
    (int axis, bool hi) = face switch {
      "east" => (0, true),
      "west" => (0, false),
      "up" => (1, true),
      "down" => (1, false),
      "south" => (2, true),
      "north" => (2, false),
      _ => throw new ArgumentException($"\"{face}\" is not a face name"),
    };
    if (members.Count == 0)
      return [];
    double bound = hi
      ? members.Max(m => m.Box.Hi[axis])
      : members.Min(m => m.Box.Lo[axis]);
    var on = new List<int>();
    for (int i = 0; i < members.Count; i++) {
      (Mat4d world, Box box) = members[i];
      double end = hi ? box.Hi[axis] : box.Lo[axis];
      if (Math.Abs(end - bound) <= Tol && world[axis, axis] > 1 - OnAxis)
        on.Add(i);
    }
    return on;
  }
}
