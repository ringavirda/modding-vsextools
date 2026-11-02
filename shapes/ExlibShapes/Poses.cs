using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vintagestory.API.Common;

namespace ExpandedLib.Shapes;

/// <summary>
/// Animation clips and keyframe interpolation, following the game's own <see cref="Animation"/>
/// semantics.
/// <para>
/// A pose is keyed by element NAME, not path: the game's own keyframes address elements by name,
/// so the same name repeated in two branches of the tree receives the same pose, and
/// <see cref="Geometry.WorldMatrices"/> applies each named pose while composing the tree, so a
/// keyframed parent and child both move.
/// </para>
/// </summary>
public static class Poses {
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
  public static HashSet<string> KeyframedNames(
    LoadedShape shape,
    string clipName
  ) {
    var names = new HashSet<string>();
    foreach (AnimationKeyFrame kf in Clip(shape, clipName).KeyFrames ?? [])
      if (kf.Elements != null)
        foreach (string name in kf.Elements.Keys)
          names.Add(name);
    return names;
  }

  /// <summary>Element names at least one keyframe of any clip <paramref name="shape"/> declares
  /// names - every part an animation moves. Empty for a shape with no animations; a moved part's
  /// own children are not listed, since a keyframed parent carries its subtree with it.</summary>
  public static HashSet<string> AnimatedNames(LoadedShape shape) {
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (Animation animation in shape.Animations)
      foreach (AnimationKeyFrame kf in animation.KeyFrames ?? [])
        if (kf.Elements != null)
          foreach (string name in kf.Elements.Keys)
            names.Add(name);
    return names;
  }

  /// <summary>
  /// Pose of every keyframed element of <paramref name="clipName"/> at <paramref name="frame"/>
  /// (may be fractional), wrapping past <c>quantityframes</c> the way the game's own animator
  /// does for a repeating clip: an element with only one keyframe holds it; with two or more, the
  /// pose is linearly interpolated between the keyframes bracketing <paramref name="frame"/>, or
  /// between the last and the first (spanning the wrap) when <paramref name="frame"/> falls
  /// before the first or after the last.
  /// </summary>
  public static Dictionary<string, Pose> PoseAt(
    LoadedShape shape,
    string clipName,
    double frame
  ) {
    Animation anim = Clip(shape, clipName);
    return Core
      .Poses.At(CoreBridge.ShapeOf(anim), anim.Code ?? anim.Name!, frame)
      .ToDictionary(
        kv => kv.Key,
        kv => new Pose {
          Offset = Float(kv.Value.Offset),
          Rotation = Float(kv.Value.Rotation),
          Stretch = Float(kv.Value.Stretch),
        }
      );
  }

  private static Vector3 Float(Core.Vec3 v) =>
    new((float)v.X, (float)v.Y, (float)v.Z);
}
