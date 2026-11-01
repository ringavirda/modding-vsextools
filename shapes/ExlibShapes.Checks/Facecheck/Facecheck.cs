using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// Faces that would draw untextured, or not at all, where they can be seen; and a copy's uvs against its source.
/// </summary>
/// <remarks>
/// <para>Every element with a non-zero size is checked on its six faces. A face is at fault when it is absent,
/// disabled, names no texture or a key the shape's <c>textures</c> does not hold, has a zero-area uv, or has a
/// uv outside 0..the texture's size. A faulty face is reported when it is visible: some point of a 0.25 grid on
/// it, 0.02 outward, lies inside no other element at rest or at any integer frame of any clip.</para>
/// <para>Given the shipped shape the copy was made from and a uv table, each face is also held to the table. A
/// copy's element is matched to the shipped one at its path, else to the last shipped one of its name. A face of
/// an element the shipped shape does not match is created, one whose uv rect differs in size from the shipped
/// face's is resized, any other is the shipped face and keeps its uv and rotation unless its texture key
/// differs. A created or resized face of a "random" texture is at fault at origin 0,0 unless its rect spans the
/// whole texture; a created face of a patterned texture is at fault off the table's origin or rotation, and a
/// kept one off the shipped uv and rotation, except an outward face of a round run that passes the run's uv
/// verdicts. Every face of a "position" texture is at fault off the uv of its place in the block or turned.
/// A table's <c>changes</c> list names shipped faces the copy changes on purpose: printed with the reason, not a
/// fault; an entry whose face is as shipped is printed as unused; one naming an element the copy lacks is a
/// usage error.</para>
/// </remarks>
public sealed class Facecheck : ICheck {
  private const double Grid = 0.25;
  private const double Out = 0.02;

  private static readonly HashSet<string> LegacyWood =
  [
    "oak",
    "generic",
    "wood",
    "charred",
  ];

  /// <inheritdoc />
  public string Id => "facecheck";

  /// <inheritdoc />
  public Profile Profile => Profile.Structural;

  private sealed record Fault(Node Node, string Face, string Reason);

  private sealed record UvCounts(int Created, int Resized, int Kept);

  /// <inheritdoc />
  public CheckResult Run(CheckInput input) {
    if (input.Shape == null)
      return CheckResult.Of(Id, 0, false, []);
    if ((input.Shipped == null) != (input.Table == null))
      throw new CheckUsageException(
        "facecheck: the shipped shape and the uv table come together"
      );
    var tree = new Tree(input.Shape);
    var lines = new List<string>();
    UvTable? table = input.Table;
    if (table != null) {
      var paths = tree.Nodes.Select(n => Tree.Bare(n.Path)).ToHashSet();
      List<FaceChange> missing =
      [
        .. table.Changes.Where(c => !paths.Contains(Tree.Bare(c.Element))),
      ];
      if (missing.Count > 0)
        throw new CheckUsageException(
          string.Join(
            "\n",
            missing.Select(c => $"changes: no element {c.Element}")
          )
        );
    }
    List<Node> els =
    [
      .. tree.Nodes.Where(e =>
        e.AllSizeOver(1e-9)
        || (e.AnySizeOver(1e-9) && e.Element.Faces.Count > 0)
      ),
    ];
    List<Node> solids = [.. els.Where(e => e.AllSizeOver(1e-6))];
    List<Fault> found =
    [
      .. els.SelectMany(e =>
        Faults(input.Shape, e).Select(f => new Fault(e, f.Face, f.Reason))
      ),
    ];
    var poses = new List<Frames> { new(input.Shape) };
    if (found.Count > 0)
      foreach (Clip clip in input.Shape.Animations) {
        string? code = clip.Code ?? clip.Name;
        if (code == null)
          continue;
        for (int f = 0; f < (clip.Quantity ?? 1); f++)
          try {
            poses.Add(new Frames(input.Shape, Poses.At(input.Shape, code, f)));
          } catch (NotSupportedException ex) {
            throw new CheckUsageException(ex.Message);
          }
      }
    List<Fault> bad =
    [
      .. found.Where(f => Visible(f.Node, f.Face, poses, solids)),
    ];
    foreach (Fault f in bad)
      lines.Add($"{f.Node.Path} {f.Face}: {f.Reason}");
    lines.Add(
      $"{els.Count} elements, {6 * els.Count} faces; {found.Count} at fault, {bad.Count} of them visible"
    );
    bool wrong = false;
    if (input.Shipped != null && table != null) {
      var shipped = new Tree(input.Shipped);
      Func<string, bool> isWood = k =>
        (input.WoodKeys ?? LegacyWood).Contains(k);
      var (faults, counts, allowed) = UvFaults(tree, shipped, table, isWood);
      wrong = faults.Count > 0;
      foreach (Fault f in faults)
        lines.Add($"{f.Node.Path} {f.Face}: {f.Reason}");
      foreach ((Fault f, FaceChange c) in allowed)
        lines.Add($"{f.Node.Path} {f.Face}: {f.Reason}; allowed: {c.Reason}");
      var used = allowed
        .Select(a => (Tree.Bare(a.Fault.Node.Path), a.Fault.Face))
        .ToHashSet();
      foreach (FaceChange c in table.Changes)
        if (!used.Contains((Tree.Bare(c.Element), c.Face)))
          lines.Add(
            $"changes: {c.Element} {c.Face} unused, the face is as shipped"
          );
      lines.Add(
        $"uv against shipped: {counts.Created} faces created, {counts.Resized} resized, "
          + $"{counts.Kept} kept; {faults.Count} at fault"
      );
    }
    return CheckResult.Of(Id, els.Count, bad.Count > 0 || wrong, lines);
  }

