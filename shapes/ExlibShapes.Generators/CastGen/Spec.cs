using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Generators;

/// <summary>A spec file or entry is malformed.</summary>
public sealed class SpecException(string message) : Exception(message);

/// <summary>The inputs cannot make the output asked: a missing group, a window that cuts a
/// rotated element, a feature without the data a step needs.</summary>
public sealed class CastGenException(string message) : Exception(message);

/// <summary>A drawing pinned at a commit or file sha, and the groups taken from it.</summary>
/// <param name="Shape">The shape path.</param>
/// <param name="Sha">The pin.</param>
/// <param name="Groups">Element paths; every drawn element under one is taken.</param>
public sealed record SourceRef(
  string Shape,
  string Sha,
  IReadOnlyList<string> Groups
);

/// <summary>Where a group lands: its elements rotated about the drawing's origin (degrees,
/// <c>RotateByXyz</c>), then moved.</summary>
/// <param name="Rotate">[rx, ry, rz] degrees.</param>
/// <param name="Offset">[x, y, z] in drawing units.</param>
public sealed record Placement(double[] Rotate, double[] Offset) {
  /// <summary>No move.</summary>
  public static Placement None { get; } = new([0, 0, 0], [0, 0, 0]);

  /// <summary>The matrix applied to a drawing's world matrices.</summary>
  public Mat4d Matrix =>
    Mat4d.Translate(Vec3.From(Offset, Vec3.Zero))
    * Mat4d.RotateByXyz(Rotate[0], Rotate[1], Rotate[2]);
}

/// <summary>A group list with its placement and lie.</summary>
/// <param name="Groups">Element paths.</param>
/// <param name="At">Where the groups land; null is unmoved.</param>
/// <param name="Lie">The lie as written; null is the entry's.</param>
public sealed record GroupSpec(
  IReadOnlyList<string> Groups,
  Placement? At,
  JToken? Lie
);

/// <summary>A core named by the feature that needs it.</summary>
/// <param name="Feature">The cored feature's id in the record.</param>
/// <param name="Print">The print as written, handed to step 10.</param>
public sealed record CoreSpec(string Feature, JToken? Print);

/// <summary>One cutting step of a worked item.</summary>
/// <param name="Step">The step id (turn, bore, plane, drill, slot).</param>
/// <param name="Machine">The machine; null is the first of the step's row.</param>
/// <param name="Cuts">Blank cut-face features the step cuts.</param>
/// <param name="Features">The user's own worked features the step cuts.</param>
public sealed record StepSpec(
  string Step,
  string? Machine,
  IReadOnlyList<string> Cuts,
  IReadOnlyList<string> Features
);

/// <summary>A worked item.</summary>
/// <param name="Item">The item name.</param>
/// <param name="From">The blank or earlier worked item it is made from; null is the entry's own cast.</param>
/// <param name="Steps">The steps, in order.</param>
public sealed record WorkedSpec(
  string Item,
  string? From,
  IReadOnlyList<StepSpec> Steps
);

/// <summary>A blank's cast form.</summary>
/// <param name="Box">A box form [x, y, z]; null for a drawn form.</param>
/// <param name="Source">The drawn form; null for a box.</param>
/// <param name="Window">[x0, y0, z0, x1, y1, z1] in the drawing; null takes the groups whole.</param>
/// <param name="Stretch">Axis and plane pairs.</param>
public sealed record FormSpec(
  double[]? Box,
  SourceRef? Source,
  double[]? Window,
  IReadOnlyList<StretchSpec> Stretch
);

/// <summary>Moves every face past <paramref name="Plane"/> on <paramref name="Axis"/>.</summary>
/// <param name="Axis">0, 1 or 2 for x, y or z.</param>
/// <param name="Plane">The coordinate of the plane in the form's frame.</param>
public sealed record StretchSpec(int Axis, double Plane);

