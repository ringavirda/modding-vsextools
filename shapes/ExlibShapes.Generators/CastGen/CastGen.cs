using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Generators;

/// <summary>The casting generator: steps 1 to 11 chained over one blank file or one machine file entry.</summary>
public sealed partial class CastGen : IGenerator {
  private static readonly Lazy<string> Sources = new(HashAssemblies);

  [GeneratedRegex("^Print")]
  private static partial Regex PrintName();

  /// <inheritdoc />
  public string Id => "castgen";

  /// <summary>The sha256 of the assemblies the generator runs from: this one, Core's and Checks'.</summary>
  public string Version => Sources.Value;

  /// <inheritdoc />
  public JObject SpecSchema => (JObject)Spec.Schema.DeepClone();

  /// <summary>Runs a blank file or a machine file. A blank file gives its cast item, pattern and loose pieces,
  /// filling (and cope of a closed pour) at its station, cores and core boxes and the worked items it lists. A machine
  /// file gives each user's worked item, and for each own casting and the set the same products as a blank, its own
  /// worked items included. A casting whose cast set holds an untagged feature, or a user whose placement is out of
  /// its blank's band, returns its question and none of its outputs; every other product is still made. A pour holds
  /// one piece: <c>perPour</c> and <c>count</c> do not lay more.</summary>
  /// <param name="ctx">The context. A machine file's users need <see cref="GenContext.Blank"/>.</param>
  /// <param name="spec">The blank file (it has <c>blank</c>) or machine file (it has <c>machine</c>) as written.</param>
  /// <exception cref="SpecException">The entry is malformed.</exception>
  /// <exception cref="CastGenException">An input cannot make its output: a user fails its blank, a casting fits no
  /// station, the lies of a kit's groups differ, a closed pour or a head is on a lie that turns y, a joined entry is
  /// given.</exception>
  public GenResult Run(GenContext ctx, JObject spec) {
    var acc = new Acc();
    string text = spec.ToString(Newtonsoft.Json.Formatting.None);
    if (spec.ContainsKey("blank"))
      RunBlank(ctx, Spec.ReadBlank(spec), text, acc);
    else if (spec.ContainsKey("machine"))
      RunMachine(ctx, Spec.ReadMachine(spec), text, acc);
    else
      throw new SpecException(
        "castgen: an entry is a blank file (\"blank\") or a machine file (\"machine\")"
      );
    return new GenResult(
      acc.Outputs,
      [.. acc.Questions.DistinctBy(q => (q.Feature, q.Text))]
    ) {
      Redraws = acc.Redraws,
    };
  }

  private sealed class Acc {
    public List<GenOutput> Outputs { get; } = [];
    public List<Question> Questions { get; } = [];
    public List<string> Redraws { get; } = [];

    public IReadOnlyList<GenOutput> Worked =>
      [.. Outputs.Where(o => o.Kinds.Contains("worked"))];
  }

  private sealed record Casting(
    string Code,
    IReadOnlyList<Leaf> Cast,
    IReadOnlyList<CoreHandoff> Cores,
    PartRecord Record,
    IReadOnlyList<string> CutFaces,
    double Allowance,
    IReadOnlyList<FlushPin> Pins,
    string? Station,
    LieSpec? Lie,
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
        LieOf(spec.Station, spec.Lie, [], spec.Blank),
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
    string text,
    Acc acc
  ) {
    if (spec.Joined.Count > 0)
      throw new CastGenException(
        $"machine \"{spec.Machine}\": joined items are not built by the composer ({spec.Joined[0].Item})"
      );
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
      WorkedResult made = Worked.ForUser(ctx, spec, user, blank, acc.Worked);
      acc.Redraws.AddRange(made.Redraws);
      acc.Questions.AddRange(made.Questions);
      if (made.Failures.Count > 0)
        throw new CastGenException(
          $"{user.Worked.Item}: {string.Join("; ", made.Failures)}"
        );
      acc.Outputs.AddRange(made.Outputs);
    }

