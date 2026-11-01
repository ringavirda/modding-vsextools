using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Whether every face takes the texture of its element's role. A pattern record's elements all take the
/// <c>pattern</c> role and a core box record's the <c>corebox</c> role; any other element takes the role its
/// record row (or its nearest tagged group's) names. One line per fault:
/// <c>PATH face: texture 'oak', the pattern role takes 'wood'</c>, or <c>PATH: no role in the record</c>.
/// Needs the record and the settings.
/// </summary>
public sealed class Role : ICheck {
  /// <inheritdoc />
  public string Id => "role";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    PartRecord record =
      input.Record
      ?? throw new CheckUsageException(
        "role reads the part record, none given"
      );
    Settings settings =
      input.Settings
      ?? throw new CheckUsageException("role reads the settings, none given");
    string? kindRole =
      record.Kinds.Contains("pattern") ? "pattern"
      : record.Kinds.Contains("corebox") ? "corebox"
      : null;
    var lines = new List<string>();
    int faces = 0;
    foreach (Node leaf in new Tree(input.Shape).Leaves) {
      string? role = kindRole ?? Tags.Of(record, leaf)?.Role;
      if (role == null) {
        lines.Add($"{leaf.Path}: no role in the record");
        faces += leaf.Faces.Count;
        continue;
      }
      string key = RoleTextures.KeyOf(settings, role);
      foreach ((string face, Face spec) in leaf.Faces) {
        faces++;
        string name = (spec.Texture ?? "").TrimStart('#');
        if (!RoleTextures.Is(input.Shape, settings, key, name))
          lines.Add(
            $"{leaf.Path} {face}: texture '{name}', the {role} role takes '{key}'"
          );
      }
    }
    bool faults = lines.Count > 0;
    if (!faults)
      lines.Add($"role: {faces} faces ok");
    return CheckResult.Of(Id, faces, faults, lines);
  }
}
