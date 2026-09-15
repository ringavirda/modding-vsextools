using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>
/// Ties a megablock's drawn mesh to the cells it reserves - the mirrored-layout defect, which reads
/// as consistent in every file while the machine draws its body where its collision is not.
/// </summary>
public class FootprintTests {
  // Megablocks whose drawn body and reserved cells do not agree where the game turns the footprint
  // to. Each entry states the defect as a bound: the blocks of overhang the art is allowed past its
  // cells, and the turn the drawing has to add to the game's angle to cover the body. A family that
  // needs neither, or whose overhang is pinned more than a quarter block above what it measures,
  // fails the fact as a stale pin, which is how the list stays current.
  private static readonly Dictionary<string, (double Overhang, int FrameOffset, string Why)> FootprintDefects =
    new() {
      ["iiex:crafting-workbench"] = (0.4, 0, "a vice standing off the bench's far end"),
      ["iiex:furnace-puddlingchargedoor"] = (1.5, 0, "the door drawn with its brick surround either side"),
      ["iiex:furnace-puddlingchimneycap"] = (2.1, 0, "the cap drawn with the stack below it"),
      ["iiex:mpenergy-flywheel-large"] = (1.0, 180, "the slab drawn one cell off the pair it reserves"),
    };

  // The families the mirrored layout was first found in. A gate that stops reaching them says
  // nothing while still passing, which is how the old boilers slipped out of it.
  private static readonly string[] MustBeChecked = [
    "ppex:boilercornish",
    "ppex:boilerlancashire",
    "ppex:enginecornish",
    "ppex:enginewatt",
    "siex:boilerlancashire",
    "siex:converterbessemer",
    "siex:enginecornish",
  ];

