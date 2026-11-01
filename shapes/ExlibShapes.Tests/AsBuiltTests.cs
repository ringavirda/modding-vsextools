using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Covers an as-built map laid over the blastcore golden: the attach and its three
/// refusals, the side segment turning with the layout, the draw, and the schematic command's
/// <c>--as-built</c> flag.</summary>
public class AsBuiltTests {
  // Cells of the golden, by number: 3 is the work arch (admits the banded work arch), 2 a stack
  // cell, 1 a lining cell.
  private static readonly Offset Arch = new(0, 3, -3);
  private static readonly Offset Stack = new(-3, 0, -3);
  private static readonly Offset Lining = new(-1, 0, 0);
  private const string WorkArch = "iiex:bandedbrick-workarch-tier1-n";

  private static string Golden() {
    string? golden = FixturePath.Workspace(
      "exmods/mods/iiex/tests/goldens/iiex/blocktypes/furnace/blastcore.json"
    );
    Skip.If(golden is null, "the sibling exmods checkout is absent");
    return golden!;
  }

  private static BlockIndex IndexOf(string golden) =>
    BlockIndex.Build(BlockIndex.DefaultRoots(golden));

  private static LayoutError Refused(string golden, Offset at, string code) {
    Layout layout = Layout.Load(golden);
    BlockIndex index = IndexOf(golden);
    return Assert.Throws<LayoutError>(() =>
      AsBuilt.Attach(layout, index, [(at, code)])
    );
  }

  [SkippableFact]
  public void Attach_sets_the_code_and_the_side_segment_of_its_cell_only() {
    string golden = Golden();
    Layout layout = Layout.Load(golden);
    Layout attached = AsBuilt.Attach(
      layout,
      IndexOf(golden),
      [(Arch, WorkArch)]
    );

    Cell cell = attached.Cells.Single(c => c.Finished != null);
    Assert.Equal(Arch, new Offset(cell.X, cell.Y, cell.Z));
    Assert.Equal(WorkArch, cell.Finished);
    Assert.Equal(3, cell.SideSegment);
    Assert.Equal(layout.Cells.Count, attached.Cells.Count);
  }

  [SkippableFact]
  public void Compose_draws_the_maps_code_at_its_cell() {
    string golden = Golden();
    Layout layout = Layout.Load(golden);
    BlockIndex index = IndexOf(golden);
    Layout attached = AsBuilt.Attach(
      layout,
      index,
      layout
        .Cells.Where(c => c.Number == 3)
        .Select(c => (new Offset(c.X, c.Y, c.Z), WorkArch))
    );
    var named = new Dictionary<int, string>(layout.Numbers) { [3] = WorkArch };
    var expectedLayout = new Layout(
      layout.Cells,
      named,
      layout.Fillers,
      layout.Facings,
      layout.Connectors,
      layout.Roles,
      layout.Anchor,
      layout.Principal
    );

    (JObject expected, _) = Schematic.Compose(expectedLayout, index);
    (JObject actual, _) = Schematic.Compose(attached, index);
    (JObject plain, _) = Schematic.Compose(layout, index);

    Assert.True(JToken.DeepEquals(expected, actual));
    Assert.False(JToken.DeepEquals(plain, actual));
  }

  [SkippableFact]
  public void Rotated_moves_the_cell_and_turns_its_side_letter() {
    string golden = Golden();
    Layout attached = AsBuilt.Attach(
      Layout.Load(golden),
      IndexOf(golden),
      [(Arch, WorkArch)]
    );

    Cell cell = attached.Rotated(90).Cells.Single(c => c.Finished != null);

    Offset moved = Layout.RotateOffset(Arch, 90);
    Assert.Equal(moved, new Offset(cell.X, cell.Y, cell.Z));
    Assert.Equal("iiex:bandedbrick-workarch-tier1-w", cell.Finished);
    Assert.Equal(
      "iiex:bandedbrick-workarch-tier1-s",
      attached
        .Rotated(90)
        .Rotated(90)
        .Cells.Single(c => c.Finished != null)
        .Finished
    );
  }

  [Fact]
  public void Rotated_leaves_a_sideless_code_unchanged() {
    var layout = new Layout(
      [new Cell(1, 0, 0, 1, "game:refractorybricks-good-tier1")],
      new Dictionary<int, string> { [1] = "game:refractorybricks-good-*" },
      [],
      new Dictionary<string, IReadOnlyList<int>>(),
      new Dictionary<string, IReadOnlyList<Offset>>(),
      new Dictionary<string, IReadOnlyList<Offset>>()
    );

    Cell cell = layout.Rotated(90).Cells.Single();

    Assert.Equal("game:refractorybricks-good-tier1", cell.Finished);
    Assert.Equal(new Offset(0, 0, -1), new Offset(cell.X, cell.Y, cell.Z));
  }

  [SkippableFact]
  public void An_entry_at_a_cell_outside_the_layout_is_refused_naming_it() {
    LayoutError e = Refused(Golden(), new Offset(40, 0, 0), WorkArch);
    Assert.Contains("(40, 0, 0)", e.Message);
    Assert.Contains("no cell", e.Message);
  }

  [SkippableFact]
  public void A_code_the_cells_selector_does_not_admit_is_refused() {
    LayoutError e = Refused(Golden(), Lining, "game:rock-granite");
    Assert.Contains("does not admit", e.Message);
  }

