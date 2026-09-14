using System.Numerics;

namespace ExpandedLib.Shapes;

/// <summary>
/// A double-precision 3-vector, carrying a shape JSON's own coordinates without
/// <see cref="System.Numerics.Vector3"/>'s float32 truncation. Model Creator shapes routinely
/// place two elements at an exactly shared face; float32 diverges from the JSON's double literals
/// by enough, after a chain of composed local matrices, to flip which of two coincident faces the
/// renderer's strict z-test picks. <see cref="Node"/> stores its own coordinates this way for
/// exactly that reason; <see cref="Geometry"/>'s public API stays float32 as specified for every
/// other consumer, converting explicitly where it needs one of these.
/// </summary>
public readonly struct Vec3d {
  /// <summary>The x component.</summary>
  public double X { get; }

  /// <summary>The y component.</summary>
  public double Y { get; }

  /// <summary>The z component.</summary>
  public double Z { get; }

  /// <summary>Builds a vector from its three components.</summary>
  public Vec3d(double x, double y, double z) {
    X = x;
    Y = y;
    Z = z;
  }

  /// <summary>The zero vector.</summary>
  public static Vec3d Zero => new(0, 0, 0);

  /// <summary>Component-wise subtraction.</summary>
  public static Vec3d operator -(Vec3d a, Vec3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

  /// <summary>Component-wise addition.</summary>
  public static Vec3d operator +(Vec3d a, Vec3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

  /// <summary>Narrows to <see cref="System.Numerics.Vector3"/> for Geometry's float32 API.</summary>
  public static explicit operator Vector3(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
}
