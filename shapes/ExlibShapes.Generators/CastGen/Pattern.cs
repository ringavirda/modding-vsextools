using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Generators;

/// <summary>A core print the pattern carries.</summary>
/// <param name="Name">The print's element name.</param>
/// <param name="From">Corner with the smallest x, y, z, in the frame of the cast leaves.</param>
/// <param name="To">Corner with the largest x, y, z.</param>
public sealed record PatternPrint(string Name, double[] From, double[] To);

/// <summary>A pattern and the loose pieces drawn out of it.</summary>
/// <param name="Pattern">The pattern: the cast's outer form with its prints. A closed pour's is two root groups,
/// <c>Drag</c> and <c>Cope</c>.</param>
/// <param name="Loose">The loose pieces, each its own item named <c>NAME-loose1</c>, <c>NAME-loose2</c> and on.</param>
public sealed record PatternResult(
  GenOutput Pattern,
  IReadOnlyList<GenOutput> Loose
);

/// <summary>Step 9: the pattern of a casting.</summary>
public static partial class Patterns {
  private const double Eps = 1e-9;

  [GeneratedRegex(@", (\d+) not unbroken")]
  private static partial Regex Broken();

  /// <summary>The pattern of a casting: its cast leaves (allowances already out) and the core prints, every face
  /// the pattern role's texture. Where a column of the pattern would not draw from the parting, the leaves that
  /// break it are taken off as loose pieces, each set of touching leaves one item. A closed pour's pattern is cut
  /// at the parting into the roots <c>Drag</c> (below) and <c>Cope</c>, a leaf crossing it into <c>NAME</c> and
  /// <c>NAME_cope</c>; an open pour's is drawn upward, its parting the top of the casting. Elements over 16 are cut
  /// as the cast's are.</summary>
  /// <param name="ctx">The context; its settings give the pattern role and the texture's class.</param>
  /// <param name="item">The pattern item's name.</param>
  /// <param name="cast">The cast leaves, in the frame the parting is given in.</param>
  /// <param name="prints">The core prints, in the same frame.</param>
  /// <param name="parting">The y of the parting; null for an open pour.</param>
  /// <param name="spec">The spec text the provenance hash is taken over.</param>
  /// <param name="head">The height of a feeding head, in voxels, for a piece stood on end; none when null. The head
  /// is the element <c>Head</c> over the top of the casting, the plan of the leaves that reach the top, and is
  /// drawn after the loose pieces are taken off.</param>
  /// <exception cref="CastGenException">A turned leaf crosses the parting, or no leaf split off draws the
  /// pattern.</exception>
  public static PatternResult Make(
    GenContext ctx,
    string item,
    IReadOnlyList<Leaf> cast,
    IReadOnlyList<PatternPrint> prints,
    double? parting,
    string spec,
    double? head = null
  ) {
    string key = "#" + ctx.Settings.RoleKey("pattern");
    List<Leaf> leaves =
    [
      .. cast.Select(l => Restyle(l, l.Name, l.Element, key)),
      .. prints.Select(p =>
        Restyle(
          new Leaf(p.Name, p.Name, p.Name, new Element(), "cast", "pattern"),
          p.Name,
          PrintBox(p.Name, p.From, p.To),
          key
        )
      ),
    ];
    double draw = parting ?? leaves.Max(l => l.Box.Hi.Y);
    (List<Leaf> main, List<List<Leaf>> loose) = Detach(item, leaves, draw);

    if (parting is double cut)
      main = [.. main.SelectMany(l => Cut(l, cut))];
    if (head is double rise)
      main.Add(Head(main, rise, key));
    var made = new List<GenOutput>();
    var hash = HashOf(spec, cast, prints, parting, ctx.Settings, head);
    GenOutput pattern = Output(ctx, item, Splits.Apply(main), parting, hash);
    for (int i = 0; i < loose.Count; i++)
      made.Add(
        Output(ctx, $"{item}-loose{i + 1}", Splits.Apply(loose[i]), null, hash)
      );
    return new PatternResult(pattern, made);
  }

  private static Leaf Restyle(
    Leaf leaf,
    string name,
    Element element,
    string key
  ) {
    Element e = Select.Clone(element, true);
    e.Name = name;
    foreach (KeyValuePair<string, Face> kv in e.Faces)
      kv.Value.Texture = key;
    return leaf with {
      Name = name,
      Element = e,
      Stage = "cast",
      Role = "pattern",
    };
  }

  private static Leaf Head(List<Leaf> main, double rise, string key) {
    double top = main.Max(l => l.Box.Hi.Y);
    List<Box> reach =
    [
      .. main.Where(l => l.Box.Hi.Y >= top - Eps).Select(l => l.Box),
    ];
    Element e = PrintBox(
      "Head",
      [reach.Min(b => b.Lo.X), top, reach.Min(b => b.Lo.Z)],
      [reach.Max(b => b.Hi.X), top + rise, reach.Max(b => b.Hi.Z)]
    );
    foreach (KeyValuePair<string, Face> kv in e.Faces)
      kv.Value.Texture = key;
    return new Leaf("Head", "Head", "Head", e, "cast", "pattern");
  }

  private static Element PrintBox(string name, double[] from, double[] to) {
    var e = new Element {
      Name = name,
      From = [.. from],
      To = [.. to],
    };
    foreach (string face in FaceAxes.Names)
      e.Faces.Add(new(face, new Face()));
    return e;
  }

