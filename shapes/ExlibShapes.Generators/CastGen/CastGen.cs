using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Generators;

/// <summary>The casting generator: steps 1 to 11 chained over one blank file or one machine file entry.</summary>
public sealed partial class CastGen : IGenerator {
  [GeneratedRegex("^Print")]
  private static partial Regex PrintName();

  /// <inheritdoc />
  public string Id => "castgen";

  /// <summary>The sha256 of the sources of Generators, Checks and Core, embedded at build; it does not depend on the
  /// path a build ran in.</summary>
  public string Version { get; } =
    typeof(CastGen)
      .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
      .First(a => a.Key == "GeneratorVersion.castgen")
      .Value!;

  /// <inheritdoc />
  public JObject SpecSchema => (JObject)Spec.Schema.DeepClone();

  /// <summary>Runs a blank file or a machine file. A blank file gives its cast item, pattern and loose pieces,
  /// filling (and cope of a closed pour) at its station, cores and core boxes and the worked items it lists. A machine
  /// file gives each user's worked item, each joined entry's worked item, and for each own casting and the set the
  /// same products as a blank, its own worked items included. A casting whose cast set holds an untagged feature, a
  /// joined entry whose sections do, or a user whose placement is out of its blank's band, returns its question and
  /// none of its outputs; every other product is still made. An open pour holds <c>perPour</c> pieces of a blank, or
  /// <c>perPour</c> of a machine's casting, in a column away from the drop in the flask's -z, each a station margin
  /// of sand twice over the last's end; the pour's order is <c>south</c>. The groups of a kit each lie by their own
  /// <c>lie</c> (else the casting's), their offsets relative to the first group's.</summary>
  /// <param name="ctx">The context. A machine file's users need <see cref="GenContext.Blank"/>.</param>
  /// <param name="spec">The blank file (it has <c>blank</c>) or machine file (it has <c>machine</c>) as written.</param>
  /// <exception cref="SpecException">The entry is malformed.</exception>
  /// <exception cref="CastGenException">An input cannot make its output: a user fails its blank, a casting fits no
  /// station, a closed pour or a head is on a lie that turns y, a closed pour holds more than one piece, a pour of
  /// several pieces or a kit whose groups lie differently has cores.</exception>
  public GenResult Run(GenContext ctx, JObject spec) {
    var acc = new Acc();
    string text = spec.ToString(Newtonsoft.Json.Formatting.None);
    if (spec.ContainsKey("blank"))
      RunBlank(ctx, Spec.ReadBlank(spec), text, acc);
    else if (spec.ContainsKey("machine"))
      RunMachine(ctx, Spec.ReadMachine(spec), spec, acc);
    else
      throw new SpecException(
        "castgen: an entry is a blank file (\"blank\") or a machine file (\"machine\")"
      );
    return new GenResult(
      acc.Outputs,
      [.. acc.Questions.DistinctBy(q => (q.Feature, q.Text))]
    ) {
      Redraws = acc.Redraws,
      Inputs = [.. acc.Inputs.Values],
    };
  }

  private sealed class Acc {
    public List<GenOutput> Outputs { get; } = [];
    public List<Question> Questions { get; } = [];
    public List<string> Redraws { get; } = [];
    public Dictionary<string, GenInput> Inputs { get; } = [];

    public IReadOnlyList<GenOutput> Worked =>
      [.. Outputs.Where(o => o.Kinds.Contains("worked"))];
  }

  private static string FormName(string code) => code + "-form";

  private static void AddForm(
    Acc acc,
    string code,
    IEnumerable<Leaf> cast,
    PartRecord from,
    IReadOnlyList<string> cutFaces
  ) {
    var rec = new PartRecord { Shape = FormName(code) + ".json" };
    foreach (KeyValuePair<string, ElementPart> kv in from.Elements)
      rec.Elements[kv.Key] = kv.Value;
    foreach (KeyValuePair<string, Feature> kv in from.Features)
      rec.Features[kv.Key] = kv.Value;
    if (cutFaces.Count > 0) {
      var step = new Step { Id = "cut" };
      step.Faces.AddRange(cutFaces);
      rec.Steps.Add(step);
    }
    List<Leaf> leaves = [.. cast];
    foreach (Leaf l in leaves)
      rec.Elements[l.Name] = new ElementPart {
        Stage = "cast",
        Role = l.Role ?? "cast",
      };
    acc.Inputs.TryAdd(
      FormName(code),
      new GenInput(FormName(code), Flush.ShapeOf(leaves), rec)
    );
  }

