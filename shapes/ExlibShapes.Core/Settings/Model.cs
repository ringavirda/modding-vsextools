using System.Collections.Generic;

namespace ExpandedLib.Shapes.Core;

/// <summary>How a texture's uv origin is chosen on a face.</summary>
public enum TextureClass {
  /// <summary>Even grain: a seeded random origin.</summary>
  Plain,

  /// <summary>A pattern drawn for the face size: origin 0,0, unturned.</summary>
  Patterned,

  /// <summary>Each face at its place in the block (brick courses).</summary>
  Position,

  /// <summary>Planks: origin 0,0 with the grain along the part; the wood is a variant.</summary>
  Wood,
}

/// <summary>A variant placeholder of a texture path: the value written into an editable and the
/// value written into a runtime asset.</summary>
public sealed class TextureVariant {
  /// <summary>Replaces <c>{name}</c> in an editable path.</summary>
  public required string Editable { get; init; }

  /// <summary>Replaces <c>{name}</c> in a runtime path; the block or item supplies the real value
  /// when this is the placeholder itself.</summary>
  public required string Runtime { get; init; }
}

/// <summary>One texture key of the registry.</summary>
public sealed class TextureEntry {
  /// <summary>The key shapes use in their <c>textures</c> map.</summary>
  public required string Key { get; init; }

  /// <summary>The path written into an editable shape; may carry <c>{variant}</c> placeholders.</summary>
  public required string Editable { get; init; }

  /// <summary>Runtime path by mod domain; the <c>*</c> entry serves every domain not listed.</summary>
  public required IReadOnlyDictionary<string, string> Runtime { get; init; }

  /// <summary>How the uv origin is chosen.</summary>
  public required TextureClass Class { get; init; }

  /// <summary>Variant placeholders by name.</summary>
  public required IReadOnlyDictionary<
    string,
    TextureVariant
  > Variants { get; init; }
}

/// <summary>A step's machines in order, then its hand route.</summary>
public sealed class StepRow {
  /// <summary>Machines that can do the step, best first.</summary>
  public required IReadOnlyList<string> Machines { get; init; }

  /// <summary>The hand route's code, null where the item physically needs a machine.</summary>
  public string? Hand { get; init; }
}

/// <summary>A flask station's sand, margins, runner, gates, risers and capacity. Lengths are
/// voxels (16 to the block).</summary>
public sealed class Station {
  /// <summary>Sand volume corner with the smallest x, y, z.</summary>
  public required double[] SandFrom { get; init; }

  /// <summary>Sand volume corner with the largest x, y, z.</summary>
  public required double[] SandTo { get; init; }

  /// <summary>The y of the flask floor.</summary>
  public required double Floor { get; init; }

  /// <summary>The y of the parting plane, null for an open flask.</summary>
  public double? Parting { get; init; }

  /// <summary>Sand kept between a cavity and the flask faces and between pieces.</summary>
  public required double Margin { get; init; }

  /// <summary>Sand kept round a footprint, null where the station has none.</summary>
  public double? Footprint { get; init; }

  /// <summary>The runner's x span, null where the station has none.</summary>
  public double[]? RunnerX { get; init; }

  /// <summary>The runner and gate cut depth, null where the station has none.</summary>
  public double? RunnerDepth { get; init; }

  /// <summary>How many voxels across a gate is, null where the station has none.</summary>
  public double? GateAcross { get; init; }

  /// <summary>Riser cross sections (two sides) by the casting kind they feed.</summary>
  public required IReadOnlyDictionary<string, double[]> Risers { get; init; }

  /// <summary>The feeding head's height, null where the station has none.</summary>
  public double? HeadHeight { get; init; }

  /// <summary>Ladle units per cubic voxel of cast plus feeds.</summary>
  public required double LadleUnits { get; init; }
}
