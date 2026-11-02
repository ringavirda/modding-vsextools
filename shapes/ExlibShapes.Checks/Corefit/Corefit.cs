using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Whether a core, its box and its seats in the sand fit. A core record (<c>core</c> slot, with the drawing and its
/// record the cast item was made from): the body runs the length of the cored feature along <c>core.run</c> with no
/// gap, spans its section across, and a print lies at each end. A core box record (<c>corebox</c> slot, with the core
/// in <c>Cores[0]</c>): each tray's pocket, the empty space its walls and floor close in, is a box; the trays' pockets
/// are alike across, and together as deep as the core is thick, so the pocket sizes equal the core's; each tray's outer
/// plan is its pocket grown by a wall of 1; the boards of the up and down faces run along the pocket's long side. A
/// filling with <c>pour.cores</c>: each placed core's prints hold no sand and each print's end face lies within 1/32 of
/// the seat's sand. One line per fault, opening with the rule: <c>length</c>, <c>section</c>, <c>prints</c>,
/// <c>pocket</c>, <c>extent</c>, <c>grain</c> or <c>seat</c>; lengths agree within 0.001.
/// </summary>
public sealed partial class Corefit : ICheck {
  internal const double Tol = 1e-3;
  internal const double Wall = 1;
  internal const double SeatTol = 1.0 / 32;

  /// <inheritdoc />
  public string Id => "corefit";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    PartRecord record =
      input.Record
      ?? throw new CheckUsageException(
        "corefit reads the part record, none given"
      );
    var lines = new List<string>();
    int counted = 0;
    bool read = false;
    if (record.Carried["core"] is JObject core) {
      read = true;
      counted += CoreRules(input, core, lines);
    }
    if (record.Carried["corebox"] is JObject box) {
      read = true;
      counted += CoreboxRules(input, box, lines);
    }
    if (Applies(record)) {
      read = true;
      counted += Seats(input, lines);
    }
    if (!read)
      throw new CheckUsageException(
        "corefit reads a core, a core box or a filling with cores; the record has none"
      );
    bool faults = lines.Count > 0;
    lines.Add($"corefit: {counted} rules read");
    return CheckResult.Of(Id, counted, faults, lines);
  }

  /// <summary>Whether <paramref name="record"/> holds a core, a core box or a pour that sets cores.</summary>
  internal static bool Reads(PartRecord? record) =>
    record != null
    && (
      record.Carried["core"] is JObject
      || record.Carried["corebox"] is JObject
      || Applies(record)
    );

  private static bool Applies(PartRecord record) {
    if (record.Carried["pour"] is not JObject || Pour.IsCope(record))
      return false;
    try {
      return Pour.Read(record).Cores.Count > 0;
    } catch (CheckUsageException) {
      return false;
    }
  }

  /// <summary>The prints globs, run axis and feature id of a <c>core</c> slot.</summary>
  internal static (IReadOnlyList<string> Prints, int Axis, string Feature) Slot(
    JObject core
  ) =>
    (
      [.. (core["prints"] as JArray ?? []).Select(t => (string?)t ?? "")],
      CoreParts.Axis((string?)core["run"]),
      (string?)core["feature"]
        ?? throw new CheckUsageException("corefit: core.feature is missing")
    );

  private static int CoreRules(
    CheckInput input,
    JObject slot,
    List<string> lines
  ) {
    (IReadOnlyList<string> prints, int a, string feature) = Slot(slot);
    Shape source =
      input.Source
      ?? throw new CheckUsageException(
        "corefit reads the drawing the cast item was made from, none given"
      );
    PartRecord sourceRecord =
      input.SourceRecord
      ?? throw new CheckUsageException(
        "corefit reads the cast item's record, none given"
      );
    if (!sourceRecord.Features.TryGetValue(feature, out Feature? f))
      throw new CheckUsageException(
        $"corefit: the cast item's record has no feature '{feature}'"
      );
    var sourceTree = new Tree(source);
    var sourceFrames = new Frames(source);
    List<Box> cored =
    [
      .. sourceTree
        .Leaves.Where(n => CoreParts.Matches(n, f.Elements))
        .Select(n =>
          Pouring.Aligned(sourceFrames.Mat(n), n.Size, n.Path, "corefit")
        ),
    ];
    if (cored.Count == 0)
      throw new CheckUsageException(
        $"corefit: feature '{feature}' names no element of the drawing"
      );
    CoreParts parts = CoreParts.Of(input.Shape!, prints, null, "corefit");
    if (parts.Body.Count == 0)
      throw new CheckUsageException("corefit: the core has only prints");
    string name = "xyz"[a].ToString();
    double lo = CoreParts.Lo(parts.Body, a),
      hi = CoreParts.Hi(parts.Body, a);
    double flo = CoreParts.Lo(cored, a),
      fhi = CoreParts.Hi(cored, a);
    if (Math.Abs(lo - flo) > Tol || Math.Abs(hi - fhi) > Tol)
      lines.Add(
        $"length: the body runs {name} {Fmt.G(lo)} to {Fmt.G(hi)}, the feature {Fmt.G(flo)} to {Fmt.G(fhi)}"
      );
    double reach = double.NegativeInfinity;
    foreach (Box b in parts.Body.OrderBy(b => b.Lo[a])) {
      if (b.Lo[a] > reach + Tol && !double.IsNegativeInfinity(reach))
        lines.Add(
          $"length: a gap in the body on {name} from {Fmt.G(reach)} to {Fmt.G(b.Lo[a])}"
        );
      reach = Math.Max(reach, b.Hi[a]);
    }
    foreach (int s in Enumerable.Range(0, 3).Where(i => i != a)) {
      double slo = CoreParts.Lo(parts.Body, s),
        shi = CoreParts.Hi(parts.Body, s);
      double fslo = CoreParts.Lo(cored, s),
        fshi = CoreParts.Hi(cored, s);
      if (Math.Abs(slo - fslo) > Tol || Math.Abs(shi - fshi) > Tol)
        lines.Add(
          $"section: the body spans {"xyz"[s]} {Fmt.G(slo)} to {Fmt.G(shi)}, the feature {Fmt.G(fslo)} to {Fmt.G(fshi)}"
        );
    }
    if (!parts.Prints.Any(p => Math.Abs(p.Box.Hi[a] - lo) <= Tol))
      lines.Add($"prints: no print at the low end of {name}");
    if (!parts.Prints.Any(p => Math.Abs(p.Box.Lo[a] - hi) <= Tol))
      lines.Add($"prints: no print at the high end of {name}");
    return 5;
  }

  private static int Seats(CheckInput input, List<string> lines) {
    Pouring p = Pouring.Of(input, "corefit");
    IReadOnlyList<Shape> cores =
      input.Cores
      ?? throw new CheckUsageException("corefit reads the cores, none given");
    IReadOnlyList<PartRecord> records =
      input.CoreRecords
      ?? throw new CheckUsageException(
        "corefit reads the cores' records, none given"
      );
    if (cores.Count != p.Pour.Cores.Count || records.Count != cores.Count)
      throw new CheckUsageException(
        $"corefit: the pour sets {p.Pour.Cores.Count} cores, {cores.Count} given with {records.Count} records"
      );
    int counted = 0;
    for (int i = 0; i < cores.Count; i++) {
      JObject slot =
        records[i].Carried["core"] as JObject
        ?? throw new CheckUsageException(
          $"corefit: core {p.Pour.Cores[i].Item} has no core slot"
        );
      (IReadOnlyList<string> prints, int a, _) = Slot(slot);
      Mat4d at = p.Pour.Cores[i].At;
      Vec3 run = at.MulLinear(
        new Vec3(a == 0 ? 1 : 0, a == 1 ? 1 : 0, a == 2 ? 1 : 0)
      );
      int axis = Enumerable.Range(0, 3).MaxBy(k => Math.Abs(run[k]));
      CoreParts parts = CoreParts.Of(cores[i], prints, at, "corefit");
      double centre =
        (CoreParts.Lo(parts.Body, axis) + CoreParts.Hi(parts.Body, axis)) / 2;
      foreach ((string name, Box print) in parts.Prints) {
        string who = $"{p.Pour.Cores[i].Item} {name}";
        counted += 2;
        if (p.Sand.Any(s => Overlaps(s, print, -1)))
          lines.Add($"seat: {who} holds sand");
        bool low = (print.Lo[axis] + print.Hi[axis]) / 2 < centre;
        double plane = low ? print.Lo[axis] : print.Hi[axis];
        if (
          !p.Sand.Any(s =>
            Math.Abs((low ? s.Hi[axis] : s.Lo[axis]) - plane) <= SeatTol
            && Overlaps(s, print, axis)
          )
        )
          lines.Add(
            $"seat: {who} has no sand within {Fmt.G(SeatTol)} of its end at {"xyz"[axis]} {Fmt.G(plane)}"
          );
      }
    }
    return counted;
  }

  /// <summary>Whether the boxes overlap with positive extent on every axis but <paramref name="except"/> (-1 for none).</summary>
  private static bool Overlaps(Box a, Box b, int except) =>
    Enumerable
      .Range(0, 3)
      .Where(k => k != except)
      .All(k => Math.Min(a.Hi[k], b.Hi[k]) - Math.Max(a.Lo[k], b.Lo[k]) > 1e-6);
}
