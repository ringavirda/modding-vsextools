using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Generators;

/// <summary>A cored feature handed on to step 10.</summary>
/// <param name="Feature">The feature id.</param>
/// <param name="Elements">The elements that form the cavity, as selected.</param>
/// <param name="Print">The print from the spec's <c>cores</c> entry; null when it names none.</param>
public sealed record CoreHandoff(
  string Feature,
  IReadOnlyList<Leaf> Elements,
  JToken? Print
);

/// <summary>The cast set of a selection: what a pour makes, what is worked afterwards and what
/// needs a core. When <see cref="Questions"/> is not empty every list is empty.</summary>
public sealed class CastSetResult {
  /// <summary>The elements the pour makes, drilled features filled.</summary>
  public List<Leaf> Cast { get; } = [];

  /// <summary>Elements of worked features and of stage worked, set aside for step 7.</summary>
  public List<Leaf> Worked { get; } = [];

  /// <summary>Cored features with their prints.</summary>
  public List<CoreHandoff> Cores { get; } = [];

  /// <summary>Eye calls left open.</summary>
  public List<Question> Questions { get; } = [];

  /// <summary>For each group entry in turn, the count of <see cref="Cast"/> leaves up to and including that
  /// entry's; empty when a question is open.</summary>
  public List<int> GroupEnds { get; } = [];

  internal void Add(CastSetResult o) {
    Cast.AddRange(o.Cast);
    Worked.AddRange(o.Worked);
    Cores.AddRange(o.Cores);
    Questions.AddRange(o.Questions);
  }
}

/// <summary>Work on sections bolted into one bed, assembled.</summary>
/// <param name="Item">The joined worked item.</param>
/// <param name="Sections">The worked item names it joins, in the spec's order.</param>
/// <param name="Cast">The sections' cast elements in the drawing's frame, names unique.</param>
/// <param name="Steps">The steps worked on the whole.</param>
public sealed record JoinedResult(
  string Item,
  IReadOnlyList<string> Sections,
  IReadOnlyList<Leaf> Cast,
  IReadOnlyList<StepSpec> Steps
) {
  /// <summary>The eye calls left open by the sections' cast sets; when present <see cref="Cast"/> is empty.</summary>
  public IReadOnlyList<Question> Questions { get; init; } = [];
}

/// <summary>Step 2: the cast set from the part record's tags alone.</summary>
public static class CastSet {
  private static readonly string[] Stages =
  [
    "cast",
    "worked",
    "fastener",
    "assembled",
  ];
  private static readonly string[] WorkedOrigins =
  [
    "turned",
    "bored",
    "planed",
    "filed",
  ];

  /// <summary>The cast set of <paramref name="parts"/> of one drawing, each part's groups placed
  /// by its <c>at</c>.</summary>
  /// <param name="ctx">The context; its answers and the record's settle untagged features.</param>
  /// <param name="src">The drawing and its record.</param>
  /// <param name="parts">Groups with their placement.</param>
  /// <param name="cores">The spec's cores, matched to cored features by id.</param>
  /// <exception cref="CastGenException">A group is missing, a drilled feature has no hole, a spec
  /// core names no feature.</exception>
  public static CastSetResult Of(
    GenContext ctx,
    ResolvedSource src,
    IEnumerable<GroupSpec> parts,
    IReadOnlyList<CoreSpec> cores,
    double[]? window = null
  ) {
    var taken = new HashSet<string>(StringComparer.Ordinal);
    var outp = new CastSetResult();
    foreach (GroupSpec part in parts) {
      Mat4d place = (part.At ?? Placement.None).Matrix;
      List<Leaf> leaves = Select.Groups(
        src.Shape,
        part.Groups,
        place,
        taken,
        true
      );
      if (window != null)
        leaves = Select.Window(leaves, window);
      outp.Add(Classify(ctx, src, leaves, place, cores, taken));
      outp.GroupEnds.Add(outp.Cast.Count);
    }
    foreach (CoreSpec c in cores)
      if (!src.Record.Features.ContainsKey(c.Feature))
        throw new CastGenException(
          $"core \"{c.Feature}\" is not a feature of the record"
        );
    if (outp.Questions.Count > 0) {
      outp.Cast.Clear();
      outp.Worked.Clear();
      outp.Cores.Clear();
      outp.GroupEnds.Clear();
    }
    return outp;
  }

  /// <summary>The cast set of one machine casting set item (a kit or the small pieces): all its
  /// groups, each at its own placement, in one item.</summary>
  public static CastSetResult ForSet(
    GenContext ctx,
    SourceRef machine,
    SetSpec set
  ) => Of(ctx, ctx.Resolve(machine.Shape, machine.Sha), set.Groups, []);

  /// <summary>The cast set of one user section: its groups cut to its window, in the drawing's
  /// frame.</summary>
  public static CastSetResult Section(
    GenContext ctx,
    SourceRef machine,
    UserSpec user
  ) =>
    Of(
      ctx,
      ctx.Resolve(machine.Shape, machine.Sha),
      [new GroupSpec(user.Groups, null, null)],
      [],
      user.Window
    );