  [SkippableFact]
  public void A_code_no_block_resolves_is_refused() {
    LayoutError e = Refused(Golden(), Stack, "iiex:bandedbrick-face-nosuch-n");
    Assert.Contains("no such block", e.Message);
  }

  [SkippableFact]
  public void The_as_built_flag_draws_the_map_and_its_absence_draws_the_selectors() {
    string golden = Golden();
    string dir = Path.Combine(
      Path.GetTempPath(),
      "exlib-shapes-" + System.Guid.NewGuid().ToString("N")
    );
    Directory.CreateDirectory(dir);
    string map = Path.Combine(dir, "map.json");
    File.WriteAllText(
      map,
      new JObject {
        ["cells"] = new JArray(
          new JObject {
            ["x"] = Arch.X,
            ["y"] = Arch.Y,
            ["z"] = Arch.Z,
            ["code"] = WorkArch,
          }
        ),
      }.ToString()
    );

    int with = Program.RunSchematic([
      golden,
      "--out",
      Path.Combine(dir, "a"),
      "--views",
      "iso",
      "--as-built",
      map,
    ]);
    int without = Program.RunSchematic([
      golden,
      "--out",
      Path.Combine(dir, "b"),
      "--views",
      "iso",
    ]);

    Assert.Equal(0, with);
    Assert.Equal(0, without);
    Assert.False(
      File.ReadAllBytes(Path.Combine(dir, "a", "blastcore-iso.png"))
        .SequenceEqual(
          File.ReadAllBytes(Path.Combine(dir, "b", "blastcore-iso.png"))
        )
    );
  }

  private static string RealMap() {
    string? map = FixturePath.Workspace(
      "exmods/mods/iiex/tests/asbuilt/blastcore.json"
    );
    Skip.If(map is null, "the sibling exmods checkout lacks the as-built map");
    return map!;
  }

  private static JObject ManifestOf(string golden, params string[] extra) {
    string dir = Path.Combine(
      Path.GetTempPath(),
      "exlib-shapes-" + System.Guid.NewGuid().ToString("N")
    );
    Assert.Equal(
      0,
      Program.RunSchematic([golden, "--out", dir, "--views", "plan", .. extra])
    );
    return (JObject)
      JToken.Parse(File.ReadAllText(Path.Combine(dir, "blastcore.json")));
  }

  [SkippableFact]
  public void The_manifest_counts_the_maps_cells_in_total_and_per_legend_row() {
    string golden = Golden();
    string map = RealMap();
    int entries = AsBuilt.Read(map).Count;

    JObject m = ManifestOf(golden, "--as-built", map);

    Assert.Equal(Path.GetFileName(map), (string?)m["asBuilt"]!["source"]);
    Assert.Equal(entries, (int)m["asBuilt"]!["cells"]!);
    int summed = m["legend"]!.Sum(row =>
      row["finished"] is JObject f ? f.Properties().Sum(p => (int)p.Value) : 0
    );
    Assert.Equal(entries, summed);
    Assert.All(
      m["legend"]!.Where(row => row["finished"] != null),
      row =>
        Assert.Equal(
          row["finished"]!
            .Children<JProperty>()
            .Select(p => p.Name)
            .OrderBy(n => n, System.StringComparer.Ordinal),
          row["finished"]!.Children<JProperty>().Select(p => p.Name)
        )
    );
  }

  [SkippableFact]
  public void Without_the_flag_the_manifest_has_no_as_built_fields() {
    JObject m = ManifestOf(Golden());

    Assert.Null(m["asBuilt"]);
    Assert.All(m["legend"]!, row => Assert.Null(row["finished"]));
  }

  [SkippableFact]
  public void The_plan_svg_is_the_same_string_with_and_without_the_map() {
    string golden = Golden();
    Layout plain = Layout.Load(golden);
    Layout attached = AsBuilt.Attach(
      plain,
      IndexOf(golden),
      AsBuilt.Read(RealMap())
    );

    foreach (int y in plain.Layers())
      Assert.Equal(
        Schematic.PlanSvg(plain, y, Schematic.LegendColors(plain)),
        Schematic.PlanSvg(attached, y, Schematic.LegendColors(attached))
      );
  }

  [SkippableFact]
  public void The_dump_has_one_asbuilt_line_per_entry_at_every_angle() {
    string golden = Golden();
    BlockIndex index = IndexOf(golden);
    var entries = AsBuilt.Read(RealMap());
    Layout attached = AsBuilt.Attach(Layout.Load(golden), index, entries);

    foreach (int angle in new[] { 0, 90, 180, 270 }) {
      List<string> lines = ResolverDumpTests
        .AsBuiltLines(attached.Rotated(angle), index, entries, angle)
        .ToList();

      Assert.Equal(entries.Count, lines.Count);
      Assert.All(lines, l => Assert.StartsWith("asbuilt ", l));
      Offset first = Layout.RotateOffset(entries[0].At, angle);
      Assert.StartsWith($"asbuilt {first.X} {first.Y} {first.Z} ", lines[0]);
      Assert.Contains(
        $" {entries[0].Code[..entries[0].Code.LastIndexOf('-')]}-",
        lines[0]
      );
    }
  }
}
