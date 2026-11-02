using System.Collections.Generic;
using System.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Whether the sand takes the shape of the metal exactly: no sand stands in the metal (<c>in</c>) and no cavity
/// stands outside the metal and the cores (<c>open</c>). The metal is the placed cast, every feed, the inlet cuts and
/// the drop; the cavity is the station's sand box less sand, runner box, cores and metal. Volumes are exact sums over
/// the box grid, in vx3, each with its worst half-voxel plan cell. A turned leaf is read on the 1/32 lattice, a cell
/// whose centre lies inside its box, while the sand follows it at <see cref="SandStep"/>: <c>in</c> and <c>open</c>
/// each pass up to the band, the area of the turned faces of the cast, the pattern elements fed and the cores times
/// half the sand step, in vx3, and stay exact zero where every leaf is axis-aligned. Needs the part record's pour,
/// the settings and the cast.
/// </summary>
public sealed class Impression : ICheck {
  private const double Eps = 1e-6;

  /// <summary>The grid step the generator reads a turned leaf's sand on, in voxels.</summary>
  public const double SandStep = 0.125;

  /// <inheritdoc />
  public string Id => "impression";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    Pouring p = Pouring.Of(input, Id);
    var grid = new BoxGrid(
      p.Sand.Concat(p.Runner).Concat(p.Cores).Concat(p.Metal).Append(p.Plan),
      p.Plan
    );
    bool[] sand = grid.Mark(p.Sand),
      metal = grid.Mark(p.Metal),
      solid = grid.Mark(p.Runner.Concat(p.Cores)),
      inside = grid.Mark([p.Plan]);
    bool[] inMetal = new bool[grid.Count],
      open = new bool[grid.Count];
    int cells = 0;
    for (int n = 0; n < grid.Count; n++) {
      inMetal[n] = sand[n] && metal[n];
      open[n] = inside[n] && !sand[n] && !solid[n] && !metal[n];
      if (inside[n])
        cells++;
    }
    double vIn = grid.Total(inMetal),
      vOpen = grid.Total(open);
    double band = p.Turned * SandStep / 2;
    bool failIn = vIn > band + Eps,
      failOpen = vOpen > band + Eps;
    var lines = new List<string>();
    if (failIn)
      lines.Add(
        $"in: {Fmt.G(vIn)} vx3 of sand in the metal, worst 0.5 plan cell {grid.Worst(inMetal)}, band {Fmt.G(band)}  FAIL"
      );
    if (failOpen)
      lines.Add(
        $"open: {Fmt.G(vOpen)} vx3 of cavity outside the metal and the cores, worst 0.5 plan cell {grid.Worst(open)}, band {Fmt.G(band)}  FAIL"
      );
    lines.Add(
      $"impression: {cells} cells, in {Fmt.G(vIn)} vx3, open {Fmt.G(vOpen)} vx3, band {Fmt.G(band)} vx3"
    );
    return CheckResult.Of(Id, cells, failIn || failOpen, lines);
  }
}
