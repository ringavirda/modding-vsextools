using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes;

/// <summary>
/// An as-built map: the code each brick cell of a structure ends with once the player has banded
/// and hooped it, in the structure's own offsets frame (layout north, core-relative), written by
/// the mod that owns the structure. <see cref="Attach"/> lays it over a <see cref="Layout"/> so a
/// schematic draws those blocks in place of each selector's first alternative.
/// </summary>
public static class AsBuilt {
  /// <summary>The <c>cells</c> of the map file <paramref name="path"/>, each <c>{x, y, z, code}</c>
  /// as an offset and a full block code, in file order.</summary>
  /// <exception cref="LayoutError">The file has no <c>cells</c> array, or an entry lacks one of
  /// its four fields.</exception>
  public static IReadOnlyList<(Offset At, string Code)> Read(string path) {
    JObject raw = (JObject)JToken.Parse(File.ReadAllText(path));
    if (raw["cells"] is not JArray cells)
      throw new LayoutError($"{path}: no cells array");
    var entries = new List<(Offset, string)>();
    foreach (JToken e in cells) {
      if (
        e["x"] == null
        || e["y"] == null
        || e["z"] == null
        || (string?)e["code"] is not { } code
      )
        throw new LayoutError($"{path}: an entry lacks x, y, z or code: {e}");
      entries.Add((new Offset((int)e["x"]!, (int)e["y"]!, (int)e["z"]!), code));
    }
    return entries;
  }

  /// <summary>
  /// <paramref name="layout"/> with each entry's code set as its cell's <see cref="Cell.Finished"/>
  /// and the side segment of that code found through <paramref name="index"/>. Cells with no entry
  /// are unchanged.
  /// </summary>
  /// <param name="layout">A layout as loaded, before any turn: the entries' offsets are in its frame.</param>
  /// <param name="entries">Offset and full code of each finished cell.</param>
  /// <exception cref="LayoutError">An entry sits on no cell of the layout, its code is not one its
  /// cell's selector admits, or the index does not resolve it.</exception>
  public static Layout Attach(
    Layout layout,
    BlockIndex index,
    IEnumerable<(Offset At, string Code)> entries
  ) {
    Dictionary<Offset, int> at = [];
    for (int i = 0; i < layout.Cells.Count; i++) {
      Cell c = layout.Cells[i];
      at[new Offset(c.X, c.Y, c.Z)] = i;
    }
    List<Cell> cells = [.. layout.Cells];
    foreach ((Offset o, string code) in entries) {
      if (!at.TryGetValue(o, out int i))
        throw new LayoutError(
          $"as-built cell ({o.X}, {o.Y}, {o.Z}) {code}: the layout has no cell there"
        );
      Cell c = cells[i];
      if (
        !layout.Numbers.TryGetValue(c.Number, out string? selector)
        || !BlockIndex.Admits(selector, code)
      )
        throw new LayoutError(
          $"as-built cell ({o.X}, {o.Y}, {o.Z}) {code}: its selector does not admit it"
        );
      Variant variant =
        index.Exact(code)
        ?? throw new LayoutError(
          $"as-built cell ({o.X}, {o.Y}, {o.Z}) {code}: no such block"
        );
      cells[i] = c with {
        Finished = code,
        SideSegment = index.SideSegment(variant),
      };
    }
    return new Layout(
      cells,
      layout.Numbers,
      layout.Fillers,
      layout.Facings,
      layout.Connectors,
      layout.Roles,
      layout.Anchor,
      layout.Principal
    );
  }
}
