using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Generators;

/// <summary>Where one flask axis takes its coordinate from.</summary>
/// <param name="Item">The item axis it reads, 0 x, 1 y or 2 z.</param>
/// <param name="Sign">1 or -1: the flask coordinate is Sign times the item's, plus <paramref name="Offset"/>.</param>
/// <param name="Offset">Added after the sign, in voxels.</param>
public sealed record LieAxis(int Item, int Sign, double Offset);

/// <summary>How a casting lies in its flask: a signed permutation of the item's axes and an offset on each.</summary>
/// <param name="Axes">The flask's x, y and z in turn.</param>
public sealed record LieSpec(IReadOnlyList<LieAxis> Axes) {
  /// <summary>The transform taking a point of the item frame to the flask.</summary>
  public Mat4d Matrix {
    get {
      var rows = new double[16];
      for (int f = 0; f < 3; f++) {
        rows[f * 4 + Axes[f].Item] = Axes[f].Sign;
        rows[f * 4 + 3] = Axes[f].Offset;
      }
      rows[15] = 1;
      return Mat4d.FromRows(rows);
    }
  }
}

/// <summary>Step 8: the lie of a casting in its flask.</summary>
public static class Lies {
  /// <summary>Reads a lie: <c>{"x": {"axis": "-x", "offset": 15}, "y": {...}, "z": {...}}</c>, each flask axis
  /// naming the item axis it takes, with its sign, and an offset in voxels. The three item axes are a permutation of
  /// x, y and z and the turn is a rotation, never a mirror.</summary>
  /// <param name="lie">The lie as written.</param>
  /// <param name="where">What the lie belongs to, for messages.</param>
  /// <exception cref="SpecException">The lie is missing, <c>"auto"</c>, the bare <c>axis</c> shorthand, or not a
  /// signed permutation of the axes.</exception>
  public static LieSpec Read(JToken? lie, string where) {
    if (lie == null)
      throw new SpecException($"{where}: \"lie\" is missing");
    if (lie.Type == JTokenType.String)
      throw new SpecException(
        (string?)lie == "auto"
          ? $"{where}: lie \"auto\" is not available until M2; give the per-flask-axis form"
          : $"{where}: lie \"{(string?)lie}\" is not a lie"
      );
    if (lie is not JObject o)
      throw new SpecException($"{where}: \"lie\" is not an object");
    if (o.ContainsKey("axis"))
      throw new SpecException(
        $"{where}: lie takes the per-flask-axis form {{\"x\": {{\"axis\", \"offset\"}}, \"y\": ..., \"z\": ...}}, not a bare axis"
      );
    var f = new Fields(o, $"{where} lie");
    var axes = new List<LieAxis>();
    foreach (string flask in new[] { "x", "y", "z" })
      axes.Add(
        ReadAxis(
          f.Object(flask) ?? throw f.Missing(flask),
          $"{where} lie {flask}"
        )
      );
    f.Done();
    if (axes.Select(a => a.Item).Distinct().Count() != 3)
      throw new SpecException(
        $"{where}: lie takes {string.Join(", ", axes.Select(a => Name(a)))} for x, y, z, which is not a signed permutation of x, y, z"
      );
    var lieSpec = new LieSpec(axes);
    Mat4d m = lieSpec.Matrix;
    double det =
      m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
      - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
      + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
    if (det < 0)
      throw new SpecException(
        $"{where}: lie turns {string.Join(", ", axes.Select(a => Name(a)))} for x, y, z, which mirrors the casting"
      );
    return lieSpec;
  }

  /// <summary>The lie of a group entry: its own, else the casting's.</summary>
  /// <exception cref="SpecException">See <see cref="Read"/>.</exception>
  public static LieSpec Of(GroupSpec group, JToken? entry, string where) =>
    Read(group.Lie ?? entry, where);

  /// <summary>The leaves laid in the flask under <paramref name="lie"/>.</summary>
  /// <exception cref="CastGenException">A leaf's world transform is not a rotation with a positive scale.</exception>
  public static List<Leaf> Apply(IEnumerable<Leaf> leaves, LieSpec lie) {
    Mat4d m = lie.Matrix;
    return
    [
      .. leaves.Select(l =>
        l with
        {
          Element = Select.Flatten(
            l.Element,
            m * Engine.Local(l.Element),
            l.SourcePath
          ),
        }
      ),
    ];
  }

  private static LieAxis ReadAxis(JObject o, string where) {
    var r = new Fields(o, where);
    string text = r.Text("axis");
    double offset = r.Number("offset") ?? throw r.Missing("offset");
    r.Done();
    int sign = text.StartsWith('-') ? -1 : 1;
    int item = (sign < 0 ? text[1..] : text) switch {
      "x" => 0,
      "y" => 1,
      "z" => 2,
      _ => throw new SpecException(
        $"{where}: axis \"{text}\" is x, y or z with an optional minus"
      ),
    };
    return new LieAxis(item, sign, offset);
  }

  private static string Name(LieAxis a) =>
    (a.Sign < 0 ? "-" : "")
    + "xyz"[a.Item].ToString(CultureInfo.InvariantCulture);
}
