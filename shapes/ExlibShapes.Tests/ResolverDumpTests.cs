using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>
/// Checks that <see cref="BlockIndex.Resolve"/> picks the same representative as the fixture,
/// not merely a resolved-or-optional one. Dumps (selector, representative code, shape file
/// name, shapeByType rotation, optional flag) for every selector of all ten of the family's real
/// multiblock goldens, at angles 0/90/180/270, and compares the text against
/// <c>expected/schematic/resolve-dump.txt</c>. None of these selectors exercises the wildcard
/// tie-break or the filler-by-type override; those are <see cref="BlocksTests"/> and
/// <see cref="LayoutTests"/>'s own facts, so this is a straight parity check. One deliberate
/// departure from the fixture: a block drawing the engine's <c>block/basic/cube</c> names that
/// shape file here, found under the install's <c>assets/game</c>, in place of a synthetic cube.
/// </summary>
public class ResolverDumpTests {
  // Relative to the family workspace's own exmods checkout.
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
      return; // skips when the sibling exmods (and exlib) checkout is absent

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

  // The fixture's own number format: True/False, and a whole number always shown with a
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
