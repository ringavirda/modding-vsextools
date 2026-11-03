using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Whether a cast item is its source's cast elements with the allowance out on exactly the faces the source's
/// steps cut. Each source element not tagged worked or fastener by stage is compared with the item's element of
/// the same name (the parts of a split, <c>NAME_s2</c> and on, count as one): its world box must equal the
/// source's, moved out by <see cref="CheckInput.Allowance"/> on each face a step names and nowhere else, to 1e-4.
/// One line per fault: <c>NAME: missing from the item</c>, <c>NAME: not in the source</c>,
/// <c>NAME: box [..] against [..], the source plus the allowance on [faces]</c>, or
/// <c>NAME/FACE: not a face of a cast element in the source</c>. A step face naming a group moves the faces of the
/// group's leaves that lie on its bounding face in that direction, every stave of a round run that reaches it with
/// them; a group none of whose leaves does is
/// <c>NAME/FACE: no leaf of the group lies on its bounding face</c>. Needs the source shape, its record and the item.
/// The item's record (<see cref="CheckInput.Record"/>) may carry <see cref="FlushMoves"/>: a recorded move of a face
/// no step cuts, a merge of two elements no step cuts or a cap beside a face no step cuts explains the difference
/// from source plus allowance, and the record's <see cref="Lays"/> move a kit group's elements to where the item lays
/// them apart; a move or merge on a cut face and a cap on one are faults
/// (<c>NAME/FACE: a RULE move on a face a step cuts</c>, <c>NAME: a merge on an element a step cuts</c>,
/// <c>NAME: cap not D thick beside OF/FACE, or on a face a step cuts</c>).
/// </summary>
public sealed class Allowance : ICheck {
  private const double Tol = 1e-4;

