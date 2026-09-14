using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>
/// The one gate <c>BlocksTests.Resolve_agrees_with_every_selector_of_the_blastcore_golden</c>
/// cannot give: that <see cref="BlockIndex.Resolve"/> picks the SAME representative the Python
/// picks, not merely a resolved-or-optional one. Dumps (selector, representative code, shape file
/// name, shapeByType rotation, optional flag) for every selector of all ten of the family's real
/// multiblock goldens, at angles 0/90/180/270 (none of them exercises the wildcard tie-break or
/// the filler-by-type override T6 adds - those are <see cref="BlocksTests"/> and
/// <see cref="LayoutTests"/>'s own facts - so this is a straight parity check), and compares the
/// text against <c>expected/schematic/resolve-dump.txt</c>, produced once by the same walk over
/// <c>vsshape.blocks</c>/<c>vsshape.layout</c> (the script is not committed; the fixture is what
/// matters), with one deliberate departure: a block drawing the engine's <c>block/basic/cube</c>
/// names that shape file here (found under the install's <c>assets/game</c>, which the Python
/// never looked in) where the Python drew its synthetic cube.
/// </summary>
public class ResolverDumpTests {
  // Relative to the family workspace's own exmods checkout - dev machine only, same as
  // BlocksTests' own workspace-dependent facts.
  private static readonly string[] Goldens = [
    "mods/iiex/tests/goldens/iiex/blocktypes/furnace/blastcore.json",
    "mods/iiex/tests/goldens/iiex/blocktypes/furnace/puddlingcore.json",
    "mods/iiex/tests/goldens/iiex/blocktypes/furnace/heatingcore.json",
    "mods/iiex/tests/goldens/iiex/blocktypes/furnace/cruciblecore.json",
    "mods/iiex/tests/goldens/iiex/blocktypes/furnace/cupolacore.json",
    "mods/iiex/tests/goldens/iiex/blocktypes/furnace/cokeovencore.json",
    "mods/siex/tests/goldens/siex/blocktypes/converter/control.json",
    "mods/siex/tests/goldens/siex/blocktypes/blastfurnace/core.json",
    "mods/siex/tests/goldens/siex/blocktypes/smokestack/intake.json",
    "mods/siex/tests/goldens/siex/blocktypes/cowperstove/intake.json",
  ];

  [Fact]
  public void Resolve_matches_the_python_representative_for_every_selector_of_every_family_golden() {
    string? exmods = FixturePath.Workspace("exmods");
    if (exmods is not { } repo)
      return; // needs the family workspace's own exmods (and exlib) checkout, dev machine only

    var sb = new StringBuilder();
    foreach (string rel in Goldens) {
      string golden = Path.Combine(repo, rel.Replace('/', Path.DirectorySeparatorChar));
      IReadOnlyList<string> roots = BlockIndex.DefaultRoots(golden);
      BlockIndex index = BlockIndex.Build(roots);
      string stem = Path.GetFileNameWithoutExtension(golden);

      foreach (int angle in new[] { 0, 90, 180, 270 }) {
        Layout layout = Layout.Load(golden);
        if (angle != 0)
          layout = layout.Rotated(angle);
        string connectors = string.Join(',', layout.Connectors.Keys.OrderBy(k => k, System.StringComparer.Ordinal));
        sb.AppendLine(
          $"== {stem} angle={angle} cells={layout.Cells.Count} fillers={layout.Fillers.Count} connectors={connectors}"
        );
        foreach (int n in layout.Numbers.Keys.OrderBy(n => n)) {
          string selector = layout.Numbers[n];
          bool optional = index.Optional(selector);
          ResolvedBlock? block = index.Resolve(selector);
          if (block == null) {
            sb.AppendLine($"{n} {selector} None optional={Py(optional)}");
          } else {
            string? shape = block.ShapePath != null ? Path.GetFileName(block.ShapePath) : null;
            sb.AppendLine(
              $"{n} {selector} {block.Code} shape={shape ?? "None"} "
                + $"rot=({Py(block.RotateX)},{Py(block.RotateY)},{Py(block.RotateZ)}) optional={Py(optional)}"
            );
          }
        }
      }
    }

    // The dump as this run produced it, beside the binary, to diff against the fixture when a
    // resolver change is meant to move it.
    string actualDir = Path.Combine(System.AppContext.BaseDirectory, "actual");
    Directory.CreateDirectory(actualDir);
    File.WriteAllText(Path.Combine(actualDir, "resolve-dump.txt"), sb.ToString());

    string expected = File.ReadAllText(FixturePath.Expected("schematic/resolve-dump.txt"));
    Assert.Equal(Normalize(expected), Normalize(sb.ToString()));
  }

  // Python's bool str() and float repr(): True/False, and a whole number always shown with a
  // decimal point (90.0, not 90).
  private static string Py(bool v) => v ? "True" : "False";

  private static string Py(double v) {
    string s = v.ToString("R", CultureInfo.InvariantCulture);
    return s.Contains('.') || s.Contains('e') || s.Contains('E') ? s : s + ".0";
  }

  // The fixture was generated on a platform whose text mode may add \r\n; only line content and
  // order matter here.
  private static string Normalize(string text) =>
    string.Join('\n', text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'));
}