    foreach (CastingSpec c in spec.Castings) {
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
        LieOf(c.Station, c.Lie, c.Groups, c.Item),
        c.Parting,
        c.Feeds,
        $"{c.Item}\n{text}",
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
            LieOf(s.Station, null, s.Groups, s.Item),
            null,
            null,
            text,
            rs.ShapeSha,
            rs.Record.Write()
          ),
          acc
        );
    }
  }

  private static PartRecord Recorded(PartRecord source) {
    var rec = new PartRecord();
    foreach (KeyValuePair<string, Feature> kv in source.Features)
      rec.Features[kv.Key] = kv.Value;
    rec.Steps.AddRange(source.Steps);
    return rec;
  }

  private static LieSpec? LieOf(
    string? station,
    JToken? entry,
    IReadOnlyList<GroupSpec> groups,
    string where
  ) {
    if (station == null && entry == null && groups.All(g => g.Lie == null))
      return null;
    List<LieSpec> lies =
    [
      .. groups.Count == 0
        ? [Lies.Read(entry, where)]
        : groups.Select(g => Lies.Of(g, entry, where)),
    ];
    foreach (LieSpec l in lies.Skip(1))
      if (!l.Axes.SequenceEqual(lies[0].Axes))
        throw new CastGenException(
          $"{where}: the groups of a casting share one lie in M1"
        );
    return lies[0];
  }

  private static void Cast(GenContext ctx, Casting c, Acc acc) {
    var rec = new PartRecord { Shape = c.Code + ".json", Owner = "generated" };
    rec.Kinds.Add("casting");
    foreach (KeyValuePair<string, Feature> kv in c.Record.Features)
      rec.Features[kv.Key] = kv.Value;
    rec.Steps.AddRange(c.Record.Steps);

    List<Leaf> leaves = Allowances.Out(c.Cast, c.CutFaces, c.Allowance);
    leaves = Flush
      .Resolve(
        leaves,
        new HashSet<string>(c.CutFaces, StringComparer.Ordinal),
        c.Allowance,
        true,
        c.Pins,
        null,
        rec
      )
      .Leaves.ToList();
    leaves = Splits.Apply(leaves);

    LieSpec lie = c.Lie ?? Identity;
    Mat4d at = Mat4d.Translate(
      lie.Axes[0].Offset,
      lie.Axes[1].Offset,
      lie.Axes[2].Offset
    );
    List<Leaf> turned = Lies.Apply(
      leaves,
      new LieSpec(lie.Axes.Select(a => a with { Offset = 0 }).ToList())
    );

    Station? st = null;
    if (
      c.Station != null
      && !ctx.Settings.Stations.TryGetValue(c.Station, out st)
    )
      throw new CastGenException(
        $"{c.Code}: station \"{c.Station}\" is not in the settings"
      );
    if (st != null) {
      FitReport fit = StationFit.Run(
        ctx.Settings,
        c.Code,
        [
          Lies.Apply(
            turned,
            new LieSpec([
              new(0, 1, lie.Axes[0].Offset),
              new(1, 1, lie.Axes[1].Offset),
              new(2, 1, lie.Axes[2].Offset),
            ])
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
    bool keepsY = lie.Axes[1] is { Item: 1, Sign: 1 };
    if (st != null && (closed || st.HeadHeight != null) && !keepsY)
      throw new CastGenException(
        $"{c.Code}: station \"{c.Station}\" parts or feeds along y, so its lie keeps the item's y"
      );
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
      st?.HeadHeight
    );

    List<Leaf> shown = Uvs.Unroll(
      ctx,
      c.Code,
      [.. Uvs.Assign(ctx, c.Code, turned)]
    );
    foreach (Leaf l in shown)
      rec.Elements[l.Name] = new ElementPart {
        Stage = "cast",
        Role = l.Role ?? "cast",
      };
    var shape = new Shape();
    foreach (Leaf l in shown) {
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
        turned,
        [at],
        pattern.Pattern.Shape,
        seats,
        "chain",
        "one",
        Risers(c.Code, c.Feeds)
      ),
      c.SpecText
    );
    acc.Outputs.Add(filling.Filling);
    if (filling.Cope != null)
      acc.Outputs.Add(filling.Cope);
  }

  private static readonly LieSpec Identity = new([
    new(0, 1, 0),
    new(1, 1, 0),
    new(2, 1, 0),
  ]);

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

  private static string HashAssemblies() {
    var all = new StringBuilder();
    foreach (
      Assembly a in new[]
      {
        typeof(CastGen).Assembly,
        typeof(Mat4d).Assembly,
        typeof(Blankmatch).Assembly,
      }
    )
      all.Append(
          a.Location.Length > 0 && File.Exists(a.Location)
            ? Provenance.Sha256Of(File.ReadAllBytes(a.Location))
            : a.FullName
        )
        .Append('\n');
    return Provenance.Sha256Of(all.ToString());
  }
}
