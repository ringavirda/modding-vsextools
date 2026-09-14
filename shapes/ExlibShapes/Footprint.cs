using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace ExpandedLib.Shapes;

/// <summary>
/// Relates a megablock's drawn mesh to the cells it reserves, and turns the declared cells into the
/// frame the mesh is drawn in.
/// <para>
/// A megablock states its volume twice: as art the game spins by the blocktype's own
/// <c>rotateY</c>/<c>rotateYByType</c>, and as a footprint the game spins by the block's
/// <c>StructureAngle</c>. Those are separate numbers, and the second one lives in C#
/// (<c>ExpandedLib.Structures.BlockFilledMegastructure.StructureAngle</c>, which a concrete block
/// may offset - the old ppex boilers and engines add a half turn so the body rises away from the
/// player) where no blocktype file names it. A drawing therefore takes the mesh's own spin as given
/// and turns the declared cells onto it: <see cref="FrameAngle"/> picks the quarter turn that covers
/// the mesh best, which is the block's structure angle wherever the footprint tells the turns apart.
/// </para>
/// <para>
/// Boxes are measured in blocks about the principal cell's centre, the point both turns are made
/// about; shape units run 0..16 across that cell, so a unit is a sixteenth of a block.
/// </para>
/// </summary>
public static class Footprint {
  /// <summary>Blocks of overhang a mesh may have past its reserved cells. Art hangs hardware over
  /// the edge - an outlet neck, a hatch's furniture standing proud of its face - while a whole cell
  /// of overhang is exactly the misplacement this comparison exists to catch.</summary>
  public const double Overhang = 0.25;

  /// <summary>An axis-aligned box in blocks about the principal cell's centre, as (x, y, z)
  /// triples.</summary>
  public readonly record struct Box(Vector3 Lo, Vector3 Hi);

  /// <summary>
  /// The cells a megablock's own body occupies: the principal's cell and its <c>fillerOffsets</c>
  /// when the file declares any, else every structure cell (a multiblock built out of the player's
  /// own blocks reserves nothing else).
  /// </summary>
  public static IReadOnlyList<Offset> Cells(Layout layout) =>
    layout.Fillers.Count > 0
      ? [layout.Anchor, .. layout.Fillers]
      : [.. layout.Cells.Select(c => new Offset(c.X, c.Y, c.Z))];

  /// <summary>
  /// Every cell <paramref name="layout"/> reserves: its anchor, its structure cells and its filler
  /// cells, each once. <see cref="Layout.Bounds"/> spans the declared offsets alone, which for a
  /// filler-only megablock leave out the block's own cell.
  /// </summary>
  public static IReadOnlyList<Offset> Reserved(Layout layout) => [
    .. new[] { layout.Anchor }
      .Concat(layout.Cells.Select(c => new Offset(c.X, c.Y, c.Z)))
      .Concat(layout.Fillers)
      .Distinct(),
  ];

  /// <summary>The box <paramref name="cells"/> span, each cell reaching half a block either side of
  /// its centre. An empty list is the principal's own cell alone.</summary>
  public static Box CellBox(IReadOnlyList<Offset> cells) {
    if (cells.Count == 0)
      return new Box(new Vector3(-0.5f), new Vector3(0.5f));
    return new Box(
      new Vector3(cells.Min(c => c.X) - 0.5f, cells.Min(c => c.Y) - 0.5f, cells.Min(c => c.Z) - 0.5f),
      new Vector3(cells.Max(c => c.X) + 0.5f, cells.Max(c => c.Y) + 0.5f, cells.Max(c => c.Z) + 0.5f)
    );
  }

  /// <summary>The box <paramref name="shape"/>'s elements span once spun by
  /// <paramref name="spinY"/> degrees about the principal cell's centre - the block's drawn
  /// mesh.</summary>
  public static Box MeshBox(LoadedShape shape, double spinY) {
    Dictionary<string, Matrix4x4> mats = Geometry.WorldMatrices(shape);
    var lo = new Vector3(float.PositiveInfinity);
    var hi = new Vector3(float.NegativeInfinity);
    foreach (Node leaf in shape.Leaves()) {
      (Vector3 elLo, Vector3 elHi) = Geometry.Aabb(Geometry.Corners(mats[leaf.Path], (Vector3)leaf.Size));
      lo = Vector3.Min(lo, elLo);
      hi = Vector3.Max(hi, elHi);
    }
    if (float.IsInfinity(lo.X))
      return new Box(new Vector3(-0.5f), new Vector3(0.5f));
    var box = new Box(lo / 16f - new Vector3(0.5f), hi / 16f - new Vector3(0.5f));
    return Turned(box, (int)Math.Round(spinY));
  }

  /// <summary>
  /// <paramref name="box"/> turned by <paramref name="angle"/> degrees about the principal cell's
  /// centre, as the axis-aligned box of the result - exact for the four quarter turns, which are
  /// the only angles either frame uses. Y is untouched.
  /// </summary>
  public static Box Turned(Box box, int angle) {
    var xs = new List<float>();
    var zs = new List<float>();
    foreach (float x in new[] { box.Lo.X, box.Hi.X })
      foreach (float z in new[] { box.Lo.Z, box.Hi.Z }) {
        (double rx, double rz) = RotateXz(x, z, angle);
        xs.Add((float)rx);
        zs.Add((float)rz);
      }
    return new Box(new Vector3(xs.Min(), box.Lo.Y, zs.Min()), new Vector3(xs.Max(), box.Hi.Y, zs.Max()));
  }