  private static string EntryText(JObject machine, JToken entry) =>
    new JObject {
      ["source"] = machine["source"]?.DeepClone(),
      ["allowance"] = machine["allowance"]?.DeepClone(),
      ["entry"] = entry.DeepClone(),
    }.ToString(Newtonsoft.Json.Formatting.None);

  private sealed record Casting(
    string Code,
    IReadOnlyList<Leaf> Cast,
    IReadOnlyList<CoreHandoff> Cores,
    PartRecord Record,
    IReadOnlyList<string> CutFaces,
    double Allowance,
    IReadOnlyList<FlushPin> Pins,
    string? Station,
    IReadOnlyList<LieSpec>? Lies,
    IReadOnlyList<int> GroupEnds,
    IReadOnlyList<string> GroupNames,
    int Pieces,
    JToken? Parting,
    JToken? Feeds,
    string SpecText,
    string ShapeSha,
    string RecordText
  );

  private void RunBlank(GenContext ctx, BlankSpec spec, string text, Acc acc) {
    BlankResult blank = Blank.Build(ctx, spec);
    acc.Questions.AddRange(blank.Questions);
    if (blank.Questions.Count > 0)
      return;
    string shapeSha = "";
    PartRecord pinned = new();
    if (spec.Form.Source is { } form) {
      ResolvedSource rs = ctx.Resolve(form.Shape, form.Sha);
      shapeSha = rs.ShapeSha;
      pinned = rs.Record;
    }
    Cast(
      ctx,
      new Casting(
        spec.Blank,
        blank.Cast,
        blank.Cores,
        blank.Record,
        blank.CutFaces,
        spec.Allowance,
        Flush.Pins(pinned),
        spec.Station,
        LiesOf(spec.Station, spec.Lie, [], spec.Blank),
        [],
        spec.Form.Source is { Groups.Count: > 0 } drawn
          ? [LastSegment(drawn.Groups[0])]
          : [],
        spec.PerPour,
        spec.Parting,
        spec.Feeds,
        text,
        shapeSha,
        pinned.Write()
      ),
      acc
    );
    foreach (WorkedSpec w in spec.Worked)
      acc.Outputs.Add(Worked.ForBlank(ctx, blank, w, acc.Worked));
  }

