using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Covers the per-cell facing of a facing-wildcard legend row on the blastcore golden's
/// two tuyeres and on small layouts of its own, and the pipe stub each tuyere cell carries.</summary>
public class CellFacingsTests {
  private const string Tuyere = "iiex:furnace-tuyere-*";
  private static readonly Offset North = new(0, 2, -1);
  private static readonly Offset South = new(0, 2, 1);

  private static string Golden() {
    string? golden = FixturePath.Workspace(
      "exmods/mods/iiex/tests/goldens/iiex/blocktypes/furnace/blastcore.json"
    );
    Skip.If(golden is null, "the sibling exmods checkout is absent");
    return golden!;
  }

  private static BlockIndex IndexOf(string golden) =>
    BlockIndex.Build(BlockIndex.DefaultRoots(golden));

  private static int IndexAt(Layout layout, Offset at) =>
    layout
      .Cells.ToList()
      .FindIndex(c => c.X == at.X && c.Y == at.Y && c.Z == at.Z);

  private static Layout Small(
    string selector,
    IEnumerable<Cell> cells,
    Dictionary<string, IReadOnlyList<int>>? facings = null,
    IReadOnlyList<Offset>? tuyereCells = null
  ) =>
    new(
      [.. cells],
      new Dictionary<int, string> { [1] = selector, [2] = "game:air" },
      [],
      facings ?? new Dictionary<string, IReadOnlyList<int>>(),
      new Dictionary<string, IReadOnlyList<Offset>>(),
      new Dictionary<string, IReadOnlyList<Offset>> {
        ["Tuyere"] = tuyereCells ?? [],
      }
    );

  private static List<JObject> Stubs(JObject raw) =>
    [
      .. ((JArray)raw["elements"]!)
        .Cast<JObject>()
        .Where(e => ((string)e["name"]!).Contains("stub")),
    ];

  [SkippableFact]
  public void The_two_blastcore_tuyeres_face_away_from_the_shaft() {
    string golden = Golden();
    Layout layout = Layout.Load(golden);

    CellFacings facings = CellFacings.Of(layout, IndexOf(golden));

    Assert.Equal(2, facings.Codes.Count);
    Assert.Equal(
      "iiex:furnace-tuyere-n",
      facings.Codes[IndexAt(layout, North)]
    );
    Assert.Equal(
      "iiex:furnace-tuyere-s",
      facings.Codes[IndexAt(layout, South)]
    );
    Assert.Empty(facings.Warnings);
  }

  [SkippableFact]
  public void A_turned_layout_turns_the_tuyere_facings() {
    string golden = Golden();
    BlockIndex index = IndexOf(golden);
    Layout turned = Layout.Load(golden).Rotated(90);

    CellFacings facings = CellFacings.Of(turned, index);

    foreach ((Offset at, char side) in new[] { (North, 'n'), (South, 's') }) {
      Offset moved = Layout.RotateOffset(at, 90);
      Assert.Equal(
        Layout.RotateSideWord(side.ToString(), 90),
        facings.Sides[IndexAt(turned, moved)].ToString()
      );
    }
  }

  [SkippableFact]
  public void Each_tuyere_cell_carries_four_stub_bars_on_its_outer_face_and_no_other_cell_does() {
    string golden = Golden();
    Layout layout = Layout.Load(golden);

    (JObject raw, _) = Schematic.Compose(layout, IndexOf(golden));

    List<JObject> stubs = Stubs(raw);
    Assert.Equal(8, stubs.Count);
    foreach ((Offset at, bool north) in new[] { (North, true), (South, false) }) {
      string owner = $"c{IndexAt(layout, at)}stub";
      List<JObject> mine =
      [
        .. stubs.Where(e => ((string)e["name"]!).StartsWith(owner)),
      ];
      Assert.Equal(4, mine.Count);
      Assert.All(
        mine,
        e => {
          double lo = (double)e["from"]![2]!;
          double hi = (double)e["to"]![2]!;
          if (north)
            Assert.True(hi <= at.Z * 16, $"{e["name"]} reaches into the cell");
          else
            Assert.True(
              lo >= (at.Z + 1) * 16,
              $"{e["name"]} reaches into the cell"
            );
          Assert.Equal(8, hi - lo);
        }
      );
    }
  }

