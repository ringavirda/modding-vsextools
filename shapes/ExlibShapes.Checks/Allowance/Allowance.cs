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
/// <c>NAME/FACE: not a face of a cast element in the source</c>. Needs the source shape, its record and the item.
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
    foreach (string spec in record.Steps.SelectMany(s => s.Faces)) {
      int cut = spec.LastIndexOf('/');
      string name = cut < 0 ? spec : spec[..cut];
      string face = cut < 0 ? "" : spec[(cut + 1)..];
      if (!Texels.FaceNames.Contains(face))
        throw new CheckUsageException(
          $"{spec}: a step face is NAME/north, east, south, west, up or down"
        );
      if (!cast.ContainsKey(name)) {
        lines.Add($"{spec}: not a face of a cast element in the source");
        continue;
      }
      if (!cuts.TryAdd(name, [face]))
        cuts[name].Add(face);
    }
    foreach ((string name, Box want) in cast) {
      List<string> faces = cuts.GetValueOrDefault(name) ?? [];
      Box out_ = Moved(want, faces, input.Allowance);
      if (!item.TryGetValue(name, out Box got)) {
        lines.Add($"{name}: missing from the item");
        continue;
      }
      if (!Near(got, out_))
        lines.Add(
          $"{name}: box {Show(got)} against {Show(out_)}, the source plus the allowance on [{string.Join(", ", faces)}]"
        );
    }
    foreach (string name in item.Keys)
      if (!drawn.ContainsKey(name))
        lines.Add($"{name}: not in the source");
    bool faults = lines.Count > 0;
    if (!faults)
      lines.Add($"allowance: {cast.Count} elements ok");
    return CheckResult.Of(Id, cast.Count, faults, lines);
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

  private static bool Near(Box a, Box b) =>
    Enumerable
      .Range(0, 3)
      .All(i =>
        Math.Abs(a.Lo[i] - b.Lo[i]) <= Tol && Math.Abs(a.Hi[i] - b.Hi[i]) <= Tol
      );

  private static string Show(Box b) =>
    $"{Fmt.List(b.Lo.ToArray())}..{Fmt.List(b.Hi.ToArray())}";
}