  private void RunMachine(
    GenContext ctx,
    MachineSpec spec,
    JObject file,
    Acc acc
  ) {
    ResolvedSource rs = ctx.Resolve(spec.Source.Shape, spec.Source.Sha);
    var blanks = new Dictionary<string, BlankResult>(StringComparer.Ordinal);
    foreach (UserSpec user in spec.Users) {
      if (!blanks.TryGetValue(user.Blank, out BlankResult? blank))
        blanks[user.Blank] = blank = Blank.Build(
          ctx,
          Spec.ReadBlank(ctx.Blank(user.Blank))
        );
      if (blank.Questions.Count > 0) {
        acc.Questions.AddRange(blank.Questions);
        continue;
      }
      AddForm(acc, blank.Spec.Blank, blank.Cast, blank.Record, blank.CutFaces);
      WorkedResult made = Worked.ForUser(ctx, spec, user, blank, acc.Worked);
      acc.Redraws.AddRange(made.Redraws);
      acc.Questions.AddRange(made.Questions);
      if (made.Failures.Count > 0)
        throw new CastGenException(
          $"{user.Worked.Item}: {string.Join("; ", made.Failures)}"
        );
      acc.Outputs.AddRange(made.Outputs);
      foreach (GenInput i in made.Inputs)
        acc.Inputs.TryAdd(i.Name, i);
    }

    foreach (JoinedSpec j in spec.Joined) {
      JoinedResult joined = CastSet.Joined(ctx, spec, j);
      acc.Questions.AddRange(joined.Questions);
      if (joined.Questions.Count > 0)
        continue;
      WorkedResult made = Worked.ForJoined(ctx, spec, joined, acc.Worked);
      var form = new PartRecord();
      foreach (KeyValuePair<string, Feature> kv in rs.Record.Features)
        form.Features[kv.Key] = kv.Value;
      form.Steps.AddRange(rs.Record.Steps);
      AddForm(acc, j.Item, joined.Cast, form, []);
      acc.Outputs.AddRange(made.Outputs);
      foreach (GenInput i in made.Inputs)
        acc.Inputs.TryAdd(i.Name, i);
    }

    for (int i = 0; i < spec.Castings.Count; i++) {
      CastingSpec c = spec.Castings[i];
      string entry = EntryText(file, file["castings"]![i]!);
      CastSetResult set = CastSet.Of(ctx, rs, c.Groups, c.Cores);
      acc.Questions.AddRange(set.Questions);
      if (set.Questions.Count > 0)
        continue;
      var cut = new List<string>();
      var cutFaces = new List<string>();
      foreach (
        string id in c
          .Worked.SelectMany(w => w.Steps)
          .SelectMany(s => s.Cuts)
          .Distinct()
      ) {
        if (!rs.Record.Features.TryGetValue(id, out Feature? f))
          throw new CastGenException(
            $"{c.Item}: cut feature \"{id}\" is not in the record"
          );
        cut.Add(id);
        cutFaces.AddRange(f.Faces);
      }
      var casting = new Casting(
        c.Item,
        set.Cast,
        set.Cores,
        Recorded(rs.Record),
        cutFaces,
        c.Allowance,
        Flush.Pins(rs.Record),
        c.Station,
        LiesOf(c.Station, c.Lie, c.Groups, c.Item),
        set.GroupEnds,
        GroupNamesOf(c.Groups),
        c.PerPour,
        c.Parting,
        c.Feeds,
        entry,
        rs.ShapeSha,
        rs.Record.Write()
      );
      Cast(ctx, casting, acc);
      if (c.Worked.Count == 0)
        continue;
      var own = new BlankResult {
        Spec = new BlankSpec(
          c.Item,
          [0, 0, 0],
          1,
          new FormSpec(null, spec.Source, null, []),
          cut,
          c.Station,
          c.Lie,
          c.Parting,
          c.Cores,
          c.Feeds,
          c.Worked,
          c.Allowance,
          c.Route
        ),
      };
      own.Cast.AddRange(set.Cast);
      own.Cores.AddRange(set.Cores);
      own.CutFeatures.AddRange(cut);
      own.CutFaces.AddRange(cutFaces);
      own.TagCast();
      foreach (KeyValuePair<string, Feature> kv in rs.Record.Features)
        own.Record.Features[kv.Key] = kv.Value;
      own.Record.Steps.AddRange(rs.Record.Steps);
      foreach (WorkedSpec w in c.Worked)
        acc.Outputs.Add(Worked.ForBlank(ctx, own, w, acc.Worked));
    }

    if (spec.Set is { } s) {
      CastSetResult set = CastSet.ForSet(ctx, spec.Source, s);
      acc.Questions.AddRange(set.Questions);
      if (set.Questions.Count == 0)
        Cast(
          ctx,
          new Casting(
            s.Item,
            set.Cast,
            [],
            Recorded(rs.Record),
            [],
            spec.Allowance,
            Flush.Pins(rs.Record),
            s.Station,
            LiesOf(s.Station, null, s.Groups, s.Item),
            set.GroupEnds,
            GroupNamesOf(s.Groups),
            1,
            null,
            null,
            EntryText(file, file["set"]!),
            rs.ShapeSha,
            rs.Record.Write()
          ),
          acc
        );
    }
  }

  private static IEnumerable<Element> Descend(Element e) =>
    new[] { e }.Concat(e.Children.SelectMany(Descend));

  private static string LastSegment(string path) =>
    path[(path.LastIndexOf('/') + 1)..];

