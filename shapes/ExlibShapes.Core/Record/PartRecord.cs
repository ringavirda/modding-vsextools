using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Core;

/// <summary>The metadata kept beside a shape, <c>&lt;name&gt;.part.json</c>, schema 1. Every slot the
/// design names is read and written back; a key the record does not model survives a round trip in
/// <see cref="Carried"/>. A part record is never owned: any caller may rewrite it.</summary>
public sealed class PartRecord {
  /// <summary>The schema version this reader accepts.</summary>
  public const int SchemaVersion = 1;

  /// <summary>The shape file name the record describes.</summary>
  public string Shape { get; set; } = "";

  /// <summary>Lowercase hex sha256 of the shape file bytes the record was last reconciled against.</summary>
  public string ShapeSha { get; set; } = "";

  /// <summary>What the part is: casting, pattern, filling, core, corebox, machine, item, fitting.</summary>
  public List<string> Kinds { get; } = [];

  /// <summary><c>fallen</c> or <c>generated</c>; null when absent.</summary>
  public string? Owner { get; set; }

  /// <summary>Per element name, its stage, role, family and machine flag. Reconcile expects every
  /// named element of the shape here.</summary>
  public Dictionary<string, ElementPart> Elements { get; } = [];

  /// <summary>Per feature id, the elements that make it and how it is worked.</summary>
  public Dictionary<string, Feature> Features { get; } = [];

  /// <summary>The cutting steps, in order.</summary>
  public List<Step> Steps { get; } = [];

  /// <summary>The <c>cells</c> slot as written, null when absent.</summary>
  public JArray? Cells { get; set; }

  /// <summary>Per clip code, the <c>onAnimationEnd</c> value the runtime shape gets.</summary>
  public Dictionary<string, string> Clips { get; } = [];

  /// <summary>The <c>ratings</c> object as written, null when absent.</summary>
  public JObject? Ratings { get; set; }

  /// <summary>Per check id, the stored baseline as written.</summary>
  public Dictionary<string, JToken> Baselines { get; } = [];

  /// <summary>The answers fallen gave to open questions, in the order given.</summary>
  public List<Answer> Answers { get; } = [];

  /// <summary>Slots the design names whose use is later (held, ports, controls, doors, stages, reach,
  /// sweeps, shapeInventory, boxes, frame, marks) and any key not modelled, in file order.</summary>
  public JObject Carried { get; } = [];

  private static readonly string[] Modelled =
  [
    "schema",
    "shape",
    "shapeSha",
    "kinds",
    "owner",
    "elements",
    "features",
    "steps",
    "cells",
    "clips",
    "ratings",
    "baselines",
    "answers",
  ];

  /// <summary>Reads the record at <paramref name="path"/>.</summary>
  /// <exception cref="FileNotFoundException"><paramref name="path"/> does not exist.</exception>
  /// <exception cref="JsonException">The text is not a schema 1 record or a slot has the wrong type.</exception>
  public static PartRecord ReadFile(string path) {
    if (!File.Exists(path))
      throw new FileNotFoundException($"No such part record: {path}", path);
    try {
      return Read(File.ReadAllText(path));
    } catch (JsonException e) {
      throw new JsonException($"{path}: {e.Message}", e);
    }
  }