  [SkippableFact]
  public void The_manifest_names_the_faced_codes_of_the_tuyere_row() {
    string golden = Golden();
    Layout layout = Layout.Load(golden);
    BlockIndex index = IndexOf(golden);

    JObject m = Schematic.Manifest(
      layout,
      Schematic.LegendColors(layout),
      [],
      index: index
    );

    JToken row = m["legend"]!.Single(r => (string)r["selector"]! == Tuyere);
    Assert.Equal(1, (int)row["faced"]!["iiex:furnace-tuyere-n"]!);
    Assert.Equal(1, (int)row["faced"]!["iiex:furnace-tuyere-s"]!);
    Assert.Single(m["legend"]!.Where(r => r["faced"] != null));
  }

  [SkippableFact]
  public void A_cell_with_no_open_neighbour_keeps_its_pick_and_is_warned() {
    string golden = Golden();
    Layout layout = Small(Tuyere, [new Cell(0, 0, 0, 1)]);

    CellFacings facings = CellFacings.Of(layout, IndexOf(golden));

    Assert.Empty(facings.Codes);
    Assert.Contains("0 open neighbours", Assert.Single(facings.Warnings));
    JObject m = Schematic.Manifest(
      layout,
      Schematic.LegendColors(layout),
      [],
      index: IndexOf(golden)
    );
    Assert.Contains(
      m["warnings"]!,
      w => ((string)w!).Contains("0 open neighbours")
    );
    Assert.All(m["legend"]!, row => Assert.Null(row["faced"]));
  }

  [SkippableFact]
  public void A_cell_between_two_open_neighbours_keeps_its_pick_and_is_warned() {
    string golden = Golden();
    Layout layout = Small(
      Tuyere,
      [new Cell(0, 0, 0, 1), new Cell(0, 0, -1, 2), new Cell(0, 0, 1, 2)]
    );

    CellFacings facings = CellFacings.Of(layout, IndexOf(golden));

    Assert.Empty(facings.Codes);
    Assert.Contains("2 open neighbours", Assert.Single(facings.Warnings));
  }

  [SkippableFact]
  public void A_wildcard_that_is_not_the_side_group_is_left_alone() {
    string golden = Golden();
    Layout layout = Small(
      "iiex:bandedbrick-*",
      [new Cell(0, 0, 0, 1), new Cell(0, 0, 1, 2)]
    );

    CellFacings facings = CellFacings.Of(layout, IndexOf(golden));

    Assert.Empty(facings.Codes);
    Assert.Empty(facings.Warnings);
  }

  [SkippableFact]
  public void An_oriented_selector_is_left_alone() {
    string golden = Golden();
    Layout oriented = Small(
      Tuyere,
      [new Cell(0, 0, 0, 1), new Cell(0, 0, 1, 2)],
      new Dictionary<string, IReadOnlyList<int>> { [Tuyere] = [2] }
    );

    Assert.Empty(CellFacings.Of(oriented, IndexOf(golden)).Codes);
  }

  [SkippableFact]
  public void An_as_built_cell_is_left_alone() {
    string golden = Golden();
    Layout finished = Small(
      Tuyere,
      [new Cell(0, 0, 0, 1, "iiex:furnace-tuyere-e", 2), new Cell(0, 0, 1, 2)]
    );

    Assert.Empty(CellFacings.Of(finished, IndexOf(golden)).Codes);
  }

  [SkippableFact]
  public void An_east_facing_tuyere_stub_lies_beyond_its_east_face() {
    string golden = Golden();
    Layout layout = Small(
      Tuyere,
      [new Cell(0, 0, 0, 1), new Cell(-1, 0, 0, 2)],
      tuyereCells: [new Offset(0, 0, 0)]
    );

    (JObject raw, _) = Schematic.Compose(layout, IndexOf(golden));

    List<JObject> stubs = Stubs(raw);
    Assert.Equal(4, stubs.Count);
    Assert.All(
      stubs,
      e => {
        Assert.Equal(16, (double)e["from"]![0]!);
        Assert.Equal(24, (double)e["to"]![0]!);
      }
    );
  }

  [SkippableFact]
  public void A_faced_cell_outside_the_tuyere_role_carries_no_stub() {
    string golden = Golden();
    Layout layout = Small(Tuyere, [new Cell(0, 0, 0, 1), new Cell(0, 0, 1, 2)]);

    (JObject raw, _) = Schematic.Compose(layout, IndexOf(golden));

    Assert.Empty(Stubs(raw));
  }
}
