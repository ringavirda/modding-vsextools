using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Generators;

/// <summary>Step 10: the wooden box a core is rammed in.</summary>
public static class CoreBoxes {
  /// <summary>Between the two trays on x, in voxels.</summary>
  private const double TrayGap = 2;

  /// <summary>The box of a core: two trays side by side on x, each a floor and four walls of
  /// <see cref="Corefit.Wall"/> round a pocket open above. The pockets are alike across, the core's longest side
  /// on x and its middle one on z, and each is half the core's thinnest side deep, so together they equal the
  /// core. Elements over 16 are cut as the cast's are; the up and down faces carry the boards along the pocket's
  /// long side. The record carries the <c>corebox</c> slot (<c>core</c>, <c>trays</c>) the corefit gate reads.</summary>
  /// <param name="ctx">The context; its settings give the core box role's texture.</param>
  /// <param name="item">The core box item's name.</param>
  /// <param name="coreCode">The code of the core the box makes.</param>
  /// <param name="core">The core, as <see cref="Cores.Make"/> returns it.</param>
  /// <param name="spec">The spec text the provenance hash is taken over.</param>
  /// <exception cref="CastGenException">The core has no volume.</exception>
  public static GenOutput Make(
    GenContext ctx,
    string item,
    string coreCode,
    GenOutput core,
    string spec
  ) {
    var tree = new Tree(core.Shape);
    var frames = new Frames(core.Shape);
    Box all = Cores.Bounds(
      tree.Leaves.Select(n => WorldBox.Of(frames.Mat(n), n.Size))
    );
    Vec3 size = all.Hi - all.Lo;
    double[] dims = [.. new[] { size.X, size.Y, size.Z }.OrderDescending()];
    if (!(dims[2] > 0))
      throw new CastGenException($"{item}: core \"{coreCode}\" has no volume");
    double px = dims[0],
      pz = dims[1],
      depth = dims[2] / 2,
      wall = Corefit.Wall;
    string key = "#" + ctx.Settings.RoleKey("corebox");
    bool alongZ = pz >= px;

    var rec = new PartRecord { Shape = item + ".json", Owner = "generated" };
    rec.Kinds.Add("corebox");
    var shape = new Shape();
    var all2 = new List<Leaf>();
    string[] trays = ["Tray1", "Tray2"];
    for (int t = 0; t < trays.Length; t++) {
      double ox = t * (px + 2 * wall + TrayGap),
        w = px + 2 * wall,
        z = pz + 2 * wall,
        top = wall + depth;
      string n = trays[t];
      (string Name, double[] From, double[] To)[] parts =
      [
        ($"{n}Floor", [ox, 0, 0], [ox + w, wall, z]),
        ($"{n}W", [ox, wall, 0], [ox + wall, top, z]),
        ($"{n}E", [ox + w - wall, wall, 0], [ox + w, top, z]),
        ($"{n}S", [ox + wall, wall, 0], [ox + w - wall, top, wall]),
        ($"{n}N", [ox + wall, wall, z - wall], [ox + w - wall, top, z]),
      ];
      List<Leaf> leaves = [];
      foreach ((string name, double[] from, double[] to) in parts) {
        var e = new Element {
          Name = name,
          From = from,
          To = to,
        };
        foreach (string face in FaceAxes.Names)
          e.Faces.Add(new(face, new Face()));
        leaves.Add(Cores.Styled(name, e, key, "corebox"));
      }
      leaves = Splits.Apply(leaves);
      leaves = [.. leaves.Select(l => Boards(l, alongZ))];
      leaves = Uvs.Assign(ctx, item, leaves);
      all2.AddRange(leaves);
      var group = new Element {
        Name = n,
        From = [0, 0, 0],
        To = [0, 0, 0],
      };
      foreach (Leaf l in leaves) {
        rec.Elements[l.Name] = new ElementPart {
          Stage = "cast",
          Role = "corebox",
        };
        group.Children.Add(Cores.Named(l));
      }
      shape.Elements.Add(group);
    }
    Cores.Textures(ctx, shape, all2);
    rec.Carried["corebox"] = new JObject {
      ["core"] = coreCode,
      ["trays"] = new JArray(trays),
    };
    string hash = Provenance.InputHash(
      spec,
      core.InputHash,
      Provenance.Sha256Of(CanonicalWriter.Write(shape)),
      Provenance.Sha256Of(key)
    );
    return new GenOutput(item, rec.Kinds, shape, rec, hash);
  }

  private static Leaf Boards(Leaf leaf, bool alongZ) {
    Element e = Select.Clone(leaf.Element, false);
    Vec3 s = Engine.Size(e);
    foreach (string face in new[] { "up", "down" }) {
      Face? f = e.Faces.FirstOrDefault(kv => kv.Key == face).Value;
      if (f == null)
        continue;
      if (alongZ) {
        f.Uv = [0, 0, s.Z, s.X];
        f.Rotation = 90;
      } else {
        f.Uv = [0, 0, s.X, s.Z];
        f.Rotation = null;
      }
    }
    return leaf with { Element = e };
  }
}