/// <summary>A blank file.</summary>
public sealed record BlankSpec(
  string Blank,
  double[] Size,
  int PerPour,
  FormSpec Form,
  IReadOnlyList<string>? CutFaces,
  string? Station,
  JToken? Lie,
  JToken? Parting,
  IReadOnlyList<CoreSpec> Cores,
  JToken? Feeds,
  IReadOnlyList<WorkedSpec> Worked,
  double Allowance,
  string? Route
);

/// <summary>A machine's use of a blank: one section of a part, or one part.</summary>
/// <param name="Blank">The blank's name.</param>
/// <param name="Groups">The user's groups in the machine drawing.</param>
/// <param name="Window">The section's world box in the drawing; null takes the groups whole.</param>
/// <param name="At">Where the section sits in the blank's frame.</param>
/// <param name="Count">How many the machine uses.</param>
/// <param name="Worked">The worked item made.</param>
public sealed record UserSpec(
  string Blank,
  IReadOnlyList<string> Groups,
  double[]? Window,
  Placement? At,
  int Count,
  WorkedSpec Worked
);

/// <summary>Work done on sections bolted into one bed.</summary>
/// <param name="Item">The worked item.</param>
/// <param name="From">The users' worked item names it joins.</param>
/// <param name="Steps">The steps, in order.</param>
public sealed record JoinedSpec(
  string Item,
  IReadOnlyList<string> From,
  IReadOnlyList<StepSpec> Steps
);

/// <summary>A machine's own casting (a kit when it has several group entries).</summary>
public sealed record CastingSpec(
  string Item,
  IReadOnlyList<GroupSpec> Groups,
  string? Station,
  JToken? Lie,
  JToken? Parting,
  IReadOnlyList<CoreSpec> Cores,
  JToken? Feeds,
  IReadOnlyList<WorkedSpec> Worked,
  JToken? Group,
  int Count,
  double Allowance,
  string? Route
);

/// <summary>The machine's small pieces cast as one item.</summary>
/// <param name="Item">The item name.</param>
/// <param name="Station">The station; the small flask.</param>
/// <param name="Groups">Each group entry with its placement.</param>
public sealed record SetSpec(
  string Item,
  string? Station,
  IReadOnlyList<GroupSpec> Groups
);

/// <summary>A drawn standard item with a casting of its own, counted in the machine's bill and not generated.</summary>
/// <param name="Item">The item name.</param>
/// <param name="Count">How many the machine uses.</param>
public sealed record StandardSpec(string Item, int Count);

/// <summary>A machine file.</summary>
public sealed record MachineSpec(
  string Machine,
  SourceRef Source,
  double Allowance,
  string? Route,
  IReadOnlyList<UserSpec> Users,
  IReadOnlyList<JoinedSpec> Joined,
  IReadOnlyList<CastingSpec> Castings,
  SetSpec? Set,
  IReadOnlyList<StandardSpec> Standard
);

/// <summary>Reads the CastGen spec files. Every object is strict: a key the schema does not name
/// is a <see cref="SpecException"/>.</summary>
public static class Spec {
  /// <summary>The allowance in drawing units when a spec gives none.</summary>
  public const double DefaultAllowance = 0.125;

  /// <summary>Reads a blank file.</summary>
  /// <exception cref="SpecException">A key is unknown, missing or of the wrong type.</exception>
  public static BlankSpec ReadBlank(JObject o) {
    var r = new Fields(o, "blank file");
    string name = r.Text("blank");
    double[] size = r.Vector("size", 3) ?? throw r.Missing("size");
    int perPour = (int)(r.Number("perPour") ?? 1);
    FormSpec form = ReadForm(r.Object("form") ?? throw r.Missing("form"));
    IReadOnlyList<string>? cut = null;
    JToken? cutFaces = r.Raw("cutFaces");
    if (cutFaces is JArray arr)
      cut = arr.Select(t => (string)t!).ToList();
    else if (cutFaces != null && (string?)cutFaces != "from-record")
      throw new SpecException(
        "blank file: cutFaces is \"from-record\" or a list"
      );
    var spec = new BlankSpec(
      name,
      size,
      perPour,
      form,
      cut,
      r.OptText("station"),
      r.Raw("lie"),
      r.Raw("parting"),
      ReadCores(r.Array("cores")),
      r.Raw("feeds"),
      ReadWorkedList(r.Array("worked")),
      r.Number("allowance") ?? DefaultAllowance,
      r.OptText("route")
    );
    r.Done();
    return spec;
  }

