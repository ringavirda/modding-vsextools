using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using ExpandedLib.Shapes.Generators;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Ops;

/// <summary>A record skeleton and the calls it could not settle.</summary>
/// <param name="Record">The record, every stage and feature a guess unless the lane's tables confirm it.</param>
/// <param name="Questions">One per feature group whose worked faces the tables name without saying how they are made.</param>
internal sealed record SkeletonResult(
  PartRecord Record,
  IReadOnlyList<StoredQuestion> Questions
);

/// <summary>Builds a part record from a drawing's textures and a lane's tables.</summary>
internal static class Skeleton {
  public static readonly string[] Origins =
  [
    "turned",
    "bored",
    "planed",
    "filed",
  ];
  private static readonly string[] FaceNames =
  [
    "west|east",
    "down|up",
    "north|south",
  ];

  /// <summary>The record skeleton of <paramref name="shape"/>.</summary>
  /// <param name="shape">The drawing.</param>
  /// <param name="drawingName">The drawing's file name, written as the record's shape.</param>
  /// <param name="shapeSha">Lowercase hex sha256 of the drawing's bytes.</param>
  /// <param name="lane">The lane's tables; null for a record from textures alone.</param>
  /// <param name="spec">Names the machine file the questions belong to.</param>
  public static SkeletonResult Build(
    Shape shape,
    string drawingName,
    string shapeSha,
    LaneTables? lane,
    string spec
  ) {
    var record = new PartRecord {
      Shape = drawingName,
      ShapeSha = shapeSha,
      Owner = "fallen",
    };
    record.Kinds.Add("machine");
    List<PathedElement> all = Engine.Walk(shape);
    Dictionary<string, PathedElement> byPath = all.ToDictionary(
      p => p.Path,
      StringComparer.Ordinal
    );
    HashSet<string> iron5 = Iron5Codes(shape);
    foreach (PathedElement pe in all)
      if (pe.Element.Name is { } name)
        record.Elements[name] = new ElementPart {
          Stage = IsFastener(pe, all, iron5) ? "fastener" : "cast",
        };

    var questions = new List<StoredQuestion>();
    var lanes = new JObject();
    if (lane != null) {
      Confirmed c = Confirmed.Read(lane.Tables);
      var groups = new SortedDictionary<string, GroupFeature>(
        StringComparer.Ordinal
      );
      foreach (PathedElement pe in all) {
        string? name = pe.Element.Name;
        if (
          name == null
          || !(c.Faces.ContainsKey(name) || c.Rings.Contains(name))
        )
          continue;
        string holder = Holder(pe, byPath);
        if (!groups.TryGetValue(holder, out GroupFeature? g))
          groups[holder] = g = new GroupFeature();
        g.Paths.Add(pe.Path);
        if (!g.Elements.Contains(name))
          g.Elements.Add(name);
        foreach (string face in c.Faces.GetValueOrDefault(name) ?? [])
          g.Faces.Add($"{name}/{face}");
        g.Bored |= c.Bored.Contains(name);
      }
      foreach ((string id, GroupFeature g) in groups) {
        if (g.Faces.Count == 0 && !g.Bored)
          continue;
        var f = new Feature { Origin = g.Bored ? "bored" : null };
        f.Elements.AddRange(g.Elements);
        f.Faces.AddRange(g.Faces);
        if (!g.Bored)
          f.Unknown["unconfirmed"] = true;
        record.Features[id] = f;
        if (g.Bored)
          continue;
        List<Leaf> leaves = Select.Groups(shape, g.Paths);
        questions.Add(
          new StoredQuestion(
            spec,
            drawingName,
            id,
            $"\"{id}\" has faces the lane works after the pour: how are they made?",
            Origins,
            "planed",
            CastSet.FeatureSha(leaves)
          )
        );
      }
      lanes["module"] = lane.Module;
      lanes["moves"] = new JObject(
        c.Moves.OrderBy(m => m.Key, StringComparer.Ordinal)
          .Select(m => new JProperty(
            m.Key,
            new JArray(m.Value.Order(StringComparer.Ordinal))
          ))
      );
      lanes["unconfirmed"] = new JArray("stages", "origins");
    } else
      lanes["unconfirmed"] = new JArray("stages");
    record.Carried["import"] = lanes;
    return new SkeletonResult(record, questions);
  }

  /// <summary>Carries what an earlier record holds into a fresh skeleton. An element the earlier record names keeps its
  /// entry. A feature the earlier record holds without <c>unconfirmed</c> is kept as it is and asks nothing. An
  /// <c>unconfirmed</c> feature takes the origin of an earlier answer whose <c>featureSha</c> is the question's now, loses
  /// <c>unconfirmed</c> and asks nothing; an answer whose sha differs is dropped and the feature asks again. Features,
  /// steps, answers and record slots the skeleton has no part in are kept; the owner and the answers are the earlier
  /// record's.</summary>
  /// <param name="fresh">The skeleton just built.</param>
  /// <param name="earlier">The record already on disk.</param>
  public static SkeletonResult Merge(SkeletonResult fresh, PartRecord earlier) {
    PartRecord record = fresh.Record;
    var questions = new List<StoredQuestion>();
    var stale = new HashSet<string>(StringComparer.Ordinal);
    foreach (StoredQuestion q in fresh.Questions) {
      Answer? answer = earlier.Answers.FirstOrDefault(a =>
        a.Feature == q.Feature
      );
      Feature f = record.Features[q.Feature];
      if (
        earlier.Features.TryGetValue(q.Feature, out Feature? old)
        && !old.Unknown.ContainsKey("unconfirmed")
      )
        record.Features[q.Feature] = old;
      else if (answer != null && answer.FeatureSha == q.FeatureSha) {
        f.Origin = answer.Text;
        f.Unknown.Remove("unconfirmed");
      } else {
        if (answer != null)
          stale.Add(q.Feature);
        questions.Add(q);
      }
    }
    foreach (KeyValuePair<string, ElementPart> kv in earlier.Elements)
      if (record.Elements.ContainsKey(kv.Key))
        record.Elements[kv.Key] = kv.Value;
    foreach (KeyValuePair<string, Feature> kv in earlier.Features)
      record.Features.TryAdd(kv.Key, kv.Value);
    if (record.Steps.Count == 0)
      record.Steps.AddRange(earlier.Steps);
    record.Answers.AddRange(
      earlier.Answers.Where(a => !stale.Contains(a.Feature))
    );
    foreach (JProperty p in earlier.Carried.Properties())
      if (p.Name != "import")
        record.Carried[p.Name] = p.Value;
    record.Owner = earlier.Owner ?? record.Owner;
    return new SkeletonResult(record, questions);
  }