  private static List<string> GroupNamesOf(IReadOnlyList<GroupSpec> groups) =>
    [.. groups.Select(g => LastSegment(g.Groups[0]))];

  private static PartRecord Recorded(PartRecord source) {
    var rec = new PartRecord();
    foreach (KeyValuePair<string, Feature> kv in source.Features)
      rec.Features[kv.Key] = kv.Value;
    rec.Steps.AddRange(source.Steps);
    return rec;
  }

  private static IReadOnlyList<LieSpec>? LiesOf(
    string? station,
    JToken? entry,
    IReadOnlyList<GroupSpec> groups,
    string where
  ) {
    if (station == null && entry == null && groups.All(g => g.Lie == null))
      return null;
    return
    [
      .. groups.Count == 0
        ? [Lies.Read(entry, where)]
        : groups.Select(g => Lies.Of(g, entry, where)),
    ];
  }

  private static void Cast(GenContext ctx, Casting c, Acc acc) {
    var rec = new PartRecord { Shape = c.Code + ".json", Owner = "generated" };
    rec.Kinds.Add("casting");
    rec.Carried["source"] = new JObject {
      ["input"] = FormName(c.Code),
      ["allowance"] = c.Allowance,
    };
    AddForm(acc, c.Code, c.Cast, c.Record, c.CutFaces);
    foreach (KeyValuePair<string, Feature> kv in c.Record.Features)
      rec.Features[kv.Key] = kv.Value;
    rec.Steps.AddRange(c.Record.Steps);

    List<Leaf> allowed = Allowances.Out(c.Cast, c.CutFaces, c.Allowance);
    List<Leaf> leaves = Flush
      .Resolve(
        allowed,
        new HashSet<string>(c.CutFaces, StringComparer.Ordinal),
        c.Allowance,
        true,
        c.Pins,
        null,
        rec
      )
      .Leaves.ToList();
    leaves = Splits.Apply(leaves);

    IReadOnlyList<LieSpec> lies = c.Lies ?? [Identity];
    LieSpec lie = lies[0];
    bool oneLie = lies.All(l => l.Axes.SequenceEqual(lie.Axes));
    if (!oneLie && c.Cores.Count > 0)
      throw new CastGenException(
        $"{c.Code}: the groups of a casting with cores share one lie"
      );
    List<Leaf> Turned(List<Leaf> from) =>
      oneLie
        ? Lies.Apply(
          from,
          new LieSpec(lie.Axes.Select(a => a with { Offset = 0 }).ToList())
        )
        : TurnedByGroup(c, from, lies);
    List<Leaf> turned = Turned(leaves);
    HashSet<string> sunk =
    [
      .. FlushMoves
        .Read(rec)
        .Where(m => m.Rule is "sink" or "raise")
        .Select(m => m.Element),
    ];
    List<Leaf> unmoved =
    [
      .. Turned(Splits.Apply(allowed)).Where(l => sunk.Contains(l.Name)),
    ];

    List<Leaf> itemLeaves = oneLie ? leaves : Lies.Apply(turned, Inverse(lie));
    if (!oneLie)
      Lays.Write(rec, LaysOf(c, leaves, lies));

    Station? st = null;
    if (
      c.Station != null
      && !ctx.Settings.Stations.TryGetValue(c.Station, out st)
    )
      throw new CastGenException(
        $"{c.Code}: station \"{c.Station}\" is not in the settings"
      );
    int pieces = st == null ? 1 : Math.Max(1, c.Pieces);
    double step = 0;
    if (pieces > 1) {
      if (c.Cores.Count > 0)
        throw new CastGenException(
          $"{c.Code}: a pour of {pieces} pieces has cores, which are seated for one piece"
        );
      Box room = BlankResult.Union(turned.Select(l => l.Box))!.Value;
      step = room.Hi.Z - room.Lo.Z + 2 * st!.Margin;
    }
    List<Mat4d> at =
    [
      .. Enumerable
        .Range(0, pieces)
        .Select(k =>
          Mat4d.Translate(
            lie.Axes[0].Offset,
            lie.Axes[1].Offset,
            lie.Axes[2].Offset - k * step
          )
        ),
    ];
    List<Mat4d> pieceAt =
    [
      .. Enumerable
        .Range(0, pieces)
        .Select(k => Mat4d.Translate(0, 0, -k * step) * lie.Matrix),
    ];
    if (st != null) {
      FitReport fit = StationFit.Run(
        ctx.Settings,
        c.Code,
        [
          .. Enumerable
            .Range(0, pieces)
            .Select(k =>
              (IReadOnlyList<Leaf>)
                Lies.Apply(
                  turned,
                  new LieSpec([
                    new(0, 1, lie.Axes[0].Offset),
                    new(1, 1, lie.Axes[1].Offset),
                    new(2, 1, lie.Axes[2].Offset - k * step),
                  ])
                )
            ),
        ],
        c.Station
      );
      foreach (string row in fit.Table)
        ctx.Log(row);
      if (fit.Failures.Count > 0)
        throw new CastGenException(string.Join("; ", fit.Failures));
    }
    bool closed = st?.Parting != null;
    double? parting = null;
    if (closed) {
      double flask = st!.Parting!.Value;
      if (
        c.Parting is { Type: JTokenType.Integer or JTokenType.Float } n
        && Math.Abs((double)n - flask) > 1e-9
      )
        throw new CastGenException(
          $"{c.Code}: parting {(double)n} is not station \"{c.Station}\"'s {flask}"
        );
      parting = flask - lie.Axes[1].Offset;
    }

    Mat4d turn = Turn(lie);
    var cores = new List<GenOutput>();
    var prints = new List<PatternPrint>();
    foreach (CoreHandoff h in c.Cores) {
      string code =
        c.Cores.Count == 1
          ? $"drysandcore-{c.Code}"
          : $"drysandcore-{c.Code}-{h.Feature}";
      GenOutput core = Cores.Make(ctx, code, c.Code, h, c.SpecText);
      var cored = new PartRecord { Shape = code + "-source.json" };
      cored.Features[h.Feature] = new Feature { Origin = "cored" };
      foreach (Leaf l in h.Elements) {
        cored.Features[h.Feature].Elements.Add(l.Name);
        cored.Elements[l.Name] = new ElementPart { Stage = "cast" };
      }
      acc.Inputs.TryAdd(
        code + "-source",
        new GenInput(code + "-source", Flush.ShapeOf(h.Elements), cored)
      );
      ((JObject)core.Record.Carried["core"]!)["source"] = code + "-source";
      cores.Add(core);
      acc.Outputs.Add(core);
      acc.Outputs.Add(
        CoreBoxes.Make(
          ctx,
          code.Replace("drysandcore-", "corebox-"),
          code,
          core,
          c.SpecText
        )
      );
      foreach (
        Element e in core.Shape.Elements.Where(e =>
          PrintName().IsMatch(e.Name ?? "")
        )
      )
        prints.Add(Print(e, turn));
    }

    PatternResult pattern = Patterns.Make(
      ctx,
      "pattern-" + c.Code,
      turned,
      prints,
      parting,
      c.SpecText,
      st?.HeadHeight,
      unmoved
    );

    List<Leaf> shown = Uvs.Unroll(
      ctx,
      c.Code,
      [.. Uvs.Assign(ctx, c.Code, itemLeaves)]
    );
    foreach (Leaf l in shown)
      rec.Elements[l.Name] = new ElementPart {
        Stage = "cast",
        Role = l.Role ?? "cast",
      };
    var shape = new Shape();
    Func<Leaf, int> groupOf = GroupOf(c);
    foreach (
      IGrouping<int, Leaf> grp in shown.GroupBy(groupOf).OrderBy(x => x.Key)
    ) {
      var members = new List<Element>();
      foreach (Leaf l in grp) {
        Element e = Select.Clone(l.Element, false);
        e.Name = l.Name;
        members.Add(e);
      }
      string? name =
        c.GroupNames.Count > grp.Key ? c.GroupNames[grp.Key] : null;
      if (name == null) {
        foreach (Element m in members)
          shape.Elements.Add(m);
        continue;
      }
      var root = new Element {
        Name = members.Any(m => m.Name == name) ? name + "Group" : name,
      };
      root.Children.AddRange(members);
      shape.Elements.Add(root);
    }
    foreach (
      string key in shape
        .Elements.SelectMany(Descend)
        .SelectMany(e => e.Faces)
        .Select(kv => (kv.Value.Texture ?? "").TrimStart('#'))
        .Distinct()
        .Order(StringComparer.Ordinal)
    )
      shape.Textures.Add(new(key, ctx.Settings.EditablePath(key)));
    acc.Outputs.Add(
      new GenOutput(
        c.Code,
        rec.Kinds,
        shape,
        rec,
        Provenance.InputHash(
          c.SpecText,
          c.ShapeSha,
          Provenance.Sha256Of(c.RecordText + c.Record.Write()),
          Worked.SettingsSha(ctx.Settings)
        )
      )
    );
    acc.Outputs.Add(pattern.Pattern);
    acc.Outputs.AddRange(pattern.Loose);

    if (st == null)
      return;
    var seats = new List<CoreSeat>();
    foreach (GenOutput core in cores)
      seats.Add(
        new CoreSeat(
          core.Name,
          [
            .. core.Shape.Elements.Select(e => new Leaf(
              e.Name ?? "",
              e.Name ?? "",
              e.Name ?? "",
              e,
              "cast",
              "core"
            )),
          ],
          lie.Matrix
        )
      );
    FillingResult filling = Fillings.Make(
      ctx,
      new PourRequest(
        c.Code,
        c.Station!,
        closed,
        c.Code,
        pattern.Pattern.Name,
        itemLeaves,
        pieceAt,
        pattern.Pattern.Shape,
        seats,
        "chain",
        pieces > 1 ? "south" : "one",
        Risers(c.Code, c.Feeds)
      ),
      c.SpecText
    );
    string drawName = c.Code + "-draw";
    var drawRec = new PartRecord { Shape = drawName + ".json" };
    foreach (Element e in pattern.Pattern.Shape.Elements)
      drawRec.Elements[e.Name ?? ""] = new ElementPart {
        Stage = "cast",
        Role = "pattern",
      };
    acc.Inputs.TryAdd(
      drawName,
      new GenInput(drawName, Placed(pattern.Pattern.Shape, at), drawRec)
    );
    foreach (GenOutput? o in new[] { filling.Filling, filling.Cope })
      if (o != null)
        o.Record.Carried["draw"] = new JObject {
          ["input"] = drawName,
          ["parting"] = closed ? st.Parting : st.SandTo[1],
        };
    acc.Outputs.Add(filling.Filling);
    if (filling.Cope != null)
      acc.Outputs.Add(filling.Cope);
  }

