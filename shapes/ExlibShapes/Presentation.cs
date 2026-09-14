using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ExpandedLib.Shapes;

/// <summary>
/// Which way an oriented family stands in the pictures a page shows.
/// <para>
/// A large machine is placed facing away from the player, so its front - the side the player
/// stands at, the boiler's firebox, the engine's cylinder end, the furnace's door - is the side
/// opposite the variant's facing. A picture shows that front, so a drawing takes the facing whose
/// front points most nearly at the camera of <see cref="ViewName"/>.
/// </para>
/// <para>
/// A layout says the same thing without a facing: the player stands at the starter block, so the
/// anchor cell lies on the machine's front and the body behind it. <see cref="Stage"/> turns a
/// layout onto that reading, which is the only one an old structure carries at all.
/// </para>
/// </summary>
public static class Presentation {
  /// <summary>The <see cref="Renderer.NamedViews"/> entry a page's main picture is drawn from, and
  /// the camera <see cref="Facing"/> is chosen for.</summary>
  public const string ViewName = "iso";

  // The four horizontal sides with their world normals (x east, z south), north first: the
  // isometric camera stands between two of them, and taking the earlier keeps north up.
  private static readonly (string Side, int X, int Z)[] Horizontals = [
    ("north", 0, -1), ("east", 1, 0), ("south", 0, 1), ("west", -1, 0),
  ];

  private static readonly Dictionary<string, string> Opposite = new(StringComparer.Ordinal) {
    ["north"] = "south", ["n"] = "south",
    ["east"] = "west", ["e"] = "west",
    ["south"] = "north", ["s"] = "north",
    ["west"] = "east", ["w"] = "east",
  };

  /// <summary>The side an oriented family is drawn facing by default, as a side word.</summary>
  public static string Facing => FacingFor(Renderer.NamedViews[ViewName]);

  /// <summary>
  /// The side an oriented family is drawn facing so that its front meets <paramref name="view"/>'s
  /// camera: the facing leaning furthest from the camera, the front being opposite it. Ties - the
  /// isometric camera stands square between two facings - go to the earlier of north, east, south,
  /// west, which keeps north up in the plans.
  /// </summary>
  public static string FacingFor(View view) {
    Vector3 eye = Renderer.Eye(view);
    double Lean((string Side, int X, int Z) side) => side.X * eye.X + side.Z * eye.Z;
    double least = Horizontals.Min(Lean);
    return Horizontals.First(side => Lean(side) <= least + 1e-6).Side;
  }

  /// <summary>
  /// The side the front of a machine facing <paramref name="side"/> looks toward: the opposite one.
  /// </summary>
  /// <param name="side">A side word or its single letter, in either case; null is accepted.</param>
  /// <returns>The side as a word, or null when <paramref name="side"/> names no horizontal side (a
  /// flywheel's <c>ns</c>/<c>we</c> axis names an axis, not a facing).</returns>
  public static string? Front(string? side) =>
    side != null && Opposite.TryGetValue(side, out string? front) ? front : null;

  /// <summary>The side <paramref name="variant"/>'s front looks toward (<see cref="Front"/> of the
  /// facing its <c>side</c> or <c>orientation</c> axis names), or null when its code names no
  /// horizontal facing.</summary>
  public static string? FrontOf(Variant variant) {
    foreach (string axis in new[] { "side", "orientation" })
      if (variant.States.TryGetValue(axis, out string? state) && Front(state) is { } front)
        return front;
    return null;
  }

  /// <summary>A drawing's layout: the cells turned so the machine's front meets the camera, the
  /// quarter turn that took (which the drawn meshes turn by too, the machine being built that way
  /// round rather than seen from elsewhere), and the world side the front then looks toward.</summary>
  /// <param name="Layout">The turned layout, the frame every picture and plan is drawn in.</param>
  /// <param name="Angle">0, 90, 180 or 270 degrees.</param>
  /// <param name="Front">A side word, or null when nothing in the files names a front.</param>
  public readonly record struct Staged(Layout Layout, int Angle, string? Front);