  private sealed class GroupFeature {
    public List<string> Paths { get; } = [];
    public List<string> Elements { get; } = [];
    public List<string> Faces { get; } = [];
    public bool Bored { get; set; }
  }

  private static HashSet<string> Iron5Codes(Shape shape) =>
    [
      .. shape
        .Textures.Where(t =>
          t.Value == "iron5"
          || t.Value.EndsWith("/iron5", StringComparison.Ordinal)
        )
        .Select(t => t.Key),
    ];

  private static bool Drawn(Element e) =>
    e.Faces.Any(f => f.Value.Enabled != false);

  private static bool IsFastener(
    PathedElement pe,
    List<PathedElement> all,
    HashSet<string> iron5
  ) {
    string prefix = pe.Path + "/";
    bool any = false;
    foreach (PathedElement q in all) {
      if (
        q.Path != pe.Path
        && !q.Path.StartsWith(prefix, StringComparison.Ordinal)
      )
        continue;
      foreach (KeyValuePair<string, Face> f in q.Element.Faces) {
        if (f.Value.Enabled == false)
          continue;
        any = true;
        string code = (f.Value.Texture ?? "").TrimStart('#');
        if (!iron5.Contains(code))
          return false;
      }
    }
    return any;
  }

  private static string Holder(
    PathedElement pe,
    Dictionary<string, PathedElement> byPath
  ) {
    if (pe.ParentPath == null)
      return pe.Element.Name!;
    PathedElement parent = byPath[pe.ParentPath];
    PathedElement p = parent;
    while (Drawn(p.Element)) {
      if (p.ParentPath == null)
        return parent.Element.Name!;
      p = byPath[p.ParentPath];
    }
    return p.Element.Name!;
  }

  /// <summary>What the lane's tables say of the drawing's elements.</summary>
  private sealed class Confirmed {
    public HashSet<string> Rings { get; } = [];
    public Dictionary<string, List<string>> Faces { get; } = [];
    public HashSet<string> Bored { get; } = [];
    public Dictionary<string, HashSet<string>> Moves { get; } = [];

    public static Confirmed Read(JObject tables) {
      var c = new Confirmed();
      bool bore = tables.ContainsKey("BORE");
      foreach (JProperty t in tables.Properties()) {
        if (t.Name.EndsWith("ALLOW", StringComparison.Ordinal))
          c.ReadAllow(t.Value);
        else if (t.Value is JObject o)
          foreach (JProperty row in o.Properties())
            if (row.Value is JObject entry && entry["allow"] is JObject allow)
              c.ReadAllow(allow);
        if (
          t.Name.EndsWith("SUNK", StringComparison.Ordinal)
          || t.Name.EndsWith("RAISED", StringComparison.Ordinal)
          || t.Name.EndsWith("TRIM", StringComparison.Ordinal)
        )
          c.ReadMoves(
            t.Name[(t.Name.LastIndexOf('_') + 1)..].ToLowerInvariant(),
            t.Value
          );
      }
      if (tables["RINGS"] is JObject rings)
        foreach (JProperty r in rings.Properties())
          if (
            r.Value is JArray names
            && names.All(n => n.Type == JTokenType.String)
          )
            foreach (JToken n in names)
              c.AddRing((string)n!, bore);
          else {
            c.AddRing(r.Name, false);
            if (r.Value is JArray { Count: 2 } pair && pair[1] is JArray ends)
              c.AddFaces(r.Name, ends);
          }
      return c;
    }

    private void AddRing(string name, bool bored) {
      Rings.Add(name);
      if (bored)
        Bored.Add(name);
    }

    private void ReadAllow(JToken table) {
      if (table is not JObject o)
        return;
      foreach (JProperty row in o.Properties())
        if (row.Value is JArray sides)
          AddFaces(row.Name, sides);
    }

    private void AddFaces(string name, JArray sides) {
      foreach (JToken s in sides)
        if (s is JArray { Count: 2 } pair) {
          int axis = (int)pair[0];
          int side = (int)pair[1];
          if (axis is < 0 or > 2 || side is < 0 or > 1)
            continue;
          string face = FaceNames[axis].Split('|')[side];
          if (!Faces.TryGetValue(name, out List<string>? list))
            Faces[name] = list = [];
          if (!list.Contains(face))
            list.Add(face);
        }
    }

    private void ReadMoves(string kind, JToken table) {
      IEnumerable<string> names = table switch {
        JObject o => o.Properties().Select(p => p.Name),
        JArray a => a.Where(x => x.Type == JTokenType.String)
          .Select(x => (string)x!),
        _ => [],
      };
      foreach (string n in names) {
        if (!Moves.TryGetValue(n, out HashSet<string>? set))
          Moves[n] = set = [];
        set.Add(kind);
      }
    }
  }
}
