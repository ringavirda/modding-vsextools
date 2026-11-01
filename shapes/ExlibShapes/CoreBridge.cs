using System.Collections.Generic;
using System.Numerics;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Mat4d = ExpandedLib.Shapes.Core.Mat4d;

namespace ExpandedLib.Shapes;

/// <summary>Carries a <see cref="LoadedShape"/> into <see cref="Core.Engine"/>, the one
/// implementation of the element transform, and its matrices back out.</summary>
internal static class CoreBridge {
  public static Core.Vec3 Vec(Vec3d v) => new(v.X, v.Y, v.Z);

  public static Core.Pose? CorePose(Pose? pose) =>
    pose == null
      ? null
      : new Core.Pose {
        Offset = new(pose.Offset.X, pose.Offset.Y, pose.Offset.Z),
        Rotation = new(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z),
        Stretch = new(pose.Stretch.X, pose.Stretch.Y, pose.Stretch.Z),
      };

  public static Mat4d Local(Node el, Pose? pose) =>
    Core.Engine.Local(
      Vec(el.From),
      Vec(el.Origin),
      Vec(el.Rotation),
      Vec(el.Scale),
      CorePose(pose)
    );

  public static Dictionary<string, Mat4d> WorldMatrices(
    LoadedShape shape,
    IReadOnlyDictionary<string, Pose>? poses,
    Mat4d? model
  ) {
    var outp = new Dictionary<string, Mat4d>();
    void Rec(Node el, Mat4d parentM) {
      Pose? pose = null;
      poses?.TryGetValue(el.Name, out pose);
      Mat4d m = parentM * Local(el, pose);
      outp[el.Path] = m;
      foreach (Node c in el.Children)
        Rec(c, m);
    }
    foreach (Node el in shape.Elements)
      Rec(el, model ?? Mat4d.Identity);
    return outp;
  }

  /// <summary>The row-vector float matrix <see cref="Geometry"/> returns: the transpose of the
  /// column-vector matrix <paramref name="m"/>.</summary>
  public static Matrix4x4 RowFloat(Mat4d m) =>
    new(
      (float)m[0, 0],
      (float)m[1, 0],
      (float)m[2, 0],
      (float)m[3, 0],
      (float)m[0, 1],
      (float)m[1, 1],
      (float)m[2, 1],
      (float)m[3, 1],
      (float)m[0, 2],
      (float)m[1, 2],
      (float)m[2, 2],
      (float)m[3, 2],
      (float)m[0, 3],
      (float)m[1, 3],
      (float)m[2, 3],
      (float)m[3, 3]
    );

  /// <summary>The element names and clip a game <see cref="Animation"/> carries, as a
  /// <see cref="Core.Shape"/> holding that one clip.</summary>
  public static Core.Shape ShapeOf(Animation anim) {
    var clip = new Core.Clip {
      Name = anim.Name,
      Code = anim.Code,
      Version = anim.Version,
      Quantity = anim.QuantityFrames,
    };
    foreach (AnimationKeyFrame kf in anim.KeyFrames ?? []) {
      var key = new Core.Keyframe { Frame = kf.Frame };
      foreach ((string name, AnimationKeyFrameElement e) in kf.Elements ?? [])
        key.Elements.Add(new(name, Body(e)));
      clip.Keyframes.Add(key);
    }
    var shape = new Core.Shape();
    shape.Animations.Add(clip);
    return shape;
  }

  private static JObject Body(AnimationKeyFrameElement e) {
    var body = new JObject();
    void Put(string key, double? v) {
      if (v is { } d)
        body[key] = d;
    }
    Put("offsetX", e.OffsetX);
    Put("offsetY", e.OffsetY);
    Put("offsetZ", e.OffsetZ);
    Put("rotationX", e.RotationX);
    Put("rotationY", e.RotationY);
    Put("rotationZ", e.RotationZ);
    Put("stretchX", e.StretchX);
    Put("stretchY", e.StretchY);
    Put("stretchZ", e.StretchZ);
    return body;
  }
}
