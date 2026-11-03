using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Generators;

/// <summary>The worked items made from one blank.</summary>
/// <param name="Outputs">The worked items; empty when a question is open or the user fails its blank.</param>
/// <param name="Questions">The out-of-band asks of the blank match, feature <c>blankmatch/x</c>, <c>/y</c> or <c>/z</c>.</param>
/// <param name="Redraws">One <c>redraw: ITEM AXIS drawn D blank B</c> row per axis the user's drawn cast form differs
/// from the blank on.</param>
/// <param name="Failures">One line per way the user does not match its blank; when present nothing is written.</param>
public sealed record WorkedResult(
  IReadOnlyList<GenOutput> Outputs,
  IReadOnlyList<Question> Questions,
  IReadOnlyList<string> Redraws,
  IReadOnlyList<string> Failures
) {
  /// <summary>The user's placed form, the drawing blankmatch ran on; empty when nothing was made.</summary>
  public IReadOnlyList<GenInput> Inputs { get; init; } = [];
}

/// <summary>Worked items: the blank's cast form with its allowance off, the faces its steps cut
/// finished and its worked features cut in.</summary>
public static partial class Worked {
  private const double Tol = 1e-4;

  [GeneratedRegex(@"^[xyz]: spans ")]
  private static partial Regex Band();

  /// <summary>The worked item of one user of a blank. The user's drawn section, placed by its <c>at</c> in the
  /// blank's frame, first passes <c>blankmatch</c>; the item is then the blank's cast form (or the earlier
  /// worked item <c>from</c> names) with each step's cut faces and worked features in that step's finished
  /// role, uv assigned and round runs unrolled. A step's machine is the spec's, else the first of its row in the
  /// settings; the record's step keeps it and the row's hand route.</summary>
  /// <param name="ctx">The context; its answers settle an out-of-band match.</param>
  /// <param name="machine">The machine file the user is in.</param>
  /// <param name="user">The user.</param>
  /// <param name="blank">The blank's cast form.</param>
  /// <param name="earlier">Worked items made before this one, which <c>from</c> may name.</param>
  /// <exception cref="CastGenException">A step, cut feature, user feature or <c>from</c> names nothing, a step's
  /// machine is outside its row, or the user's groups are missing.</exception>
  public static WorkedResult ForUser(
    GenContext ctx,
    MachineSpec machine,
    UserSpec user,
    BlankResult blank,
    IReadOnlyList<GenOutput>? earlier = null
  ) {
    ResolvedSource rs = ctx.Resolve(machine.Source.Shape, machine.Source.Sha);
    List<Leaf> placed = Section(rs, user);
    List<Leaf> drawn =
    [
      .. placed.Where(l =>
        !(
          rs.Record.Elements.TryGetValue(l.SourceName, out ElementPart? ep)
          && ep.Stage is "worked" or "fastener"
        )
      ),
    ];
    string item = user.Worked.Item;
    List<string> redraws = Redraws(item, drawn, blank);

    PartRecord rec = PartRecord.Read(rs.Record.Write());
    rec.Answers.AddRange(ctx.Answers);
    foreach (
      string name in rec
        .Features.Where(kv =>
          kv.Value.Origin is not (null or "cast" or "cored")
          && !placed.Any(l => kv.Value.Elements.Contains(l.SourceName))
        )
        .Select(kv => kv.Key)
        .ToList()
    )
      rec.Features.Remove(name);
    CheckResult match = new Blankmatch().Run(
      new CheckInput(
        Flush.ShapeOf(placed),
        Record: rec,
        Source: Flush.ShapeOf(blank.Cast),
        SourceRecord: blank.Record,
        CutFaces: blank.CutFeatures
      )
    );
    if (match.Verdict == Verdict.Fail)
      return new WorkedResult(
        [],
        [],
        redraws,
        [
          .. match.Lines.Where(l =>
            !Band().IsMatch(l) && !l.EndsWith(", answered")
          ),
        ]
      );
    if (match.Verdict == Verdict.Question)
      return new WorkedResult(
        [],
        [
          .. match
            .Lines.Where(l => Band().IsMatch(l))
            .Select(l => new Question(
              "blankmatch/" + l[0],
              l,
              ["accept"],
              "accept"
            )
            {
              Part = rs.Path,
              FeatureSha = Provenance.Sha256Of(l),
            }),
        ],
        redraws,
        []
      );

    string spec = JsonConvert.SerializeObject(user);
    GenOutput output = Make(
      ctx,
      blank,
      user.Worked,
      earlier,
      new UserPart(rs.Record, placed),
      spec,
      rs.ShapeSha,
      rs.Record.Write()
    );
    string placedName = item + "-placed";
    PartRecord placedRecord = PartRecord.Read(rec.Write());
    placedRecord.Carried["blank"] = blank.Spec.Blank;
    ((JObject)output.Record.Carried["blankform"]!)["placed"] = placedName;
    return new WorkedResult([output], [], redraws, []) {
      Inputs = [new GenInput(placedName, Flush.ShapeOf(placed), placedRecord)],
    };
  }

