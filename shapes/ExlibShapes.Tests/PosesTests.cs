using System;
using System.Linq;
using Newtonsoft.Json;
using SkiaSharp;
using Vintagestory.API.Common;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Covers Poses' keyframe lookup and interpolation, including a posed render.</summary>
public class PosesTests {
  private static LoadedShape WattImproved =>
    ShapeFile.Load(FixturePath.Of("machines/steam/machine-pipe-megablock-engine-watt-improved.json"));

  private static AnimationKeyFrame[] PistonKeyframes(LoadedShape shape, string clipName) =>
    [.. Poses.Clip(shape, clipName).KeyFrames!.Where(kf => kf.Elements?.ContainsKey("Piston") == true)];

  [Fact]
  public void Pose_at_reproduces_authored_offsets() {
    LoadedShape shape = WattImproved;
    AnimationKeyFrame[] kfs = PistonKeyframes(shape, "cyclepump");
    Assert.True(kfs.Length >= 2);
    AnimationKeyFrame first = kfs[0];
    AnimationKeyFrame second = kfs[1];
    double firstY = first.Elements!["Piston"].OffsetY ?? 0.0;
    double secondY = second.Elements!["Piston"].OffsetY ?? 0.0;

    Pose poseFirst = Poses.PoseAt(shape, "cyclepump", first.Frame)["Piston"];
    Assert.Equal(firstY, poseFirst.Offset.Y, 5);

    Pose poseSecond = Poses.PoseAt(shape, "cyclepump", second.Frame)["Piston"];
    Assert.Equal(secondY, poseSecond.Offset.Y, 5);

    double midFrame = (first.Frame + second.Frame) / 2.0;
    Pose poseMid = Poses.PoseAt(shape, "cyclepump", midFrame)["Piston"];
    Assert.Equal((firstY + secondY) / 2, poseMid.Offset.Y, 5);
  }

  [Fact]
  public void Keyframed_names_contains_piston() {
    LoadedShape shape = WattImproved;
    Assert.Contains("Piston", Poses.KeyframedNames(shape, "cyclepump"));
  }

  [Fact]
  public void Missing_clip_raises_with_available_names() {
    LoadedShape shape = WattImproved;
    var ex = Assert.Throws<System.Collections.Generic.KeyNotFoundException>(
      () => Poses.Clip(shape, "no-such-clip")
    );
    Assert.Contains("cyclepump", ex.Message);
  }

  [Fact]
  public void Wraparound_interpolation() {
    // Two keyframes on a 60-frame clip: frame 45 is halfway between the frame-30 value
    // (wrapping forward past quantityframes) and the frame-0 value.
    string json = """
      {"elements": [{"name": "Arm", "from": [0, 0, 0], "to": [1, 1, 1], "faces": {}}],
        "animations": [{"code": "spin", "quantityframes": 60, "onAnimationEnd": "Repeat",
          "keyframes": [{"frame": 0, "elements": {"Arm": {"offsetY": 0.0}}},
                        {"frame": 30, "elements": {"Arm": {"offsetY": 10.0}}}]}]}
      """;
    Shape raw = JsonConvert.DeserializeObject<Shape>(json)!;
    LoadedShape shape = ShapeFile.FromRaw(raw, null);
    // prev = frame 30 (value 10), next = frame 0 wrapped to 60 (value 0), span 30, t = 0.5
    Pose pose = Poses.PoseAt(shape, "spin", 45)["Arm"];
    Assert.Equal(5.0, pose.Offset.Y, 5);
  }

  // A posed render at a fractional frame, against a self-contained fixture whose texture
  // resolves without an install (under fixtures/mods/).
  [Fact]
  public void Posed_render_matches_the_reference_frame() {
    LoadedShape shape = ShapeFile.Load(FixturePath.Of("anim/simple-clip.json"));
    var poses = Poses.PoseAt(shape, "bob", 7.5);
    using SKBitmap actual = Renderer.Render(shape, Renderer.NamedViews["south"], ppu: 8, poses: poses, grid: false);
    using SKBitmap expected = SKBitmap.Decode(FixturePath.Expected("simple-clip-south-f7.5.png"));

    PixelCompare.Assert(expected, actual, "simple-clip bob@7.5 south");
  }
}
