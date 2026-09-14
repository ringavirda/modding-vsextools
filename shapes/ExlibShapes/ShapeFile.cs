using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;
using Vintagestory.API.Common;

namespace ExpandedLib.Shapes;

/// <summary>
/// One element of a shape's tree, mirroring the game's <see cref="ShapeElement"/> but with its
/// <c>from</c>/<c>to</c>/<c>rotationOrigin</c> already as <see cref="Vector3"/> and its faces
/// filtered to the enabled ones - the JSON's own coordinates, still relative to the parent's
/// <c>from</c>; <see cref="Geometry"/> is what turns a tree of these into world space.
/// </summary>
public sealed class Node {
  /// <summary>The element's own <c>name</c> ("?" when the JSON omits it, matching the game).</summary>
  public string Name { get; }

  /// <summary>Every ancestor's name joined by <c>/</c>, then this element's own name - the same
  /// address <see cref="LoadedShape.Find"/> and a quad's <see cref="Quad.Path"/> use.</summary>
  public string Path { get; }

  /// <summary>The element's near corner, in its parent's local space.</summary>
  public Vector3 From { get; }

  /// <summary>The element's far corner, in its parent's local space.</summary>
  public Vector3 To { get; }

  /// <summary>The pivot faces rotate about; defaults to <see cref="From"/> when the JSON omits
  /// <c>rotationOrigin</c>.</summary>
  public Vector3 Origin { get; }

  /// <summary>Degrees about x, y, z - <c>rotationX</c>/<c>rotationY</c>/<c>rotationZ</c>.</summary>
  public Vector3 Rotation { get; }

  /// <summary>The element's faces with <c>enabled</c> false dropped - Model Creator writes a
  /// disabled face rather than omitting the key, so a caller that only wants what actually draws
  /// never has to check <see cref="ShapeElementFace.Enabled"/> itself.</summary>
  public IReadOnlyDictionary<string, ShapeElementFace> Faces { get; }

  /// <summary>This element's children, still in tree order.</summary>
  public IReadOnlyList<Node> Children => ChildrenList;

  /// <summary>Null for a root element.</summary>
  public Node? Parent { get; }

  internal Node(
    string name,
    Vector3 from,
    Vector3 to,
    Vector3 origin,
    Vector3 rotation,
    IReadOnlyDictionary<string, ShapeElementFace> faces,
    string path,
    Node? parent
  ) {
    Name = name;
    From = from;
    To = to;
    Origin = origin;
    Rotation = rotation;
    Faces = faces;
    Path = path;
    Parent = parent;
  }

  // Filled after construction, since a child needs its parent's Path/instance to exist first.
  internal List<Node> ChildrenList { get; } = [];

  /// <summary>The element's extent along x, y, z (<see cref="To"/> minus <see cref="From"/>).</summary>
  public Vector3 Size => To - From;

  /// <summary>Whether this element draws: has at least one enabled face and a non-zero size
  /// (Model Creator writes faces on zero-size groups too, which never render).</summary>
  public bool IsLeaf =>
    Faces.Count > 0 && (Size.X > 1e-9f || Size.Y > 1e-9f || Size.Z > 1e-9f);

  /// <summary>Whether any rotation axis is non-zero.</summary>
  public bool IsRotated =>
    MathF.Abs(Rotation.X) > 1e-9f
    || MathF.Abs(Rotation.Y) > 1e-9f
    || MathF.Abs(Rotation.Z) > 1e-9f;
}

/// <summary>
/// A shape file loaded through the game's own <see cref="Shape"/> class, with its element tree
/// wrapped as <see cref="Node"/> for the geometry and the renderer to walk. Deserialised straight
/// off disk with <see cref="Newtonsoft.Json"/> rather than through <c>Shape.ResolveReferences</c>:
/// this tool never needs step-parenting or reference elements resolved against a running game, and
/// a shape with those still unresolved is exactly what Model Creator itself saves.
/// </summary>
public sealed class LoadedShape {
  /// <summary>The file this shape was loaded from, or null for one built in memory.</summary>
  public string? Path { get; }

  /// <summary>The shape's own <c>textures</c> map, key to the asset it names (a <c>domain:path</c>
  /// or, for the owner's editables, a raw filesystem path under a <c>#</c>-less key).</summary>
  public IReadOnlyDictionary<string, AssetLocation> Textures { get; }

  /// <summary>The shape's root elements, in file order.</summary>
  public IReadOnlyList<Node> Elements { get; }

  /// <summary>The shape's <c>animations</c>, unresolved (as <see cref="Poses"/> reads them).</summary>
  public IReadOnlyList<Animation> Animations { get; }

