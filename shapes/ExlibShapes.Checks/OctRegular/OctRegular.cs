using System.Collections.Generic;
using System.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Whether the octagonal bars <see cref="CheckInput.Regular"/> names are regular, all eight faces equal: the arms of
/// the cross the across times tan 22.5 degrees and the diagonal flats within 0.01 of the axis flats. Only the
/// octagons the globs match are read (the record's <c>regular</c> globs when the input gives none); none matched enumerates nothing and fails. One report line per octagon, then
/// one <c>  FAULT PATH: ...</c> line per fault.
/// </summary>
public sealed class OctRegular : ICheck {
  /// <inheritdoc />
  public string Id => "octregular";

  /// <inheritdoc />
  public Profile Profile => Profile.ExpandedPeriod;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    List<Octagons.Reading> read = Octagons.Read(
      new Tree(input.Shape),
      new Frames(input.Shape),
      new HashSet<string>(),
      input.Regular ?? (input.Record is { } r ? Selection.RegularGlobs(r) : []),
      Octagons.Hold.Regular
    );
    var lines = new List<string>();
    foreach (Octagons.Reading o in read) {
      lines.Add(o.Line);
      lines.AddRange(o.Faults.Select(f => "  FAULT " + f));
    }
    bool faults = read.Any(o => o.Faults.Count > 0);
    lines.Add(
      $"{read.Count} regular octagons held; {(faults ? "faults" : "no fault")}"
    );
    return CheckResult.Of(Id, read.Count, faults, lines);
  }
}
