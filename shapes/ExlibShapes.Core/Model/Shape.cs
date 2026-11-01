using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Core;

/// <summary>
/// A shape file as written: its element tree, clips and texture tables, with every key the reader
/// did not recognise and the order of all keys kept for a writer to reproduce the file.
/// </summary>
public sealed class Shape {
  /// <summary>The <c>textures</c> map, key to asset string exactly as written, in file order.</summary>
  public List<KeyValuePair<string, string>> Textures { get; } = [];

  /// <summary>The <c>textureSizes</c> map, key to [width, height], in file order.</summary>
  public List<KeyValuePair<string, double[]>> TextureSizes { get; } = [];

  /// <summary>The <c>textureWidth</c> value, null when absent.</summary>
  public double? TextureWidth { get; set; }

  /// <summary>The <c>textureHeight</c> value, null when absent.</summary>
  public double? TextureHeight { get; set; }

  /// <summary>The <c>editor</c> block Model Creator writes, null when absent.</summary>
  public JObject? Editor { get; set; }

  /// <summary>The root elements, in file order.</summary>
  public List<Element> Elements { get; } = [];

  /// <summary>The <c>animations</c>, in file order.</summary>
  public List<Clip> Animations { get; } = [];

  /// <summary>Whether the file text ended with a line feed; true for a shape not read from text.</summary>
  public bool FinalNewline { get; set; } = true;

  /// <summary>Top-level keys the reader does not model, in file order.</summary>
  public JObject Unknown { get; } = [];

  /// <summary>Every top-level key in the order the file wrote it.</summary>
  public List<string> KeyOrder { get; } = [];
}
