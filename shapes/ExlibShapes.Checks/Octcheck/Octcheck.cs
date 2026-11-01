using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Whether every octagonal bar a copy builds takes the construction and proportions fallen draws them in: an
/// element with children turned 45 degrees about one of its axes is a bar of one unturned child that is its cross
/// section transposed about the same centre (or inset on one axis and outset on the other by the same amount, a
/// chamfered rectangular bar) and four chamfers turned 45 degrees, each pivoted on its own outer corner of the cross
/// section. The chamfers and the box run the bar's length to within 0.05, their ends stand at least 0.005 from the
/// bar's and from each other's, and the union of the boxes midway along is cut on 1440 bearings and held to the
/// octagon the box's outer corners span, a reach off by more than 0.02 (0.01 of the corner radius where that is
/// more) a hole or a bump. The arms of the cross are half its across, except on octagons the regular globs name, which <see cref="OctRegular"/> holds. A bored gear hub (four
/// eye chamfers) is passed unheld; octagons whose turned children all stand by name in the shipped shape are
/// fallen's own and reported, not held. A plate ring (a zero-size group of plates turned by multiples of 45 degrees
/// about one axis) is an octagon too, hollow, and its section is walked out from the axis and held the same way.
/// One report line per octagon, then one <c>  FAULT PATH: ...</c> line per fault.
/// </summary>
public sealed class Octcheck : ICheck {
  /// <inheritdoc />
  public string Id => "octcheck";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    var tree = new Tree(input.Shape);
    var fr = new Frames(input.Shape);
    ISet<string> shipped =
      input.Shipped == null
        ? new HashSet<string>()
        : new Tree(input.Shipped).Nodes.Select(n => n.Name).ToHashSet();
    List<Octagons.Reading> read = Octagons.Read(
      tree,
      fr,
      shipped,
      input.Regular ?? [],
      Octagons.Hold.Structure
    );
    var lines = new List<string>();
    foreach (Octagons.Reading r in read) {
      lines.Add(r.Line);
      lines.AddRange(r.Faults.Select(f => "  FAULT " + f));
    }
    foreach (string path in PlateRingMeasure.SixteenGons(tree))
      lines.Add(
        $"SKIPPED {path}  {PlateRingMeasure.Gon} plates, a {PlateRingMeasure.Gon}-gon: held by facetcheck, not by octcheck"
      );
    bool faults = read.Any(r => r.Faults.Count > 0);
    lines.Add(
      $"{read.Count(r => !r.Own)} octagons the copy builds; {(faults ? "faults" : "no fault")}"
    );
    return CheckResult.Of(Id, read.Count, faults, lines);
  }
}
