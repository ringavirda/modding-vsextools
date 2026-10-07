using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using SkiaSharp;
using Vintagestory.API.Common;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Covers Poses' keyframe lookup and interpolation, including a posed render.</summary>
public class PosesTests {
  private static LoadedShape WattImproved =>
    ShapeFile.Load(
      FixturePath.Of(
        "machines/steam/machine-pipe-megablock-engine-watt-improved.json"
      )
    );

  private static AnimationKeyFrame[] PistonKeyframes(
    LoadedShape shape,
    string clipName
  ) =>
    [
      .. Poses
        .Clip(shape, clipName)
        .KeyFrames!.Where(kf => kf.Elements?.ContainsKey("Piston") == true),
    ];

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
      () =>
        Poses.Clip(shape, "no-such-clip")
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
    using SKBitmap actual = Renderer.Render(
      shape,
      Renderer.NamedViews["south"],
      ppu: 8,
      poses: poses,
      grid: false
    );
    using SKBitmap expected = SKBitmap.Decode(
      FixturePath.Expected("simple-clip-south-f7.5.png")
    );

    PixelCompare.Assert(expected, actual, "simple-clip bob@7.5 south");
  }

  private static int Drawn(SKBitmap bmp) {
    int count = 0;
    for (int y = 0; y < bmp.Height; y++)
      for (int x = 0; x < bmp.Width; x++)
        if (bmp.GetPixel(x, y) != Renderer.Background)
          count++;
    return count;
  }

  // The first row from the top holding a drawn pixel.
  private static int TopRow(SKBitmap bmp) {
    for (int y = 0; y < bmp.Height; y++)
      for (int x = 0; x < bmp.Width; x++)
        if (bmp.GetPixel(x, y) != Renderer.Background)
          return y;
    return -1;
  }

  // Fails if Render ignores the canvas it is given: each frame is cropped to its own pose, and
  // the bobbing cube's top edge stands on the same row in both.
  [Fact]
  public void Frames_on_one_canvas_share_its_size_and_the_clip_moves_within_it() {
    LoadedShape shape = ShapeFile.Load(FixturePath.Of("anim/simple-clip.json"));
    View south = Renderer.NamedViews["south"];
    var low = Poses.PoseAt(shape, "bob", 0);
    var high = Poses.PoseAt(shape, "bob", 15);
    Renderer.Canvas canvas = Renderer
      .Fit(shape, south, low)!
      .Value.Union(Renderer.Fit(shape, south, high)!.Value);

    using SKBitmap lowImg = Renderer.Render(
      shape,
      south,
      ppu: 8,
      poses: low,
      grid: false,
      fitTo: canvas
    );
    using SKBitmap highImg = Renderer.Render(
      shape,
      south,
      ppu: 8,
      poses: high,
      grid: false,
      fitTo: canvas
    );

    Assert.Equal(lowImg.Width, highImg.Width);
    Assert.Equal(lowImg.Height, highImg.Height);
    Assert.Equal(8 * 8, TopRow(lowImg) - TopRow(highImg));
  }

  // Fails if Fit's extents or margin differ from the ones Render fits itself to.
  [Fact]
  public void A_render_on_its_own_fitted_canvas_matches_the_plain_render() {
    LoadedShape shape = ShapeFile.Load(FixturePath.Of("anim/slide-clip.json"));
    View iso = Renderer.NamedViews["iso"];
    var poses = Poses.PoseAt(shape, "slide", 7.5);

    using SKBitmap plain = Renderer.Render(shape, iso, ppu: 8, poses: poses);
    using SKBitmap fitted = Renderer.Render(
      shape,
      iso,
      ppu: 8,
      poses: poses,
      fitTo: Renderer.Fit(shape, iso, poses)
    );

    PixelCompare.Assert(plain, fitted, "slide@7.5 iso on its own canvas");
  }

  // Fails if Render draws the floor grid over its own pose's range when the canvas names a wider
  // one: the grid under the place the cube slides to would be missing from the first frame.
  [Fact]
  public void The_floor_grid_spans_the_canvas_grid_range() {
    LoadedShape shape = ShapeFile.Load(FixturePath.Of("anim/slide-clip.json"));
    View iso = Renderer.NamedViews["iso"];
    var start = Poses.PoseAt(shape, "slide", 0);
    Renderer.Canvas own = Renderer.Fit(shape, iso, start)!.Value;
    Renderer.Canvas union = own.Union(
      Renderer.Fit(shape, iso, Poses.PoseAt(shape, "slide", 15))!.Value
    );

    Assert.Equal(16, own.GridX1);
    Assert.Equal(32, union.GridX1);
    using SKBitmap wide = Renderer.Render(
      shape,
      iso,
      ppu: 8,
      poses: start,
      fitTo: union
    );
    using SKBitmap narrow = Renderer.Render(
      shape,
      iso,
      ppu: 8,
      poses: start,
      fitTo: union with
      {
        GridX1 = own.GridX1,
      }
    );

    Assert.True(Drawn(wide) > Drawn(narrow));
  }

  // Fails if `render --anim` crops each frame to its own pose: the cube's frames would all be
  // the one cube's size.
  [Fact]
  public void Render_anim_writes_every_frame_on_the_whole_clips_canvas() {
    string outDir = Path.Combine(
      Path.GetTempPath(),
      "exlib-shapes-anim-" + Guid.NewGuid()
    );
    try {
      string file = FixturePath.Of("anim/slide-clip.json");
      Assert.Equal(
        0,
        Program.RunRender(
          [
            file,
            "--out",
            outDir,
            "--views",
            "south",
            "--ppu",
            "8",
            "--anim",
            "slide",
            "--frames",
            "0,15",
          ]
        )
      );
      using SKBitmap first = SKBitmap.Decode(
        Path.Combine(outDir, "slide-clip-south-f0.png")
      );
      using SKBitmap last = SKBitmap.Decode(
        Path.Combine(outDir, "slide-clip-south-f15.png")
      );
      using SKBitmap still = Renderer.Render(
        ShapeFile.Load(file),
        Renderer.NamedViews["south"],
        ppu: 8
      );

      Assert.Equal(first.Width, last.Width);
      Assert.Equal(still.Width + 16 * 8, first.Width);
    } finally {
      if (Directory.Exists(outDir))
        Directory.Delete(outDir, true);
    }
  }
}
