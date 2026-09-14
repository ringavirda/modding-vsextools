using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using Vintagestory.API.Common;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>
/// Ported from <c>vsshape/tests/test_render.py</c>. <c>test_pose_changes_the_render</c> is not
/// ported here: it needs <c>Poses.PoseAt</c>, which lands with T5; T5's own tests cover a posed
/// render instead.
/// </summary>
public class RendererTests {
  private static LoadedShape UnitCubeShape(string face) {
    string texturePath =
      "//wsl.localhost/archlinux" + FixturePath.Of("textures/repo/workbench/shapes/wsl-target");
    string json =
      "{\"textures\": {\"a\": \""
      + texturePath
      + "\"}, \"elements\": [{\"name\": \"C\", \"from\": [0,0,0], \"to\": [16,16,16], "
      + "\"faces\": {\""
      + face
      + "\": {\"texture\": \"#a\", \"uv\": [0,0,16,16], \"enabled\": true}}}]}";
    Shape raw = Newtonsoft.Json.JsonConvert.DeserializeObject<Shape>(json)!;
    return ShapeFile.FromRaw(raw, null);
  }

  private static bool AllBackground(SKBitmap bmp) {
    for (int y = 0; y < bmp.Height; y++)
      for (int x = 0; x < bmp.Width; x++) {
        SKColor c = bmp.GetPixel(x, y);
        if (c.Red != Renderer.Background.Red || c.Green != Renderer.Background.Green || c.Blue != Renderer.Background.Blue)
          return false;
      }
    return true;
  }

  [Fact]
  public void Cube_up_face_visible_from_up_and_culled_from_down() {
    LoadedShape shape = UnitCubeShape("up");
    using SKBitmap upImg = Renderer.Render(shape, Renderer.NamedViews["up"], ppu: 8, grid: false);
    using SKBitmap downImg = Renderer.Render(shape, Renderer.NamedViews["down"], ppu: 8, grid: false);
    Assert.False(AllBackground(upImg));
    Assert.True(AllBackground(downImg));
  }

  [Fact]
  public void Strip_sums_widths() {
    LoadedShape shape = ShapeFile.Load(FixturePath.Of("items/machined/item-shaped-gearpinion.json"));
    using SKBitmap img1 = Renderer.Render(shape, Renderer.NamedViews["south"], ppu: 16, grid: false);
    using SKBitmap img2 = Renderer.Render(shape, Renderer.NamedViews["east"], ppu: 16, grid: false);
    using SKBitmap combined = Renderer.Strip([img1, img2], ["south", "east"]);
    Assert.Equal(img1.Width + img2.Width, combined.Width);
    Assert.Equal(Math.Max(img1.Height, img2.Height), combined.Height);
  }

  // Every fixture/view the Python toolkit rendered into expected/, compared pixel by pixel.
  public static IEnumerable<object[]> ReferenceViews() {
    foreach (string view in new[] { "iso", "south", "north", "east", "up" })
      yield return [view];
  }

  // south, north, east and up are bit-for-bit against the Python renderer once two real bugs are
  // fixed: Node stored its element coordinates as System.Numerics.Vector3 (float32), which
  // diverges from the JSON's float64 literals by enough, after several composed local matrices, to
  // flip a strict z-test at two elements' exactly coincident faces (fixed by Vec3d, storing the
  // JSON's own double precision; Geometry's public float32 API is unaffected); and DrawLine
  // applied its `mul` darkening once per line-drawing *step* rather than once per distinct pixel,
  // double- or triple-darkening any pixel a short edge's sub-pixel sampling revisited (the Python
  // reference reads the whole line's pixels once, multiplies, then scatters - fixed by
  // deduplicating before applying).
  //
  // iso (the only view combining a nonzero yaw AND pitch) still differs on ~0.6% of pixels: every
  // world-space quad point (156 quads, all 26 leaves) and the view rotation matrix itself are
  // bit-identical to the Python reference by direct comparison, but `view_rot @ point` for a
  // handful of specific points differs from numpy's `point @ view_rot.T` in the last bit - neither
  // FMA accumulation nor a reversed summation order reproduces numpy's rounding, so this is numpy's
  // matmul (BLAS-backed) choosing a different, equally valid summation than a scalar dot product,
  // not a defect in this port. For a small number of exactly-touching gear teeth that 1-ULP
  // difference is just enough to flip which face wins the z-buffer's strict `>` test.
  // Beside expected/, so a reviewer can look at what this build actually produced without
  // rebuilding a scratch harness.
  private static string ActualDir {
    get {
      string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "actual");
      System.IO.Directory.CreateDirectory(dir);
      return dir;
    }
  }

  [Theory]
  [MemberData(nameof(ReferenceViews))]
  public void Matches_the_reference_render(string viewName) {
    LoadedShape shape = ShapeFile.Load(FixturePath.Of("items/machined/item-shaped-gearpinion.json"));
    using SKBitmap actual = Renderer.Render(shape, Renderer.NamedViews[viewName]);
    using SKBitmap expected = SKBitmap.Decode(
      FixturePath.Expected($"item-shaped-gearpinion-{viewName}.png")
    );

    string actualPath = System.IO.Path.Combine(ActualDir, $"item-shaped-gearpinion-{viewName}.png");
    using (var data = actual.Encode(SKEncodedImageFormat.Png, 100))
    using (var file = System.IO.File.OpenWrite(actualPath))
      data.SaveTo(file);

    Assert.Equal(expected.Width, actual.Width);
    Assert.Equal(expected.Height, actual.Height);

    int differing = 0;
    const int tolerance = 2;
    for (int y = 0; y < expected.Height; y++)
      for (int x = 0; x < expected.Width; x++) {
        SKColor e = expected.GetPixel(x, y);
        SKColor a = actual.GetPixel(x, y);
        if (
          Math.Abs(e.Red - a.Red) > tolerance
          || Math.Abs(e.Green - a.Green) > tolerance
          || Math.Abs(e.Blue - a.Blue) > tolerance
        )
          differing++;
      }

    System.Console.WriteLine($"{viewName}: {differing} differing pixel(s) beyond tolerance {tolerance}");
    Assert.Equal(0, differing);
  }
}
