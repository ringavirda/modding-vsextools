using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace ExpandedLib.Shapes;

public static partial class ItemViews {
  // The player's shape and the skin the player entity paints it with, and the texture key and
  // element the shape carries them on.
  internal const string SeraphShape = "game:entity/humanoid/seraph-faceless";
  internal const string SeraphSkin =
    "game:entity/humanoid/seraph-naked-hairless";
  internal const string SeraphKey = "seraph";
  private const string AnchorElement = "ItemAnchor";
  private const string ReadyClip = "helditemready";

  // Pixels per voxel of the tp view, fixed so every item is drawn to the same player.
  internal const int TpPpu = 8;

  // The RightHand attachment point's rotationY on the ItemAnchor element.
  internal const double HandRotationY = -180;

  // The two angles of the tp view, side by side: the player's right side (the player faces -X, so
  // the camera stands north) and a front-right three-quarter.
  internal static readonly View[] TpAngles =
  [
    Renderer.NamedViews["north"],
    new(135, 15),
  ];

  // Gap between the tp view's two pictures, in pixels.
  private const int TpGap = 8;

  /// <summary>The player shape, its skin and the width and height its face UVs are written in.
  /// The skin is twice the UV size on each axis.</summary>
  internal sealed record Seraph(
    LoadedShape Shape,
    TextureSet Textures,
    (double Width, double Height) UvSize
  );

  /// <summary>The player shape and skin as <paramref name="index"/> resolves them, or null when
  /// the index carries no game assets that hold the shape.</summary>
  internal static Seraph? LoadSeraph(BlockIndex index) {
    if (index.ShapePath(SeraphShape) is not { } path)
      return null;
    JObject raw = JObject.Parse(File.ReadAllText(path));
    (double, double) uv = raw["textureSizes"]?[SeraphKey]
      is JArray { Count: 2 } sizes
      ? ((double)sizes[0], (double)sizes[1])
      : ((double)raw["textureWidth"]!, (double)raw["textureHeight"]!);
    return new Seraph(
      ShapeFile.Load(path),
      TextureSet.FromResolved(
        new Dictionary<string, TextureRef> {
          [SeraphKey] = new TextureRef(SeraphSkin),
        },
        index.ResolveTexture
      ),
      uv
    );
  }

  /// <summary>The clip the player is posed by: <paramref name="idle"/>, else
  /// <paramref name="ready"/>, else the generic held-item clip, the first the shape carries.
  /// Null when it carries none of them.</summary>
  internal static string? HeldClip(
    LoadedShape seraph,
    string? idle,
    string? ready
  ) =>
    new[] { idle, ready, ReadyClip }.FirstOrDefault(name =>
      name != null
      && seraph.Animations.Any(a => a.Code == name || a.Name == name)
    );

  /// <summary>The warnings a tp view owes: a clip the item names that the player shape does not
  /// carry.</summary>
  internal static IEnumerable<string> TpWarnings(
    LoadedShape seraph,
    string? idle,
    string? ready
  ) {
    string? used = HeldClip(seraph, idle, ready);
    foreach (string? named in new[] { idle, ready })
      if (
        named != null
        && named != used
        && HeldClip(seraph, named, null) != named
      )
        yield return $"held clip {named} is not on the player shape";
  }

  /// <summary>
  /// The matrix an item is held with, from the item shape's voxels to the player shape's voxels:
  /// the anchor's world matrix, then the hand's chain in blocks - origin, scale, translation, the
  /// attachment point's rotation added to the item's, origin back.
  /// </summary>
  internal static Renderer.Mat4d HandModel(
    Renderer.Mat4d anchor,
    ItemTransform t
  ) =>
    anchor
    * Renderer.Mat4d.Scale(16, 16, 16)
    * Renderer.Mat4d.Translate(t.Origin.X, t.Origin.Y, t.Origin.Z)
    * Renderer.Mat4d.Scale(t.ScaleXyz.X, t.ScaleXyz.Y, t.ScaleXyz.Z)
    * Renderer.Mat4d.Translate(
      t.Translation.X,
      t.Translation.Y,
      t.Translation.Z
    )
    * Renderer.Mat4d.RotateByXyz(
      t.Rotation.X,
      t.Rotation.Y + HandRotationY,
      t.Rotation.Z
    )
    * Renderer.Mat4d.Translate(-t.Origin.X, -t.Origin.Y, -t.Origin.Z)
    * Renderer.Mat4d.Scale(1.0 / 16, 1.0 / 16, 1.0 / 16);

  /// <summary>The player posed at frame 0 of <paramref name="clip"/> (the rest pose when null)
  /// and the item held in its right hand: the player is the first part, the item the second.
  /// </summary>
  internal static Renderer.RenderPart[] TpParts(
    LoadedShape shape,
    TextureSet textures,
    ItemTransform tp,
    Seraph seraph,
    string? clip
  ) {
    IReadOnlyDictionary<string, Pose>? poses =
      clip == null ? null : Poses.PoseAt(seraph.Shape, clip, 0);
    Node anchor =
      seraph.Shape.Find(AnchorElement)
      ?? throw new InvalidDataException(
        $"the player shape has no {AnchorElement} element"
      );
    Renderer.Mat4d anchorMatrix = Renderer.WorldMatricesD(seraph.Shape, poses)[
      anchor.Path
    ];
    return
    [
      new Renderer.RenderPart(
        seraph.Shape,
        seraph.Textures,
        poses,
        Renderer.Mat4d.Identity,
        seraph.UvSize
      ),
      new Renderer.RenderPart(
        shape,
        textures,
        null,
        HandModel(anchorMatrix, tp)
      ),
    ];
  }

  /// <summary>The tp view: the player holding the item, from its right side and from the front
  /// right, side by side at <c>TpPpu</c> pixels per voxel.</summary>
  internal static SKBitmap RenderTp(
    LoadedShape shape,
    TextureSet textures,
    ItemTransform tp,
    Seraph seraph,
    string? idle,
    string? ready
  ) {
    Renderer.RenderPart[] parts = TpParts(
      shape,
      textures,
      tp,
      seraph,
      HeldClip(seraph.Shape, idle, ready)
    );
    SKBitmap[] pictures =
    [
      .. TpAngles.Select(view =>
        Renderer.Render(parts, view, TpPpu, grid: false, edges: false)
      ),
    ];
    var image = new SKBitmap(
      pictures.Sum(p => p.Width) + TpGap * (pictures.Length - 1),
      pictures.Max(p => p.Height),
      SKColorType.Rgba8888,
      SKAlphaType.Unpremul
    );
    using var canvas = new SKCanvas(image);
    canvas.Clear(Renderer.Background);
    int x = 0;
    foreach (SKBitmap picture in pictures) {
      canvas.DrawBitmap(picture, x, 0);
      x += picture.Width + TpGap;
      picture.Dispose();
    }
    return image;
  }

  /// <summary>The item layer of the tp view's side angle: the held item alone on a transparent
  /// canvas, placed as <see cref="RenderTp"/> places it among the player's pixels.</summary>
  internal static SKBitmap RenderTpItem(
    LoadedShape shape,
    TextureSet textures,
    ItemTransform tp,
    Seraph seraph,
    string? idle,
    string? ready
  ) {
    Renderer.RenderPart[] parts = TpParts(
      shape,
      textures,
      tp,
      seraph,
      HeldClip(seraph.Shape, idle, ready)
    );
    View view = TpAngles[0];
    return Renderer.Render(
      [parts[1]],
      view,
      TpPpu,
      grid: false,
      edges: false,
      transparent: true,
      fitTo: Renderer.Fit(parts, view)
    );
  }
}
