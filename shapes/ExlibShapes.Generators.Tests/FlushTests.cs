using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;
using static ExpandedLib.Shapes.Generators.Tests.FlushFixtures;

namespace ExpandedLib.Shapes.Generators.Tests;

public class FlushTests {
  private static readonly HashSet<string> NoCut = [];

  private static FlushResult Run(
    IReadOnlyList<Leaf> leaves,
    IEnumerable<string>? cut = null,
    bool cast = false,
    IReadOnlyList<FlushPin>? pins = null,
    double? parting = null,
    PartRecord? record = null
  ) =>
    Flush.Resolve(
      leaves,
      cut == null ? NoCut : cut.ToHashSet(),
      0.125,
      cast,
      pins ?? [],
      parting,
      record
    );

  private static int Pairs(IEnumerable<Leaf> leaves) =>
    Flushcheck.Pairs(ItemShape(leaves), null, []).Found.Count;

  private static readonly Box3 Wide = Bx("A", "[0, 0, 0]", "[8, 4, 8]");
  private static readonly Box3 Riser = Bx(
    "B",
    "[4, 1, 2]",
    "[12, 4, 6]",
    "other"
  );

  [Fact]
  public void Flush_keeps_allowance_faces() {
    FlushResult r = Run(Leaves(Wide, Riser), ["A/up"]);

    FlushMove move = Assert.Single(r.Moves);
    Assert.Equal(new FlushMove("B", "sink", "up", -0.125), move);
    Assert.Equal(4, r.Leaves.Single(l => l.Name == "A").Element.To![1]);
    Assert.Equal(3.875, r.Leaves.Single(l => l.Name == "B").Element.To![1]);
    Assert.Equal(0, Pairs(r.Leaves));
  }

  [Fact]
  public void Flush_merges_two_leaves_whose_union_is_a_box() {
    FlushResult r = Run(
      Leaves(
        Bx("A", "[0, 0, 0]", "[8, 4, 8]"),
        Bx("B", "[4, 0, 0]", "[12, 4, 8]")
      )
    );

    Leaf merged = Assert.Single(r.Leaves);
    Assert.Equal("A", merged.Name);
    Assert.Equal([0, 0, 0], merged.Element.From!);
    Assert.Equal([12, 4, 8], merged.Element.To!);
    Assert.Equal(
      new FlushMove("B", "merge", "", 0, "A"),
      Assert.Single(r.Moves)
    );
  }

  [Fact]
  public void Flush_sinks_the_first_sorting_leaf_and_raises_a_bottom() {
    FlushResult sunk = Run(Leaves(Wide, Riser));
    Assert.Equal(
      new FlushMove("A", "sink", "up", -0.125),
      Assert.Single(sunk.Moves)
    );

    FlushResult raised = Run(
      Leaves(Wide, Bx("B", "[4, 0, 2]", "[12, 3, 6]", "other"))
    );
    Assert.Equal(
      new FlushMove("A", "raise", "down", -0.125),
      Assert.Single(raised.Moves)
    );
    Assert.Equal(
      0.125,
      raised.Leaves.Single(l => l.Name == "A").Element.From![1]
    );
  }

  [Fact]
  public void Flush_trims_to_the_neighbour_when_both_tops_are_cut() {
    FlushResult r = Run(
      Leaves(
        Bx("A", "[0, 0, 0]", "[8, 4, 8]"),
        Bx("B", "[6, 1, 2]", "[12, 4, 6]", "other")
      ),
      ["A/up", "B/up"]
    );

    Assert.Equal(
      new FlushMove("B", "trim", "west", -2),
      Assert.Single(r.Moves)
    );
    Assert.Equal(8, r.Leaves.Single(l => l.Name == "B").Element.From![0]);
  }

  [Fact]
  public void Flush_skips_a_move_that_breaks_the_draw() {
    Box3 inner = Bx("B", "[1, 1, 2]", "[5, 4, 6]", "other");

    FlushResult drawn = Run(Leaves(Wide, inner), parting: 4);
    Assert.Equal(
      new FlushMove("B", "sink", "up", -0.125),
      Assert.Single(drawn.Moves)
    );

    FlushResult free = Run(Leaves(Wide, inner));
    Assert.Equal("A", Assert.Single(free.Moves).Element);
  }

  [Fact]
  public void A_pin_picks_the_rule_for_its_pair() {
    FlushResult cap = Run(
      Leaves(Wide, Riser),
      cast: true,
      pins: [new FlushPin("B", "A", "cap")]
    );
    Leaf lid = cap.Leaves.Single(l => l.Name == "A_cap");
    Assert.Equal([4, 4, 2], lid.Element.From!);
    Assert.Equal([8, 4.125, 6], lid.Element.To!);
    Assert.Equal(
      new FlushMove("A_cap", "cap", "up", 0.125, null, "A"),
      Assert.Single(cap.Moves)
    );
    Assert.Equal(0, Pairs(cap.Leaves));
  }