  // The two sides the camera of ViewName looks at, in the order a tie between them is settled:
  // south first, so the deeper axis of a plan stays vertical and north stays up more often.
  private static readonly string[] CameraSides = ["south", "east"];

  private static readonly int[] Quarters = [0, 90, 180, 270];

  /// <summary>
  /// <paramref name="placed"/> turned so its anchor cell lies on the edge of its footprint the
  /// camera looks at - the player stands at the starter block, so that edge is the machine's front.
  /// <para>
  /// The turn is taken after the frame fit (<see cref="Footprint.Placed"/>), whose cells are the
  /// ones a picture draws. An anchor walled in on both axes names no front: the layout is left as
  /// it stands and <paramref name="variant"/>'s own facing answers instead.
  /// </para>
  /// </summary>
  /// <param name="placed">A layout already turned into its principal's drawn frame.</param>
  /// <param name="variant">The variant being drawn, for the fallback; null accepts none.</param>
  public static Staged Stage(Layout placed, Variant? variant = null) {
    (int angle, string? side) = AnchorTurn(placed);
    if (side == null)
      return new Staged(placed, 0, variant == null ? null : FrontOf(variant));
    return new Staged(angle == 0 ? placed : placed.Rotated(angle), angle, side);
  }

  /// <summary>
  /// The quarter turn that puts <paramref name="layout"/>'s anchor cell on the camera-facing edge
  /// of its footprint's bounding box (every cell and filler), with the side word that edge names:
  /// the south edge before the east one, and, among the turns that reach the same edge, the one
  /// standing the anchor furthest in front of the footprint's centre, then the least turn. (0,
  /// null) when the anchor is interior on both axes at every turn.
  /// </summary>
  public static (int Angle, string? Side) AnchorTurn(Layout layout) {
    IReadOnlyList<Offset> reserved = Footprint.Reserved(layout);
    foreach (string side in CameraSides) {
      int[] reaching = [.. Quarters.Where(angle => OnEdge(reserved, layout.Anchor, angle, side))];
      if (reaching.Length > 0)
        return (reaching.OrderByDescending(angle => TowardCamera(reserved, layout.Anchor, angle)).First(), side);
    }
    return (0, null);
  }

  // Whether the anchor lies on `side`'s edge of the reserved cells once turned by `angle`: their
  // greatest Z for south, their greatest X for east.
  private static bool OnEdge(IReadOnlyList<Offset> reserved, Offset anchor, int angle, string side) {
    Offset turned = Layout.RotateOffset(anchor, angle);
    IEnumerable<Offset> cells = reserved.Select(c => Layout.RotateOffset(c, angle));
    return side == "south" ? turned.Z == cells.Max(c => c.Z) : turned.X == cells.Max(c => c.X);
  }

  /// <summary>
  /// The quarter turn that brings the most of <paramref name="shape"/>'s detail to the camera, and
  /// the side that detail then looks toward. A family whose facing lives in C# has no variant to
  /// choose between, so the art itself has to say which side is the front: detail is every face
  /// painted with a texture other than the model's most-used one - the furnace door's iron and
  /// straps against its brick - weighed by the area the camera sees of it. (0, null) for a model
  /// painted one texture throughout, which says nothing about a front.
  /// </summary>
  public static (int Angle, string? Front) DetailTurn(LoadedShape shape) {
    List<(Vector3 Normal, double Area, string Texture)> faces = Faces(shape);
    if (faces.Count == 0)
      return (0, null);
    var area = new Dictionary<string, double>(StringComparer.Ordinal);
    foreach ((Vector3 _, double a, string texture) in faces)
      area[texture] = area.GetValueOrDefault(texture) + a;
    string body = area.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
    List<(Vector3 Normal, double Area, string Texture)> detail = [.. faces.Where(f => f.Texture != body)];
    if (detail.Count == 0)
      return (0, null);

    double best = Quarters.Max(angle => Seen(detail, angle));
    if (best <= 0)
      return (0, null);
    // Two turns showing the same detail to within a hundredth are the same picture of it; the
    // least one keeps the model nearest the frame its art was drawn in.
    int chosen = Quarters.First(angle => Seen(detail, angle) >= best * 0.99);
    return (chosen, DetailSide(detail, chosen));
  }

