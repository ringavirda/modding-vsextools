using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>An element with its path, parent and derived box, as the checks read it.</summary>
internal sealed class Node {
  public required string Path { get; init; }
  public required string Name { get; init; }
  public required Element Element { get; init; }
  public Node? Parent { get; init; }
  public List<Node> Children { get; } = [];
  public Vec3 Frm { get; init; }
  public Vec3 To { get; init; }
  public Vec3 Origin { get; init; }
  public Vec3 Rot { get; init; }

  /// <summary>The enabled faces, in file order.</summary>
  public List<KeyValuePair<string, Face>> Faces { get; init; } = [];

  public Vec3 Size => To - Frm;

  public bool AllSizeOver(double min) =>
    Size.X > min && Size.Y > min && Size.Z > min;

  public bool AnySizeOver(double min) =>
    Size.X > min || Size.Y > min || Size.Z > min;

  /// <summary>Draws faces: has enabled faces and a non-zero size.</summary>
  public bool IsLeaf => Faces.Count > 0 && AnySizeOver(1e-9);

  public bool IsRotated =>
    Math.Abs(Rot.X) > 1e-9 || Math.Abs(Rot.Y) > 1e-9 || Math.Abs(Rot.Z) > 1e-9;

  public bool Solid => AllSizeOver(1e-6);
}

/// <summary>A shape's elements as <see cref="Node"/>s, in pre-order.</summary>
internal sealed class Tree {
  public Shape Shape { get; }
  public List<Node> Nodes { get; } = [];
  public List<Node> Roots { get; } = [];
  private readonly Dictionary<string, Node> _byPath = [];

  public Tree(Shape shape) {
    Shape = shape;
    foreach (PathedElement pe in Engine.Walk(shape)) {
      Element el = pe.Element;
      Vec3 frm = Vec3.From(el.From, Vec3.Zero);
      Node? parent = pe.ParentPath == null ? null : _byPath[pe.ParentPath];
      var node = new Node {
        Path = pe.Path,
        Name = el.Name ?? "?",
        Element = el,
        Parent = parent,
        Frm = frm,
        To = Vec3.From(el.To, Vec3.Zero),
        Origin = Vec3.From(el.RotationOrigin, Vec3.Zero),
        Rot = new Vec3(el.RotationX ?? 0, el.RotationY ?? 0, el.RotationZ ?? 0),
        Faces = [.. el.Faces.Where(kv => kv.Value.Enabled != false)],
      };
      _byPath[pe.Path] = node;
      Nodes.Add(node);
      if (parent == null)
        Roots.Add(node);
      else
        parent.Children.Add(node);
    }
  }

  public Node this[string path] => _byPath[path];

  public IEnumerable<Node> Leaves => Nodes.Where(n => n.IsLeaf);

  /// <summary>The element at <paramref name="nameOrPath"/>, else the first of that name; null when none.</summary>
  public Node? Find(string nameOrPath) =>
    Nodes.FirstOrDefault(n => n.Path == nameOrPath)
    ?? Nodes.FirstOrDefault(n => n.Name == nameOrPath);

  /// <summary>The path without a leading <c>Root/</c>.</summary>
  public static string Bare(string path) =>
    path.StartsWith("Root/", StringComparison.Ordinal) ? path[5..] : path;

  /// <summary>The element and every element under it.</summary>
  public static IEnumerable<Node> Subtree(Node n) {
    var stack = new Stack<Node>();
    stack.Push(n);
    while (stack.Count > 0) {
      Node e = stack.Pop();
      yield return e;
      foreach (Node c in e.Children)
        stack.Push(c);
    }
  }
}

/// <summary>World matrices of one pose of a shape, with their affine forms and inverses cached.</summary>
internal sealed class Frames {
  private readonly Dictionary<string, Mat4d> _mats;
  private readonly Dictionary<string, Aff> _aff = [];
  private readonly Dictionary<string, Aff> _inv = [];

  public Frames(Shape shape, IReadOnlyDictionary<string, Pose>? poses = null) =>
    _mats = Engine.WorldMatrices(shape, poses);

  public Mat4d Mat(Node n) => _mats[n.Path];

  public Aff World(Node n) {
    if (!_aff.TryGetValue(n.Path, out Aff? a))
      _aff[n.Path] = a = new Aff(_mats[n.Path]);
    return a;
  }

  public Aff Inverse(Node n) {
    if (!_inv.TryGetValue(n.Path, out Aff? a))
      _inv[n.Path] = a = World(n).Inverse();
    return a;
  }

  public Vec3 Centre(Node n) => World(n).Apply(n.Size * 0.5);
}
