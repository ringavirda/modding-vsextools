using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Core;

/// <summary>One animation of a shape.</summary>
public sealed class Clip {
  /// <summary>The clip's <c>code</c>, null when absent.</summary>
  public string? Code { get; set; }

  /// <summary>The clip's <c>version</c>, null when absent.</summary>
  public int? Version { get; set; }

  /// <summary>The <c>quantityFrames</c> length in frames, null when absent.</summary>
  public int? Quantity { get; set; }

  /// <summary>The <c>onAnimationEnd</c> value (<c>Hold</c>, <c>Repeat</c>, ...), null when absent.</summary>
  public string? OnAnimationEnd { get; set; }

  /// <summary>The keyframes, in file order.</summary>
  public List<Keyframe> Keyframes { get; } = [];

  /// <summary>Keys the reader does not model, in file order.</summary>
  public JObject Unknown { get; } = [];

  /// <summary>Every key in the order the file wrote it.</summary>
  public List<string> KeyOrder { get; } = [];
}

/// <summary>One keyframe of a clip.</summary>
public sealed class Keyframe {
  /// <summary>The frame number, null when absent.</summary>
  public int? Frame { get; set; }

  /// <summary>The per-element transforms by element name, as written, in file order.</summary>
  public List<KeyValuePair<string, JToken>> Elements { get; } = [];

  /// <summary>Keys the reader does not model, in file order.</summary>
  public JObject Unknown { get; } = [];

  /// <summary>Every key in the order the file wrote it.</summary>
  public List<string> KeyOrder { get; } = [];
}
