using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>One change a generator's flush resolution made to a cast item against its source.</summary>
/// <param name="Element">The element moved, merged away or added, by its name in the item.</param>
/// <param name="Rule"><c>sink</c>, <c>raise</c>, <c>trim</c>, <c>stagger</c>, <c>merge</c> or <c>cap</c>.</param>
/// <param name="Face">The face moved, or the face a cap stands on; empty for a merge.</param>
/// <param name="Distance">How far the face moved along its outward normal in voxels, negative inward; a cap's
/// thickness; 0 for a merge.</param>
/// <param name="Into">The element a merge joined <paramref name="Element"/> into.</param>
/// <param name="Of">The element a cap stands on.</param>
public sealed record FlushMove(
  string Element,
  string Rule,
  string Face,
  double Distance,
  string? Into = null,
  string? Of = null
);

/// <summary>The flush moves a part record carries, in its <see cref="PartRecord.Carried"/> slot
/// <see cref="Slot"/>.</summary>
public static class FlushMoves {
  /// <summary>The record slot the moves are kept in.</summary>
  public const string Slot = "flushMoves";

  /// <summary>The moves <paramref name="record"/> carries; none when it is null or has no slot.</summary>
  public static List<FlushMove> Read(PartRecord? record) {
    if (record == null || record.Carried[Slot] is not JArray rows)
      return [];
    return
    [
      .. rows.OfType<JObject>()
        .Select(r => new FlushMove(
          r.Value<string>("element") ?? "",
          r.Value<string>("rule") ?? "",
          r.Value<string>("face") ?? "",
          r.Value<double?>("distance") ?? 0,
          r.Value<string>("into"),
          r.Value<string>("of")
        )),
    ];
  }

  /// <summary>Replaces the moves <paramref name="record"/> carries with <paramref name="moves"/>; an empty list
  /// removes the slot.</summary>
  public static void Write(PartRecord record, IEnumerable<FlushMove> moves) {
    var rows = new JArray();
    foreach (FlushMove m in moves) {
      var o = new JObject { ["element"] = m.Element, ["rule"] = m.Rule };
      if (m.Face != "")
        o["face"] = m.Face;
      if (m.Rule != "merge")
        o["distance"] = m.Distance;
      if (m.Into != null)
        o["into"] = m.Into;
      if (m.Of != null)
        o["of"] = m.Of;
      rows.Add(o);
    }
    if (rows.Count == 0)
      record.Carried.Remove(Slot);
    else
      record.Carried[Slot] = rows;
  }
}
