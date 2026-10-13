using System;
using System.Collections.Generic;
using ExpandedLib.Assets;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes;

/// <summary>One of an item's <c>guiTransform</c>, <c>tpHandTransform</c> or
/// <c>groundTransform</c> as the game resolves it: translation and origin in blocks (the gui
/// translation in screen pixels), rotation in degrees, scale per axis.</summary>
internal readonly record struct ItemTransform(
  (double X, double Y, double Z) Translation,
  (double X, double Y, double Z) Rotation,
  (double X, double Y, double Z) Origin,
  (double X, double Y, double Z) ScaleXyz,
  bool Rotate
);

public static partial class ItemViews {
  internal const string GuiKey = "guiTransform";
  internal const string TpKey = "tpHandTransform";
  internal const string GroundKey = "groundTransform";

  // ModelTransform.ItemDefaultGui, ItemDefaultTp and ItemDefaultGround.
  private static readonly ItemTransform GuiDefault = new(
    (0, 0, 0),
    (0, 0, 0),
    (0.5, 0.5, 0.5),
    (1, 1, 1),
    false
  );
  private static readonly ItemTransform TpDefault = new(
    (-1.5, -1.6, -1.4),
    (0, -62, 18),
    (0.5, 0.5, 0.5),
    (0.33, 0.33, 0.33),
    true
  );
  private static readonly ItemTransform GroundDefault = new(
    (0, 0, 0),
    (90, 0, 0),
    (0.5, 0.5, 0.53),
    (1.5, 1.5, 1.5),
    true
  );

  /// <summary>
  /// The transform <paramref name="key"/> (<see cref="GuiKey"/>, <see cref="TpKey"/> or
  /// <see cref="GroundKey"/>) gives <paramref name="variant"/>: the entry its
  /// <c>&lt;key&gt;ByType</c> picks for the variant, laid over the plain key's object, laid over
  /// the game's default for an item. A key the itemtype omits keeps the default's value; a vector
  /// it authors replaces the default whole, its omitted components 0.
  /// </summary>
  internal static ItemTransform ReadTransform(Variant variant, string key) {
    ItemTransform fallback = key switch {
      GuiKey => GuiDefault,
      TpKey => TpDefault,
      GroundKey => GroundDefault,
      _ => throw new ArgumentException(
        $"no such transform: {key}",
        nameof(key)
      ),
    };
    JObject? plain = BlockTypeResolution.GetCi(variant.Raw, key) as JObject;
    JToken? picked = BlockTypeResolution.ByType(variant.Raw, key, variant.Path);
    JObject? entry = picked as JObject;
    if (plain != null && entry != null && !ReferenceEquals(plain, entry)) {
      entry = (JObject)plain.DeepClone();
      entry.Merge(picked);
    }
    if (entry == null)
      return fallback;
    return new ItemTransform(
      Vector(entry, "translation", fallback.Translation),
      Vector(entry, "rotation", fallback.Rotation),
      Vector(entry, "origin", fallback.Origin),
      Scale(entry, fallback.ScaleXyz),
      BlockTypeResolution.GetCi(entry, "rotate")
        is { Type: JTokenType.Boolean } r
        ? (bool)r
        : fallback.Rotate
    );
  }

  private static double Number(JObject obj, string key, double fallback) =>
    BlockTypeResolution.GetCi(obj, key)
      is { Type: JTokenType.Integer or JTokenType.Float } n
      ? (double)n
      : fallback;

  // The game fills a vector from the JSON object whole: a component the object omits is 0.
  private static (double X, double Y, double Z) Vector(
    JObject obj,
    string key,
    (double X, double Y, double Z) fallback
  ) =>
    BlockTypeResolution.GetCi(obj, key) is JObject v
      ? (Number(v, "x", 0), Number(v, "y", 0), Number(v, "z", 0))
      : fallback;

