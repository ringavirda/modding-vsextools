using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vintagestory.API.Common;

namespace ExpandedLib.Shapes;

/// <summary>
/// Animation clips and keyframe interpolation, following the game's own <see cref="Animation"/>
/// semantics (<c>anim.py</c>).
/// <para>
/// A pose is keyed by element NAME, not path: the game's own keyframes address elements by name,
/// so the same name repeated in two branches of the tree receives the same pose, and
/// <see cref="Geometry.WorldMatrices"/> applies each named pose while composing the tree, so a
/// keyframed parent and child both move.
/// </para>
/// </summary>
public static class Poses {
  // (offsetX/Y/Z, rotationX/Y/Z, stretchX/Y/Z selectors) and the fallback used when a keyframe
  // names the element but leaves this particular channel unset - anim.py's CHANNELS.
  private static readonly (
    System.Func<AnimationKeyFrameElement, double?> X,
    System.Func<AnimationKeyFrameElement, double?> Y,
    System.Func<AnimationKeyFrameElement, double?> Z,
    double Fallback
  )[] Channels =
  [
    (e => e.OffsetX, e => e.OffsetY, e => e.OffsetZ, 0.0),
    (e => e.RotationX, e => e.RotationY, e => e.RotationZ, 0.0),
    (e => e.StretchX, e => e.StretchY, e => e.StretchZ, 1.0),
  ];

  /// <summary>
  /// The animation whose <see cref="Animation.Code"/> or <see cref="Animation.Name"/> equals
  /// <paramref name="name"/>.
  /// </summary>
  /// <exception cref="KeyNotFoundException">No clip matches; the message lists every clip
  /// <paramref name="shape"/> does carry.</exception>
  public static Animation Clip(LoadedShape shape, string name) {
    foreach (Animation a in shape.Animations)
      if (a.Code == name || a.Name == name)
        return a;
    string available = string.Join(
      ", ",
      shape
        .Animations.Select(a => a.Code ?? a.Name)
        .Where(n => n != null)
        .Distinct()
        .OrderBy(n => n, StringComparer.Ordinal)
    );
    throw new KeyNotFoundException($"no clip '{name}'; available: {available}");
  }

  /// <summary>Element names that appear in at least one keyframe of <paramref name="clipName"/>.</summary>
  public static HashSet<string> KeyframedNames(LoadedShape shape, string clipName) {
    var names = new HashSet<string>();
    foreach (AnimationKeyFrame kf in Clip(shape, clipName).KeyFrames ?? [])
      if (kf.Elements != null)
        foreach (string name in kf.Elements.Keys)
          names.Add(name);
    return names;
  }

  // The value of one channel axis on the keyframe that actually names the element (elements[name]
  // is present by construction: every keyframe passed here comes from a name-filtered list), or
  // fallback when that keyframe leaves the specific field null.
  private static double ChannelValue(
    AnimationKeyFrame kf,
    string name,
    System.Func<AnimationKeyFrameElement, double?> select,
    double fallback
  ) => kf.Elements!.TryGetValue(name, out AnimationKeyFrameElement? e) && select(e) is { } v ? v : fallback;

  // anim.py's _earliest_value: the value of one channel axis on the first (file-order) keyframe
  // naming the element - the close_loop rule for a keyframe whose own value is unset - falling
  // back to the channel's own default when even that keyframe leaves the field null.
  private static double EarliestValue(
    IReadOnlyList<AnimationKeyFrame> named,
    string name,
    System.Func<AnimationKeyFrameElement, double?> select,
    double fallback
  ) {
    foreach (AnimationKeyFrame kf in named)
      if (kf.Elements != null && kf.Elements.TryGetValue(name, out AnimationKeyFrameElement? e))
        return select(e) is { } v ? v : fallback;
    return fallback;
  }

  // Floor-mod: the result is always non-negative for a positive m. C#'s % keeps the
  // dividend's sign instead, which the wrap-around span below cannot use.
  private static double Mod(double a, double m) => ((a % m) + m) % m;

  /// <summary>
  /// Pose of every keyframed element of <paramref name="clipName"/> at <paramref name="frame"/>
  /// (may be fractional), wrapping past <c>quantityframes</c> the way the game's own animator
  /// does for a repeating clip: an element with only one keyframe holds it; with two or more, the
  /// pose is linearly interpolated between the keyframes bracketing <paramref name="frame"/>, or
  /// between the last and the first (spanning the wrap) when <paramref name="frame"/> falls
  /// before the first or after the last.
  /// </summary>
  public static Dictionary<string, Pose> PoseAt(LoadedShape shape, string clipName, double frame) {
    Animation anim = Clip(shape, clipName);
    List<AnimationKeyFrame> keyframes = [.. (anim.KeyFrames ?? []).OrderBy(kf => kf.Frame)];
    double quantity = anim.QuantityFrames;
    var poses = new Dictionary<string, Pose>();

    foreach (string name in KeyframedNames(shape, clipName)) {
      List<AnimationKeyFrame> named = [.. keyframes.Where(kf => kf.Elements?.ContainsKey(name) == true)];
      if (named.Count == 0)
        continue;

      AnimationKeyFrame prev;
      AnimationKeyFrame next;
      double t;
      if (named.Count == 1) {
        prev = next = named[0];
        t = 0.0;
      } else {
        AnimationKeyFrame? prevOpt = named.Where(kf => kf.Frame <= frame).OrderByDescending(kf => kf.Frame).FirstOrDefault();
        AnimationKeyFrame? nextOpt = named.Where(kf => kf.Frame > frame).OrderBy(kf => kf.Frame).FirstOrDefault();
        double span;
        if (prevOpt == null) {
          // frame sits before the first named keyframe: wrap back to the last one as "previous".
          prev = named[^1];
          next = nextOpt ?? named[0];
          span = next.Frame + (quantity - prev.Frame);
          t = span != 0 ? Mod(frame - prev.Frame + quantity, quantity) / span : 0.0;
        } else if (nextOpt == null) {
          // frame sits at or after the last named keyframe: wrap forward to the first one.
          prev = prevOpt;
          next = named[0];
          span = next.Frame + (quantity - prev.Frame);
          t = span != 0 ? (frame - prev.Frame) / span : 0.0;
        } else {
          prev = prevOpt;
          next = nextOpt;
          span = next.Frame - prev.Frame;
          t = span != 0 ? (frame - prev.Frame) / span : 0.0;
        }
      }

      var comp = new double[3][];
      for (int c = 0; c < Channels.Length; c++) {
        (var selX, var selY, var selZ, double fallback) = Channels[c];
        System.Func<AnimationKeyFrameElement, double?>[] sel = [selX, selY, selZ];
        comp[c] = new double[3];
        for (int axis = 0; axis < 3; axis++) {
          double earliest = EarliestValue(named, name, sel[axis], fallback);
          double a = ChannelValue(prev, name, sel[axis], earliest);
          double b = ChannelValue(next, name, sel[axis], earliest);
          comp[c][axis] = a + (b - a) * t;
        }
      }

      poses[name] = new Pose {
        Offset = new Vector3((float)comp[0][0], (float)comp[0][1], (float)comp[0][2]),
        Rotation = new Vector3((float)comp[1][0], (float)comp[1][1], (float)comp[1][2]),
        Stretch = new Vector3((float)comp[2][0], (float)comp[2][1], (float)comp[2][2]),
      };
    }
    return poses;
  }
}
