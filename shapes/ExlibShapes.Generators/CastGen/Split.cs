using System;
using System.Collections.Generic;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Generators;

/// <summary>Step 5: elements over a block on any axis cut into equal parts.</summary>
public static class Splits {
  /// <summary>The longest an element may be on any axis, in voxels.</summary>
  public const double Limit = 16;

  /// <summary>The leaves with each one over <see cref="Limit"/> on a local axis cut into
  /// ceil(size / 16) equal parts along it, the cells of x outer and of z inner. The first part keeps the name,
  /// the others are <c>NAME_s2</c>, <c>NAME_s3</c> and on; the parts lose their uv. A leaf within the limit is
  /// returned as it is.</summary>
  public static List<Leaf> Apply(IEnumerable<Leaf> leaves) {
    var outp = new List<Leaf>();
    foreach (Leaf leaf in leaves) {
      double[] from = leaf.Element.From!,
        to = leaf.Element.To!;
      var n = new int[3];
      for (int a = 0; a < 3; a++)
        n[a] = Math.Max(1, (int)Math.Ceiling((to[a] - from[a]) / Limit - 1e-9));
      if (n[0] * n[1] * n[2] == 1) {
        outp.Add(leaf);
        continue;
      }
      int part = 0;
      for (int i = 0; i < n[0]; i++)
        for (int j = 0; j < n[1]; j++)
          for (int k = 0; k < n[2]; k++) {
            Element e = Select.Clone(leaf.Element, true);
            int[] at = [i, j, k];
            for (int a = 0; a < 3; a++) {
              e.From![a] = Edge(from[a], to[a], at[a], n[a]);
              e.To![a] = Edge(from[a], to[a], at[a] + 1, n[a]);
            }
            part++;
            outp.Add(
              leaf with {
                Name = part == 1 ? leaf.Name : $"{leaf.Name}_s{part}",
                Element = e,
              }
            );
          }
    }
    return outp;
  }

  private static double Edge(double from, double to, int i, int n) =>
    i == n ? FaceAxes.Round(to) : FaceAxes.Round(from + (to - from) * i / n);
}
