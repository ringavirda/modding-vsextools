using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>A core's boxes split into its body and its prints, in one frame.</summary>
/// <param name="Body">The boxes that are not prints.</param>
/// <param name="Prints">The print boxes with their element names.</param>
internal sealed record CoreParts(
  IReadOnlyList<Box> Body,
  IReadOnlyList<(string Name, Box Box)> Prints
) {
  /// <summary>Splits the leaves of <paramref name="core"/>, each placed by <paramref name="at"/> when given.</summary>
  /// <exception cref="CheckUsageException">A leaf is turned off the axes.</exception>
  public static CoreParts Of(
    Shape core,
    IReadOnlyList<string> prints,
    Mat4d? at,
    string gate
  ) {
    var tree = new Tree(core);
    var frames = new Frames(core);
    var body = new List<Box>();
    var found = new List<(string, Box)>();
    foreach (Node leaf in tree.Leaves) {
      Mat4d world = at is Mat4d a ? a * frames.Mat(leaf) : frames.Mat(leaf);
      Box box = Pouring.Aligned(world, leaf.Size, leaf.Path, gate);
      if (Matches(leaf, prints))
        found.Add((leaf.Name, box));
      else
        body.Add(box);
    }
    return new CoreParts(body, found);
  }

  /// <summary>Whether <paramref name="node"/> or an element above it is named by one of <paramref name="globs"/>,
  /// by name or by path without <c>Root/</c>.</summary>
  public static bool Matches(Node node, IReadOnlyList<string> globs) {
    for (Node? n = node; n != null; n = n.Parent)
      if (
        globs.Any(g =>
          Sweep.GlobMatches(Tree.Bare(n.Path), g)
          || Sweep.GlobMatches(n.Name, g)
        )
      )
        return true;
    return false;
  }

  /// <summary>The axis index of <c>x</c>, <c>y</c> or <c>z</c>.</summary>
  /// <exception cref="CheckUsageException">Any other letter.</exception>
  public static int Axis(string? run) =>
    run switch {
      "x" => 0,
      "y" => 1,
      "z" => 2,
      _ => throw new CheckUsageException(
        $"corefit: core.run is '{run}', x, y or z wanted"
      ),
    };

  public static double Lo(IEnumerable<Box> boxes, int axis) =>
    boxes.Min(b => b.Lo[axis]);

  public static double Hi(IEnumerable<Box> boxes, int axis) =>
    boxes.Max(b => b.Hi[axis]);
}