  /// <summary>Assembles the sections a joined entry names, each the cast set of the user whose
  /// worked item it is.</summary>
  /// <exception cref="CastGenException">A section names no user of the machine.</exception>
  public static JoinedResult Joined(
    GenContext ctx,
    MachineSpec machine,
    JoinedSpec joined
  ) {
    var cast = new List<Leaf>();
    var questions = new List<Question>();
    var taken = new HashSet<string>(StringComparer.Ordinal);
    foreach (string from in joined.From) {
      UserSpec user =
        machine.Users.FirstOrDefault(u => u.Worked.Item == from)
        ?? throw new CastGenException(
          $"joined \"{joined.Item}\": no user makes \"{from}\""
        );
      CastSetResult section = Section(ctx, machine.Source, user);
      questions.AddRange(section.Questions);
      foreach (Leaf l in section.Cast) {
        string n = l.Name;
        for (int i = 2; !taken.Add(n); i++)
          n = $"{l.Name}_{i}";
        cast.Add(l with { Name = n });
      }
    }
    if (questions.Count > 0)
      cast.Clear();
    return new JoinedResult(joined.Item, joined.From, cast, joined.Steps) {
      Questions = questions,
    };
  }

  private static CastSetResult Classify(
    GenContext ctx,
    ResolvedSource src,
    List<Leaf> leaves,
    Mat4d place,
    IReadOnlyList<CoreSpec> cores,
    ISet<string> taken
  ) {
    PartRecord rec = src.Record;
    var outp = new CastSetResult();
    var byName = leaves
      .GroupBy(l => l.SourceName)
      .ToDictionary(g => g.Key, g => g.ToList());
    var featureOf = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (KeyValuePair<string, Feature> kv in rec.Features)
      foreach (string el in kv.Value.Elements)
        featureOf.TryAdd(el, kv.Key);

    var drilled = new List<string>();
    var untagged = new Dictionary<string, List<Leaf>>();
    foreach (Leaf leaf in leaves) {
      rec.Elements.TryGetValue(leaf.SourceName, out ElementPart? ep);
      featureOf.TryGetValue(leaf.SourceName, out string? fid);
      Feature? feature = fid == null ? null : rec.Features[fid];
      string? origin = feature?.Origin;
      if (origin == "cored")
        continue;
      if (origin == "drilled") {
        if (!drilled.Contains(fid!))
          drilled.Add(fid!);
        if (ep?.Stage != "cast")
          continue;
      }
      if (
        origin != null
        && WorkedOrigins.Contains(origin)
        && ep?.Stage != "cast"
      ) {
        outp.Worked.Add(leaf with { Stage = "worked", Role = ep?.Role });
        continue;
      }
      string? stage = Stages.Contains(ep?.Stage) ? ep!.Stage : null;
      if (stage == null) {
        string key = fid ?? leaf.SourceName;
        if (!untagged.TryGetValue(key, out List<Leaf>? list))
          untagged[key] = list = [];
        list.Add(leaf);
        continue;
      }
      Place(outp, leaf, stage, ep!.Role);
    }

    foreach (KeyValuePair<string, List<Leaf>> kv in untagged) {
      string sha = FeatureSha(kv.Value);
      Answer? ans = ctx
        .Answers.Concat(rec.Answers)
        .FirstOrDefault(a =>
          a.Feature == kv.Key && a.FeatureSha == sha && Stages.Contains(a.Text)
        );
      if (ans != null) {
        foreach (Leaf l in kv.Value) {
          rec.Elements.TryGetValue(l.SourceName, out ElementPart? ep);
          Place(outp, l, ans.Text, ep?.Role);
        }
      } else
        outp.Questions.Add(
          new Question(
            kv.Key,
            $"\"{kv.Key}\" has no stage in its record: is it cast, worked or a fastener?",
            ["cast", "worked", "fastener"],
            "cast"
          ) {
            Part = src.Path,
            FeatureSha = sha,
          }
        );
    }

    foreach (string fid in drilled)
      outp.Cast.AddRange(Fill(ctx, rec.Features[fid], fid, place, taken));

    foreach (KeyValuePair<string, Feature> kv in rec.Features) {
      if (kv.Value.Origin != "cored")
        continue;
      var members = kv
        .Value.Elements.Where(byName.ContainsKey)
        .SelectMany(n => byName[n])
        .ToList();
      if (members.Count > 0)
        outp.Cores.Add(
          new CoreHandoff(
            kv.Key,
            members,
            cores.FirstOrDefault(c => c.Feature == kv.Key)?.Print
          )
        );
    }
    return outp;
  }

  private static void Place(
    CastSetResult outp,
    Leaf leaf,
    string stage,
    string? role
  ) {
    switch (stage) {
      case "cast":
        outp.Cast.Add(leaf with { Stage = "cast", Role = role ?? "cast" });
        break;
      case "worked":
        outp.Worked.Add(leaf with { Stage = "worked", Role = role });
        break;
    }
  }