  private static (double W, double H) TextureSize(Shape sh, string key) {
    foreach (KeyValuePair<string, double[]> kv in sh.TextureSizes)
      if (kv.Key == key && kv.Value.Length >= 2)
        return (kv.Value[0], kv.Value[1]);
    return (sh.TextureWidth ?? 16, sh.TextureHeight ?? 16);
  }

  private static string PyRepr(string? s) => s == null ? "None" : "'" + s + "'";

  private static double[]? UvOf(Face f) =>
    f.Uv is { Length: >= 4 } uv ? uv : null;

  private static IEnumerable<(string Face, string Reason)> Faults(
    Shape sh,
    Node el
  ) {
    Vec3 size = el.Size;
    var known = sh.Textures.Select(t => t.Key).ToHashSet();
    foreach (string face in Texels.FaceNames) {
      (int ua, int va) = Texels.UvAxes(face);
      if (size[ua] < 1e-9 || size[va] < 1e-9)
        continue;
      Face? spec = el.Element.Faces.FirstOrDefault(kv => kv.Key == face).Value;
      if (spec == null) {
        yield return (face, "absent");
        continue;
      }
      if (spec.Enabled == false) {
        yield return (face, "disabled");
        continue;
      }
      string key = (spec.Texture ?? "").TrimStart('#');
      if (key.Length == 0 || key == "null" || !known.Contains(key)) {
        yield return (face, $"texture {PyRepr(spec.Texture)}");
        continue;
      }
      (double tw, double th) = TextureSize(sh, key);
      double[]? uv = UvOf(spec);
      if (uv == null)
        continue;
      if (Math.Abs(uv[2] - uv[0]) < 1e-6 || Math.Abs(uv[3] - uv[1]) < 1e-6)
        yield return (face, $"zero uv {Fmt.List(uv)}");
      else if (
        Math.Min(uv[0], uv[2]) < -1e-6
        || Math.Max(uv[0], uv[2]) > tw + 1e-6
        || Math.Min(uv[1], uv[3]) < -1e-6
        || Math.Max(uv[1], uv[3]) > th + 1e-6
      )
        yield return (
          face,
          $"uv {Fmt.List(uv)} outside {Fmt.G(tw)}x{Fmt.G(th)}"
        );
    }
  }