  /// <summary>Reads a machine file.</summary>
  /// <exception cref="SpecException">A key is unknown, missing or of the wrong type.</exception>
  public static MachineSpec ReadMachine(JObject o) {
    var r = new Fields(o, "machine file");
    string machine = r.Text("machine");
    SourceRef source = ReadSource(
      r.Object("source") ?? throw r.Missing("source"),
      null,
      true
    );
    var users = new List<UserSpec>();
    foreach (JToken t in r.Array("users") ?? [])
      users.Add(ReadUser(Fields.AsObject(t, "users")));
    var joined = new List<JoinedSpec>();
    foreach (JToken t in r.Array("joined") ?? [])
      joined.Add(ReadJoined(Fields.AsObject(t, "joined")));
    var castings = new List<CastingSpec>();
    double allowance = r.Number("allowance") ?? DefaultAllowance;
    string? route = r.OptText("route");
    foreach (JToken t in r.Array("castings") ?? [])
      castings.Add(
        ReadCasting(Fields.AsObject(t, "castings"), source, allowance, route)
      );
    JObject? setObj = r.Object("set");
    SetSpec? set = setObj == null ? null : ReadSet(setObj);
    var standard = new List<StandardSpec>();
    foreach (JToken t in r.Array("standard") ?? []) {
      var s = new Fields(Fields.AsObject(t, "standard"), "standard");
      int count = (int)(s.Number("count") ?? 1);
      standard.Add(new StandardSpec(s.Text("item"), count));
      s.Done();
    }
    r.Done();
    return new MachineSpec(
      machine,
      source,
      allowance,
      route,
      users,
      joined,
      castings,
      set,
      standard
    );
  }

  private static SourceRef ReadSource(
    JObject o,
    SourceRef? inherit,
    bool groupsOptional
  ) {
    var r = new Fields(o, "source");
    string shape =
      r.OptText("shape") ?? inherit?.Shape ?? throw r.Missing("shape");
    string sha = r.OptText("sha") ?? inherit?.Sha ?? throw r.Missing("sha");
    List<string> groups = r.Texts("groups");
    if (groups.Count == 0 && !groupsOptional)
      throw r.Missing("groups");
    r.Done();
    return new SourceRef(shape, sha, groups);
  }

  private static FormSpec ReadForm(JObject o) {
    var r = new Fields(o, "form");
    double[]? box = r.Vector("box", 3);
    string? shape = r.OptText("shape");
    string? sha = r.OptText("sha");
    List<string> groups = r.Texts("groups");
    double[]? window = r.Vector("window", 6);
    var stretch = new List<StretchSpec>();
    foreach (JToken t in r.Array("stretch") ?? []) {
      var s = new Fields(Fields.AsObject(t, "stretch"), "stretch");
      string axis = s.Text("axis");
      int a = axis switch {
        "x" => 0,
        "y" => 1,
        "z" => 2,
        _ => throw new SpecException($"stretch: axis \"{axis}\" is x, y or z"),
      };
      if (stretch.Any(x => x.Axis == a))
        throw new SpecException($"stretch: axis \"{axis}\" twice");
      stretch.Add(
        new StretchSpec(a, s.Number("plane") ?? throw s.Missing("plane"))
      );
      s.Done();
    }
    SourceRef? source = null;
    if (box != null) {
      if (
        shape != null
        || groups.Count > 0
        || window != null
        || stretch.Count > 0
      )
        throw new SpecException(
          "form: a box takes no shape, window or stretch"
        );
    } else
      source = new SourceRef(
        shape ?? throw r.Missing("shape or box"),
        sha ?? throw r.Missing("sha"),
        groups.Count > 0 ? groups : throw r.Missing("groups")
      );
    r.Done();
    return new FormSpec(box, source, window, stretch);
  }

