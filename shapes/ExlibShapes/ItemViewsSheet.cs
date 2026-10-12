using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace ExpandedLib.Shapes;

public static partial class ItemViews {
  private const int SheetCellWidth = 192;
  private const int SheetCellHeight = 144;
  private const int SheetLabelHeight = 40;
  private const int SheetFrame = 4;
  private static readonly SKColor SheetBackground = new(0xE8, 0xE8, 0xE4);

  /// <summary>
  /// Writes the contact sheet of the dumps whose manifests <see cref="WriteDump"/> returned: one row
  /// per manifest, a column per view in the order gui, tp, fp, ground, iso, the dump's view and
  /// kind under it and each failing gate in red. A row with a failing gate has a red frame.
  /// </summary>
  /// <param name="manifests">The manifests, in row order.</param>
  /// <param name="views">The views to give a column; one that a manifest has no picture for is
  /// left blank in its row.</param>
  /// <param name="path">The PNG to write; its folder must exist.</param>
  /// <exception cref="ArgumentException"><paramref name="manifests"/> is empty or
  /// <paramref name="views"/> names no view.</exception>
  public static void WriteSheet(
    IReadOnlyList<JObject> manifests,
    IReadOnlyList<string> views,
    string path
  ) {
    if (manifests.Count == 0)
      throw new ArgumentException("no dump to sheet", nameof(manifests));
    string[] columns = [.. SheetOrder.Where(views.Contains)];
    if (columns.Length == 0)
      throw new ArgumentException("no view to sheet", nameof(views));
    int rowHeight = SheetCellHeight + SheetLabelHeight;
    using var sheet = new SKBitmap(
      columns.Length * SheetCellWidth,
      manifests.Count * rowHeight
    );
    using var canvas = new SKCanvas(sheet);
    canvas.Clear(SheetBackground);
    using var font = new SKFont(Text.Face, 12);
    using var black = new SKPaint {
      Color = SKColors.Black,
      IsAntialias = true,
    };
    using var red = new SKPaint { Color = SKColors.Red, IsAntialias = true };
    using var scaling = new SKPaint { FilterQuality = SKFilterQuality.High };
    for (int row = 0; row < manifests.Count; row++) {
      JObject manifest = manifests[row];
      int top = row * rowHeight;
      string[] files =
      [
        .. ((JArray)manifest["files"]!).Select(f => (string)f!),
      ];
      for (int col = 0; col < columns.Length; col++) {
        string? file = files.FirstOrDefault(f =>
          f.EndsWith($"-{columns[col]}.png", StringComparison.Ordinal)
        );
        if (file == null)
          continue;
        using SKBitmap image = SKBitmap.Decode(file);
        float scale = Math.Min(
          (float)SheetCellWidth / image.Width,
          (float)SheetCellHeight / image.Height
        );
        float w = image.Width * scale;
        float h = image.Height * scale;
        canvas.DrawBitmap(
          image,
          SKRect.Create(
            col * SheetCellWidth + (SheetCellWidth - w) / 2,
            top + (SheetCellHeight - h) / 2,
            w,
            h
          ),
          scaling
        );
      }
      string[] failing =
      [
        .. ((JArray)manifest["failing"]!).Select(g =>
          string.Join(' ', ((string)g!).Split(' ').Take(2))
        ),
      ];
      canvas.DrawText(
        $"{(string)manifest["view"]!}  ({(string)manifest["kind"]!})",
        SheetFrame + 2,
        top + SheetCellHeight + 14,
        font,
        black
      );
      if (failing.Length > 0)
        canvas.DrawText(
          string.Join(", ", failing),
          SheetFrame + 2,
          top + SheetCellHeight + 30,
          font,
          red
        );
      if (failing.Length > 0) {
        using var frame = new SKPaint {
          Color = SKColors.Red,
          Style = SKPaintStyle.Stroke,
          StrokeWidth = SheetFrame,
        };
        canvas.DrawRect(
          SKRect.Create(
            SheetFrame / 2f,
            top + SheetFrame / 2f,
            sheet.Width - SheetFrame,
            rowHeight - SheetFrame
          ),
          frame
        );
      }
    }
    Save(sheet, path);
  }

  private static readonly string[] SheetOrder =
  [
    "gui",
    "tp",
    "fp",
    "ground",
    "iso",
  ];
}
