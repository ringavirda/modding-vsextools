using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes;

/// <summary>A box inside one cell, each coordinate 0..1 of the cell, in the cell's frame.</summary>
public readonly record struct LocalBox(
  double X1,
  double Y1,
  double Z1,
  double X2,
  double Y2,
  double Z2
);

/// <summary>One drawn box of a planned layout: the cell holding it, the box, and whether it is a
/// control's selection box rather than a collision box.</summary>
public readonly record struct PlannedBox(Offset At, LocalBox Box, bool Control);

/// <summary>The collision boxes of one declared cell: none, one box or several, in the cell's frame.</summary>
public readonly record struct PlannedCell(
  Offset At,
  IReadOnlyList<LocalBox> Boxes
);

/// <summary>
/// A machine's planned layout (<c>"schema": "layout-recap/1"</c>, written by <c>vsshape layout</c>):
/// a blocktype-shaped file whose <c>attributes.fillerOffsets</c> are the declared cells, plus a
/// <c>planned</c> object carrying the glyph grid, the shape copy the plan was measured from and the
/// frame turn between that copy and the cells. The file names no variants: the copy is the one shape
/// drawn.
/// </summary>
public sealed class PlannedLayout {
  /// <summary>The declared cells (<c>fillerOffsets</c>) around the principal at the origin.</summary>
  public Layout Layout { get; }

  /// <summary>Every position of the glyph grid, '.' included, one entry per glyph.</summary>
  public IReadOnlyList<(Offset At, char Glyph)> Grid { get; }

  /// <summary>Every collision box of a declared cell, then every control's selection box.</summary>
  public IReadOnlyList<PlannedBox> Boxes { get; }

  /// <summary>The collision boxes of the principal's cell, then of each declared cell: a
  /// <c>collisionBoxes</c> list as written, a full cube when the entry has none, no box when the
  /// list is empty. The principal is absent when the file has no <c>planned.principal</c>.</summary>
  public IReadOnlyList<PlannedCell> Collision { get; }

  /// <summary>Control glyph to what it is, <c>"door mainhatch"</c>: the kind and id of a
  /// <c>planned.controls</c> entry, the ids of further entries sharing the glyph appended after
  /// commas.</summary>
  public IReadOnlyDictionary<char, string> Controls { get; }

  /// <summary><c>planned.copy.path</c>, relative to the repository root; null when the file names
  /// none.</summary>
  public string? CopyPath { get; }

  /// <summary>Degrees (a quarter turn) the copy is turned by to land in the cells' frame
  /// (<c>planned.frame.turn</c>, 0 when absent).</summary>
  public int FrameTurn { get; }

  /// <summary>The shape-frame cell the principal stands in (<c>planned.frame.anchor</c>); the copy
  /// is drawn shifted by its negation.</summary>
  public Offset FrameAnchor { get; }

  private PlannedLayout(
    Layout layout,
    IReadOnlyList<(Offset, char)> grid,
    IReadOnlyList<PlannedBox> boxes,
    IReadOnlyList<PlannedCell> collision,
    IReadOnlyDictionary<char, string> controls,
    string? copyPath,
    int frameTurn,
    Offset frameAnchor
  ) {
    Layout = layout;
    Grid = grid;
    Boxes = boxes;
    Collision = collision;
    Controls = controls;
    CopyPath = copyPath;
    FrameTurn = frameTurn;
    FrameAnchor = frameAnchor;
  }

  /// <summary>Whether <paramref name="path"/> is a planned layout: <c>schema</c> is
  /// <c>layout-recap/1</c>, or the file has a top-level <c>planned</c> object. False for a file that
  /// does not parse as a JSON object.</summary>
  public static bool Is(string path) {
    try {
      return JToken.Parse(File.ReadAllText(path)) is JObject raw
        && (
          (string?)raw["schema"] == "layout-recap/1"
          || raw["planned"] is JObject
        );
    } catch (Exception e) when (e is IOException or Newtonsoft.Json.JsonException) {
      return false;
    }
  }

  /// <summary>
  /// Reads <paramref name="path"/>. A <c>planned.grid.layers</c> row runs +x left to right, one
  /// space between glyphs, the first row at <c>planned.grid.origin</c>'s z and each next row one
  /// further +z; a layer's key is its y.
  /// </summary>
  /// <exception cref="LayoutError">The file has no <c>planned.grid</c> or no
  /// <c>fillerOffsets</c>.</exception>
  public static PlannedLayout Load(string path) {
    JObject raw = (JObject)JToken.Parse(File.ReadAllText(path));
    Layout layout = Layout.Load(path);
    if (raw["planned"]?["grid"] is not JObject grid)
      throw new LayoutError($"{path}: no planned.grid");
    int ox = (int)grid["origin"]![0]!,
      oz = (int)grid["origin"]![1]!;
    var glyphs = new List<(Offset, char)>();
    foreach (JProperty layer in ((JObject)grid["layers"]!).Properties()) {
      int y = int.Parse(layer.Name, CultureInfo.InvariantCulture);
      string[] rows = ((string)layer.Value!).Split('\n');
      for (int r = 0; r < rows.Length; r++) {
        string[] cols = rows[r]
          .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int c = 0; c < cols.Length; c++)
          glyphs.Add((new Offset(ox + c, y, oz + r), cols[c][0]));
      }
    }