  private static Shape Placed(Shape pattern, IReadOnlyList<Mat4d> pieces) {
    var shape = new Shape();
    for (int k = 0; k < pieces.Count; k++) {
      Vec3 d = pieces[k].Mul(Vec3.Zero);
      foreach (Element src in pattern.Elements) {
        Element e = Select.CloneTree(src, false);
        if (k > 0)
          e.Name = $"{e.Name}_piece{k + 1}";
        e.From = [e.From![0] + d.X, e.From[1] + d.Y, e.From[2] + d.Z];
        e.To = [e.To![0] + d.X, e.To[1] + d.Y, e.To[2] + d.Z];
        if (e.RotationOrigin != null)
          e.RotationOrigin =
          [
            e.RotationOrigin[0] + d.X,
            e.RotationOrigin[1] + d.Y,
            e.RotationOrigin[2] + d.Z,
          ];
        shape.Elements.Add(e);
      }
    }
    foreach (var t in pattern.Textures)
      shape.Textures.Add(t);
    return shape;
  }

  private static Func<Leaf, int> GroupOf(Casting c) {
    if (c.GroupEnds.Count == 0)
      return _ => 0;
    var byPath = new Dictionary<string, int>(StringComparer.Ordinal);
    int g = 0;
    for (int i = 0; i < c.Cast.Count; i++) {
      while (i >= c.GroupEnds[g])
        g++;
      byPath.TryAdd(c.Cast[i].SourcePath, g);
    }
    return l =>
      byPath.TryGetValue(l.SourcePath, out int found)
        ? found
        : throw new CastGenException(
          $"{c.Code}: {l.Name} belongs to no group of the casting"
        );
  }