  private static UserSpec ReadUser(JObject o) {
    var r = new Fields(o, "user");
    string blank = r.Text("blank");
    List<string> groups = r.Texts("groups");
    if (groups.Count == 0)
      throw r.Missing("groups");
    double[]? window = r.Vector("window", 6);
    JObject? at = r.Object("at");
    int count = (int)(r.Number("count") ?? 1);
    WorkedSpec worked = ReadWorked(
      r.Object("worked") ?? throw r.Missing("worked")
    );
    r.Done();
    return new UserSpec(
      blank,
      groups,
      window,
      at == null ? null : ReadPlacement(at),
      count,
      worked
    );
  }

  private static JoinedSpec ReadJoined(JObject o) {
    var r = new Fields(o, "joined");
    string item = r.Text("item");
    List<string> from = r.Texts("from");
    if (from.Count == 0)
      throw r.Missing("from");
    var steps = ReadSteps(r.Array("steps"));
    r.Done();
    return new JoinedSpec(item, from, steps);
  }

  private static CastingSpec ReadCasting(
    JObject o,
    SourceRef machine,
    double allowance,
    string? route
  ) {
    var r = new Fields(o, "casting");
    string item = r.Text("item");
    JObject? src = r.Object("source");
    if (src != null && (src.ContainsKey("shape") || src.ContainsKey("sha")))
      throw new SpecException(
        $"{item}: a casting takes its drawing from the machine file's source; source.shape and source.sha are not read"
      );
    SourceRef? own = src == null ? null : ReadSource(src, machine, true);
    var groups = new List<GroupSpec>();
    JArray? kit = r.Array("groups");
    if (kit != null)
      foreach (JToken t in kit)
        groups.Add(ReadGroup(Fields.AsObject(t, "groups")));
    else if (own != null)
      groups.Add(new GroupSpec(own.Groups, null, null));
    if (groups.Count == 0)
      throw r.Missing("source.groups or groups");
    var spec = new CastingSpec(
      item,
      groups,
      r.OptText("station"),
      r.Raw("lie"),
      r.Raw("parting"),
      ReadCores(r.Array("cores")),
      r.Raw("feeds"),
      ReadWorkedList(r.Array("worked")),
      r.Raw("group"),
      (int)(r.Number("count") ?? 1),
      r.Number("allowance") ?? allowance,
      r.OptText("route") ?? route
    );
    r.Done();
    return spec;
  }

  private static SetSpec ReadSet(JObject o) {
    var r = new Fields(o, "set");
    string item = r.Text("item");
    var groups = new List<GroupSpec>();
    foreach (JToken t in r.Array("groups") ?? [])
      groups.Add(ReadGroup(Fields.AsObject(t, "groups")));
    if (groups.Count == 0)
      throw r.Missing("groups");
    string? station = r.OptText("station");
    r.Done();
    return new SetSpec(item, station, groups);
  }

  private static GroupSpec ReadGroup(JObject o) {
    var r = new Fields(o, "group entry");
    List<string> groups = r.Texts("groups");
    if (groups.Count == 0)
      throw r.Missing("groups");
    JObject? at = r.Object("at");
    JToken? lie = r.Raw("lie");
    r.Done();
    return new GroupSpec(groups, at == null ? null : ReadPlacement(at), lie);
  }

  private static Placement ReadPlacement(JObject o) {
    var r = new Fields(o, "at");
    var p = new Placement(
      r.Vector("rotate", 3) ?? [0, 0, 0],
      r.Vector("offset", 3) ?? [0, 0, 0]
    );
    r.Done();
    return p;
  }

  private static List<CoreSpec> ReadCores(JArray? a) {
    var outp = new List<CoreSpec>();
    foreach (JToken t in a ?? []) {
      var r = new Fields(Fields.AsObject(t, "cores"), "core");
      outp.Add(new CoreSpec(r.Text("feature"), r.Raw("print")));
      r.Done();
    }
    return outp;
  }