  private static Vec3[] FaceGrid(Node el, string face) {
    int ax = face switch {
      "north" or "south" => 2,
      "east" or "west" => 0,
      _ => 1,
    };
    double at = face is "south" or "east" or "up" ? el.Size[ax] + Out : -Out;
    var lo = new double[3];
    double[] hi = el.Size.ToArray();
    lo[ax] = hi[ax] = at;
    var axes = new double[3][];
    for (int a = 0; a < 3; a++) {
      int n = Math.Max(1, (int)Math.Ceiling((hi[a] - lo[a]) / Grid) + 1);
      axes[a] =
      [
        .. Enumerable
          .Range(0, n)
          .Select(i => n == 1 ? lo[a] : lo[a] + (hi[a] - lo[a]) * i / (n - 1)),
      ];
    }
    return
    [
      .. from x in axes[0]
      from y in axes[1]
      from z in axes[2]
      select new Vec3(x, y, z),
    ];
  }

  private static bool Visible(
    Node el,
    string face,
    List<Frames> poses,
    List<Node> solids
  ) {
    Vec3[] local = FaceGrid(el, face);
    foreach (Frames fr in poses) {
      Vec3[] w = [.. local.Select(fr.World(el).Apply)];
      var hidden = new bool[w.Length];
      int count = 0;
      foreach (Node o in solids) {
        if (ReferenceEquals(o, el))
          continue;
        Aff inv = fr.Inverse(o);
        Vec3 sz = o.Size;
        for (int i = 0; i < w.Length; i++) {
          if (hidden[i])
            continue;
          Vec3 p = inv.Apply(w[i]);
          if (
            p.X > -1e-4
            && p.X < sz.X + 1e-4
            && p.Y > -1e-4
            && p.Y < sz.Y + 1e-4
            && p.Z > -1e-4
            && p.Z < sz.Z + 1e-4
          ) {
            hidden[i] = true;
            count++;
          }
        }
        if (count == w.Length)
          break;
      }
      if (count < w.Length)
        return true;
    }
    return false;
  }

  private static HashSet<(string Path, string Face)> Unrolled(
    Tree tree,
    Frames rest,
    Func<string, bool> isWood
  ) {
    var outp = new HashSet<(string, string)>();
    foreach (RoundRun run in RoundRuns.Find(tree, rest, nested: false)) {
      double tex =
        run.Faces.Count > 0
          ? TextureSize(tree.Shape, run.Faces[0].Quad.Texture).W
          : 16.0;
      if (
        RoundRuns.VerdictDirected(run.Faces, run.Axis, tex, isWood).Passes
        || RoundRuns.Verdict(run.Faces, tex).Passes
      )
        foreach (RunFace f in run.Faces)
          outp.Add((f.Member.Path, f.Quad.Face));
    }
    return outp;
  }

  private static bool SameList(double[]? a, double[] b) =>
    a != null && a.SequenceEqual(b);

  private static bool Close(double[] a, double[] b, double atol) =>
    a.Zip(b)
      .All(p =>
        Math.Abs(p.First - p.Second) <= atol + 1e-5 * Math.Abs(p.Second)
      );