  private static List<Leaf> TurnedByGroup(
    Casting c,
    IReadOnlyList<Leaf> leaves,
    IReadOnlyList<LieSpec> lies
  ) {
    Func<Leaf, int> groupOf = GroupOf(c);
    return
    [
      .. leaves.Select(l =>
      {
        int k = groupOf(l);
        LieSpec own = lies[k];
        return Lies.Apply(
            [l],
            new LieSpec([
              .. own.Axes.Select(
                (a, f) => a with { Offset = a.Offset - lies[0].Axes[f].Offset }
              ),
            ])
          )
          .Single();
      }),
    ];
  }

  private static Dictionary<string, Vec3> LaysOf(
    Casting c,
    IReadOnlyList<Leaf> leaves,
    IReadOnlyList<LieSpec> lies
  ) {
    Func<Leaf, int> groupOf = GroupOf(c);
    var lays = new Dictionary<string, Vec3>();
    foreach (Leaf l in leaves) {
      LieSpec own = lies[groupOf(l)];
      bool turnedApart = Enumerable
        .Range(0, 3)
        .Any(f =>
          own.Axes[f].Item != lies[0].Axes[f].Item
          || own.Axes[f].Sign != lies[0].Axes[f].Sign
        );
      if (turnedApart)
        continue;
      var by = new double[3];
      for (int f = 0; f < 3; f++)
        by[lies[0].Axes[f].Item] =
          lies[0].Axes[f].Sign * (own.Axes[f].Offset - lies[0].Axes[f].Offset);
      if (by.Any(v => v != 0))
        lays[Tags.Unsplit(l.Name)] = new Vec3(by[0], by[1], by[2]);
    }
    return lays;
  }