  [Fact]
  public void A_cap_needs_a_cast_item_and_clear_faces() {
    FlushPin pin = new FlushPin("A", "B", "cap");
    Assert.Throws<CastGenException>(() =>
      Run(Leaves(Wide, Riser), pins: [pin])
    );
    Assert.Throws<CastGenException>(() =>
      Run(Leaves(Wide, Riser), ["B/up"], cast: true, pins: [pin])
    );
  }

  [Fact]
  public void Stagger_clears_the_flush_tolerance() {
    FlushResult r = Run(
      Leaves(Wide, Riser),
      pins: [new FlushPin("A", "B", "stagger")]
    );

    FlushMove move = Assert.Single(r.Moves);
    Assert.Equal("stagger", move.Rule);
    Assert.Equal(-0.002, move.Distance);
    Assert.Equal(0, Pairs(r.Leaves));
  }

  [Fact]
  public void Flush_names_the_pair_when_no_rule_resolves_it() {
    Box3 a = Bx("A", "[0, 0, 0]", "[8, 4, 8]");
    Box3 b = Bx("B", "[0, 0, 0]", "[8, 4, 8]", "other");
    string[] all =
    [
      .. new[] { "A", "B" }.SelectMany(n =>
        new[] { "north", "east", "south", "west", "up", "down" }.Select(f =>
          $"{n}/{f}"
        )
      ),
    ];

    CastGenException e = Assert.Throws<CastGenException>(() =>
      Run(Leaves(a, b), all, cast: true)
    );
    Assert.StartsWith(
      "A and B: no flush rule resolves the down faces",
      e.Message
    );
  }

  [Fact]
  public void A_pin_is_read_from_the_record_and_a_bad_rule_is_refused() {
    PartRecord rec = PartRecord.Read(
      """{ "schema": 1, "flushPins": [ { "pair": ["A", "B"], "rule": "trim" } ] }"""
    );
    Assert.Equal([new FlushPin("A", "B", "trim")], Flush.Pins(rec));
    rec.Carried["flushPins"] = JArray.Parse(
      """[ { "pair": ["A", "B"], "rule": "paint" } ]"""
    );
    Assert.Throws<CastGenException>(() => Flush.Pins(rec));
    Assert.Empty(Flush.Pins(null));
  }

  [Fact]
  public void Moves_are_written_to_the_record_and_the_gate_reads_them() {
    var a = new Box3("A", "[0, 0, 0]", "[8, 4, 8]");
    var b = new Box3("B", "[10, 1, 2]", "[16, 4, 6]", "other");
    var c = new Box3("C", "[12, 0, 0]", "[20, 4, 8]");
    PartRecord source = SourceRecord(
      """{ "id": "plane", "tool": "planer", "faces": ["A/up"] }""",
      "A",
      "B",
      "C"
    );
    List<Leaf> leaves = Allowances.Out(Leaves(a, b, c), ["A/up"], 0.125);
    var item = new PartRecord();
    FlushResult r = Run(leaves, ["A/up"], record: item);
    FlushMove sink = Assert.Single(r.Moves);
    Assert.Equal(new FlushMove("B", "sink", "up", -0.125), sink);
    Assert.Equal([sink], FlushMoves.Read(item));

    CheckInput input = new(
      ItemShape(r.Leaves),
      Source: Drawn(a, b, c),
      SourceRecord: source,
      Record: item
    );
    Assert.Equal(0, new Allowance().Run(input).ExitCode);
    Assert.Equal(
      1,
      new Allowance().Run(input with { Record = new PartRecord() }).ExitCode
    );
  }

  [Fact]
  public void The_head_bush_pair_passes_flushcheck_with_no_pair_table() {
    FlushResult r = Run(
      Leaves(
        Bx("Cube262", "[0, 0, 0]", "[2, 3, 4]", "bush"),
        Bx("Cube287", "[1, 1, 0]", "[3, 3, 4]", "bush")
      ),
      ["Cube262/west", "Cube287/east"]
    );

    Assert.NotEmpty(r.Moves);
    Assert.Equal(
      0,
      new Flushcheck().Run(new CheckInput(ItemShape(r.Leaves))).ExitCode
    );
  }

