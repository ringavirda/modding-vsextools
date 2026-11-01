using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;
using CoreMat = ExpandedLib.Shapes.Core.Mat4d;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Checks Core's reader and engine against the game's own classes on every shape of the
/// finished set and iiex: the same element trees, the same enabled faces and textures, and world
/// matrices within <see cref="Tolerance"/>. The only project with the game dll, so the only place
/// the game's <see cref="ShapeElement.GetLocalTransformMatrix"/> can be the reference.</summary>
public class CoreOracleTests(ITestOutputHelper output) {
  private const double Tolerance = 1e-5;

  private static List<string> ShapeFiles(string exmods) {
    var files = new List<string>();
    foreach (
      string root in new[]
      {
        Path.Combine(exmods, "workbench", "shapes-finished"),
        Path.Combine(exmods, "mods", "iiex"),
      }
    )
      if (Directory.Exists(root))
        files.AddRange(
          Directory
            .EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(f =>
              f.Replace('\\', '/').Contains("/shapes/")
              || f.Replace('\\', '/').Contains("/shapes-finished/")
            )
        );
    return files;
  }

  [SkippableFact]
  public void Core_agrees_with_the_game_on_every_shape() {
    string? exmods = FixturePath.Workspace("exmods");
    Skip.If(exmods is null, "the sibling exmods checkout is absent");
    int compared = 0;
    var mismatches = new List<string>();
    foreach (string file in ShapeFiles(exmods!)) {
      Shape? game;
      try {
        game = JsonConvert.DeserializeObject<Shape>(File.ReadAllText(file));
      } catch (JsonException) {
        continue;
      }
      if (game?.Elements == null)
        continue;
      Core.Shape core = Core.Reader.ReadFile(file);
      compared++;
      var found = new List<string>();
      Compare(
        game.Elements,
        core.Elements,
        Mat4f.Create(),
        CoreMat.Identity,
        "",
        found
      );
      if (found.Count > 0)
        mismatches.Add($"{file}: {found[0]} (+{found.Count - 1} more)");
    }
    output.WriteLine(
      $"shapes compared {compared}, mismatches {mismatches.Count}"
    );
    foreach (string m in mismatches.Take(20))
      output.WriteLine(m);
    Assert.True(compared > 1000, $"only {compared} shapes compared");
    Assert.Empty(mismatches);
  }

  private static void Compare(
    ShapeElement[] game,
    List<Core.Element> core,
    float[] gameParent,
    CoreMat coreParent,
    string where,
    List<string> found
  ) {
    if (game.Length != core.Count) {
      found.Add($"{where}: {game.Length} elements, core {core.Count}");
      return;
    }
    for (int i = 0; i < game.Length; i++) {
      ShapeElement g = game[i];
      Core.Element c = core[i];
      string at = $"{where}/{g.Name}";
      if (g.Name != c.Name)
        found.Add($"{at}: core name {c.Name}");
      if (!Same(g.From, c.From) || !Same(g.To, c.To))
        found.Add($"{at}: from/to differ");
      if (g.RotationOrigin == null != (c.RotationOrigin == null))
        found.Add($"{at}: rotationOrigin presence differs");
      if (Faces(g) != Faces(c))
        found.Add($"{at}: faces {Faces(g)} against {Faces(c)}");

      float[] gm = Mat4f.Create();
      Mat4f.Mul(gm, gameParent, g.GetLocalTransformMatrix(0));
      CoreMat cm = coreParent * Core.Engine.Local(c);
      double worst = 0;
      for (int r = 0; r < 4; r++)
        for (int col = 0; col < 4; col++)
          worst = Math.Max(
            worst,
            Math.Abs(gm[col * 4 + r] - cm[r, col] / (col == 3 && r < 3 ? 16 : 1))
          );
      if (worst > Tolerance)
        found.Add($"{at}: world matrix off by {worst:G3}");

      Compare(g.Children ?? [], c.Children, gm, cm, at, found);
    }
  }

  private static bool Same(double[]? a, double[]? b) =>
    (a ?? [0, 0, 0]).Zip(b ?? [0, 0, 0]).All(p => p.First == p.Second);

  private static string Faces(ShapeElement g) =>
    string.Join(
      ",",
      (g.FacesResolved ?? [])
        .Select((f, i) => f == null ? null : $"{i}:{f.Texture}")
        .Where(s => s != null)
    );

  private static string Faces(Core.Element c) {
    string[] order = ["north", "east", "south", "west", "up", "down"];
    return string.Join(
      ",",
      Core.Engine.FaceQuads(c, "", CoreMat.Identity)
        .Select(q => (Array.IndexOf(order, q.Face), q.Texture))
        .OrderBy(p => p.Item1)
        .Select(p => $"{p.Item1}:{p.Texture}")
    );
  }
}
