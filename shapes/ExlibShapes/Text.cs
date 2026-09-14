using System;
using System.IO;
using SkiaSharp;

namespace ExpandedLib.Shapes;

/// <summary>The typeface every raster label is drawn with: a Latin subset of Noto Sans carried
/// inside the assembly, so a picture comes out the same on every machine whatever fonts it has.</summary>
public static class Text {
  /// <summary>The embedded typeface. Loaded once; never disposed.</summary>
  public static SKTypeface Face { get; } = Load();

  private static SKTypeface Load() {
    using Stream stream = typeof(Text).Assembly.GetManifestResourceStream("ExpandedLib.Shapes.Fonts.NotoSans")
      ?? throw new InvalidOperationException("The embedded label font is missing from the assembly.");
    using SKData data = SKData.Create(stream);
    return SKTypeface.FromData(data)
      ?? throw new InvalidOperationException("The embedded label font could not be read.");
  }
}
