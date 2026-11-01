using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>The bearings round an axis that octcheck walks reaches out along, and the octagon they are held to.</summary>
internal static class Sweep {
  public const double Step = 0.002;

  /// <summary>Bearings of a quarter degree: 1440 round the axis, in radians.</summary>
  public static readonly double[] Th =
  [
    .. Enumerable.Range(0, 1440).Select(i => i * 0.25 * Math.PI / 180),
  ];

  public static string F(double v, int digits) =>
    v.ToString("F" + digits, CultureInfo.InvariantCulture);

  public static double Deg(int bearing) => bearing * 0.25;

  /// <summary>Distances out along a ray, <c>Step</c> apart, from 0 up to but not including <paramref name="stop"/>.</summary>
  public static double[] Radii(double stop) =>
    [
      .. Enumerable
        .Range(0, (int)Math.Ceiling(stop / Step))
        .Select(i => i * Step),
    ];

  /// <summary>Per bearing, the distance from the centre to the octagon through (ha, sb) and (sa, hb) in each
  /// quadrant: flats ha and hb out on the two axes, diagonal flats joining them.</summary>
  public static double[] OctagonReach(
    double ha,
    double hb,
    double sa,
    double sb
  ) {
    var outp = new double[Th.Length];
    for (int i = 0; i < Th.Length; i++) {
      double u = Math.Abs(Math.Cos(Th[i])),
        v = Math.Abs(Math.Sin(Th[i]));
      double dn = (hb - sb) * u + (ha - sa) * v;
      double diag =
        dn > 1e-12
          ? ((hb - sb) * ha + (ha - sa) * sb) / dn
          : double.PositiveInfinity;
      outp[i] = Math.Min(
        Math.Min(
          u > 1e-12 ? ha / u : double.PositiveInfinity,
          v > 1e-12 ? hb / v : double.PositiveInfinity
        ),
        diag
      );
    }
    return outp;
  }

  /// <summary>Whether a point in <paramref name="from"/>'s frame lies in the box of <paramref name="el"/>, grown by
  /// <paramref name="tol"/> on every side.</summary>
  public static bool Inside(Frames fr, Node from, Vec3 p, Node el, double tol) {
    Vec3 l = fr.Inverse(el).Apply(fr.World(from).Apply(p));
    Vec3 s = el.Size;
    return l.X > -tol
      && l.X < s.X + tol
      && l.Y > -tol
      && l.Y < s.Y + tol
      && l.Z > -tol
      && l.Z < s.Z + tol;
  }

  /// <summary>The corners of <paramref name="el"/> in <paramref name="frame"/>'s frame.</summary>
  public static Vec3[] CornersIn(Frames fr, Node frame, Node el) =>
    [.. Engine.Corners(fr.Mat(el), el.Size).Select(fr.Inverse(frame).Apply)];

  /// <summary>Whether <paramref name="path"/> matches a glob of <c>*</c>, <c>?</c> and <c>[set]</c>, case-sensitive,
  /// <c>*</c> crossing slashes.</summary>
  public static bool GlobMatches(string path, string glob) {
    var rx = new System.Text.StringBuilder("^");
    for (int i = 0; i < glob.Length; i++) {
      char c = glob[i];
      if (c == '*')
        rx.Append(".*");
      else if (c == '?')
        rx.Append('.');
      else if (c == '[' && glob.IndexOf(']', i + 2) is var j && j > 0) {
        string set = glob[(i + 1)..j];
        if (set.StartsWith('!'))
          set = "^" + set[1..];
        rx.Append('[').Append(set.Replace("\\", "\\\\")).Append(']');
        i = j;
      } else
        rx.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
    }
    return System.Text.RegularExpressions.Regex.IsMatch(
      path,
      rx.Append('$').ToString(),
      System.Text.RegularExpressions.RegexOptions.Singleline
    );
  }
}