  /// <summary>A worked item the blank file lists itself: the same steps with no user, so its steps name only
  /// the blank's cut features.</summary>
  /// <param name="ctx">The context.</param>
  /// <param name="blank">The blank's cast form.</param>
  /// <param name="spec">The worked item.</param>
  /// <param name="earlier">Worked items made before this one, which <c>from</c> may name.</param>
  /// <exception cref="CastGenException">A step or cut feature names nothing, a step names features of its own,
  /// a step's machine is outside its row, or <c>from</c> names neither the blank nor an earlier item.</exception>
  public static GenOutput ForBlank(
    GenContext ctx,
    BlankResult blank,
    WorkedSpec spec,
    IReadOnlyList<GenOutput>? earlier = null
  ) =>
    Make(
      ctx,
      blank,
      spec,
      earlier,
      null,
      JsonConvert.SerializeObject(spec),
      "",
      blank.Record.Write()
    );

  /// <summary>The worked item of a joined entry: each section's worked item, placed back in the machine drawing's
  /// frame by the inverse of its user's <c>at</c>, with each step's features (the machine record's) carved in and
  /// finished, and the faces its cuts name finished. There is no blank to match: the sections are matched as
  /// users.</summary>
  /// <param name="ctx">The context.</param>
  /// <param name="machine">The machine file.</param>
  /// <param name="joined">The assembled sections.</param>
  /// <param name="earlier">The worked items made before this one; each section's is among them.</param>
  /// <exception cref="CastGenException">A step or feature names nothing, a step's machine is outside its row, or a
  /// section's worked item is not in <paramref name="earlier"/>.</exception>
  public static WorkedResult ForJoined(
    GenContext ctx,
    MachineSpec machine,
    JoinedResult joined,
    IReadOnlyList<GenOutput> earlier
  ) {
    ResolvedSource rs = ctx.Resolve(machine.Source.Shape, machine.Source.Sha);
    List<Leaf> placed = [];
    foreach (string from in joined.Sections) {
      UserSpec user = machine.Users.First(u => u.Worked.Item == from);
      List<Leaf> leaves = Select.Groups(
        rs.Shape,
        user.Groups,
        null,
        new HashSet<string>(StringComparer.Ordinal)
      );
      placed.AddRange(
        user.Window == null ? leaves : Select.Window(leaves, user.Window)
      );
    }

    var blank = new BlankResult {
      Spec = new BlankSpec(
        joined.Item,
        [0, 0, 0],
        1,
        new FormSpec(null, machine.Source, null, []),
        [],
        null,
        null,
        null,
        [],
        null,
        [],
        machine.Allowance,
        machine.Route
      ),
    };
    blank.Cast.AddRange(Sections(machine, joined, earlier));
    blank.TagCast();
    foreach (KeyValuePair<string, Feature> kv in rs.Record.Features)
      blank.Record.Features[kv.Key] = kv.Value;
    blank.Record.Steps.AddRange(rs.Record.Steps);

    string spec = JsonConvert.SerializeObject(
      new {
        joined = machine.Joined.First(j => j.Item == joined.Item),
        users = joined.Sections.Select(f =>
          machine.Users.First(u => u.Worked.Item == f)
        ),
      }
    );
    GenOutput output = Make(
      ctx,
      blank,
      new WorkedSpec(joined.Item, null, joined.Steps),
      null,
      new UserPart(rs.Record, placed),
      spec,
      rs.ShapeSha,
      rs.Record.Write()
    );
    string placedName = joined.Item + "-placed";
    PartRecord placedRecord = PartRecord.Read(rs.Record.Write());
    placedRecord.Carried["blank"] = joined.Item;
    ((JObject)output.Record.Carried["blankform"]!)["placed"] = placedName;
    return new WorkedResult([output], [], [], []) {
      Inputs = [new GenInput(placedName, Flush.ShapeOf(placed), placedRecord)],
    };
  }

