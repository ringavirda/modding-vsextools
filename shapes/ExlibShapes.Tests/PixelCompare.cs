using System;
using SkiaSharp;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

// Counts pixels beyond tolerance between two same-sized bitmaps and asserts the count stays
// within an allowance, shared by every render/pixel comparison test.
internal static class PixelCompare {
  public const int Tolerance = 2;

  // Differing pixels beyond Tolerance on any channel. When background is given, also counts
  // (through backgroundFlips) how many of those touch the background on either side - a
  // divergence between drawn geometry and empty space, distinct from one face winning over
  // another at an exact tie.
  public static int Differing(
    SKBitmap expected,
    SKBitmap actual,
    SKColor? background = null
  ) => Differing(expected, actual, background, out _);

  public static int Differing(
    SKBitmap expected,
    SKBitmap actual,
    SKColor? background,
    out int backgroundFlips
  ) {
    int differing = 0;
    backgroundFlips = 0;
    for (int y = 0; y < expected.Height; y++)
      for (int x = 0; x < expected.Width; x++) {
        SKColor e = expected.GetPixel(x, y);
        SKColor a = actual.GetPixel(x, y);
        if (
          Math.Abs(e.Red - a.Red) > Tolerance
          || Math.Abs(e.Green - a.Green) > Tolerance
          || Math.Abs(e.Blue - a.Blue) > Tolerance
        ) {
          differing++;
          if (
            background is { } bg
            && (IsBackground(e, bg) || IsBackground(a, bg))
          )
            backgroundFlips++;
        }
      }
    return differing;
  }

  private static bool IsBackground(SKColor c, SKColor background) =>
    c.Red == background.Red
    && c.Green == background.Green
    && c.Blue == background.Blue;

  // Asserts equal dimensions and no more than `allowed` differing pixels, printing the count either way.
  public static void Assert(
    SKBitmap expected,
    SKBitmap actual,
    string name,
    int allowed = 0
  ) {
    Xunit.Assert.Equal(expected.Width, actual.Width);
    Xunit.Assert.Equal(expected.Height, actual.Height);
    int differing = Differing(expected, actual);
    Console.WriteLine(
      $"{name}: {differing} differing pixel(s) beyond tolerance {Tolerance}, {allowed} allowed"
    );
    Xunit.Assert.True(
      differing <= allowed,
      $"{differing} differing pixels exceed the tie allowance of {allowed}"
    );
  }
}
