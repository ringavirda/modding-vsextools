using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>The poses a check sweeps: rest, and the frames of the shape's clips.</summary>
internal static class PoseRuns {
  /// <summary>Every clip's code, its name where it has none, in file order.</summary>
  public static List<string> AllClips(Shape shape) =>
    [.. shape.Animations.Select(a => a.Code ?? a.Name).OfType<string>()];

  /// <summary>The poses of one run: <c>rest</c> is a single null pose; <c>CLIP</c> every integer frame of the clip
  /// from 0 below its quantityframes (frame 0 for a clip of none); <c>CLIP@F</c> frame F. Labels read
  /// <c>rest</c> and <c>CLIP fF</c>.</summary>
  /// <exception cref="CheckUsageException">The clip is not in the shape, is animation version 1, or the spec lays
  /// clips together or ramps.</exception>
  public static List<(string Label, Dictionary<string, Pose>? Poses)> Run(
    Shape shape,
    string spec
  ) {
    if (spec == "rest")
      return [("rest", null)];
    if (spec.Contains('+') || spec.Contains('~'))
      throw new CheckUsageException(
        $"run '{spec}': a run is rest, CLIP or CLIP@F"
      );
    string code = spec;
    double? hold = null;
    int at = spec.IndexOf('@');
    if (at >= 0) {
      code = spec[..at];
      hold = double.Parse(
        spec[(at + 1)..],
        System.Globalization.CultureInfo.InvariantCulture
      );
    }
    try {
      List<double> frames = [];
      if (hold is double h)
        frames.Add(h);
      else {
        double q = Core.Poses.Find(shape, code).Quantity ?? 0;
        if (q <= 0)
          frames.Add(0);
        else
          for (double f = 0; f < q - 1e-9; f += 1)
            frames.Add(f);
      }
      return
      [
        .. frames.Select(f =>
          (
            $"{code} f{Fmt.G(f)}",
            (Dictionary<string, Pose>?)Core.Poses.At(shape, code, f)
          )
        ),
      ];
    } catch (Exception e)
        when (e is KeyNotFoundException or NotSupportedException) {
      throw new CheckUsageException(e.Message);
    }
  }
}