  [Fact]
  public void The_ways_and_ram_tops_pass_flushcheck_with_no_pair_table() {
    FlushResult r = Run(
      Leaves(
        Bx("Way", "[0, 0, 0]", "[8, 2, 8]"),
        Bx("TopN", "[2, 1, 0]", "[6, 2, 3]", "top"),
        Bx("TopS", "[2, 1, 5]", "[6, 2, 8]", "top")
      ),
      ["Way/up"],
      cast: true
    );

    Assert.NotEmpty(r.Moves);
    Assert.Equal(
      0,
      new Flushcheck().Run(new CheckInput(ItemShape(r.Leaves))).ExitCode
    );
  }

  private static List<Leaf> TurnedPair() {
    string text =
      "{ \"elements\": [ "
      + Fx.Cube("A", "[0, 0, 0]", "[8, 4, 8]")
      + ", "
      + Fx.Cube(
        "B",
        "[4, 1, 2]",
        "[12, 4, 6]",
        "\"rotationOrigin\": [8, 2.5, 4], \"rotationX\": 180,"
      )
      + " ] }";
    return
    [
      .. Select
        .Groups(Reader.Read(text), ["A", "B"])
        .Select(l => l with { Stage = "cast", Role = "cast" }),
    ];
  }

  [Fact]
  public void A_pair_whose_faces_meet_through_a_half_turn_is_resolved() {
    FlushResult r = Run(TurnedPair(), ["A/up"]);

    Assert.Equal(
      new FlushMove("B", "sink", "down", -0.125),
      Assert.Single(r.Moves)
    );
    Assert.Equal(0, Pairs(r.Leaves));
  }

  [SkippableFact]
  public void The_lathe_head_bush_pair_of_its_drawing_passes_flushcheck() {
    string? dir = AppContext.BaseDirectory;
    string? file = null;
    while (dir != null && file == null) {
      string f = System.IO.Path.Combine(
        dir,
        "exmods/workbench/shapes-finished/machine-tools/lathe/machine-mp-megablock-lathe-proposed.json"
      );
      if (System.IO.File.Exists(f))
        file = f;
      dir = System.IO.Path.GetDirectoryName(dir);
    }
    Skip.If(file == null, "the lathe drawing is not in this workspace");
    Shape lathe = Reader.Read(System.IO.File.ReadAllText(file!));
    List<string> paths =
    [
      .. Engine
        .Walk(lathe)
        .Select(p => p.Path)
        .Where(p => p.EndsWith("/Cube262") || p.EndsWith("/Cube287")),
    ];
    Assert.Equal(2, paths.Count);
    List<Leaf> leaves =
    [
      .. Select
        .Groups(lathe, paths)
        .Select(l => l with { Stage = "cast", Role = "cast" }),
    ];

    FlushResult r = Run(leaves);

    Assert.NotEmpty(r.Moves);
    Assert.Equal(0, Pairs(r.Leaves));
  }

  private static List<Leaf> Cast(string json, params string[] groups) =>
    [
      .. Select
        .Groups(Reader.Read(json), groups, null, null, true)
        .Select(l => l with { Stage = "cast", Role = "cast" }),
    ];

  [Fact]
  public void A_pivot_is_not_a_leaf() {
    string json = $$"""
      { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
        { "name": "Pivot", "from": [2, 2, 2], "to": [2, 2, 2], {{Fx.Faces}},
          "children": [
            {{Fx.Cube("A", "[0, 0, 0]", "[8, 4, 8]")}},
            {{Fx.Cube("B", "[4, 1, 2]", "[12, 4, 6]")}}
          ] }
      ] } ] }
      """;

    List<Leaf> leaves = Cast(json, "Root");
    FlushResult r = Run(leaves);

    Assert.Equal(["A", "B"], leaves.Select(l => l.Name));
    Assert.Equal(0, Pairs(r.Leaves));
  }

  [Fact]
  public void A_flat_element_in_a_cast_set_is_refused() {
    string json = $$"""
      { "elements": [ {{Fx.Cube("Sheet", "[0, 0, 0]", "[8, 0, 8]")}} ] }
      """;

    CastGenException e = Assert.Throws<CastGenException>(() =>
      Cast(json, "Sheet")
    );
    Assert.Contains("Sheet", e.Message);
  }

  [Fact]
  public void Ring_stave_end_staggers() {
    string json = $$"""
      { "elements": [
        {{Fx.Cube("A", "[0, 0, 0]", "[8, 4, 8]")}},
        {{Fx.Cube(
        "B",
        "[2, 1, 2]",
        "[6, 4, 6]",
        "\"rotationY\": 22.5, \"rotationOrigin\": [4, 1, 4],"
      )}}
      ] }
      """;

    FlushResult r = Run(
      Cast(json, "A", "B"),
      pins: [new FlushPin("A", "B", "stagger")]
    );

    FlushMove move = Assert.Single(r.Moves);
    Assert.Equal("stagger", move.Rule);
    Assert.Equal(0, Pairs(r.Leaves));
  }
}
