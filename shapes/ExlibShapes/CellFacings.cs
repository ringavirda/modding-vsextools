using System.Collections.Generic;
using System.Linq;

namespace ExpandedLib.Shapes;

/// <summary>
/// The block code each cell of a facing-wildcard legend row (<c>iiex:furnace-tuyere-*</c>) draws
/// with: the horizontal side away from the one open cell beside it, the wall side the block sits
/// in. Computed from a layout's own geometry, so a turned layout yields the turned codes.
/// </summary>
/// <param name="Codes">Index into <see cref="Layout.Cells"/> to the full block code.</param>
/// <param name="Sides">Index into <see cref="Layout.Cells"/> to the side letter (n, e, s, w) the
/// code faces.</param>
/// <param name="Warnings">One line per wildcard cell left on its selector's first alternative.</param>
internal sealed record CellFacings(
  IReadOnlyDictionary<int, string> Codes,
  IReadOnlyDictionary<int, char> Sides,
  IReadOnlyList<string> Warnings
) {
  private static readonly (char Side, int Dx, int Dz)[] Steps =
  [
    ('n', 0, -1),
    ('e', 1, 0),
    ('s', 0, 1),
    ('w', -1, 0),
  ];

  /// <summary>
  /// The facings of every cell whose selector ends in <c>-*</c> at the variant group that holds
  /// the four horizontal sides, and is not an oriented selector of <see cref="Layout.Facings"/>
  /// or an as-built cell. An open neighbour is a cell whose selector
  /// <see cref="BlockIndex.Optional"/> reads as air. A cell with no open horizontal neighbour, or
  /// more than one, keeps its selector's pick and is named in <see cref="Warnings"/>.
  /// </summary>
  public static CellFacings Of(Layout layout, BlockIndex index) {
    var codes = new Dictionary<int, string>();
    var sides = new Dictionary<int, char>();
    var warnings = new List<string>();
    var numberAt = new Dictionary<Offset, int>();
    foreach (Cell c in layout.Cells)
      numberAt[new Offset(c.X, c.Y, c.Z)] = c.Number;
    var open = new Dictionary<int, bool>();
    bool IsOpen(int number) =>
      layout.Numbers.TryGetValue(number, out string? s)
      && (
        open.TryGetValue(number, out bool known)
          ? known
          : open[number] = index.Optional(s)
      );

    for (int i = 0; i < layout.Cells.Count; i++) {
      Cell c = layout.Cells[i];
      if (
        c.Finished != null
        || !layout.Numbers.TryGetValue(c.Number, out string? selector)
        || layout.Facings.ContainsKey(selector)
        || !selector.EndsWith("-*", System.StringComparison.Ordinal)
        || index.Representative(selector) is not { } pick
      )
        continue;
      int segment = index.SideSegment(pick);
      if (
        segment
        != selector[(selector.IndexOf(':') + 1)..].Split('-').Length - 1
      )
        continue;

      string where = $"{selector} at ({c.X}, {c.Y}, {c.Z})";
      var inward = Steps
        .Where(s =>
          numberAt.TryGetValue(
            new Offset(c.X + s.Dx, c.Y, c.Z + s.Dz),
            out int n
          ) && IsOpen(n)
        )
        .ToList();
      if (inward.Count != 1) {
        warnings.Add($"{where}: {inward.Count} open neighbours, no facing");
        continue;
      }
      char away = inward[0].Side switch {
        'n' => 's',
        's' => 'n',
        'e' => 'w',
        _ => 'e',
      };
      int colon = pick.Code.IndexOf(':');
      string[] parts = pick.Code[(colon + 1)..].Split('-');
      parts[segment] = away.ToString();
      string code = pick.Code[..(colon + 1)] + string.Join('-', parts);
      if (!BlockIndex.Admits(selector, code) || index.Exact(code) == null) {
        warnings.Add($"{where}: no block {code}");
        continue;
      }
      codes[i] = code;
      sides[i] = away;
    }
    return new CellFacings(codes, sides, warnings);
  }
}
