using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Core;

/// <summary>
/// One element of a shape's tree. Coordinates are the JSON's own, relative to the parent. A value
/// the file omits is null, so a writer can tell an absent key from a written default.
/// </summary>
public sealed class Element {
  /// <summary>The element's <c>name</c>; null when the file gives none.</summary>
  public string? Name { get; set; }

  /// <summary>The near corner [x, y, z], null when absent.</summary>
  public double[]? From { get; set; }

  /// <summary>The far corner [x, y, z], null when absent.</summary>
  public double[]? To { get; set; }

  /// <summary>The pivot [x, y, z]; null when the file omits it, which the game reads as 0,0,0 of
  /// the parent's frame.</summary>
  public double[]? RotationOrigin { get; set; }

  /// <summary><c>rotationX</c> in degrees, null when absent.</summary>
  public double? RotationX { get; set; }

  /// <summary><c>rotationY</c> in degrees, null when absent.</summary>
  public double? RotationY { get; set; }

  /// <summary><c>rotationZ</c> in degrees, null when absent.</summary>
  public double? RotationZ { get; set; }

  /// <summary><c>scaleX</c>, null when absent.</summary>
  public double? ScaleX { get; set; }

  /// <summary><c>scaleY</c>, null when absent.</summary>
  public double? ScaleY { get; set; }

  /// <summary><c>scaleZ</c>, null when absent.</summary>
  public double? ScaleZ { get; set; }

  /// <summary>The faces by name (north, east, south, west, up, down), in file order, disabled ones
  /// included.</summary>
  public List<KeyValuePair<string, Face>> Faces { get; } = [];

  /// <summary>The child elements, in file order.</summary>
  public List<Element> Children { get; } = [];

  /// <summary>Which numbers the file wrote without a fraction, by lowercase key; bit i of an array
  /// key's mask (bit 0 of a scalar's) is set when value i was an integer literal. A writer that
  /// reproduces the file's number forms reads it.</summary>
  public Dictionary<string, uint> IntegerForms { get; } = [];

  /// <summary>Keys the reader does not model, in file order.</summary>
  public JObject Unknown { get; } = [];

  /// <summary>Every key in the order the file wrote it.</summary>
  public List<string> KeyOrder { get; } = [];
}

/// <summary>One face of an element.</summary>
public sealed class Face {
  /// <summary>The <c>texture</c> reference as written (with its leading <c>#</c>), null when absent.</summary>
  public string? Texture { get; set; }

  /// <summary>The <c>uv</c> rectangle [u1, v1, u2, v2], null when absent.</summary>
  public double[]? Uv { get; set; }

  /// <summary>The <c>rotation</c> in degrees, null when absent.</summary>
  public double? Rotation { get; set; }

  /// <summary>The <c>enabled</c> flag, null when absent (the game reads absent as enabled).</summary>
  public bool? Enabled { get; set; }

  /// <summary>Which numbers the file wrote without a fraction, by lowercase key; bit i of an array
  /// key's mask (bit 0 of a scalar's) is set when value i was an integer literal. A writer that
  /// reproduces the file's number forms reads it.</summary>
  public Dictionary<string, uint> IntegerForms { get; } = [];

  /// <summary>Keys the reader does not model, in file order.</summary>
  public JObject Unknown { get; } = [];

  /// <summary>Every key in the order the file wrote it.</summary>
  public List<string> KeyOrder { get; } = [];
}
