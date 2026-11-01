using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>Every bolt head, nut, stud, shank and washer is wrought; the boss round it keeps its casting's
/// texture.</summary>
public static class FastenerLint {
  /// <summary>The lint's id.</summary>
  public const string Id = "fastener";

  /// <summary>The date of fallen's ruling.</summary>
  public const string Ruling = "2026-10-28";

  /// <summary>The finding in one line.</summary>
  public const string Message =
    "a fastener takes the fastener texture and an element round it does not";

  /// <summary>Lints one part. A leaf the record tags as a fastener (by stage or role) with a face off the
  /// fastener role's texture, or a leaf it tags as cast with a face on it, is an error.</summary>
  /// <param name="shape">The shape.</param>
  /// <param name="record">The shape's part record.</param>
  /// <param name="settings">The settings that give the fastener role its texture.</param>
  /// <returns>The findings in element order, and the number of leaf elements checked.</returns>
  /// <exception cref="CheckUsageException">The settings give the fastener role no texture.</exception>
  public static (IReadOnlyList<Finding> Findings, int Enumerated) Check(
    Shape shape,
    PartRecord record,
    Settings settings
  ) {
    string key = RoleTextures.KeyOf(settings, "fastener");
    var findings = new List<Finding>();
    List<Node> leaves = [.. new Tree(shape).Leaves];
    foreach (Node leaf in leaves) {
      ElementPart? part = Tags.Of(record, leaf);
      bool fastener = Tags.IsFastener(part);
      if (!fastener && part is not { Stage: "cast" })
        continue;
      foreach ((string face, Face spec) in leaf.Faces) {
        string name = (spec.Texture ?? "").TrimStart('#');
        bool wrought = RoleTextures.Is(shape, settings, key, name);
        if (fastener == wrought)
          continue;
        findings.Add(
          new Finding(
            "error",
            Id,
            fastener ? "fastener_texture" : "boss_texture",
            leaf.Path,
            fastener
              ? $"{leaf.Path} {face} is a fastener on '{name}', not '{key}'"
              : $"{leaf.Path} {face} is cast on the fastener texture '{key}'"
          )
        );
      }
    }
    return (findings, leaves.Count);
  }
}