  // Every drawn face of a shape as its world normal, its area and the texture key painting it.
  private static List<(Vector3 Normal, double Area, string Texture)> Faces(LoadedShape shape) {
    Dictionary<string, Renderer.Mat4d> mats = Renderer.WorldMatricesD(shape, null);
    var faces = new List<(Vector3, double, string)>();
    foreach (Node leaf in shape.Leaves())
      foreach (Renderer.QuadD quad in Renderer.FaceQuadsD(leaf, mats[leaf.Path])) {
        Vector3[] points = [.. quad.Points.Select(p => new Vector3((float)p.X, (float)p.Y, (float)p.Z))];
        double a = Vector3.Cross(points[2] - points[0], points[3] - points[1]).Length() / 2;
        faces.Add((new Vector3((float)quad.Normal.X, (float)quad.Normal.Y, (float)quad.Normal.Z), a, quad.Texture));
      }
    return faces;
  }

  // The detail area the camera sees once the model is turned by `angle`: each face's own area times
  // how squarely it meets the camera, a face turned away counting nothing.
  private static double Seen(IReadOnlyList<(Vector3 Normal, double Area, string Texture)> detail, int angle) {
    Vector3 eye = Renderer.Eye(Renderer.NamedViews[ViewName]);
    double total = 0;
    foreach ((Vector3 normal, double area, string _) in detail) {
      Vector3 turned = TurnY(normal, angle);
      total += area * Math.Max(0, Vector3.Dot(turned, eye));
    }
    return total;
  }

  // The horizontal side the detail the camera sees at `angle` mostly looks toward, or null when
  // none of it faces sideways at all.
  private static string? DetailSide(IReadOnlyList<(Vector3 Normal, double Area, string Texture)> detail, int angle) {
    Vector3 eye = Renderer.Eye(Renderer.NamedViews[ViewName]);
    var seen = new Dictionary<string, double>(StringComparer.Ordinal);
    foreach ((Vector3 normal, double area, string _) in detail) {
      Vector3 turned = TurnY(normal, angle);
      double lit = area * Vector3.Dot(turned, eye);
      if (lit <= 0)
        continue;
      if (Math.Abs(turned.X) < 1e-6 && Math.Abs(turned.Z) < 1e-6)
        continue;
      string side = Math.Abs(turned.X) >= Math.Abs(turned.Z)
        ? turned.X > 0 ? "east" : "west"
        : turned.Z > 0 ? "south" : "north";
      seen[side] = seen.GetValueOrDefault(side) + lit;
    }
    return seen.Count == 0
      ? null
      : seen.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
  }

  // A direction turned about the y axis by a quarter turn, the same mapping a cell offset takes.
  private static Vector3 TurnY(Vector3 v, int angle) =>
    (((angle % 360) + 360) % 360) switch {
      90 => new Vector3(v.Z, v.Y, -v.X),
      180 => new Vector3(-v.X, v.Y, -v.Z),
      270 => new Vector3(-v.Z, v.Y, v.X),
      _ => v,
    };

  // How far the turned anchor stands in front of the turned footprint's centre, along the camera's
  // own direction: a one-cell-deep footprint turned side-on puts its body beside the anchor and
  // scores zero, while the turn that puts the body behind it scores the depth.
  private static double TowardCamera(IReadOnlyList<Offset> reserved, Offset anchor, int angle) {
    List<Offset> cells = [.. reserved.Select(c => Layout.RotateOffset(c, angle))];
    Offset turned = Layout.RotateOffset(anchor, angle);
    Vector3 eye = Renderer.Eye(Renderer.NamedViews[ViewName]);
    double cx = (cells.Min(c => c.X) + cells.Max(c => c.X)) / 2.0;
    double cz = (cells.Min(c => c.Z) + cells.Max(c => c.Z)) / 2.0;
    return (turned.X - cx) * eye.X + (turned.Z - cz) * eye.Z;
  }
}
