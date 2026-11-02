using System;
using System.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>Element references as the checks' spec files write them.</summary>
internal static class Refs {
  /// <summary>Whether <paramref name="path"/> is <paramref name="reference"/> or lies in its subtree, whole
  /// segments, a leading <c>Root/</c> on either ignored.</summary>
  public static bool Under(string path, string reference) {
    string p = Tree.Bare(path),
      r = Tree.Bare(reference);
    return p == r || p.StartsWith(r + "/", StringComparison.Ordinal);
  }

  /// <summary>One reference: <c>PATH</c> its subtree, <c>PATH=</c> that element alone, <c>*NAME</c> every element
  /// named NAME and its subtree, <c>*NAME/</c> the subtrees below them only.</summary>
  public static bool Matches(string path, string reference) {
    if (reference.StartsWith('*')) {
      string[] segs = path.Split('/');
      return reference.EndsWith('/')
        ? segs[..^1].Contains(reference[1..^1])
        : segs.Contains(reference[1..]);
    }
    return reference.EndsWith('=')
      ? Tree.Bare(path) == Tree.Bare(reference[..^1])
      : Under(path, reference);
  }
}
