using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace ExpandedLib.Shapes;

public static partial class ItemViews {
  // The arm elements of the player shape the first-person hands are meshed from: the game keeps
  // only the vertices skinned to the upper arms and what hangs from them.
  private static readonly string[] FpArms = ["UpperArmR", "UpperArmL"];

  // The twin of a held clip the game plays in first person.
  private const string FpSuffix = "-fp";

  // The player entity's eye height above the feet, in blocks.
  private const double EyeHeight = 1.7;

  /// <summary>The vertical field of view of the hands' projection, degrees: the game's
  /// <c>fpHandsFoV</c> setting as a stock install holds it.</summary>
  internal const double FpFov = 75;

  /// <summary>The game's <c>fpHandsYOffset</c> setting, blocks, as a stock install holds it.</summary>
  internal const double FpHandsYOffset = 0;

  /// <summary>The share of the gaze pitch the hands follow, the game's default
  /// <c>heldItemPitchFollow</c>.</summary>
  internal const double FpPitchFollow = 0.75;

  /// <summary>Size of the fp view in pixels.</summary>
  internal const int FpWidth = 480,
    FpHeight = 270;

  /// <summary>The camera of the fp view.</summary>
  internal static readonly Renderer.Perspective FpCamera =
    Renderer.Perspective.FromFov(FpFov, FpWidth, FpHeight);

  // The player faces -x, so the eye looks along it: view-space x is the player's right (-z), the
  // depth axis is x. A level gaze is pitch 0; a positive pitch looks down.
  internal static readonly View FpLevel = new(-90, 0);

  /// <summary>The first-person twin of the held clip <paramref name="clip"/>, or the clip itself
  /// when the player shape has no twin; null for a null clip.</summary>
  internal static string? FpClip(LoadedShape seraph, string? clip) =>
    clip != null
    && seraph.Animations.Any(a =>
      a.Code == clip + FpSuffix || a.Name == clip + FpSuffix
    )
      ? clip + FpSuffix
      : clip;

  /// <summary>The warnings an fp view owes: a held clip with no first-person twin on the player
  /// shape.</summary>
  internal static IEnumerable<string> FpWarnings(
    LoadedShape seraph,
    string? idle,
    string? ready
  ) {
    string? clip = HeldClip(seraph, idle, ready);
    if (clip != null && FpClip(seraph, clip) == clip)
      yield return $"held clip {clip} has no {clip}{FpSuffix} twin on the player shape";
  }

  /// <summary>The scene of the fp view, in world units with the eye at the origin: the player's
  /// arms posed at frame 0 of the -fp twin of the held clip and the item on the anchor.</summary>
  /// <param name="pitchDeg">The gaze pitch, degrees, positive looking down; the hands follow
  /// <paramref name="follow"/> of it about the eye.</param>
  /// <param name="handsYOffset">Blocks the hands are moved up, the game's <c>fpHandsYOffset</c>.</param>
  internal static Renderer.RenderPart[] FpParts(
    LoadedShape shape,
    TextureSet textures,
    ItemTransform tp,
    Seraph seraph,
    string? clip,
    double pitchDeg = 0,
    double follow = FpPitchFollow,
    double handsYOffset = FpHandsYOffset
  ) {
    Renderer.RenderPart[] tpParts = TpParts(
      shape,
      textures,
      tp,
      seraph,
      FpClip(seraph.Shape, clip)
    );
    Renderer.Mat4d placement =
      Renderer.Mat4d.RotateByXyz(0, 0, pitchDeg * follow)
      * Renderer.Mat4d.Translate(-8, -EyeHeight * 16, -8)
      * Renderer.Mat4d.Translate(0, handsYOffset * 16, 0);
    HashSet<string> arms =
    [
      .. FpArms.Select(name =>
        seraph.Shape.Find(name)?.Path
        ?? throw new InvalidDataException(
          $"the player shape has no {name} element"
        )
      ),
    ];
    return
    [
      tpParts[0] with
      {
        Model = placement,
        Only = arms,
      },
      tpParts[1] with
      {
        Model = placement * tpParts[1].Model,
      },
    ];
  }

  /// <summary>The fp view: the player's hands holding the item seen from the eye, at a level
  /// gaze unless <paramref name="pitchDeg"/> says otherwise.</summary>
  internal static SKBitmap RenderFp(
    LoadedShape shape,
    TextureSet textures,
    ItemTransform tp,
    Seraph seraph,
    string? idle,
    string? ready,
    double pitchDeg = 0
  ) =>
    Renderer.Render(
      FpParts(
        shape,
        textures,
        tp,
        seraph,
        HeldClip(seraph.Shape, idle, ready),
        pitchDeg
      ),
      new View(FpLevel.Yaw, pitchDeg),
      FpCamera.Focal,
      grid: false,
      edges: false,
      perspective: FpCamera
    );

  /// <summary>The item layer of the fp view: the held item alone on a transparent canvas of the
  /// view's size, placed as <see cref="RenderFp"/> places it among the hands' pixels.</summary>
  internal static SKBitmap RenderFpItem(
    LoadedShape shape,
    TextureSet textures,
    ItemTransform tp,
    Seraph seraph,
    string? idle,
    string? ready,
    double pitchDeg = 0
  ) {
    Renderer.RenderPart[] parts = FpParts(
      shape,
      textures,
      tp,
      seraph,
      HeldClip(seraph.Shape, idle, ready),
      pitchDeg
    );
    return Renderer.Render(
      [parts[1]],
      new View(FpLevel.Yaw, pitchDeg),
      FpCamera.Focal,
      grid: false,
      edges: false,
      transparent: true,
      perspective: FpCamera
    );
  }
}