  internal LoadedShape(
    string? path,
    IReadOnlyDictionary<string, AssetLocation> textures,
    IReadOnlyList<Node> elements,
    IReadOnlyList<Animation> animations
  ) {
    Path = path;
    Textures = textures;
    Elements = elements;
    Animations = animations;
  }

  /// <summary>Every element in depth-first, parent-before-child order.</summary>
  public IEnumerable<Node> Walk() {
    foreach (Node root in Elements)
      foreach (Node el in WalkFrom(root))
        yield return el;
  }

  private static IEnumerable<Node> WalkFrom(Node el) {
    yield return el;
    foreach (Node c in el.Children)
      foreach (Node d in WalkFrom(c))
        yield return d;
  }

  /// <summary>Every leaf element (<see cref="Node.IsLeaf"/>), in tree order.</summary>
  public IReadOnlyList<Node> Leaves() => [.. Walk().Where(el => el.IsLeaf)];

  /// <summary>The element whose <see cref="Node.Path"/> equals <paramref name="nameOrPath"/>, else
  /// the first whose <see cref="Node.Name"/> does, else null - a path is checked first since a
  /// name can repeat across siblings' subtrees while a path cannot.</summary>
  public Node? Find(string nameOrPath) {
    foreach (Node el in Walk())
      if (el.Path == nameOrPath)
        return el;
    foreach (Node el in Walk())
      if (el.Name == nameOrPath)
        return el;
    return null;
  }
}

/// <summary>Loads a shape JSON file into a <see cref="LoadedShape"/>.</summary>
public static class ShapeFile {
  /// <summary>
  /// Reads <paramref name="path"/> through the game's <see cref="Shape"/> deserialisation and
  /// wraps its element tree as <see cref="Node"/>s.
  /// </summary>
  /// <exception cref="FileNotFoundException"><paramref name="path"/> does not exist.</exception>
  /// <exception cref="JsonException">The file is not valid shape JSON.</exception>
  public static LoadedShape Load(string path) {
    if (!File.Exists(path))
      throw new FileNotFoundException($"No such shape file: {path}", path);
    string text = File.ReadAllText(path);
    Shape raw =
      JsonConvert.DeserializeObject<Shape>(text)
      ?? throw new JsonException($"{path}: not a shape (empty document)");
    return FromRaw(raw, path);
  }

  /// <summary>Wraps an already-parsed <see cref="Shape"/> (the synthetic shapes a test builds
  /// in memory, say), attributing it to <paramref name="path"/> for messages only.</summary>
  public static LoadedShape FromRaw(Shape raw, string? path) {
    List<Node> roots = [];
    foreach (ShapeElement el in raw.Elements ?? [])
      roots.Add(Build(el, null, ""));
    return new LoadedShape(
      path,
      raw.Textures ?? new Dictionary<string, AssetLocation>(),
      roots,
      raw.Animations ?? []
    );
  }

  private static Node Build(ShapeElement raw, Node? parent, string prefix) {
    string name = raw.Name ?? "?";
    string path = (prefix.Length > 0 ? prefix + "/" : "") + name;
    Vector3 from = ToVector3(raw.From, Vector3.Zero);
    Vector3 to = ToVector3(raw.To, Vector3.Zero);
    Vector3 origin = raw.RotationOrigin != null ? ToVector3(raw.RotationOrigin, from) : from;
    Vector3 rotation = new((float)raw.RotationX, (float)raw.RotationY, (float)raw.RotationZ);
    // ShapeElement.Faces (a face-name-keyed dictionary) is obsolete and left null once the
    // deserialiser's own [OnDeserialized] hook runs: FacesResolved is a fixed 6-slot array in
    // Geometry.Faces order (north, east, south, west, up, down), a disabled or absent face left
    // null, its texture already stripped of its leading '#'.
    Dictionary<string, ShapeElementFace> faces = [];
    if (raw.FacesResolved != null)
      for (int i = 0; i < raw.FacesResolved.Length && i < Geometry.Faces.Length; i++)
        if (raw.FacesResolved[i] is { } face)
          faces[Geometry.Faces[i]] = face;

    var node = new Node(name, from, to, origin, rotation, faces, path, parent);
    if (raw.Children != null)
      foreach (ShapeElement child in raw.Children)
        node.ChildrenList.Add(Build(child, node, path));
    return node;
  }

  private static Vector3 ToVector3(double[]? v, Vector3 fallback) =>
    v is { Length: >= 3 } ? new Vector3((float)v[0], (float)v[1], (float)v[2]) : fallback;
}
