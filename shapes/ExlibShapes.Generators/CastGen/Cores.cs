using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Generators;

/// <summary>Step 10: the dry sand core of a cored feature.</summary>
public static partial class Cores {
  /// <summary>The length of each print along the run, in voxels, where the spec's print names none.</summary>
  public const double DefaultPrint = 2;

  [GeneratedRegex("^Print")]
  private static partial Regex PrintName();

  /// <summary>The core of a cored feature: its elements as the sand's role texture, a print of the feature's
  /// section at each end of the run, elements over 16 cut as the cast's are. The run is the feature's longest axis
  /// (x before y before z on a tie) unless the print names one. The record carries the <c>core</c> slot
  /// (<c>of</c>, <c>feature</c>, <c>run</c>, <c>prints</c>) the corefit gate reads.</summary>
  /// <param name="ctx">The context; its settings give the core role's texture.</param>
  /// <param name="item">The core item's name.</param>
  /// <param name="cast">The code of the cast item the feature belongs to.</param>
  /// <param name="handoff">The cored feature step 2 handed on. Its print is a number, the print length in
  /// voxels, or an object with <c>length</c> and <c>run</c> (<c>x</c>, <c>y</c> or <c>z</c>); null gives
  /// <see cref="DefaultPrint"/> on the longest axis.</param>
  /// <param name="spec">The spec text the provenance hash is taken over.</param>
  /// <exception cref="CastGenException">The feature has no elements, the print is malformed, an element is named
  /// like a print.</exception>
  public static GenOutput Make(
    GenContext ctx,
    string item,
    string cast,
    CoreHandoff handoff,
    string spec
  ) {
    if (handoff.Elements.Count == 0)
      throw new CastGenException(
        $"{item}: core \"{handoff.Feature}\" has no elements"
      );
    foreach (Leaf l in handoff.Elements)
      if (PrintName().IsMatch(l.Name))
        throw new CastGenException(
          $"{item}: element \"{l.Name}\" is named like a print"
        );
    (double length, int run) = ReadPrint(item, handoff);
    Box all = Bounds(handoff.Elements.Select(l => l.Box));
    string key = "#" + ctx.Settings.RoleKey("core");

    List<Leaf> leaves =
    [
      .. handoff.Elements.Select(l => Styled(l.Name, l.Element, key, "core")),
    ];
    for (int end = 0; end < 2; end++) {
      double[] from = [all.Lo.X, all.Lo.Y, all.Lo.Z],
        to = [all.Hi.X, all.Hi.Y, all.Hi.Z];
      if (end == 0) {
        to[run] = from[run];
        from[run] -= length;
      } else {
        from[run] = to[run];
        to[run] += length;
      }
      string name = $"Print{end + 1}";
      var e = new Element {
        Name = name,
        From = from,
        To = to,
      };
      foreach (string face in FaceAxes.Names)
        e.Faces.Add(new(face, new Face()));
      leaves.Add(Styled(name, e, key, "core"));
    }
    leaves = Splits.Apply(leaves);
    leaves = Uvs.Assign(ctx, item, leaves);

    var rec = new PartRecord { Shape = item + ".json", Owner = "generated" };
    rec.Kinds.Add("core");
    var shape = new Shape();
    foreach (Leaf l in leaves) {
      rec.Elements[l.Name] = new ElementPart { Stage = "cast", Role = "core" };
      shape.Elements.Add(Named(l));
    }
    Textures(ctx, shape, leaves);
    rec.Carried["core"] = new JObject {
      ["of"] = cast,
      ["feature"] = handoff.Feature,
      ["run"] = "xyz"[run].ToString(),
      ["prints"] = new JArray("Print*"),
    };
    string hash = Provenance.InputHash(
      spec,
      "",
      Provenance.Sha256Of(
        CanonicalWriter.Write(Flush.ShapeOf(handoff.Elements))
          + $"\n{handoff.Feature} {run} {length.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
      ),
      Provenance.Sha256Of(key)
    );
    return new GenOutput(item, rec.Kinds, shape, rec, hash);
  }

  private static (double Length, int Run) ReadPrint(
    string item,
    CoreHandoff handoff
  ) {
    Box all = Bounds(handoff.Elements.Select(l => l.Box));
    Vec3 size = all.Hi - all.Lo;
    int run = Enumerable.Range(0, 3).MaxBy(a => size[a]);
    double length = DefaultPrint;
    switch (handoff.Print) {
      case null:
        break;
      case JValue { Type: JTokenType.Integer or JTokenType.Float } v:
        length = (double)v;
        break;
      case JObject o:
        foreach (JProperty p in o.Properties())
          switch (p.Name) {
            case "length"
              when p.Value
                is JValue { Type: JTokenType.Integer or JTokenType.Float } n:
              length = (double)n;
              break;
            case "run" when (string?)p.Value is "x" or "y" or "z":
              run = "xyz".IndexOf((string)p.Value!, StringComparison.Ordinal);
              break;
            default:
              throw new CastGenException(
                $"{item}: core \"{handoff.Feature}\" print has no valid \"{p.Name}\""
              );
          }
        break;
      default:
        throw new CastGenException(
          $"{item}: core \"{handoff.Feature}\" print is a number or an object of length and run"
        );
    }
    if (!(length > 0))
      throw new CastGenException(
        $"{item}: core \"{handoff.Feature}\" print length {length} is not positive"
      );
    return (length, run);
  }

  internal static Box Bounds(IEnumerable<Box> boxes) {
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

  internal static Leaf Styled(
    string name,
    Element element,
    string key,
    string role
  ) {
    Element e = Select.Clone(element, true);
    e.Name = name;
    foreach (KeyValuePair<string, Face> kv in e.Faces)
      kv.Value.Texture = key;
    return new Leaf(name, name, name, e, "cast", role);
  }

  internal static Element Named(Leaf l) {
    Element e = Select.Clone(l.Element, false);
    e.Name = l.Name;
    return e;
  }

  internal static void Textures(
    GenContext ctx,
    Shape shape,
    IEnumerable<Leaf> leaves
  ) {
    foreach (
      string key in leaves
        .SelectMany(l => l.Element.Faces)
        .Select(kv => (kv.Value.Texture ?? "").TrimStart('#'))
        .Distinct()
        .Order(StringComparer.Ordinal)
    )
      shape.Textures.Add(new(key, ctx.Settings.EditablePath(key)));
  }
}