  /// <summary>Reads a record from its JSON text.</summary>
  /// <exception cref="JsonException">The text is not a schema 1 record or a slot has the wrong type.</exception>
  public static PartRecord Read(string text) {
    JToken root = JToken.Parse(text);
    if (root is not JObject o)
      throw new JsonException(
        "not a part record (the document is not an object)"
      );
    if (
      o["schema"]?.Type != JTokenType.Integer
      || o.Value<int>("schema") != SchemaVersion
    )
      throw new JsonException($"part record schema is not {SchemaVersion}");
    var r = new PartRecord {
      Shape = RecordJson.Text(o, "shape") ?? "",
      ShapeSha = RecordJson.Text(o, "shapeSha") ?? "",
      Owner = RecordJson.Text(o, "owner"),
      Cells = RecordJson.Array(o, "cells"),
      Ratings = RecordJson.Object(o, "ratings"),
    };
    r.Kinds.AddRange(RecordJson.Texts(o, "kinds"));
    foreach (
      JProperty p in RecordJson.Object(o, "elements")?.Properties() ?? []
    )
      r.Elements[p.Name] = ElementPart.Read(RecordJson.AsObject(p));
    foreach (
      JProperty p in RecordJson.Object(o, "features")?.Properties() ?? []
    )
      r.Features[p.Name] = Feature.Read(RecordJson.AsObject(p));
    foreach (JToken t in RecordJson.Array(o, "steps") ?? [])
      r.Steps.Add(Step.Read(RecordJson.AsObject(t, "steps")));
    foreach (JProperty p in RecordJson.Object(o, "clips")?.Properties() ?? [])
      r.Clips[p.Name] = RecordJson.AsText(p);
    foreach (
      JProperty p in RecordJson.Object(o, "baselines")?.Properties() ?? []
    )
      r.Baselines[p.Name] = p.Value;
    foreach (JToken t in RecordJson.Array(o, "answers") ?? [])
      r.Answers.Add(Answer.Read(RecordJson.AsObject(t, "answers")));
    foreach (JProperty p in o.Properties())
      if (!Modelled.Contains(p.Name))
        r.Carried[p.Name] = p.Value.DeepClone();
    return r;
  }

  /// <summary>The record as canonical text, ending in a line feed.</summary>
  public string Write() {
    var o = new JObject {
      ["schema"] = SchemaVersion,
      ["shape"] = Shape,
      ["shapeSha"] = ShapeSha,
      ["kinds"] = new JArray(Kinds),
    };
    if (Owner != null)
      o["owner"] = Owner;
    o["elements"] = new JObject(
      Elements.Select(e => new JProperty(e.Key, e.Value.Write()))
    );
    o["features"] = new JObject(
      Features.Select(e => new JProperty(e.Key, e.Value.Write()))
    );
    o["steps"] = new JArray(Steps.Select(s => s.Write()));
    if (Cells != null)
      o["cells"] = Cells.DeepClone();
    o["clips"] = new JObject(Clips.Select(e => new JProperty(e.Key, e.Value)));
    if (Ratings != null)
      o["ratings"] = Ratings.DeepClone();
    o["baselines"] = new JObject(
      Baselines.Select(e => new JProperty(e.Key, e.Value.DeepClone()))
    );
    o["answers"] = new JArray(Answers.Select(a => a.Write()));
    foreach (JProperty p in Carried.Properties())
      o[p.Name] = p.Value.DeepClone();
    return JsonLayout.Canonical.Print(o) + "\n";
  }

  /// <summary>Writes the record to <paramref name="path"/> through <see cref="AtomicFile"/>. A record is
  /// never owned, so no provenance is consulted.</summary>
  /// <exception cref="IOException">The file cannot be written.</exception>
  public void WriteFile(string path) => AtomicFile.WriteAllText(path, Write());

  /// <summary>Counts the record's elements for a bill: a rod is an element flagged <c>rod</c>, a
  /// fastener is a non-rod element at stage <c>fastener</c>, every other element is a part.</summary>
  public PartCount Count() {
    int parts = 0,
      fasteners = 0,
      rods = 0;
    var by = new SortedDictionary<string, int>(System.StringComparer.Ordinal);
    foreach (ElementPart e in Elements.Values) {
      if (e.Rod)
        rods++;
      else if (e.Stage == "fastener")
        fasteners++;
      else
        parts++;
      string key = $"{e.Stage ?? ""}/{e.Role ?? ""}";
      by[key] = by.GetValueOrDefault(key) + 1;
    }
    return new PartCount(parts, fasteners, rods, by);
  }
}

/// <summary>The result of <see cref="PartRecord.Count"/>.</summary>
/// <param name="Parts">Elements that are neither fasteners nor rods.</param>
/// <param name="Fasteners">Non-rod elements at stage <c>fastener</c>.</param>
/// <param name="Rods">Elements flagged <c>rod</c>.</param>
/// <param name="ByStageRole">Element counts keyed <c>stage/role</c>; an absent stage or role is empty.</param>
public sealed record PartCount(
  int Parts,
  int Fasteners,
  int Rods,
  IReadOnlyDictionary<string, int> ByStageRole
);

/// <summary>One element's entry in a part record.</summary>
public sealed class ElementPart {
  /// <summary>cast, worked, fastener or assembled; null when absent.</summary>
  public string? Stage { get; set; }

