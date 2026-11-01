using System.Collections.Generic;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>Which lint profile a check belongs to.</summary>
public enum Profile {
  /// <summary>Always on: what makes a shape wrong whatever the house rules.</summary>
  Structural,

  /// <summary>Our period rulings; a user can switch them off.</summary>
  ExpandedPeriod,
}

/// <summary>One row of a sizes spec: the extent an element is to span on an axis.</summary>
/// <param name="Element">The element path, else its name.</param>
/// <param name="Axis"><c>x</c>, <c>y</c> or <c>z</c>.</param>
/// <param name="Voxels">The span in voxels.</param>
public sealed record SizeRow(string Element, string Axis, double Voxels);

/// <summary>A texture key's uv row: how a created face of it takes its uv.</summary>
/// <param name="Mode"><c>random</c>, <c>position</c> or <c>patterned</c>.</param>
/// <param name="U">The patterned origin's u.</param>
/// <param name="V">The patterned origin's v.</param>
/// <param name="Rotations">The rotations a patterned face may take.</param>
public sealed record UvRow(
  string Mode,
  double U = 0,
  double V = 0,
  IReadOnlyList<double>? Rotations = null
);

/// <summary>A shipped face the copy changes on purpose.</summary>
/// <param name="Element">The element path.</param>
/// <param name="Face">The face name.</param>
/// <param name="Reason">Why, printed with the change.</param>
public sealed record FaceChange(string Element, string Face, string Reason);

/// <summary>The uv table and the allowed changes a copy is held to against the shape it was made from.</summary>
/// <param name="Rows">Texture key to its row.</param>
/// <param name="Changes">The allowed changes.</param>
public sealed record UvTable(
  IReadOnlyDictionary<string, UvRow> Rows,
  IReadOnlyList<FaceChange> Changes
);

/// <summary>A declared footprint cell: a 16-unit cube, or a box of it.</summary>
/// <param name="X">The cell's x.</param>
/// <param name="Y">The cell's y.</param>
/// <param name="Z">The cell's z.</param>
/// <param name="Box">The box in cell fractions, x1 y1 z1 x2 y2 z2; the whole cell when null.</param>
public sealed record FootCell(
  int X,
  int Y,
  int Z,
  IReadOnlyList<double>? Box = null
);

/// <summary>A reach past the footprint limit accepted for an element in a cell.</summary>
/// <param name="Element">The element path; the reach of every element under it passes too.</param>
/// <param name="Cell">The cell, x y z.</param>
/// <param name="Max">The farthest reach it passes, in voxels.</param>
/// <param name="Reason">Why, printed with the reach.</param>
public sealed record ReachAllow(
  string Element,
  IReadOnlyList<int> Cell,
  double Max,
  string Reason
);

/// <summary>The volume footsweep holds a shape to: the principal cell and <paramref name="Cells"/>, less
/// <paramref name="Drop"/>, plus <paramref name="Extra"/>.</summary>
/// <param name="Cells">The declared cells besides the principal; an entry at 0,0,0 replaces the principal's whole
/// cell. Entries of one cell add their boxes.</param>
/// <param name="Extra">Cells declared besides, added whatever <paramref name="Drop"/> says.</param>
/// <param name="Drop">Cells left out, by x y z.</param>
/// <param name="Reach">The reaches accepted past <paramref name="Limit"/>.</param>
/// <param name="Limit">The farthest reach allowed, in voxels.</param>
/// <param name="Depth">The path segments that name a part group in the report.</param>
public sealed record FootprintSpec(
  IReadOnlyList<FootCell>? Cells = null,
  IReadOnlyList<FootCell>? Extra = null,
  IReadOnlyList<FootCell>? Drop = null,
  IReadOnlyList<ReachAllow>? Reach = null,
  double Limit = 4,
  int Depth = 2
);

/// <summary>What a check reads. Each check takes the parts it needs.</summary>
/// <param name="Shape">The shape under check; null is an empty input.</param>
/// <param name="Shipped">The shape the copy was made from, for the uv comparison.</param>
/// <param name="Table">The uv table, required with <paramref name="Shipped"/>.</param>
/// <param name="Sizes">The sizes spec rows.</param>
/// <param name="Hide">The subtree references flushcheck leaves out; a filling's structure when null.</param>
/// <param name="Footprint">The declared footprint footsweep holds the shape to; the principal cell when null.</param>
/// <param name="Regular">The paths or globs (<c>*</c>, <c>?</c>, <c>[set]</c>) of octagons octcheck holds regular.</param>
/// <param name="Directed">Whether uvwrap judges the texels faces meet in world terms and a wood grain along the axis.</param>
/// <param name="Tex">The texture width in texels uvwrap judges run-ons against.</param>
/// <param name="WoodKeys">The texture keys whose grain runs along a part; the legacy set when null.</param>
public sealed record CheckInput(
  Shape? Shape,
  Shape? Shipped = null,
  UvTable? Table = null,
  IReadOnlyList<SizeRow>? Sizes = null,
  IReadOnlySet<string>? WoodKeys = null,
  IReadOnlyList<string>? Hide = null,
  FootprintSpec? Footprint = null,
  IReadOnlyList<string>? Regular = null,
  bool Directed = false,
  double Tex = 16
);

/// <summary>A check: a pure function of its input to a pass, a fail or a question.</summary>
public interface ICheck {
  /// <summary>The check's id; its folder under <c>Checks/</c>.</summary>
  string Id { get; }

  /// <summary>The profile the check runs under.</summary>
  Profile Profile { get; }

  /// <summary>Runs the check.</summary>
  /// <exception cref="CheckUsageException">The input is one the check cannot read.</exception>
  CheckResult Run(CheckInput input);
}
