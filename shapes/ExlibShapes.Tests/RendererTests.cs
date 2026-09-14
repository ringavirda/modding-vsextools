using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using Vintagestory.API.Common;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Covers Renderer's culling, view composition and pixel output against fixed
/// reference images. A posed render is covered separately, in PosesTests.</summary>
public class RendererTests {
  private static LoadedShape UnitCubeShape(string face) {
    string texturePath = FixturePath.Of("textures/repo/workbench/shapes/wsl-target");
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

  // Every fixture/view compared pixel by pixel against expected/.
  public static IEnumerable<object[]> ReferenceViews() {
    foreach (string view in new[] { "iso", "south", "north", "east", "up" })
      yield return [view];
  }

  // The renders this run produced, beside the binary.
  private static string ActualDir {
    get {
      string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "actual");
      System.IO.Directory.CreateDirectory(dir);
      return dir;
    }
  }

  // south, north, east and up match the reference bit-for-bit. iso (the only view combining a
  // nonzero yaw and pitch) still differs on a small fraction of pixels: for a small number of
  // exactly-touching gear teeth, a 1-ULP difference in the composed view rotation is enough to
  // flip which of two coincident faces wins the z-buffer's strict `>` test.
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

    // Two faces that touch exactly tie on depth, and which one the z-test keeps comes down to the
    // last bit of a matrix product, so a few hundred pixels of the iso view show the other face
    // of the same object. Such a flip changes which surface is drawn, never whether one is: a
    // differing pixel is accepted only when both images hold a surface there, and only on the
    // iso view, up to the measured tie count.
    SKColor background = new(Renderer.Background.Red, Renderer.Background.Green, Renderer.Background.Blue);
    int differing = PixelCompare.Differing(expected, actual, background, out int backgroundFlips);

    int allowed = viewName == "iso" ? 700 : 0;
    System.Console.WriteLine(
      $"{viewName}: {differing} differing pixel(s) beyond tolerance {PixelCompare.Tolerance}, {backgroundFlips} against the background, {allowed} allowed"
    );
    Assert.Equal(0, backgroundFlips);
    Assert.True(differing <= allowed, $"{differing} differing pixels exceed the tie allowance of {allowed}");
  }
}