  /// <summary>The texture role; null when absent.</summary>
  public string? Role { get; set; }

  /// <summary>The shared part id; null when absent.</summary>
  public string? Family { get; set; }

  /// <summary>Whether the element keeps its in-service look inside a machine.</summary>
  public bool InMachine { get; set; }

  /// <summary>Whether the element is a rod for the bill (<see cref="PartRecord.Count"/>).</summary>
  public bool Rod { get; set; }

  /// <summary>Keys not modelled, in file order.</summary>
  public JObject Unknown { get; } = [];

  internal static ElementPart Read(JObject o) {
    var e = new ElementPart {
      Stage = RecordJson.Text(o, "stage"),
      Role = RecordJson.Text(o, "role"),
      Family = RecordJson.Text(o, "family"),
      InMachine = RecordJson.Flag(o, "inMachine"),
      Rod = RecordJson.Flag(o, "rod"),
    };
    RecordJson.CopyUnknown(
      o,
      e.Unknown,
      "stage",
      "role",
      "family",
      "inMachine",
      "rod"
    );
    return e;
  }

  internal JObject Write() {
    var o = new JObject();
    if (Stage != null)
      o["stage"] = Stage;
    if (Role != null)
      o["role"] = Role;
    if (Family != null)
      o["family"] = Family;
    if (InMachine)
      o["inMachine"] = true;
    if (Rod)
      o["rod"] = true;
    return RecordJson.Merge(o, Unknown);
  }
}

/// <summary>One feature of a part: the elements that form it and how it is made.</summary>
public sealed class Feature {
  /// <summary>Names of the elements that make the feature.</summary>
  public List<string> Elements { get; } = [];

  /// <summary>cast, cored, drilled, turned, bored, planed or filed; null when absent.</summary>
  public string? Origin { get; set; }

  /// <summary>For a cast feature, boss, lug, ear, rib, tongue, web or foot; null when absent.</summary>
  public string? Form { get; set; }

  /// <summary>Faces as <c>name/direction</c>.</summary>
  public List<string> Faces { get; } = [];

  /// <summary>The cutting step id that makes the feature; null when absent.</summary>
  public string? Step { get; set; }

  /// <summary>Whether something slides or runs on the feature's face.</summary>
  public bool Sliding { get; set; }

  /// <summary>Keys not modelled, in file order.</summary>
  public JObject Unknown { get; } = [];

  internal static Feature Read(JObject o) {
    var f = new Feature {
      Origin = RecordJson.Text(o, "origin"),
      Form = RecordJson.Text(o, "form"),
      Step = RecordJson.Text(o, "step"),
      Sliding = RecordJson.Flag(o, "sliding"),
    };
    f.Elements.AddRange(RecordJson.Texts(o, "elements"));
    f.Faces.AddRange(RecordJson.Texts(o, "faces"));
    RecordJson.CopyUnknown(
      o,
      f.Unknown,
      "elements",
      "origin",
      "form",
      "faces",
      "step",
      "sliding"
    );
    return f;
  }

  internal JObject Write() {
    var o = new JObject { ["elements"] = new JArray(Elements) };
    if (Origin != null)
      o["origin"] = Origin;
    if (Form != null)
      o["form"] = Form;
    o["faces"] = new JArray(Faces);
    if (Step != null)
      o["step"] = Step;
    if (Sliding)
      o["sliding"] = true;
    return RecordJson.Merge(o, Unknown);
  }
}

/// <summary>One cutting step of a part.</summary>
public sealed class Step {
  /// <summary>The step id (turn, bore, plane, drill, slot).</summary>
  public string Id { get; set; } = "";

  /// <summary>The machine that does the step; null when absent.</summary>
  public string? Tool { get; set; }

  /// <summary>The hand route beside the machine; null when absent.</summary>
  public string? Hand { get; set; }

  /// <summary>Faces the step cuts, as <c>name/direction</c>.</summary>
  public List<string> Faces { get; } = [];

  /// <summary>Keys not modelled, in file order.</summary>
  public JObject Unknown { get; } = [];

  internal static Step Read(JObject o) {
    var s = new Step {
      Id = RecordJson.Text(o, "id") ?? "",
      Tool = RecordJson.Text(o, "tool"),
      Hand = RecordJson.Text(o, "hand"),
    };
    s.Faces.AddRange(RecordJson.Texts(o, "faces"));
    RecordJson.CopyUnknown(o, s.Unknown, "id", "tool", "hand", "faces");
    return s;
  }