  /// <inheritdoc />
  public string Id => "allowance";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    Shape source =
      input.Source
      ?? throw new CheckUsageException(
        "allowance reads the source shape, none given"
      );
    PartRecord record =
      input.SourceRecord
      ?? throw new CheckUsageException(
        "allowance reads the source's part record, none given"
      );
    var tree = new Tree(source);
    Dictionary<string, Box> drawn = Boxes(tree, n => n.Name);
    Dictionary<string, Box> cast = Boxes(
      tree,
      n => n.Name,
      n => !Tags.LeftOutOfCast(Tags.Of(record, n))
    );
    Dictionary<string, Box> item = Boxes(
      new Tree(input.Shape),
      n => Tags.Unsplit(n.Name)
    );
    var cuts = new Dictionary<string, List<string>>();
    var lines = new List<string>();
    var frames = new Frames(source);
    List<StaveRun> runs = StaveRuns.Find(tree, frames);
    foreach (string spec in record.Steps.SelectMany(s => s.Faces)) {
      int cut = spec.LastIndexOf('/');
      string name = cut < 0 ? spec : spec[..cut];
      string face = cut < 0 ? "" : spec[(cut + 1)..];
      if (!Texels.FaceNames.Contains(face))
        throw new CheckUsageException(
          $"{spec}: a step face is NAME/north, east, south, west, up or down"
        );
      if (cast.ContainsKey(name)) {
        AddCut(cuts, name, face);
        continue;
      }
      List<Node> group =
      [
        .. tree.Leaves.Where(n =>
          !Tags.LeftOutOfCast(Tags.Of(record, n))
          && Refs.Matches(n.Path, "*" + name + "/")
        ),
      ];
      if (group.Count == 0) {
        lines.Add($"{spec}: not a face of a cast element in the source");
        continue;
      }
      List<int> outer = GroupFaces.OnBound(
        [
          .. group.Select(n =>
            (frames.Mat(n), WorldBox.Of(frames.Mat(n), n.Size))
          ),
        ],
        face,
        [
          .. runs.Select(r => r.Members.Select(m => group.IndexOf(m)).ToList())
            .Where(r => r.All(i => i >= 0)),
        ]
      );
      if (outer.Count == 0)
        lines.Add($"{spec}: no leaf of the group lies on its bounding face");
      foreach (int i in outer)
        AddCut(cuts, group[i].Name, face);
    }
    List<FlushMove> moves = FlushMoves.Read(input.Record);
    Dictionary<string, Vec3> lays = Lays.Read(input.Record);
    var absorbed = moves
      .Where(m => m.Rule == "merge")
      .ToDictionary(m => m.Element, m => m.Into ?? "");
    foreach ((string name, Box want) in cast) {
      List<string> faces = cuts.GetValueOrDefault(name) ?? [];
      if (absorbed.ContainsKey(name)) {
        if (faces.Count > 0)
          lines.Add($"{name}: a merge on an element a step cuts");
        continue;
      }
      Box out_ = Moved(want, faces, input.Allowance);
      foreach (
        FlushMove m in moves.Where(m =>
          m.Rule is not ("merge" or "cap") && m.Element == name
        )
      ) {
        if (faces.Contains(m.Face))
          lines.Add($"{name}/{m.Face}: a {m.Rule} move on a face a step cuts");
        else
          out_ = Shifted(out_, m.Face, m.Distance);
      }
      foreach (
        FlushMove m in moves.Where(m => m.Rule == "merge" && m.Into == name)
      ) {
        if (faces.Count > 0)
          lines.Add($"{name}: a merge on an element a step cuts");
        if (cast.TryGetValue(m.Element, out Box other))
          out_ = Union(out_, other);
      }
      if (lays.TryGetValue(name, out Vec3 laid))
        out_ = new Box(out_.Lo + laid, out_.Hi + laid);
      if (!item.TryGetValue(name, out Box got)) {
        lines.Add($"{name}: missing from the item");
        continue;
      }
      if (!Near(got, out_))
        lines.Add(
          $"{name}: box {Show(got)} against {Show(out_)}, the source plus the allowance on [{string.Join(", ", faces)}]"
        );
    }
    foreach ((string name, Box got) in item)
      if (!drawn.ContainsKey(name)) {
        FlushMove? cap = moves.FirstOrDefault(m =>
          m.Rule == "cap" && m.Element == name
        );
        if (cap == null)
          lines.Add($"{name}: not in the source");
        else if (
          cuts.GetValueOrDefault(cap.Of ?? "")?.Contains(cap.Face) == true
          || !item.TryGetValue(cap.Of ?? "", out Box of)
          || !CapFits(got, cap, of)
        )
          lines.Add(
            $"{name}: cap not {Fmt.G(cap.Distance)} thick beside {cap.Of}/{cap.Face}, or on a face a step cuts"
          );
      }
    bool faults = lines.Count > 0;
    if (!faults)
      lines.Add($"allowance: {cast.Count} elements ok");
    return CheckResult.Of(Id, cast.Count, faults, lines);
  }

  private static void AddCut(
    Dictionary<string, List<string>> cuts,
    string name,
    string face
  ) {
    if (!cuts.TryAdd(name, [face]) && !cuts[name].Contains(face))
      cuts[name].Add(face);
  }

  private static Dictionary<string, Box> Boxes(
    Tree tree,
    Func<Node, string> key,
    Func<Node, bool>? keep = null
  ) {
    var frames = new Frames(tree.Shape);
    var boxes = new Dictionary<string, Box>();
    foreach (Node n in tree.Leaves.Where(n => keep?.Invoke(n) ?? true)) {
      Box b = WorldBox.Of(frames.Mat(n), n.Size);
      string k = key(n);
      boxes[k] = boxes.TryGetValue(k, out Box was)
        ? new Box(
          new Vec3(
            Math.Min(was.Lo.X, b.Lo.X),
            Math.Min(was.Lo.Y, b.Lo.Y),
            Math.Min(was.Lo.Z, b.Lo.Z)
          ),
          new Vec3(
            Math.Max(was.Hi.X, b.Hi.X),
            Math.Max(was.Hi.Y, b.Hi.Y),
            Math.Max(was.Hi.Z, b.Hi.Z)
          )
        )
        : b;
    }
    return boxes;
  }

  private static Box Moved(Box b, List<string> faces, double a) {
    double[] lo = b.Lo.ToArray(),
      hi = b.Hi.ToArray();
    foreach (string face in faces)
      switch (face) {
        case "east":
          hi[0] += a;
          break;
        case "west":
          lo[0] -= a;
          break;
        case "up":
          hi[1] += a;
          break;
        case "down":
          lo[1] -= a;
          break;
        case "south":
          hi[2] += a;
          break;
        default:
          lo[2] -= a;
          break;
      }
    return new Box(
      new Vec3(lo[0], lo[1], lo[2]),
      new Vec3(hi[0], hi[1], hi[2])
    );
  }

  private static Box Shifted(Box b, string face, double d) {
    double[] lo = b.Lo.ToArray(),
      hi = b.Hi.ToArray();
    switch (face) {
      case "east":
        hi[0] += d;
        break;
      case "west":
        lo[0] -= d;
        break;
      case "up":
        hi[1] += d;
        break;
      case "down":
        lo[1] -= d;
        break;
      case "south":
        hi[2] += d;
        break;
      default:
        lo[2] -= d;
        break;
    }
    return new Box(
      new Vec3(lo[0], lo[1], lo[2]),
      new Vec3(hi[0], hi[1], hi[2])
    );
  }

  private static Box Union(Box a, Box b) =>
    new(
      new Vec3(
        Math.Min(a.Lo.X, b.Lo.X),
        Math.Min(a.Lo.Y, b.Lo.Y),
        Math.Min(a.Lo.Z, b.Lo.Z)
      ),
      new Vec3(
        Math.Max(a.Hi.X, b.Hi.X),
        Math.Max(a.Hi.Y, b.Hi.Y),
        Math.Max(a.Hi.Z, b.Hi.Z)
      )
    );

  private static bool CapFits(Box b, FlushMove cap, Box of) {
    int axis = cap.Face switch {
      "east" or "west" => 0,
      "up" or "down" => 1,
      _ => 2,
    };
    bool hi = cap.Face is "east" or "up" or "south";
    double plane = hi ? of.Hi[axis] : of.Lo[axis];
    double start = hi ? b.Lo[axis] : b.Hi[axis];
    if (
      Math.Abs(b.Hi[axis] - b.Lo[axis] - cap.Distance) > Tol
      || Math.Abs(start - plane) > Tol
    )
      return false;
    return Enumerable
      .Range(0, 3)
      .Where(i => i != axis)
      .All(i => b.Lo[i] >= of.Lo[i] - Tol && b.Hi[i] <= of.Hi[i] + Tol);
  }

  private static bool Near(Box a, Box b) =>
    Enumerable
      .Range(0, 3)
      .All(i =>
        Math.Abs(a.Lo[i] - b.Lo[i]) <= Tol && Math.Abs(a.Hi[i] - b.Hi[i]) <= Tol
      );

  private static string Show(Box b) =>
    $"{Fmt.List(b.Lo.ToArray())}..{Fmt.List(b.Hi.ToArray())}";
}
