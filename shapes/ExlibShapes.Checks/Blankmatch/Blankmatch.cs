using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Whether a user of a shared cast blank matches it. <see cref="CheckInput.Shape"/> is the user's drawing placed in
/// the blank's frame and <see cref="CheckInput.Record"/> its record; <see cref="CheckInput.Source"/> and
/// <see cref="CheckInput.SourceRecord"/> are the blank's. The user's cast elements (those not tagged worked or
/// fastener) span every axis between 0.75 and 1.0 of the blank's; the cast features it names (origin cast, a
/// <c>form</c>) count as many of each form as the blank's; every face its steps cut is one of the blank's cut faces
/// (the faces of the blank record's steps, or of the features <see cref="CheckInput.CutFaces"/> names); every
/// feature it works lies inside the blank's box. An axis out of the band is a question unless the user's record
/// holds an answer for feature <c>blankmatch/x</c>, <c>blankmatch/y</c> or <c>blankmatch/z</c>; any other fault is
/// a failure and wins over a question. One line per fault or question: <c>x: spans 7 of 10 (0.7), the band is 0.75
/// to 1</c>, <c>form 'boss': 2, the blank has 1</c>, <c>Way/up: not a cut face of the blank</c> or
/// <c>NAME: lies outside the blank [..] against [..]</c>.
/// </summary>
public sealed class Blankmatch : ICheck {
  private const double Lower = 0.75;
  private const double Upper = 1.0;
  private const double Tol = 1e-4;

  /// <inheritdoc />
  public string Id => "blankmatch";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    PartRecord user =
      input.Record
      ?? throw new CheckUsageException(
        "blankmatch reads the user's part record, none given"
      );
    Shape blankShape =
      input.Source
      ?? throw new CheckUsageException(
        "blankmatch reads the blank's shape, none given"
      );
    PartRecord blank =
      input.SourceRecord
      ?? throw new CheckUsageException(
        "blankmatch reads the blank's part record, none given"
      );
    var tree = new Tree(input.Shape);
    var frames = new Frames(input.Shape);
    List<Node> userCast =
    [
      .. tree.Leaves.Where(n => !Tags.LeftOutOfCast(Tags.Of(user, n))),
    ];
    var blankTree = new Tree(blankShape);
    var blankFrames = new Frames(blankShape);
    List<Node> blankCast =
    [
      .. blankTree.Leaves.Where(n => !Tags.LeftOutOfCast(Tags.Of(blank, n))),
    ];
    if (userCast.Count == 0 || blankCast.Count == 0)
      return CheckResult.Of(Id, 0, false, []);
    Box have = Union(userCast.Select(n => WorldBox.Of(frames.Mat(n), n.Size)));
    Box room = Union(
      blankCast.Select(n => WorldBox.Of(blankFrames.Mat(n), n.Size))
    );
    var faults = new List<string>();
    var asks = new List<string>();
    var ok = new List<string>();
    int enumerated = 0;
    for (int a = 0; a < 3; a++) {
      enumerated++;
      string axis = "xyz"[a].ToString();
      double span = have.Hi[a] - have.Lo[a],
        of = room.Hi[a] - room.Lo[a];
      double ratio = of > 0 ? span / of : 0;
      if (ratio >= Lower - Tol && ratio <= Upper + Tol)
        continue;
      string line =
        $"{axis}: spans {Fmt.G(span)} of {Fmt.G(of)} ({Fmt.G(Math.Round(ratio, 4))}), the band is {Fmt.G(Lower)} to {Fmt.G(Upper)}";
      if (user.Answers.Any(x => x.Feature == "blankmatch/" + axis))
        ok.Add(line + ", answered");
      else
        asks.Add(line);
    }
    Dictionary<string, int> userForms = Forms(user),
      blankForms = Forms(blank);
    foreach (string form in userForms.Keys.Union(blankForms.Keys).Order()) {
      enumerated++;
      int got = userForms.GetValueOrDefault(form),
        want = blankForms.GetValueOrDefault(form);
      if (got != want)
        faults.Add($"form '{form}': {got}, the blank has {want}");
    }
    HashSet<string> cuts = CutFaces(input, blank);
    foreach (string face in user.Steps.SelectMany(s => s.Faces)) {
      enumerated++;
      if (!cuts.Contains(face))
        faults.Add($"{face}: not a cut face of the blank");
    }
    foreach ((string name, Feature f) in user.Features) {
      if (f.Origin is null or "cast" or "cored")
        continue;
      enumerated++;
      List<Box> boxes =
      [
        .. tree
          .Leaves.Where(n => Under(n, f.Elements))
          .Select(n => WorldBox.Of(frames.Mat(n), n.Size)),
      ];
      if (boxes.Count == 0) {
        faults.Add($"{name}: no element of the feature in the drawing");
        continue;
      }
      Box at = Union(boxes);
      if (!Inside(at, room))
        faults.Add(
          $"{name}: lies outside the blank {Show(at)} against {Show(room)}"
        );
    }
    var lines = new List<string>();
    lines.AddRange(faults);
    lines.AddRange(asks);
    lines.AddRange(ok);
    if (faults.Count > 0)
      return CheckResult.Of(Id, enumerated, true, lines);
    if (asks.Count > 0)
      return CheckResult.Ask(Id, enumerated, lines);
    lines.Add($"blankmatch: {enumerated} things match the blank");
    return CheckResult.Of(Id, enumerated, false, lines);
  }

  private static Dictionary<string, int> Forms(PartRecord r) =>
    r
      .Features.Values.Where(f => f.Origin == "cast" && f.Form != null)
      .GroupBy(f => f.Form!)
      .ToDictionary(g => g.Key, g => g.Count());

  private static HashSet<string> CutFaces(CheckInput input, PartRecord blank) {
    if (input.CutFaces == null)
      return [.. blank.Steps.SelectMany(s => s.Faces)];
    var faces = new HashSet<string>();
    foreach (string name in input.CutFaces) {
      if (!blank.Features.TryGetValue(name, out Feature? f))
        throw new CheckUsageException(
          $"cut faces name feature '{name}', the blank's record has none"
        );
      faces.UnionWith(f.Faces);
    }
    return faces;
  }

  private static bool Under(Node leaf, IEnumerable<string> names) {
    HashSet<string> set = [.. names];
    for (Node? n = leaf; n != null; n = n.Parent)
      if (set.Contains(n.Name))
        return true;
    return false;
  }

  private static bool Inside(Box a, Box room) =>
    Enumerable
      .Range(0, 3)
      .All(i => a.Lo[i] >= room.Lo[i] - Tol && a.Hi[i] <= room.Hi[i] + Tol);

  private static Box Union(IEnumerable<Box> boxes) {
    List<Box> list = [.. boxes];
    return new Box(
      new Vec3(
        list.Min(b => b.Lo.X),
        list.Min(b => b.Lo.Y),
        list.Min(b => b.Lo.Z)
      ),
      new Vec3(
        list.Max(b => b.Hi.X),
        list.Max(b => b.Hi.Y),
        list.Max(b => b.Hi.Z)
      )
    );
  }

  private static string Show(Box b) =>
    $"{Fmt.List(b.Lo.ToArray())}..{Fmt.List(b.Hi.ToArray())}";
}
