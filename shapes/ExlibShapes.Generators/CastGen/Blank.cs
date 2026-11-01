using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Generators;

/// <summary>A blank's cast form after steps 1 and 2 and the stretch.</summary>
public sealed class BlankResult {
  /// <summary>The blank file's spec.</summary>
  public required BlankSpec Spec { get; init; }

  /// <summary>The cast elements, worked features stripped, drilled features filled, stretched.</summary>
  public List<Leaf> Cast { get; } = [];

  /// <summary>The cores the form's cored features hand on.</summary>
  public List<CoreHandoff> Cores { get; } = [];

  /// <summary>Record features that carry the blank's allowance: the spec's <c>cutFaces</c>, by
  /// default every cast feature of the form's record that names a step.</summary>
  public List<string> CutFeatures { get; } = [];

  /// <summary>The faces of <see cref="CutFeatures"/>, as <c>name/direction</c>.</summary>
  public List<string> CutFaces { get; } = [];

  /// <summary>Eye calls left open; when present <see cref="Cast"/> is empty.</summary>
  public List<Question> Questions { get; } = [];

  /// <summary>The bounds of <see cref="Cast"/>; the size the blank reaches.</summary>
  public Box Extent =>
    Union(Cast.Select(l => l.Box)) ?? new Box(Vec3.Zero, Vec3.Zero);

  /// <summary>The bounds of <paramref name="boxes"/>; null when there are none.</summary>
  public static Box? Union(IEnumerable<Box> boxes) {
    Box? u = null;
    foreach (Box b in boxes)
      u =
        u == null
          ? b
          : new Box(
            new Vec3(
              Math.Min(u.Value.Lo.X, b.Lo.X),
              Math.Min(u.Value.Lo.Y, b.Lo.Y),
              Math.Min(u.Value.Lo.Z, b.Lo.Z)
            ),
            new Vec3(
              Math.Max(u.Value.Hi.X, b.Hi.X),
              Math.Max(u.Value.Hi.Y, b.Hi.Y),
              Math.Max(u.Value.Hi.Z, b.Hi.Z)
            )
          );
    return u;
  }
}

/// <summary>The blank form: a cast form cut to a window, stripped of worked features and
/// stretched to the blank's size.</summary>
public static class Blank {
  private const double Eps = 1e-9;

  /// <summary>Builds the cast form of a blank.</summary>
  /// <param name="ctx">The context.</param>
  /// <param name="spec">The blank file.</param>
  /// <exception cref="CastGenException">The form's groups are missing, the window cuts a rotated
  /// element, a stretch meets a rotated element, or a named cut feature is not in the record.</exception>
  public static BlankResult Build(GenContext ctx, BlankSpec spec) {
    var res = new BlankResult { Spec = spec };
    FormSpec form = spec.Form;
    if (form.Box is { } box) {
      string cast = ctx.Settings.RoleKey("cast");
      var e = new Element {
        Name = spec.Blank,
        From = [0, 0, 0],
        To = (double[])box.Clone(),
      };
      foreach (
        string face in new[] { "north", "east", "south", "west", "up", "down" }
      )
        e.Faces.Add(new(face, new Face { Texture = "#" + cast }));
      res.Cast.Add(
        new Leaf(spec.Blank, spec.Blank, spec.Blank, e, "cast", "cast")
      );
      return res;
    }

    SourceRef src = form.Source!;
    ResolvedSource rs = ctx.Resolve(src.Shape, src.Sha);
    CastSetResult set = CastSet.Of(
      ctx,
      rs,
      [new GroupSpec(src.Groups, null, null)],
      spec.Cores,
      form.Window
    );
    res.Questions.AddRange(set.Questions);
    if (set.Questions.Count > 0)
      return res;
    res.Cast.AddRange(set.Cast);
    res.Cores.AddRange(set.Cores);

    foreach (StretchSpec s in form.Stretch)
      Stretch(res.Cast, s, spec.Size[s.Axis]);

    IEnumerable<string> cut =
      spec.CutFaces
      ?? rs.Record.Features.Where(kv =>
          kv.Value.Step != null && kv.Value.Origin is null or "cast"
        )
        .Select(kv => kv.Key);
    foreach (string id in cut) {
      if (!rs.Record.Features.TryGetValue(id, out Feature? f))
        throw new CastGenException(
          $"blank \"{spec.Blank}\": cut feature \"{id}\" is not in the record"
        );
      res.CutFeatures.Add(id);
      res.CutFaces.AddRange(f.Faces);
    }
    return res;
  }

  /// <summary>Moves every face past the plane by the difference between the blank's size on the
  /// axis and the form's extent there. A face on the plane stays; an element across the plane
  /// grows, one wholly past it moves.</summary>
  private static void Stretch(List<Leaf> cast, StretchSpec s, double size) {
    int a = s.Axis;
    Box? u = BlankResult.Union(cast.Select(l => l.Box));
    if (u == null)
      return;
    double d = size - (u.Value.Hi[a] - u.Value.Lo[a]);
    for (int i = 0; i < cast.Count; i++) {
      Leaf l = cast[i];
      if (!l.AxisAligned)
        throw new CastGenException(
          $"{l.SourcePath}: a rotated element cannot be stretched"
        );
      Element e = Select.Clone(l.Element, true);
      var from = (double[])e.From!.Clone();
      var to = (double[])e.To!.Clone();
      if (from[a] > s.Plane + Eps)
        from[a] += d;
      if (to[a] > s.Plane + Eps)
        to[a] += d;
      e.From = from;
      e.To = to;
      cast[i] = l with { Element = e };
    }
  }
}
