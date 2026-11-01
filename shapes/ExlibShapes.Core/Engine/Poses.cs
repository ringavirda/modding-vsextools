using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Core;

/// <summary>One element's animation transform: added to its own rotation, added to its
/// <c>from</c>, and multiplied into its scale.</summary>
public sealed class Pose {
  /// <summary>Shape units added to the element's <c>from</c>.</summary>
  public Vec3 Offset { get; init; } = Vec3.Zero;

  /// <summary>Degrees added to the element's own rotation.</summary>
  public Vec3 Rotation { get; init; } = Vec3.Zero;

  /// <summary>Per-axis factor multiplied into the element's scale.</summary>
  public Vec3 Stretch { get; init; } = Vec3.One;
}

/// <summary>Keyframe interpolation following the game's animator. Poses are keyed by element
/// name, as keyframes address elements.</summary>
public static class Poses {
  private static readonly string[][] ChannelKeys =
  [
    ["offsetX", "offsetY", "offsetZ"],
    ["rotationX", "rotationY", "rotationZ"],
    ["stretchX", "stretchY", "stretchZ"],
  ];

  private static readonly double[] Fallbacks = [0.0, 0.0, 1.0];

  /// <summary>The clip of <paramref name="shape"/> whose code is <paramref name="code"/>.</summary>
  /// <exception cref="KeyNotFoundException">No clip has that code; the message lists the codes.</exception>
  public static Clip Find(Shape shape, string code) =>
    shape.Animations.FirstOrDefault(a => a.Code == code)
    ?? throw new KeyNotFoundException(
      $"no clip '{code}'; available: "
        + string.Join(
          ", ",
          shape.Animations.Select(a => a.Code).Where(c => c != null).Order()
        )
    );

  /// <summary>
  /// The pose of every element the clip <paramref name="code"/> keys, at <paramref name="frame"/>
  /// (fractional allowed). An element with one keyframe holds it; with two or more the pose is
  /// linearly interpolated between the keyframes bracketing the frame, wrapping from the last to
  /// the first across <c>quantityframes</c>. A channel a keyframe leaves unset takes the element's
  /// first keyframe's value, else 0 (offset, rotation) or 1 (stretch).
  /// </summary>
  /// <exception cref="KeyNotFoundException">No clip has that code.</exception>
  /// <exception cref="NotSupportedException">The clip is animation version 1.</exception>
  public static Dictionary<string, Pose> At(
    Shape shape,
    string code,
    double frame
  ) {
    Clip clip = Find(shape, code);
    if (clip.Version == 1)
      throw new NotSupportedException(
        $"clip '{code}' is animation version 1, which the shape tool does not pose"
      );
    List<Keyframe> keyframes = [.. clip.Keyframes.OrderBy(k => k.Frame ?? 0)];
    double quantity = clip.Quantity ?? 0;
    var poses = new Dictionary<string, Pose>();
    var names = keyframes
      .SelectMany(k => k.Elements.Select(e => e.Key))
      .Distinct();
    foreach (string name in names) {
      List<(int Frame, JObject? Body)> named =
      [
        .. keyframes
          .Where(k => k.Elements.Any(e => e.Key == name))
          .Select(k =>
            (
              k.Frame ?? 0,
              k.Elements.First(e => e.Key == name).Value as JObject
            )
          ),
      ];
      (int Frame, JObject? Body) prev,
        next;
      double t;
      if (named.Count == 1) {
        prev = next = named[0];
        t = 0;
      } else {
        var before = named.Where(k => k.Frame <= frame).ToList();
        var after = named.Where(k => k.Frame > frame).ToList();
        if (before.Count == 0) {
          prev = named[^1];
          next = after.Count > 0 ? after[0] : named[0];
          double span = next.Frame + (quantity - prev.Frame);
          t =
            span != 0 ? Mod(frame - prev.Frame + quantity, quantity) / span : 0;
        } else if (after.Count == 0) {
          prev = before[^1];
          next = named[0];
          double span = next.Frame + (quantity - prev.Frame);
          t = span != 0 ? (frame - prev.Frame) / span : 0;
        } else {
          prev = before[^1];
          next = after[0];
          double span = next.Frame - prev.Frame;
          t = span != 0 ? (frame - prev.Frame) / span : 0;
        }
      }
      var comp = new Vec3[3];
      for (int c = 0; c < 3; c++) {
        var axes = new double[3];
        for (int a = 0; a < 3; a++) {
          string key = ChannelKeys[c][a];
          double first = Channel(named[0].Body, key) ?? Fallbacks[c];
          double va = Channel(prev.Body, key) ?? first;
          double vb = Channel(next.Body, key) ?? first;
          axes[a] = va + (vb - va) * t;
        }
        comp[c] = new Vec3(axes[0], axes[1], axes[2]);
      }
      poses[name] = new Pose {
        Offset = comp[0],
        Rotation = comp[1],
        Stretch = comp[2],
      };
    }
    return poses;
  }

  private static double? Channel(JObject? body, string key) =>
    body?.GetValue(key, StringComparison.OrdinalIgnoreCase)
      is { Type: JTokenType.Integer or JTokenType.Float } v
      ? (double)v
      : null;

  private static double Mod(double a, double m) =>
    m <= 0 ? 0 : ((a % m) + m) % m;
}
