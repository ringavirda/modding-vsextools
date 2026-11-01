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

/// <summary>What a check reads. Each check takes the parts it needs.</summary>
/// <param name="Shape">The shape under check; null is an empty input.</param>
/// <param name="Shipped">The shape the copy was made from, for the uv comparison.</param>
/// <param name="Table">The uv table, required with <paramref name="Shipped"/>.</param>
/// <param name="Sizes">The sizes spec rows.</param>
/// <param name="WoodKeys">The texture keys whose grain runs along a part; the legacy set when null.</param>
public sealed record CheckInput(
  Shape? Shape,
  Shape? Shipped = null,
  UvTable? Table = null,
  IReadOnlyList<SizeRow>? Sizes = null,
  IReadOnlySet<string>? WoodKeys = null
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