  internal JObject Write() {
    var o = new JObject { ["id"] = Id };
    if (Tool != null)
      o["tool"] = Tool;
    if (Hand != null)
      o["hand"] = Hand;
    o["faces"] = new JArray(Faces);
    return RecordJson.Merge(o, Unknown);
  }
}

/// <summary>An answer fallen gave to a generator's question.</summary>
public sealed class Answer {
  /// <summary>The feature id the question was about.</summary>
  public string Feature { get; set; } = "";

  /// <summary>The question as asked.</summary>
  public string Question { get; set; } = "";

  /// <summary>The answer in his words.</summary>
  public string Text { get; set; } = "";

  /// <summary>Who answered.</summary>
  public string By { get; set; } = "";

  /// <summary>The date given, as written.</summary>
  public string Date { get; set; } = "";

  /// <summary>Hash of the feature's elements when answered; a regeneration asks again when it differs.</summary>
  public string FeatureSha { get; set; } = "";

  /// <summary>Keys not modelled, in file order.</summary>
  public JObject Unknown { get; } = [];

  internal static Answer Read(JObject o) {
    var a = new Answer {
      Feature = RecordJson.Text(o, "feature") ?? "",
      Question = RecordJson.Text(o, "question") ?? "",
      Text = RecordJson.Text(o, "answer") ?? "",
      By = RecordJson.Text(o, "by") ?? "",
      Date = RecordJson.Text(o, "date") ?? "",
      FeatureSha = RecordJson.Text(o, "featureSha") ?? "",
    };
    RecordJson.CopyUnknown(
      o,
      a.Unknown,
      "feature",
      "question",
      "answer",
      "by",
      "date",
      "featureSha"
    );
    return a;
  }

  internal JObject Write() =>
    RecordJson.Merge(
      new JObject {
        ["feature"] = Feature,
        ["question"] = Question,
        ["answer"] = Text,
        ["by"] = By,
        ["date"] = Date,
        ["featureSha"] = FeatureSha,
      },
      Unknown
    );
}

internal static class RecordJson {
  public static string? Text(JObject o, string key) =>
    o.TryGetValue(key, out JToken? v)
      ? v.Type == JTokenType.String
        ? v.Value<string>()
        : throw new JsonException($"\"{key}\" is not a string")
      : null;

  public static bool Flag(JObject o, string key) =>
    !o.TryGetValue(key, out JToken? v) ? false
    : v.Type == JTokenType.Boolean ? v.Value<bool>()
    : throw new JsonException($"\"{key}\" is not a boolean");

  public static IEnumerable<string> Texts(JObject o, string key) =>
    Array(o, key)
      ?.Select(t =>
        t.Type == JTokenType.String
          ? t.Value<string>()!
          : throw new JsonException($"\"{key}\" holds a non-string")
      )
    ?? [];

  public static JArray? Array(JObject o, string key) =>
    o.TryGetValue(key, out JToken? v)
      ? v as JArray ?? throw new JsonException($"\"{key}\" is not an array")
      : null;

  public static JObject? Object(JObject o, string key) =>
    o.TryGetValue(key, out JToken? v)
      ? v as JObject ?? throw new JsonException($"\"{key}\" is not an object")
      : null;

  public static JObject AsObject(JProperty p) =>
    p.Value as JObject
    ?? throw new JsonException($"\"{p.Name}\" is not an object");

  public static JObject AsObject(JToken t, string list) =>
    t as JObject ?? throw new JsonException($"\"{list}\" holds a non-object");

  public static string AsText(JProperty p) =>
    p.Value.Type == JTokenType.String
      ? p.Value.Value<string>()!
      : throw new JsonException($"\"{p.Name}\" is not a string");

  public static void CopyUnknown(
    JObject from,
    JObject into,
    params string[] known
  ) {
    foreach (JProperty p in from.Properties())
      if (!known.Contains(p.Name))
        into[p.Name] = p.Value.DeepClone();
  }

  public static JObject Merge(JObject o, JObject unknown) {
    foreach (JProperty p in unknown.Properties())
      o[p.Name] = p.Value.DeepClone();
    return o;
  }
}