  /// <summary>The fingerprint of a feature's elements an answer is stored against.</summary>
  public static string FeatureSha(IEnumerable<Leaf> members) =>
    Provenance.Sha256Of(
      string.Join(
        "\n",
        members
          .OrderBy(l => l.SourceName, StringComparer.Ordinal)
          .Select(l => {
            Box b = l.Box;
            return string.Create(
              CultureInfo.InvariantCulture,
              $"{l.SourceName}:{b.Lo.X:F6},{b.Lo.Y:F6},{b.Lo.Z:F6}:{b.Hi.X:F6},{b.Hi.Y:F6},{b.Hi.Z:F6}"
            );
          })
      )
    );

  /// <summary>The boxes that fill a drilled feature. Its record entry carries a <c>hole</c> object:
  /// <c>axis</c> (x, y or z), <c>centre</c> [x, y, z] in the drawing, <c>depth</c> (signed, from
  /// the centre along the axis) and either <c>radius</c> (the circumscribed square of side 2 x radius, so a bore across
  /// the draw leaves no sand corner) or <c>polygon</c> (convex [u, v] points, u and v the other two axes in x, y, z
  /// order, relative to the centre). A polygon is cut into strips between its vertex levels, each
  /// an inscribed box.</summary>
  private static List<Leaf> Fill(
    GenContext ctx,
    Feature f,
    string id,
    Mat4d place,
    ISet<string> taken
  ) {
    JObject h =
      f.Unknown["hole"] as JObject
      ?? throw new CastGenException($"drilled feature \"{id}\" has no hole");
    string axis = (string?)h["axis"] ?? "";
    int a = axis switch {
      "x" => 0,
      "y" => 1,
      "z" => 2,
      _ => throw new CastGenException($"hole of \"{id}\": axis is x, y or z"),
    };
    double[] c = Num(h["centre"], 3, id, "centre");
    double depth =
      (double?)h["depth"]
      ?? throw new CastGenException($"hole of \"{id}\": no depth");
    int u = a == 0 ? 1 : 0,
      v = a == 2 ? 1 : 2;
    List<(double U, double V)> poly;
    if (h["radius"] != null) {
      double r = (double)h["radius"]!;
      poly = [(-r, -r), (r, -r), (r, r), (-r, r)];
    } else if (h["polygon"] is JArray pts && pts.Count >= 3)
      poly =
      [
        .. pts.Select(p =>
          (Num(p, 2, id, "polygon")[0], Num(p, 2, id, "polygon")[1])
        ),
      ];
    else
      throw new CastGenException($"hole of \"{id}\": radius or polygon");

    double[] levels = [.. poly.Select(p => p.V).Distinct().Order()];
    string cast = ctx.Settings.RoleKey("cast");
    var outp = new List<Leaf>();
    for (int i = 0; i + 1 < levels.Length; i++) {
      (double lo, double hi) = Chord(poly, levels[i], levels[i + 1]);
      if (hi - lo < 1e-9)
        continue;
      var from = new double[3];
      var to = new double[3];
      from[a] = Math.Min(c[a], c[a] + depth);
      to[a] = Math.Max(c[a], c[a] + depth);
      from[u] = c[u] + lo;
      to[u] = c[u] + hi;
      from[v] = c[v] + levels[i];
      to[v] = c[v] + levels[i + 1];
      string name = $"{id}_fill";
      for (int n = 2; !taken.Add(name); n++)
        name = $"{id}_fill{n}";
      var e = new Element {
        Name = name,
        From = from,
        To = to,
      };
      foreach (
        string face in new[] { "north", "east", "south", "west", "up", "down" }
      )
        e.Faces.Add(new(face, new Face { Texture = "#" + cast }));
      outp.Add(
        new Leaf(
          name,
          name,
          name,
          Select.Flatten(e, place * Engine.Local(e), name),
          "cast",
          "cast"
        )
      );
    }
    return outp;
  }

  private static (double Lo, double Hi) Chord(
    List<(double U, double V)> poly,
    double v0,
    double v1
  ) {
    double lo = double.NegativeInfinity,
      hi = double.PositiveInfinity;
    foreach (double v in new[] { v0, v1 }) {
      double cl = double.PositiveInfinity,
        ch = double.NegativeInfinity;
      for (int i = 0; i < poly.Count; i++) {
        var p = poly[i];
        var q = poly[(i + 1) % poly.Count];
        if ((p.V - v) * (q.V - v) > 0 || p.V == q.V)
          continue;
        double t = (v - p.V) / (q.V - p.V);
        double x = p.U + t * (q.U - p.U);
        cl = Math.Min(cl, x);
        ch = Math.Max(ch, x);
      }
      if (cl <= ch) {
        lo = Math.Max(lo, cl);
        hi = Math.Min(hi, ch);
      }
    }
    return (lo, hi);
  }

  private static double[] Num(JToken? t, int n, string id, string what) =>
    t is JArray a
    && a.Count == n
    && a.All(x => x.Type is JTokenType.Integer or JTokenType.Float)
      ? [.. a.Select(x => (double)x)]
      : throw new CastGenException($"hole of \"{id}\": {what} is {n} numbers");
}