  // `scale` sets all three axes and `scaleXyz` sets each; the one later in the object wins.
  private static (double X, double Y, double Z) Scale(
    JObject obj,
    (double X, double Y, double Z) fallback
  ) {
    var scale = fallback;
    foreach (JProperty p in obj.Properties()) {
      if (
        string.Equals(p.Name, "scale", StringComparison.OrdinalIgnoreCase)
        && p.Value is { Type: JTokenType.Integer or JTokenType.Float }
      ) {
        double n = (double)p.Value;
        scale = (n, n, n);
      } else if (
          string.Equals(p.Name, "scaleXyz", StringComparison.OrdinalIgnoreCase)
          && p.Value is JObject v
        )
        scale = (Number(v, "x", 0), Number(v, "y", 0), Number(v, "z", 0));
    }
    return scale;
  }

  /// <summary>
  /// The matrix the inventory slot applies to an item mesh, from the shape's voxels to the gui
  /// view's units: screen pixels from the slot centre, x right, y up, z toward the viewer. The
  /// game's slot is Y down with no flip of the mesh, so the last factor turns its y up for the
  /// renderer. An item sits 3 px left and 1 px above the slot centre; a block sits on it and
  /// turns a further 180 degrees about x. The translation is in pixels, the mesh scales to
  /// 25.6 px per block, and rotation turns x, then y, then z in call order (a vertex takes z
  /// first).
  /// </summary>
  /// <param name="t">The collectible's resolved gui transform.</param>
  /// <param name="block">True when the collectible is a block, false for an item.</param>
  internal static Renderer.Mat4d SlotModel(ItemTransform t, bool block = false) {
    const double size = 25.6;
    (double ox, double oy, double oz) = t.Origin;
    return Renderer.Mat4d.Scale(1, -1, 1)
      * (block ? Renderer.Mat4d.Identity : Renderer.Mat4d.Translate(-3, -1, 0))
      * Renderer.Mat4d.Translate(
        ox + t.Translation.X,
        oy + t.Translation.Y,
        oz * size + t.Translation.Z
      )
      * Renderer.Mat4d.Scale(
        size * t.ScaleXyz.X,
        size * t.ScaleXyz.Y,
        size * t.ScaleXyz.Z
      )
      * Renderer.Mat4d.RotateByXyz(
        t.Rotation.X + (block ? 180 : 0),
        t.Rotation.Y,
        t.Rotation.Z
      )
      * Renderer.Mat4d.Translate(-ox, -oy, -oz)
      * Renderer.Mat4d.Scale(1.0 / 16, 1.0 / 16, 1.0 / 16);
  }

  /// <summary>
  /// The matrix a dropped item is drawn with, from the shape's voxels to world voxels with the
  /// item's resting point at the origin and no yaw: translation (not scaled), then 0.2 block per
  /// mesh unit per unit of scale, then rotation about y, z and x in call order (a vertex takes x
  /// first, then z, then y).
  /// </summary>
  internal static Renderer.Mat4d GroundModel(ItemTransform t) =>
    Renderer.Mat4d.Scale(16, 16, 16)
    * Renderer.Mat4d.Translate(
      t.Translation.X,
      t.Translation.Y,
      t.Translation.Z
    )
    * Renderer.Mat4d.Scale(
      0.2 * t.ScaleXyz.X,
      0.2 * t.ScaleXyz.Y,
      0.2 * t.ScaleXyz.Z
    )
    * Renderer.Mat4d.RotateByXyz(0, t.Rotation.Y, 0)
    * Renderer.Mat4d.RotateByXyz(0, 0, t.Rotation.Z)
    * Renderer.Mat4d.RotateByXyz(t.Rotation.X, 0, 0)
    * Renderer.Mat4d.Translate(-t.Origin.X, -t.Origin.Y, -t.Origin.Z)
    * Renderer.Mat4d.Scale(1.0 / 16, 1.0 / 16, 1.0 / 16);
}