  private static readonly LieSpec Identity = new([
    new(0, 1, 0),
    new(1, 1, 0),
    new(2, 1, 0),
  ]);

  private static LieSpec Inverse(LieSpec lie) {
    var axes = new LieAxis[3];
    for (int f = 0; f < 3; f++)
      axes[lie.Axes[f].Item] = new LieAxis(f, lie.Axes[f].Sign, 0);
    return new LieSpec(axes);
  }

  private static Mat4d Turn(LieSpec lie) =>
    new LieSpec([.. lie.Axes.Select(a => a with { Offset = 0 })]).Matrix;

  private static PatternPrint Print(Element e, Mat4d turn) {
    Vec3 a = turn.Mul(Vec3.From(e.From!, Vec3.Zero)),
      b = turn.Mul(Vec3.From(e.To!, Vec3.Zero));
    return new PatternPrint(
      e.Name!,
      [Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)],
      [Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)]
    );
  }

  private static List<RiserAsk>? Risers(string where, JToken? feeds) {
    if (
      feeds == null
      || (feeds.Type == JTokenType.String && (string?)feeds == "auto")
    )
      return null;
    if (feeds is not JObject o || o.Properties().Any(p => p.Name != "risers"))
      throw new SpecException(
        $"{where}: feeds is \"auto\" or {{\"risers\": [{{\"kind\", \"at\"}}]}}"
      );
    var asks = new List<RiserAsk>();
    foreach (JToken t in o["risers"] as JArray ?? [])
      asks.Add(
        t is JObject r
        && r["kind"] is { Type: JTokenType.String } kind
        && r["at"] is JArray at
        && at.Count == 3
          ? new RiserAsk(
            (string)kind!,
            [(double)at[0], (double)at[1], (double)at[2]],
            (int?)r["piece"] ?? 1
          )
          : throw new SpecException(
            $"{where}: a riser is {{\"kind\", \"at\": [x, y, z]}}"
          )
      );
    return asks;
  }
}