  private static List<Leaf> Sections(
    MachineSpec machine,
    JoinedResult joined,
    IReadOnlyList<GenOutput> earlier
  ) {
    var leaves = new List<Leaf>();
    var taken = new HashSet<string>(StringComparer.Ordinal);
    foreach (string from in joined.Sections) {
      GenOutput section =
        earlier.FirstOrDefault(o => o.Name == from)
        ?? throw new CastGenException(
          $"joined \"{joined.Item}\": \"{from}\" is not an earlier worked item"
        );
      UserSpec user = machine.Users.First(u => u.Worked.Item == from);
      Mat4d? back = user.At == null ? null : Uvs.Inverse(user.At.Matrix);
      foreach (Element e in section.Shape.Elements) {
        string source = e.Name ?? "";
        section.Record.Elements.TryGetValue(source, out ElementPart? ep);
        Element placed = back is { } m
          ? Select.Flatten(e, m * Engine.Local(e), source)
          : Select.Clone(e, false);
        string name = source;
        for (int i = 2; !taken.Add(name); i++)
          name = $"{source}_{i}";
        leaves.Add(
          new Leaf(
            name,
            source,
            source,
            placed,
            ep?.Stage ?? "cast",
            ep?.Role ?? "cast"
          )
        );
      }
    }
    return leaves;
  }

  private sealed record UserPart(PartRecord Record, List<Leaf> Placed);

  private static List<Leaf> Section(ResolvedSource rs, UserSpec user) {
    List<Leaf> leaves = Select.Groups(
      rs.Shape,
      user.Groups,
      null,
      new HashSet<string>(StringComparer.Ordinal)
    );
    if (user.Window != null)
      leaves = Select.Window(leaves, user.Window);
    if (user.At == null)
      return leaves;
    Mat4d m = user.At.Matrix;
    return
    [
      .. leaves.Select(l =>
        l with
        {
          Element = Select.Flatten(
            l.Element,
            m * Engine.Local(l.Element),
            l.SourcePath
          ),
        }
      ),
    ];
  }

  private static List<string> Redraws(
    string item,
    List<Leaf> drawn,
    BlankResult blank
  ) {
    var rows = new List<string>();
    Box? have = BlankResult.Union(drawn.Select(l => l.Box));
    if (have == null)
      return rows;
    Box room = blank.Extent;
    for (int a = 0; a < 3; a++) {
      double span = have.Value.Hi[a] - have.Value.Lo[a],
        of = room.Hi[a] - room.Lo[a];
      if (Math.Abs(span - of) > Tol)
        rows.Add(
          $"redraw: {item} {"xyz"[a]} drawn {Fmt.G(span)} blank {Fmt.G(of)}"
        );
    }
    return rows;
  }