  // ExOrientation.RotateXZ: the continuous twin of Layout.RotateOffset, 90:(z,-x) 180:(-x,-z)
  // 270:(-z,x).
  private static (double X, double Z) RotateXz(double x, double z, int angle) =>
    (((angle % 360) + 360) % 360) switch {
      90 => (z, -x),
      180 => (-x, -z),
      270 => (-z, x),
      _ => (x, z),
    };

  /// <summary>
  /// How <paramref name="mesh"/> falls outside <paramref name="cells"/>, naming the axis and both
  /// spans in blocks from the principal cell's centre, or null when it fits within
  /// <paramref name="overhang"/> blocks on every axis.
  /// </summary>
  public static string? Misfit(Box mesh, Box cells, double overhang = Overhang) {
    foreach ((string axis, float meshLo, float meshHi, float cellLo, float cellHi) in Axes(mesh, cells))
      if (meshLo < cellLo - overhang || meshHi > cellHi + overhang)
        return $"draws {axis} over [{F(meshLo)}, {F(meshHi)}] but reserves [{F(cellLo)}, {F(cellHi)}] "
          + $"(blocks from the principal's centre, {F(overhang)} of overhang allowed)";
    return null;
  }

  private static IEnumerable<(string Axis, float MeshLo, float MeshHi, float CellLo, float CellHi)> Axes(
    Box mesh,
    Box cells
  ) {
    yield return ("x", mesh.Lo.X, mesh.Hi.X, cells.Lo.X, cells.Hi.X);
    yield return ("y", mesh.Lo.Y, mesh.Hi.Y, cells.Lo.Y, cells.Hi.Y);
    yield return ("z", mesh.Lo.Z, mesh.Hi.Z, cells.Lo.Z, cells.Hi.Z);
  }

  private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

  /// <summary>How far past <paramref name="cells"/> <paramref name="mesh"/> reaches, in blocks on
  /// its worst axis; zero when it is inside.</summary>
  public static double Excess(Box mesh, Box cells) {
    double worst = 0;
    foreach ((string _, float meshLo, float meshHi, float cellLo, float cellHi) in Axes(mesh, cells))
      worst = Math.Max(worst, Math.Max(cellLo - meshLo, meshHi - cellHi));
    return Math.Max(0, worst);
  }

  /// <summary>
  /// The quarter turns of <paramref name="cells"/> that cover <paramref name="mesh"/> best - the
  /// ones where <see cref="Excess"/> is least - ascending. More than one when the footprint cannot
  /// tell those turns apart, which is every turn of a footprint symmetric about its principal.
  /// </summary>
  public static IReadOnlyList<int> Frames(Box mesh, Box cells) {
    int[] angles = [0, 90, 180, 270];
    double best = angles.Min(a => Excess(mesh, Turned(cells, a)));
    return [.. angles.Where(a => Excess(mesh, Turned(cells, a)) <= best + 1e-6)];
  }

  /// <summary>
  /// The quarter turn a drawing places <paramref name="layout"/>'s declared cells at around its
  /// principal's drawn mesh - the block's own structure angle, the one covering the mesh best
  /// (<see cref="Frames"/>). Where several cover it equally the principal's own spin is taken, that
  /// being the angle the block's art is placed at and the structure angle of every family whose C#
  /// does not offset it; failing that, the smallest of the tie. Zero when the principal resolves to
  /// no shape file (a synthetic cube fills its own cell whichever way the footprint faces) or its
  /// shape cannot be read, which leaves the declared frame as authored.
  /// </summary>
  public static int FrameAngle(Layout layout, BlockIndex index) {
    if (PrincipalMesh(layout, index) is not { } mesh)
      return 0;
    IReadOnlyList<int> frames = Frames(mesh, CellBox(Cells(layout)));
    int spin = PrincipalSpin(layout, index);
    return frames.Contains(spin) ? spin : frames[0];
  }

  // The quarter turn the principal's own art is drawn at - its shape entry's rotateY, which the
  // index has already resolved through the game's ByType rule. Zero when nothing resolves.
  private static int PrincipalSpin(Layout layout, BlockIndex index) {
    string? selector = layout.Principal ?? AnchorSelector(layout);
    if (selector == null || index.Resolve(selector) is not { } block)
      return 0;
    return (((int)Math.Round(block.RotateY / 90) * 90 % 360) + 360) % 360;
  }

  /// <summary><paramref name="layout"/> with its cells turned into the frame its principal's mesh is
  /// drawn in (<see cref="FrameAngle"/>), which is what every picture of it is laid out in.</summary>
  public static Layout Placed(Layout layout, BlockIndex index) {
    int angle = FrameAngle(layout, index);
    return angle == 0 ? layout : layout.Rotated(angle);
  }

  /// <summary>
  /// The principal's drawn mesh box, or null when it has no shape file of its own. The principal is
  /// <see cref="Layout.Principal"/> for a filler-only megablock and the selector of the cell at
  /// <see cref="Layout.Anchor"/> for a structure.
  /// </summary>
  public static Box? PrincipalMesh(Layout layout, BlockIndex index) {
    string? selector = layout.Principal ?? AnchorSelector(layout);
    if (selector == null || index.Resolve(selector) is not { ShapePath: { } path } block)
      return null;
    try {
      return MeshBox(ShapeFile.Load(path), block.RotateY);
    } catch (Exception e) {
      Console.Error.WriteLine($"warning: {path}: shape failed to load ({e.Message}); the declared footprint frame is kept");
      return null;
    }
  }

  private static string? AnchorSelector(Layout layout) {
    foreach (Cell c in layout.Cells)
      if (new Offset(c.X, c.Y, c.Z) == layout.Anchor && layout.Numbers.TryGetValue(c.Number, out string? selector))
        return selector;
    return null;
  }
}