  private static List<WorkedSpec> ReadWorkedList(JArray? a) =>
    [.. (a ?? []).Select(t => ReadWorked(Fields.AsObject(t, "worked")))];

  private static WorkedSpec ReadWorked(JObject o) {
    var r = new Fields(o, "worked");
    var w = new WorkedSpec(
      r.Text("item"),
      r.OptText("from"),
      ReadSteps(r.Array("steps"))
    );
    r.Done();
    return w;
  }

  private static List<StepSpec> ReadSteps(JArray? a) {
    var outp = new List<StepSpec>();
    foreach (JToken t in a ?? []) {
      if (t.Type == JTokenType.String) {
        outp.Add(new StepSpec((string)t!, null, [], []));
        continue;
      }
      var r = new Fields(Fields.AsObject(t, "steps"), "step");
      outp.Add(
        new StepSpec(
          r.Text("step"),
          r.OptText("machine"),
          r.Texts("cuts"),
          r.Texts("features")
        )
      );
      r.Done();
    }
    return outp;
  }

  /// <summary>The JSON-schema document of the machine and blank files.</summary>
  public static JObject Schema { get; } =
    JObject.Parse(
      """
      {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "title": "CastGen spec",
        "oneOf": [
          { "required": ["blank", "size", "form"], "properties": { "blank": { "type": "string" }, "size": { "type": "array", "minItems": 3, "maxItems": 3 }, "form": { "type": "object" } } },
          { "required": ["machine", "source"], "properties": { "machine": { "type": "string" }, "source": { "type": "object" } } }
        ]
      }
      """
    );
}

internal sealed class Fields {
  private readonly JObject o;
  private readonly string where;
  private readonly HashSet<string> used = [];

  public Fields(JObject o, string where) {
    this.o = o;
    this.where = where;
  }

  public static JObject AsObject(JToken t, string list) =>
    t as JObject
    ?? throw new SpecException($"{list}: an entry is not an object");

  public SpecException Missing(string key) =>
    new($"{where}: \"{key}\" is missing");

  public JToken? Raw(string key) {
    used.Add(key);
    return o[key];
  }

  public string Text(string key) => OptText(key) ?? throw Missing(key);

  public string? OptText(string key) {
    JToken? t = Raw(key);
    if (t == null)
      return null;
    return t.Type == JTokenType.String
      ? (string)t!
      : throw new SpecException($"{where}: \"{key}\" is not text");
  }

  public double? Number(string key) {
    JToken? t = Raw(key);
    if (t == null)
      return null;
    return t.Type is JTokenType.Integer or JTokenType.Float
      ? (double)t
      : throw new SpecException($"{where}: \"{key}\" is not a number");
  }

  public double[]? Vector(string key, int n) {
    JToken? t = Raw(key);
    if (t == null)
      return null;
    if (
      t is not JArray a
      || a.Count != n
      || a.Any(x => x.Type is not (JTokenType.Integer or JTokenType.Float))
    )
      throw new SpecException($"{where}: \"{key}\" is {n} numbers");
    return [.. a.Select(x => (double)x)];
  }

  public JObject? Object(string key) {
    JToken? t = Raw(key);
    if (t == null)
      return null;
    return t as JObject
      ?? throw new SpecException($"{where}: \"{key}\" is not an object");
  }

  public JArray? Array(string key) {
    JToken? t = Raw(key);
    if (t == null)
      return null;
    return t as JArray
      ?? throw new SpecException($"{where}: \"{key}\" is not a list");
  }

  public List<string> Texts(string key) {
    JArray? a = Array(key);
    var outp = new List<string>();
    foreach (JToken t in a ?? [])
      outp.Add(
        t.Type == JTokenType.String
          ? (string)t!
          : throw new SpecException($"{where}: \"{key}\" holds a non-text")
      );
    return outp;
  }

  public void Done() {
    foreach (JProperty p in o.Properties())
      if (!used.Contains(p.Name))
        throw new SpecException($"{where}: unknown key \"{p.Name}\"");
  }
}
