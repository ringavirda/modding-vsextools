using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>The offsets a cast item's kit groups were laid apart by, in the item's frame, in the part record's
/// <see cref="PartRecord.Carried"/> slot <see cref="Slot"/>. A group with no row sits where the source drew it.</summary>
public static class Lays {
  /// <summary>The record slot the offsets are kept in.</summary>
  public const string Slot = "lays";

  /// <summary>The offset of each element <paramref name="record"/> names, by element name; none when it is null or
  /// has no slot.</summary>
  public static Dictionary<string, Vec3> Read(PartRecord? record) {
    var lays = new Dictionary<string, Vec3>();
    if (record == null || record.Carried[Slot] is not JArray rows)
      return lays;
    foreach (JObject r in rows.OfType<JObject>())
      if (r["offset"] is JArray o && o.Count == 3)
        lays[r.Value<string>("element") ?? ""] = new Vec3(
          o[0].Value<double>(),
          o[1].Value<double>(),
          o[2].Value<double>()
        );
    return lays;
  }

  /// <summary>Replaces the offsets <paramref name="record"/> carries with <paramref name="lays"/>; an empty
  /// dictionary removes the slot.</summary>
  public static void Write(
    PartRecord record,
    IReadOnlyDictionary<string, Vec3> lays
  ) {
    if (lays.Count == 0) {
      record.Carried.Remove(Slot);
      return;
    }
    var rows = new JArray();
    foreach (
      (string element, Vec3 v) in lays.OrderBy(
        k => k.Key,
        System.StringComparer.Ordinal
      )
    )
      rows.Add(
        new JObject {
          ["element"] = element,
          ["offset"] = new JArray(v.X, v.Y, v.Z),
        }
      );
    record.Carried[Slot] = rows;
  }
}
