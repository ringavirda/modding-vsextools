using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>Basic shape hygiene: oversize elements, mismatched uv rects, missing textures, stray Root wrappers.</summary>
public static class SizesLint {
  private const double UvTol = 0.5;

  /// <summary>Lints one shape. An element over 16 units on a side, a uv rect off the face's size by more than
  /// 0.5 (sides swapped on a quarter-turned face) and a face naming an undeclared texture are errors; a
  /// top-level group named <c>Root</c> with children is a warning.</summary>
  /// <param name="shape">The shape.</param>
  /// <returns>The findings in element order, and the number of leaf elements checked.</returns>
  public static (IReadOnlyList<Finding> Findings, int Enumerated) Check(
    Shape shape
  ) {
    var tree = new Tree(shape);
    var known = shape.Textures.Select(t => t.Key).ToHashSet();
    var findings = new List<Finding>();
    List<Node> leaves = [.. tree.Leaves];
    foreach (Node el in leaves) {
      Vec3 size = el.Size;
      int over = Enumerable
        .Range(0, 3)
        .FirstOrDefault(a => size[a] > 16.001, -1);
      if (over >= 0)
        findings.Add(
          new Finding(
            "error",
            "sizes",
            "oversize",
            el.Path,
            $"{el.Path} has a dimension of {size[over]:F3} units, over the 16-unit block"
          )
        );
      foreach ((string face, Face spec) in el.Faces) {
        if (!Texels.FaceNames.Contains(face))
          continue;
        if (spec.Uv is { Length: >= 4 } uv) {
          (int ua, int va) = Texels.UvAxes(face);
          (double eu, double ev) = (size[ua], size[va]);
          int rot = (int)(spec.Rotation ?? 0);
          if (((rot % 180) + 180) % 180 == 90)
            (eu, ev) = (ev, eu);
          (double gu, double gv) = (
            Math.Abs(uv[2] - uv[0]),
            Math.Abs(uv[3] - uv[1])
          );
          if (Math.Abs(gu - eu) > UvTol || Math.Abs(gv - ev) > UvTol)
            findings.Add(
              new Finding(
                "error",
                "sizes",
                "uv",
                el.Path,
                $"{el.Path} {face} uv rect ({Fmt.G(gu)}, {Fmt.G(gv)}) does not match face size ({Fmt.G(eu)}, {Fmt.G(ev)})"
              )
            );
        }
        string tex = (spec.Texture ?? "").TrimStart('#');
        if (tex.Length > 0 && !known.Contains(tex))
          findings.Add(
            new Finding(
              "error",
              "sizes",
              "missing_texture",
              el.Path,
              $"{el.Path} {face} references undeclared texture key '{tex}'"
            )
          );
      }
    }
    foreach (Node el in tree.Roots)
      if (el.Name == "Root" && el.Children.Count > 0)
        findings.Add(
          new Finding(
            "warn",
            "sizes",
            "root_wrapper",
            el.Path,
            $"{el.Path} is a top-level group named Root with children"
          )
        );
    return (findings, leaves.Count);
  }
}
