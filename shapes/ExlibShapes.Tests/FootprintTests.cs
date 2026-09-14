using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>
/// Ties a megablock's drawn mesh to the cells it reserves - the mirrored-layout defect, which reads
/// as consistent in every file while the machine draws its body where its collision is not.
/// </summary>
public class FootprintTests {
  // Megablocks whose own art reaches further than the cells they reserve, at every turn: a part of
  // a structure, drawn with the surround or the stack it sits in, which belongs to the blocks
  // around it. Their footprint still has to turn with their body; the containment alone is theirs
  // to break, by the margin named here. A block that leaves this list fails the fact, which is how
  // the list stays current.
  private static readonly Dictionary<string, string> ArtOverItsFootprint = new() {
    ["iiex:crafting-workbench"] = "a vice standing 0.38 blocks off the bench's far end",
    ["iiex:furnace-puddlingchargedoor"] = "the door's brick surround, 1.42 blocks either side",
    ["iiex:furnace-puddlingchimneycap"] = "the cap drawn with 2.06 blocks of stack below it",
  };

  // The two trees the family's megablocks live in: ppex's published assets (their spin in
  // rotateYByType, their structure angle offset by a half turn in C#) and iiex's code-first
  // goldens (several of them shapeless, drawn as a unit cube).
  private static readonly (string Tree, bool Legacy)[] Trees = [
    ("exmods/legacy/ppex/assets", true),
    ("exmods/mods/iiex/tests/goldens", false),
  ];

  [Fact]
  public void A_demo_megablock_turns_its_declared_footprint_onto_its_drawn_body() {
    BlockIndex index = BlockIndex.Build([FixturePath.Of("schematic")]);
    // The body is authored along -z and the footprint along +z, so the half turn is what brings
    // the two together - what the old boilers and engines do in C# and no file states.
    Layout north = Layout.Load(FixturePath.Of("schematic/mods/demo/assets/demo/blocktypes/mega.json"), "mega-north");
    Assert.Equal(180, Footprint.FrameAngle(north, index));
    Assert.Equal([new Offset(0, 0, -1)], Footprint.Placed(north, index).Fillers);
  }

  [Fact]
  public void A_demo_megablock_keeps_the_frame_at_every_facing() {
    BlockIndex index = BlockIndex.Build([FixturePath.Of("schematic")]);
    foreach ((string side, int spin, int frame) in new[] {
      ("north", 0, 180),
      ("east", 270, 90),
      ("south", 180, 0),
      ("west", 90, 270),
    }) {
      Layout layout = Layout.Load(
        FixturePath.Of("schematic/mods/demo/assets/demo/blocktypes/mega.json"),
        $"mega-{side}"
      );
      Assert.Equal(spin, (int)index.Resolve(layout.Principal!)!.RotateY);
      Assert.Equal(frame, Footprint.FrameAngle(layout, index));
    }
  }

  [SkippableFact]
  public void Every_filler_only_megablock_draws_inside_its_footprint_at_every_facing() {
    string? exmods = FixturePath.Workspace("exmods");
    Skip.If(exmods is null, "the sibling exmods (and exlib) checkout is absent");

    var report = new StringBuilder();
    var failures = new List<string>();
    int checkedVariants = 0;
    foreach ((string tree, bool legacy) in Trees) {
      string root = Path.Combine(Path.GetDirectoryName(exmods!)!, tree.Replace('/', Path.DirectorySeparatorChar));
      Skip.If(!Directory.Exists(root), $"{tree} is absent");
      BlockIndex index = BlockIndex.Build(BlockIndex.DefaultRoots(root), null, legacy);

      foreach (string file in FillerOnlyMegablocks(root)) {
        var turns = new List<(string Code, int Spin, IReadOnlyList<int> Frames)>();
        foreach (Variant variant in index.VariantsOf(file)) {
          Layout layout = Layout.Load(file, variant.Path);
          if (Footprint.PrincipalMesh(layout, index) is not { } mesh)
            continue; // no shape file of its own: a unit cube fills the principal's own cell
          checkedVariants++;
          Footprint.Box declared = Footprint.CellBox(Footprint.Cells(layout));
          IReadOnlyList<int> frames = Footprint.Frames(mesh, declared);
          int spin = (int)index.Resolve(variant.Code)!.RotateY;
          turns.Add((variant.Code, spin, frames));
          string? misfit = Footprint.Misfit(mesh, Footprint.Turned(declared, frames[0]));
          report.AppendLine(
            $"{variant.Code} spin={spin} frames={string.Join('|', frames)} "
              + $"excess={Footprint.Excess(mesh, Footprint.Turned(declared, frames[0])):0.###}"
          );
          if (misfit != null && !ArtOverItsFootprint.ContainsKey(Family(variant.Code)))
            failures.Add($"{variant.Code} ({Path.GetFileName(file)}) {misfit}");
        }

        // One family turns rigidly: every facing stands its footprint the same way round its own
        // body, so the turn follows the spin. Only the facings whose footprint tells the quarter
        // turns apart say anything - a footprint symmetric about its principal draws the same cells
        // whichever turn is taken.
        var constants = turns.Select(t => t.Frames.Select(f => Mod360(f - t.Spin)).ToHashSet()).ToList();
        if (constants.Count > 0 && constants.Aggregate((a, b) => [.. a.Intersect(b)]).Count == 0)
          failures.Add(
            "the footprint of "
              + Path.GetFileNameWithoutExtension(file)
              + " does not turn with its body: "
              + string.Join(", ", turns.Select(t => $"{t.Code} spin {t.Spin} frames {string.Join('|', t.Frames)}"))
          );
      }
    }

    string actualDir = Path.Combine(AppContext.BaseDirectory, "actual");
    Directory.CreateDirectory(actualDir);
    File.WriteAllText(Path.Combine(actualDir, "megablock-frames.txt"), report.ToString());

    Assert.True(checkedVariants > 0, "no megablock with a shape file was found to check");
    Assert.True(failures.Count == 0, string.Join("\n", failures));
  }

  private static int Mod360(int angle) => ((angle % 360) + 360) % 360;

  // The block code without its facing segment - one family's four facings share it.
  private static string Family(string code) => code[..code.LastIndexOf('-')];

  // Every blocktype file under `root` that declares fillerOffsets and no structure table - what
  // Layout reports as a footprint with no cells.
  private static IEnumerable<string> FillerOnlyMegablocks(string root) {
    foreach (
      string file in Directory
        .EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
        .Where(f => f.Split(Path.DirectorySeparatorChar).Contains("blocktypes"))
        .OrderBy(f => f, StringComparer.Ordinal)
    ) {
      Layout layout;
      try {
        layout = Layout.Load(file);
      } catch (Exception) {
        continue; // not a footprint (LayoutError) or not a blocktype file at all
      }
      if (layout.Cells.Count == 0 && layout.Fillers.Count > 0)
        yield return file;
    }
  }
}