  private static GenOutput Make(
    GenContext ctx,
    BlankResult blank,
    WorkedSpec spec,
    IReadOnlyList<GenOutput>? earlier,
    UserPart? user,
    string specText,
    string shapeSha,
    string recordText
  ) {
    var rec = new PartRecord {
      Shape = spec.Item + ".json",
      Owner = "generated",
    };
    rec.Kinds.AddRange(["worked", "item"]);
    rec.Carried["blank"] = blank.Spec.Blank;
    rec.Carried["blankform"] = new JObject {
      ["input"] = blank.Spec.Blank + "-form",
      ["cutFeatures"] = new JArray(blank.CutFeatures),
    };

    List<Leaf> items = Start(blank, spec, earlier);
    var taken = new HashSet<string>(
      items.Select(l => l.Name),
      StringComparer.Ordinal
    );
    foreach (Leaf l in items)
      rec.Elements[l.Name] = new ElementPart {
        Stage = l.Stage ?? "cast",
        Role = l.Role ?? "cast",
      };

    foreach (StepSpec step in spec.Steps) {
      StepRow row = ctx.Settings.Steps.TryGetValue(step.Step, out StepRow? r)
        ? r
        : throw new CastGenException(
          $"{spec.Item}: unknown step \"{step.Step}\""
        );
      string tool = step.Machine ?? row.Machines[0];
      try {
        ctx.Settings.CheckMachine(step.Step, tool);
      } catch (SettingsException ex) {
        throw new CastGenException($"{spec.Item}: {ex.Message}");
      }
      string role = "finished." + step.Step;
      string key = "#" + ctx.Settings.RoleKey(role);
      var recStep = new Step {
        Id = step.Step,
        Tool = tool,
        Hand = row.Hand,
      };

      foreach (string id in step.Cuts) {
        Feature f = blank.Record.Features.TryGetValue(id, out Feature? bf)
          ? bf
          : throw new CastGenException(
            $"{spec.Item}: cut feature \"{id}\" is not a feature of blank \"{blank.Spec.Blank}\""
          );
        foreach (string cut in f.Faces) {
          int at = cut.LastIndexOf('/');
          string name = cut[..at],
            face = cut[(at + 1)..];
          foreach (Leaf l in items.Where(l => l.SourceName == name))
            foreach (KeyValuePair<string, Face> kv in l.Element.Faces)
              if (kv.Key == face)
                kv.Value.Texture = key;
          recStep.Faces.Add(cut);
        }
        if (f.Origin == "drilled") {
          var fill = new Regex("^" + Regex.Escape(id) + @"_fill\d*$");
          items.RemoveAll(l => fill.IsMatch(l.Name));
          foreach (
            string gone in rec
              .Elements.Keys.Where(k => fill.IsMatch(k))
              .ToList()
          )
            rec.Elements.Remove(gone);
        }
        var feature = new Feature { Step = step.Step };
        feature.Elements.AddRange(f.Elements);
        feature.Faces.AddRange(f.Faces);
        rec.Features[id] = feature;
      }

      foreach (string id in step.Features) {
        if (user == null)
          throw new CastGenException(
            $"{spec.Item}: step \"{step.Step}\" names feature \"{id}\" but the item has no user"
          );
        Feature uf = user.Record.Features.TryGetValue(id, out Feature? ufound)
          ? ufound
          : throw new CastGenException(
            $"{spec.Item}: feature \"{id}\" is not in the user's record"
          );
        List<Leaf> made =
        [
          .. user.Placed.Where(l => uf.Elements.Contains(l.SourceName)),
        ];
        if (made.Count == 0)
          throw new CastGenException(
            $"{spec.Item}: feature \"{id}\" has no element in the user's drawing"
          );
        var feature = new Feature { Origin = uf.Origin, Step = step.Step };
        feature.Faces.AddRange(uf.Faces);
        foreach (Leaf l in made)
          items = Carve(items, l.Box, taken, rec, recStep);
        foreach (Leaf l in made) {
          Element e = Select.Clone(l.Element, false);
          foreach (KeyValuePair<string, Face> kv in e.Faces)
            kv.Value.Texture = key;
          string name = l.Name;
          for (int i = 2; !taken.Add(name); i++)
            name = $"{l.Name}_{i}";
          items.Add(
            l with {
              Name = name,
              Element = e,
              Stage = "worked",
              Role = role,
            }
          );
          rec.Elements[name] = new ElementPart {
            Stage = "worked",
            Role = role,
          };
          feature.Elements.Add(name);
        }
        rec.Features[id] = feature;
        recStep.Faces.AddRange(uf.Faces);
      }
      rec.Steps.Add(recStep);
    }

    var cutFaces = new HashSet<string>(
      rec.Steps.SelectMany(st => st.Faces),
      StringComparer.Ordinal
    );
    items = Flush
      .Resolve(
        items,
        cutFaces,
        blank.Spec.Allowance,
        false,
        Flush.Pins(user?.Record),
        null,
        rec,
        true
      )
      .Leaves.ToList();
    items = Splits.Apply(items);
    foreach (Leaf l in items)
      rec.Elements.TryAdd(
        l.Name,
        new ElementPart { Stage = l.Stage ?? "cast", Role = l.Role ?? "cast" }
      );
    items = Uvs.Unroll(ctx, spec.Item, [.. Uvs.Assign(ctx, spec.Item, items)]);
    var shape = new Shape();
    foreach (Leaf l in items) {
      Element e = Select.Clone(l.Element, false);
      e.Name = l.Name;
      shape.Elements.Add(e);
    }
    foreach (
      string key in shape
        .Elements.SelectMany(e => e.Faces)
        .Select(kv => (kv.Value.Texture ?? "").TrimStart('#'))
        .Distinct()
        .Order(StringComparer.Ordinal)
    )
      shape.Textures.Add(new(key, ctx.Settings.EditablePath(key)));

    string hash = Provenance.InputHash(
      specText,
      shapeSha,
      Provenance.Sha256Of(recordText + blank.Record.Write()),
      SettingsSha(ctx.Settings)
    );
    return new GenOutput(spec.Item, rec.Kinds, shape, rec, hash);
  }