  // The trees the family's megablocks live in: ppex's published assets and the code-first goldens of
  // iiex and siex (several of them shapeless, drawn as a unit cube).
  private static readonly (string Tree, bool Legacy)[] Trees = [
    ("exmods/legacy/ppex/assets", true),
    ("exmods/mods/iiex/tests/goldens", false),
    ("exmods/mods/siex/tests/goldens", false),
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
  public void Every_megablock_draws_inside_the_footprint_the_game_turns_it_to() {
    string? exmods = FixturePath.Workspace("exmods");
    Skip.If(exmods is null, "the sibling exmods (and exlib) checkout is absent");

    var report = new StringBuilder();
    var failures = new List<string>();
    var pinned = new HashSet<string>(StringComparer.Ordinal);
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach ((string tree, bool legacy) in Trees) {
      string root = Path.Combine(Path.GetDirectoryName(exmods!)!, tree.Replace('/', Path.DirectorySeparatorChar));
      Skip.If(!Directory.Exists(root), $"{tree} is absent");
      BlockIndex index = BlockIndex.Build(BlockIndex.DefaultRoots(root), null, legacy);

      foreach (string file in Megablocks(root)) {
        foreach (Variant variant in index.VariantsOf(file)) {
          Layout layout = Layout.Load(file, variant.Path);
          if (Footprint.PrincipalMesh(layout, index) is not { } mesh)
            continue; // no shape file of its own: a unit cube fills the principal's own cell
          string family = Family(variant.Code);
          seen.Add(family);
          (double overhang, int frameOffset, string why) =
            FootprintDefects.TryGetValue(family, out var defect) ? defect : (Footprint.Overhang, 0, "");

          Footprint.Box declared = Footprint.CellBox(Footprint.Cells(layout));
          int game = StructureAngle(variant);
          Footprint.Box placed = Footprint.Turned(declared, game);
          int drawn = Footprint.FrameAngle(layout, index);
          double excess = Footprint.Excess(mesh, placed);
          report.AppendLine(
            $"{variant.Code} spin={(int)index.Resolve(variant.Code)!.RotateY} game={game} drawn={drawn} "
              + $"excess={excess:0.###}"
          );

          if (Footprint.Misfit(mesh, placed, overhang) is { } misfit)
            failures.Add($"{variant.Code} ({Path.GetFileName(file)}) {misfit}");
          // The angles themselves may differ where the footprint cannot tell two turns apart; the
          // cells they land on may not.
          IReadOnlyList<Offset> placedCells = Footprint.Cells(Footprint.Placed(layout, index));
          bool stray = !SameCells(CellsAt(layout, game), placedCells);
          if (!SameCells(CellsAt(layout, Mod360(game + frameOffset)), placedCells))
            failures.Add(
              $"{variant.Code} ({Path.GetFileName(file)}) is turned {game}deg by its own C# but the "
                + $"drawing places its cells at {drawn}deg, which is not that frame turned by the "
                + $"{frameOffset}deg pinned for it"
            );
          if (why.Length == 0)
            continue;
          if (excess > Footprint.Overhang || stray)
            pinned.Add(family);
          if (overhang > excess + Footprint.Overhang)
            failures.Add(
              $"{family} is pinned at {overhang} blocks of overhang ({why}) but {variant.Code} "
                + $"reaches only {excess:0.###}; pin it at what it measures"
            );
        }
      }
    }

    foreach (string family in FootprintDefects.Keys.Except(pinned).OrderBy(f => f, StringComparer.Ordinal))
      failures.Add(
        $"{family} is pinned as a footprint defect ({FootprintDefects[family].Why}) but no longer is one"
      );

    foreach (string family in MustBeChecked.Except(seen))
      failures.Add($"{family} has a shape and a footprint but this fact never reached it");

    string actualDir = Path.Combine(AppContext.BaseDirectory, "actual");
    Directory.CreateDirectory(actualDir);
    File.WriteAllText(Path.Combine(actualDir, "megablock-frames.txt"), report.ToString());

    Assert.True(failures.Count == 0, string.Join("\n", failures));
  }

  // The footprint cells `layout` reserves once turned by `angle`.
  private static IReadOnlyList<Offset> CellsAt(Layout layout, int angle) =>
    Footprint.Cells(angle == 0 ? layout : layout.Rotated(angle));

  private static bool SameCells(IReadOnlyList<Offset> a, IReadOnlyList<Offset> b) =>
    a.ToHashSet().SetEquals(b);

  // The rotation a horizontal side variant names, the game's own rule
  // (ExpandedLib.Helpers.ExOrientation): north 0, west 90, south 180, east 270, full words and the
  // single-letter orientation codes alike.
  private static int AngleFromSide(string? side) =>
    side switch {
      "east" or "e" => 270,
      "south" or "s" => 180,
      "west" or "w" => 90,
      _ => 0,
    };

  // The angle the block's own C# turns its declared footprint by before placing it, which a drawing
  // has to reach from the files alone. The default is the side variant's angle
  // (ExpandedLib.Structures.BlockFilledMegastructure.StructureAngle); the classes named below
  // override it, and no blocktype file records that they do.
  private static int StructureAngle(Variant variant) {
    string? side = variant.States.GetValueOrDefault("side") ?? variant.States.GetValueOrDefault("orientation");
    return Mod360(
      (string?)variant.Raw["class"] switch {
        // The body extends away from the player, so the footprint takes the same half turn:
        // BlockBoiler, BlockEngine, BlockMpFluidPump, BlockSandCastingBed, BlockSandCastingLongCell
        // and BlockWorkbench.
        "ppex.BlockBoilerCornish"
        or "ppex.BlockBoilerLancashire"
        or "ppex.BlockEngineCornish"
        or "ppex.BlockEngineWatt"
        or "ppex.BlockMpFluidPump"
        or "iiex.BlockBoilerCornish"
        or "iiex.BlockEngineWatt"
        or "siex.BlockBoilerLancashire"
        or "siex.BlockEngineCornish"
        or "iiex.BlockSandCastingBed"
        or "iiex.BlockSandCastingLongCell"
        or "iiex.BlockWorkbench" => AngleFromSide(side) + 180,
        // An axis, not a facing: the footprint is authored along one of them and turned a quarter
        // onto the other. BlockFlywheel, BlockShear and BlockFastenerBench author `ns`;
        // BlockRollingMill authors `we`.
        "iiex.BlockFlywheel" or "iiex.BlockShear" or "iiex.BlockFastenerBench" => side == "we" ? 90 : 0,
        "iiex.BlockRollingMill" => side == "ns" ? 90 : 0,
        // BlockTwinTubMPBlower reads the pipe fittings' own orientation axis, which numbers its
        // letters the other way round from a side variant.
        "iiex.BlockTwinTubMPBlower" => side switch {
          "e" => 90,
          "s" => 180,
          "w" => 270,
          _ => 0,
        },
        _ => AngleFromSide(side),
      }
    );
  }

  private static int Mod360(int angle) => ((angle % 360) + 360) % 360;

  // The block code without its facing segment - one family's four facings share it. An unoriented
  // code keeps all of itself.
  private static string Family(string code) {
    int dash = code.LastIndexOf('-');
    return dash < 0 ? code : code[..dash];
  }

  // Every blocktype file under `root` that reserves filler cells around its own body, structure
  // table or not - Footprint.Cells reads the fillers of both.
  private static IEnumerable<string> Megablocks(string root) {
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
      if (layout.Fillers.Count > 0)
        yield return file;
    }
  }
}
