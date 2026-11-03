using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Checks;
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
  /// A name no leaf carries is skipped. An entry naming a group is read through <see cref="Expand"/> first.</summary>
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

  /// <summary>Every drawn element of <paramref name="drawing"/> as a leaf in its world place, the set
  /// <see cref="Expand"/> and <see cref="Names"/> read groups against.</summary>
  /// <exception cref="CastGenException">An element is unnamed or its world matrix is not a rotation and positive
  /// scale.</exception>
  public static List<Leaf> Drawing(Shape drawing) =>
    Select.Groups(
      drawing,
      [
        .. drawing.Elements.Select(e =>
          e.Name ?? throw new CastGenException("an element has no name")
        ),
      ]
    );

  /// <summary>Whether <paramref name="name"/> reaches one of <paramref name="cast"/>: it is the drawing name of one,
  /// or it names no drawn element of <paramref name="drawing"/> and one of <paramref name="cast"/> lies under it.
  /// A drawn element that is not cast reaches none, whatever lies under it.</summary>
  /// <param name="drawing">Every drawn leaf of the drawing (<see cref="Drawing"/>).</param>
  /// <param name="cast">The leaves one item takes.</param>
  /// <param name="name">An element or group name from the record.</param>
  public static bool Names(
    IReadOnlyList<Leaf> drawing,
    IEnumerable<Leaf> cast,
    string name
  ) =>
    cast.Any(l => l.SourceName == name)
    || (
      drawing.All(l => l.SourceName != name) && cast.Any(l => Under(l, name))
    );

  /// <summary>The entries <paramref name="faces"/> with each one that names a group replaced by the leaf entries it
  /// stands for. A name of a drawn element of <paramref name="drawing"/> is that element's and is kept as written,
  /// whatever lies under it; any other name with drawn leaves under it is a group, and its entry becomes
  /// <c>LEAF/FACE</c> for each leaf of the group whose face lies on the group's bounding face in that direction,
  /// within 1e-4 voxel, and turns about no other axis. The bounding face is the whole group's in the drawing, so an
  /// item that takes part of a group gets the face only where its leaves reach that bound. The staves of a round run
  /// about the face's axis count as one solid over their full axial extent: when the run reaches the bound every
  /// stave takes the face, a stave drawn short of it by the drawing's stagger included. An entry naming nothing is
  /// kept; an entry that names a group no leaf of which qualifies is dropped; a repeat is dropped.</summary>
  /// <param name="drawing">Every drawn leaf of the drawing (<see cref="Drawing"/>), in its drawn place.</param>
  /// <param name="faces">Entries <c>NAME/FACE</c>.</param>
  /// <exception cref="CastGenException">An entry is not <c>NAME/FACE</c> or names an unknown face.</exception>
  public static List<string> Expand(
    IReadOnlyList<Leaf> drawing,
    IEnumerable<string> faces
  ) {
    var outp = new List<string>();
    foreach (string spec in faces) {
      int at = spec.LastIndexOf('/');
      if (at < 0 || !FaceAxes.Names.Contains(spec[(at + 1)..]))
        throw new CastGenException($"\"{spec}\" is not NAME/FACE");
      string name = spec[..at],
        face = spec[(at + 1)..];
      List<Leaf> group = [];
      if (!drawing.Any(l => l.SourceName == name))
        group = [.. drawing.Where(l => Under(l, name))];
      if (group.Count == 0) {
        if (!outp.Contains(spec))
          outp.Add(spec);
        continue;
      }
      foreach (
        int i in GroupFaces.OnBound(
          [
            .. group.Select(l =>
            {
              Mat4d world = Engine.Local(l.Element);
              return (world, WorldBox.Of(world, Engine.Size(l.Element)));
            }),
          ],
          face,
          Runs(group)
        )
      ) {
        string entry = group[i].SourceName + "/" + face;
        if (!outp.Contains(entry))
          outp.Add(entry);
      }
    }
    return outp;
  }

  private static List<List<int>> Runs(List<Leaf> group) {
    Shape shape = Flush.ShapeOf(group);
    var tree = new Tree(shape);
    var index = new Dictionary<string, int>(StringComparer.Ordinal);
    for (int i = 0; i < group.Count; i++)
      index[group[i].Name] = i;
    return
    [
      .. StaveRuns
        .Find(tree, new Frames(shape))
        .Select(r => r.Members.Select(m => index[m.Path]).ToList()),
    ];
  }

  private static bool Under(Leaf leaf, string group) =>
    leaf.SourcePath.Split('/')[..^1].Contains(group);
}