  private static List<Leaf> Carve(
    List<Leaf> items,
    Box cut,
    ISet<string> taken,
    PartRecord rec,
    Step current
  ) {
    var outp = new List<Leaf>();
    foreach (Leaf l in items) {
      Box b = l.Box;
      bool overlaps = Enumerable
        .Range(0, 3)
        .All(a =>
          Math.Min(b.Hi[a], cut.Hi[a]) - Math.Max(b.Lo[a], cut.Lo[a]) > Tol
        );
      if (!overlaps) {
        outp.Add(l);
        continue;
      }
      if (!l.AxisAligned)
        throw new CastGenException(
          $"{l.SourcePath}: a rotated element is cut by a worked feature"
        );
      var cutOut = new List<Leaf>();
      var from = (double[])l.Element.From!.Clone();
      var to = (double[])l.Element.To!.Clone();
      for (int a = 0; a < 3; a++) {
        double lo = Math.Max(from[a], cut.Lo[a]),
          hi = Math.Min(to[a], cut.Hi[a]);
        foreach ((double f, double t) in new[] { (from[a], lo), (hi, to[a]) }) {
          if (t - f <= Tol)
            continue;
          Element e = Select.Clone(l.Element, true);
          e.From = (double[])from.Clone();
          e.To = (double[])to.Clone();
          e.From[a] = f;
          e.To[a] = t;
          cutOut.Add(l with { Element = e });
        }
        from[a] = lo;
        to[a] = hi;
      }
      bool first = true;
      foreach (Leaf piece in cutOut) {
        string name = l.Name;
        if (!first)
          for (int i = 2; !taken.Add(name = $"{l.Name}_c{i}"); i++) { }
        first = false;
        if (name != l.Name)
          Rename(rec, current, l.Name, name);
        rec.Elements[name] = new ElementPart {
          Stage = l.Stage ?? "cast",
          Role = l.Role ?? "cast",
        };
        outp.Add(piece with { Name = name });
      }
      if (cutOut.Count == 0)
        rec.Elements.Remove(l.Name);
    }
    return outp;
  }

  private static void Rename(
    PartRecord rec,
    Step current,
    string from,
    string to
  ) {
    void Add(List<string> faces) {
      foreach (string f in faces.Where(f => f.StartsWith(from + "/")).ToList()) {
        string added = to + f[from.Length..];
        if (!faces.Contains(added))
          faces.Add(added);
      }
    }
    foreach (Step s in rec.Steps.Append(current))
      Add(s.Faces);
    foreach (Feature f in rec.Features.Values)
      Add(f.Faces);
  }

  private static List<Leaf> Start(
    BlankResult blank,
    WorkedSpec spec,
    IReadOnlyList<GenOutput>? earlier
  ) {
    if (spec.From == null || spec.From == blank.Spec.Blank)
      return
      [
        .. blank.Cast.Select(l =>
          l with
          {
            Element = Select.Clone(l.Element, false),
          }
        ),
      ];
    GenOutput from =
      earlier?.FirstOrDefault(o => o.Name == spec.From)
      ?? throw new CastGenException(
        $"{spec.Item}: \"{spec.From}\" is neither blank \"{blank.Spec.Blank}\" nor an earlier worked item"
      );
    return
    [
      .. from.Shape.Elements.Select(e =>
      {
        string name = e.Name ?? "";
        from.Record.Elements.TryGetValue(name, out ElementPart? ep);
        return new Leaf(
          name,
          name,
          name,
          Select.Clone(e, false),
          ep?.Stage ?? "cast",
          ep?.Role ?? "cast"
        );
      }),
    ];
  }

  internal static string SettingsSha(Settings s) =>
    Provenance.Sha256Of(
      string.Join(
        "\n",
        s.Roles.OrderBy(kv => kv.Key, StringComparer.Ordinal)
          .Select(kv => $"{kv.Key}={kv.Value}")
          .Concat(
            s.Steps.OrderBy(kv => kv.Key, StringComparer.Ordinal)
              .Select(kv =>
                $"{kv.Key}={string.Join(",", kv.Value.Machines)}/{kv.Value.Hand}"
              )
          )
      )
    );
}