  private static (List<Leaf> Main, List<List<Leaf>> Loose) Detach(
    string item,
    List<Leaf> leaves,
    double parting
  ) {
    List<Leaf> main = [.. leaves];
    var taken = new List<Leaf>();
    int broken = BrokenColumns(main, parting);
    while (broken > 0) {
      Leaf? best = null;
      int least = broken;
      foreach (Leaf l in main.OrderBy(l => l.Name, StringComparer.Ordinal)) {
        int left = BrokenColumns([.. main.Where(m => m != l)], parting);
        if (left < least) {
          best = l;
          least = left;
        }
      }
      if (best == null)
        throw new CastGenException(
          $"{item}: {broken} columns do not draw from the parting at y {Fmt.G(parting)} and no one leaf taken off frees them"
        );
      main.Remove(best);
      taken.Add(best);
      broken = least;
    }

    var groups = new List<List<Leaf>>();
    foreach (Leaf l in taken) {
      List<List<Leaf>> touching =
      [
        .. groups.Where(g => g.Any(m => Touch(m.Box, l.Box))),
      ];
      var merged = new List<Leaf> { l };
      foreach (List<Leaf> g in touching) {
        merged.AddRange(g);
        groups.Remove(g);
      }
      groups.Add(merged);
    }
    return (
      main,
      [
        .. groups
          .Select(g => g.OrderBy(l => l.Name, StringComparer.Ordinal).ToList())
          .OrderBy(g => g[0].Name, StringComparer.Ordinal),
      ]
    );
  }

  private static int BrokenColumns(List<Leaf> leaves, double parting) =>
    leaves.Count == 0
      ? 0
      : new Draw()
        .Run(new CheckInput(Flush.ShapeOf(leaves), Parting: parting))
        .Lines.Select(l => Broken().Match(l))
        .Where(m => m.Success)
        .Sum(m => int.Parse(m.Groups[1].Value));

  private static bool Touch(Box a, Box b) =>
    Enumerable
      .Range(0, 3)
      .All(i => a.Lo[i] <= b.Hi[i] + Eps && b.Lo[i] <= a.Hi[i] + Eps);

  private static IEnumerable<Leaf> Cut(Leaf l, double parting) {
    Box b = l.Box;
    if (b.Hi.Y <= parting + Eps || b.Lo.Y >= parting - Eps) {
      yield return l;
      yield break;
    }
    if (!l.AxisAligned)
      throw new CastGenException(
        $"{l.Name}: a turned leaf crosses the parting at y {Fmt.G(parting)}"
      );
    Element low = Select.Clone(l.Element, true),
      high = Select.Clone(l.Element, true);
    low.To![1] = parting;
    high.From![1] = parting;
    high.Name = l.Name + "_cope";
    yield return l with {
      Element = low,
    };
    yield return l with {
      Name = high.Name,
      Element = high,
    };
  }

  private static GenOutput Output(
    GenContext ctx,
    string item,
    List<Leaf> leaves,
    double? parting,
    string hash
  ) {
    var rec = new PartRecord { Shape = item + ".json", Owner = "generated" };
    rec.Kinds.Add("pattern");
    foreach (Leaf l in leaves)
      rec.Elements[l.Name] = new ElementPart {
        Stage = "cast",
        Role = "pattern",
      };
    leaves = Uvs.Unroll(ctx, item, [.. Uvs.Assign(ctx, item, leaves)]);

    var shape = new Shape();
    if (parting is double cut)
      foreach (
        (string half, bool cope) in new[] { ("Drag", false), ("Cope", true) }
      ) {
        var root = new Element {
          Name = half,
          From = [0, 0, 0],
          To = [0, 0, 0],
        };
        foreach (Leaf l in leaves.Where(l => (l.Box.Lo.Y >= cut - Eps) == cope))
          root.Children.Add(Named(l));
        shape.Elements.Add(root);
      }
    else
      foreach (Leaf l in leaves)
        shape.Elements.Add(Named(l));
    foreach (
      string key in leaves
        .SelectMany(l => l.Element.Faces)
        .Select(kv => (kv.Value.Texture ?? "").TrimStart('#'))
        .Distinct()
        .Order(StringComparer.Ordinal)
    )
      shape.Textures.Add(new(key, ctx.Settings.EditablePath(key)));
    return new GenOutput(item, rec.Kinds, shape, rec, hash);
  }

  private static Element Named(Leaf l) {
    Element e = Select.Clone(l.Element, false);
    e.Name = l.Name;
    return e;
  }

  private static string Invariant(double v) =>
    v.ToString(CultureInfo.InvariantCulture);

  private static string HashOf(
    string spec,
    IReadOnlyList<Leaf> cast,
    IReadOnlyList<PatternPrint> prints,
    double? parting,
    Settings settings,
    double? head
  ) {
    var sb = new StringBuilder(CanonicalWriter.Write(Flush.ShapeOf(cast)));
    foreach (PatternPrint p in prints)
      sb.Append(
        string.Create(
          CultureInfo.InvariantCulture,
          $"\n{p.Name} {string.Join(",", p.From.Select(Invariant))} {string.Join(",", p.To.Select(Invariant))}"
        )
      );
    sb.Append(
      string.Create(
        CultureInfo.InvariantCulture,
        $"\nparting {parting}{(head is double h ? $" head {h}" : "")}"
      )
    );
    return Provenance.InputHash(
      spec,
      "",
      Provenance.Sha256Of(sb.ToString()),
      Provenance.Sha256Of(settings.RoleKey("pattern"))
    );
  }
}