  private static (
    List<Fault> Faults,
    UvCounts Counts,
    List<(Fault Fault, FaceChange Change)> Allowed
  ) UvFaults(Tree tree, Tree shipped, UvTable table, Func<string, bool> isWood) {
    var byName = new Dictionary<string, Node>();
    foreach (Node e in shipped.Nodes)
      byName[e.Name] = e;
    var outp = new List<Fault>();
    var allowed = new List<(Fault, FaceChange)>();
    int created = 0,
      resized = 0,
      kept = 0;
    var allow = new Dictionary<(string, string), FaceChange>();
    foreach (FaceChange c in table.Changes)
      allow[(Tree.Bare(c.Element), c.Face)] = c;
    var rest = new Frames(tree.Shape);
    HashSet<(string Path, string Face)> rolled = Unrolled(tree, rest, isWood);

    bool Cornered(string key, double[] uv) {
      (double tw, double th) = TextureSize(tree.Shape, key);
      return uv[0] == 0
        && uv[1] == 0
        && (
          Math.Abs(uv[2] - uv[0]) < tw - 1e-6
          || Math.Abs(uv[3] - uv[1]) < th - 1e-6
        );
    }

    (double, double) Dims(double[] uv) =>
      (Math.Abs(uv[2] - uv[0]), Math.Abs(uv[3] - uv[1]));

    string? Placed(
      Node el,
      string face,
      string key,
      double[] uv,
      double rot,
      string kind
    ) {
      Aff m = rest.World(el);
      if (!m.L.IsIdentity(1e-6, 1e-5))
        return $"{kind}, position texture '{key}' on an element turned at rest";
      Vec3 lo = m.T;
      double[] want;
      try {
        want = Texels.PositionUv(face, lo, lo + el.Size);
      } catch (ArgumentException e) {
        return $"{kind}, {e.Message}";
      }
      if (rot != 0 || !Close(uv, want, 1e-4))
        return $"{kind}, uv {Fmt.List(uv)} rotation {Fmt.G(rot)} off '{key}'s position uv {Fmt.List(want)} rotation 0";
      return null;
    }

    foreach (Node el in tree.Nodes) {
      Node? old =
        shipped.Nodes.FirstOrDefault(s => s.Path == el.Path)
        ?? byName.GetValueOrDefault(el.Name);
      foreach ((string face, Face spec) in el.Element.Faces) {
        double[]? uv = UvOf(spec);
        if (uv == null)
          continue;
        string key = (spec.Texture ?? "").TrimStart('#');
        table.Rows.TryGetValue(key, out UvRow? mode);
        Face? was = old
          ?.Element.Faces.FirstOrDefault(kv => kv.Key == face)
          .Value;
        double rot = spec.Rotation ?? 0;
        if (old == null) {
          created++;
          if (mode == null)
            outp.Add(
              new Fault(el, face, $"texture '{key}' not in the UV table")
            );
          else if (mode.Mode == "random" && Cornered(key, uv))
            outp.Add(
              new Fault(
                el,
                face,
                $"created, origin 0,0 on random texture '{key}'"
              )
            );
          else if (mode.Mode == "position") {
            if (Placed(el, face, key, uv, rot, "created") is { } why)
              outp.Add(new Fault(el, face, why));
          } else if (
              mode.Mode == "patterned"
              && !rolled.Contains((el.Path, face))
              && (
                uv[0] != mode.U
                || uv[1] != mode.V
                || !mode.Rotations!.Contains(rot)
              )
            )
            outp.Add(
              new Fault(
                el,
                face,
                $"created, uv {Fmt.List(uv)} rotation {Fmt.G(rot)} off '{key}'s aligned "
                  + $"{Fmt.G(mode.U)},{Fmt.G(mode.V)} rotation {string.Join("/", mode.Rotations!.Select(Fmt.G))}"
              )
            );
        } else if (
            was == null
            || Dims(was.Uv is { Length: >= 4 } wuv ? wuv : [0, 0, 0, 0])
              != Dims(uv)
          ) {
          resized++;
          if (mode == null)
            outp.Add(
              new Fault(el, face, $"texture '{key}' not in the UV table")
            );
          else if (mode.Mode == "random" && Cornered(key, uv))
            outp.Add(
              new Fault(
                el,
                face,
                $"resized, origin 0,0 on random texture '{key}'"
              )
            );
          else if (mode.Mode == "position") {
            if (Placed(el, face, key, uv, rot, "resized") is { } why)
              outp.Add(new Fault(el, face, why));
          }
        } else {
          kept++;
          if (mode is { Mode: "position" }) {
            if (Placed(el, face, key, uv, rot, "kept") is { } why)
              outp.Add(new Fault(el, face, why));
          } else if (key != (was.Texture ?? "").TrimStart('#')) {
            continue;
          } else if (
              !rolled.Contains((el.Path, face))
              && (!SameList(was.Uv, uv) || (was.Rotation ?? 0) != rot)
            ) {
            string why =
              $"shipped face changed: uv {Fmt.List(was.Uv ?? [])} rotation {Fmt.G(was.Rotation ?? 0)} now "
              + $"{Fmt.List(uv)} rotation {Fmt.G(rot)}";
            var fault = new Fault(el, face, why);
            if (
              allow.TryGetValue(
                (Tree.Bare(el.Path), face),
                out FaceChange? entry
              )
            )
              allowed.Add((fault, entry));
            else
              outp.Add(fault);
          }
        }
      }
    }
    return (outp, new UvCounts(created, resized, kept), allowed);
  }
}