    var boxes = new List<PlannedBox>();
    var controlBoxes = new List<PlannedBox>();
    var collision = new List<PlannedCell>();
    if (raw["planned"]?["principal"] is JObject principal) {
      collision.Add(CellOf(new Offset(0, 0, 0), principal));
      foreach (JToken control in principal["controls"] as JArray ?? [])
        if (control["box"] is { } selection)
          controlBoxes.Add(
            new PlannedBox(new Offset(0, 0, 0), BoxOf(selection), true)
          );
    }
    foreach (
      JObject entry in (
        (JArray)raw["attributes"]!["fillerOffsets"]!
      ).Cast<JObject>()
    ) {
      var at = new Offset((int)entry["x"]!, (int)entry["y"]!, (int)entry["z"]!);
      collision.Add(CellOf(at, entry));
      foreach (JToken box in entry["collisionBoxes"] as JArray ?? [])
        boxes.Add(new PlannedBox(at, BoxOf(box), false));
      foreach (JToken control in entry["controls"] as JArray ?? [])
        if (control["box"] is { } selection)
          controlBoxes.Add(new PlannedBox(at, BoxOf(selection), true));
    }

    var controls = new Dictionary<char, string>();
    foreach (JToken control in raw["planned"]!["controls"] as JArray ?? [])
      if ((string?)control["glyph"] is { Length: > 0 } glyph)
        controls[glyph[0]] = controls.TryGetValue(glyph[0], out string? known)
          ? $"{known}, {(string?)control["id"]}"
          : $"{(string?)control["kind"] ?? "control"} {(string?)control["id"]}";

    JToken? frame = raw["planned"]!["frame"];
    JToken? anchor = frame?["anchor"];
    return new PlannedLayout(
      layout,
      glyphs,
      [.. boxes, .. controlBoxes],
      collision,
      controls,
      (string?)raw["planned"]!["copy"]?["path"],
      (int?)frame?["turn"] ?? 0,
      anchor == null
        ? default
        : new Offset((int)anchor[0]!, (int)anchor[1]!, (int)anchor[2]!)
    );
  }

  private static PlannedCell CellOf(Offset at, JObject entry) =>
    new(
      at,
      entry["collisionBoxes"] is JArray list
        ? [.. list.Select(BoxOf)]
        : [new LocalBox(0, 0, 0, 1, 1, 1)]
    );

  private static LocalBox BoxOf(JToken box) =>
    new(
      (double)box["x1"]!,
      (double)box["y1"]!,
      (double)box["z1"]!,
      (double)box["x2"]!,
      (double)box["y2"]!,
      (double)box["z2"]!
    );

  /// <summary>The glyphs of the grid that mark something: every one but '.' and the ',' a run
  /// prints for an overhang cell, in file order.</summary>
  public IEnumerable<(Offset At, char Glyph)> Marks() =>
    Grid.Where(g => g.Glyph is not ('.' or ','));

  /// <summary>
  /// This plan turned by <paramref name="angle"/> (0, 90, 180 or 270) about the principal's cell, as
  /// <see cref="Layout.Rotated"/> turns the cells: glyph positions, boxes and the declared cells
  /// move together, and a box turns inside its cell.
  /// </summary>
  public PlannedLayout Rotated(int angle) {
    if (((angle % 360) + 360) % 360 == 0)
      return this;
    return new PlannedLayout(
      Layout.Rotated(angle),
      [.. Grid.Select(g => (Layout.RotateOffset(g.At, angle), g.Glyph))],
      [
        .. Boxes.Select(b => new PlannedBox(
          Layout.RotateOffset(b.At, angle),
          TurnBox(b.Box, angle),
          b.Control
        )),
      ],
      [
        .. Collision.Select(c => new PlannedCell(
          Layout.RotateOffset(c.At, angle),
          [.. c.Boxes.Select(b => TurnBox(b, angle))]
        )),
      ],
      Controls,
      CopyPath,
      FrameTurn,
      FrameAnchor
    );
  }

  // A cell-local point (u, v) follows RotateOffset's (x, z) turn about the cell's centre:
  // 90 (v, 1-u), 180 (1-u, 1-v), 270 (1-v, u).
  private static LocalBox TurnBox(LocalBox b, int angle) {
    (double U, double V) Turn(double u, double v) =>
      (((angle % 360) + 360) % 360) switch {
        90 => (v, 1 - u),
        180 => (1 - u, 1 - v),
        270 => (1 - v, u),
        _ => (u, v),
      };
    (double ax, double az) = Turn(b.X1, b.Z1);
    (double bx, double bz) = Turn(b.X2, b.Z2);
    return new LocalBox(
      Math.Min(ax, bx),
      b.Y1,
      Math.Min(az, bz),
      Math.Max(ax, bx),
      b.Y2,
      Math.Max(az, bz)
    );
  }
}
