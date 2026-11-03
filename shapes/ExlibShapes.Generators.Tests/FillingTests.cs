using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;
using static ExpandedLib.Shapes.Generators.Tests.FlushFixtures;

namespace ExpandedLib.Shapes.Generators.Tests;

public class FillingTests(ITestOutputHelper output) {
  private static readonly GenContext Ctx = CoreFixtures.Ctx;

  private static Mat4d At(double x, double y, double z) =>
    Mat4d.Translate(x, y, z);

  private static PourRequest Request(
    string station,
    IReadOnlyList<Leaf> cast,
    IReadOnlyList<Mat4d> at,
    string fill = "chain",
    string order = "one",
    bool closed = false,
    Shape? pattern = null,
    IReadOnlyList<CoreSeat>? cores = null,
    IReadOnlyList<RiserAsk>? risers = null,
    double? step = null
  ) =>
    new(
      "castthing",
      station,
      closed,
      "cast-thing",
      "pattern-castthing",
      cast,
      at,
      pattern,
      cores ?? [],
      fill,
      order,
      risers,
      step
    );

  private static readonly ICheck[] Gates =
  [
    new Impression(),
    new Flood(),
    new Capacity(),
    new Fit(),
    new Runner(),
    new Pieces(),
    new Feeding(),
    new Pit(),
    new Parting(),
    new Corefit(),
  ];

  private static List<string> Failing(
    FillingResult f,
    PourRequest r,
    IReadOnlyList<GenOutput>? cores = null
  ) {
    Shape? pattern = r.PatternShape;
    var input = new CheckInput(
      f.Filling.Shape,
      Record: f.Filling.Record,
      Settings: Fx.Settings,
      Cast: ItemShape(r.CastLeaves),
      Pattern: pattern,
      Pair: f.Cope?.Shape,
      Cores: cores?.Select(c => c.Shape).ToList(),
      CoreRecords: cores?.Select(c => c.Record).ToList()
    );
    var bad = new List<string>();
    foreach (ICheck g in Gates.Where(g => Selection.Applies(g, input))) {
      CheckResult res = g.Run(input);
      if (res.ExitCode != 0)
        bad.Add(
          $"{g.Id}: {string.Join(" | ", res.Lines.Where(l => l.Contains("FAIL") || res.ExitCode == 1).Take(4))}"
        );
    }
    return bad;
  }

  private static FillingResult Make(PourRequest r) =>
    Fillings.Make(Ctx, r, "spec");

  private static List<Leaf> Block(double x, double y, double z) =>
    Leaves(Bx("Body", "[0, 0, 0]", $"[{x}, {y}, {z}]"));

  private static JObject Pour(FillingResult f) =>
    (JObject)f.Filling.Record.Carried["pour"]!;

  private static IEnumerable<string> Names(IEnumerable<Element> es) =>
    es.SelectMany(e => new[] { e.Name! }.Concat(Names(e.Children)));

  [Fact]
  public void A_piece_away_from_the_drop_is_poured_from_a_runner_and_passes_the_gates() {
    PourRequest r = Request("floor", Block(6, 4, 6), [At(13, 10, -3)]);

    FillingResult f = Make(r);

    Assert.Empty(Failing(f, r));
    JToken run = Pour(f)["feeds"]!.Single(x => (string?)x["name"] == "Runner1");
    Assert.Equal(
      [15.0, 13.5, 2.5, 17, 14, 10],
      run["boxes"]![0]!.Select(n => (double)n)
    );
    Assert.Equal(360, (double)Pour(f)["units"]!["perPiece"]!);
    Assert.Equal(
      [
        "BoxFloor",
        "BoxWallE",
        "BoxWallN1",
        "BoxWallN2",
        "BoxWallS1",
        "BoxWallS2",
        "BoxWallW",
      ],
      f.Filling.Shape.Elements.Where(e => e.Name!.StartsWith("Box"))
        .Select(e => e.Name!)
        .Order()
    );
  }

  [Fact]
  public void A_piece_of_two_bodies_gates_the_second_and_floods_it() {
    List<Leaf> kit = Leaves(
      Bx("Body", "[0, 0, 0]", "[6, 4, 6]"),
      Bx("Arm", "[0, 0, -8]", "[2, 4, 0]"),
      Bx("Apart", "[3, 0, -14]", "[6, 4, -11]")
    );
    PourRequest r = Request("floor", kit, [At(13, 10, 4)]);

    FillingResult f = Make(r);

    Assert.Empty(Failing(f, r));
    FeedLaid gate = Assert.Single(f.Feeds, x => x.Kind == "gate");
    Assert.Equal("Gate1.1", gate.Name);
    Assert.Equal(1, gate.Into);
  }

  [Fact]
  public void A_drop_over_the_piece_lays_no_runner() {
    PourRequest r = Request("floor", Block(6, 4, 6), [At(13, 10, 4)]);

    FillingResult f = Make(r);

    Assert.DoesNotContain(f.Feeds, x => x.Kind == "runner");
    Assert.Empty(Failing(f, r));
  }

  [Fact]
  public void Two_pieces_chain_a_gate_and_hold_piece_groups() {
    PourRequest r = Request(
      "floor",
      Block(6, 4, 6),
      [At(5, 10, -3), At(19, 10, -3)],
      "chain",
      "alt"
    );

    FillingResult f = Make(r);

    Assert.Empty(Failing(f, r));
    FeedLaid gate = Assert.Single(f.Feeds, x => x.Kind == "gate");
    Assert.Equal(2, gate.Into);
    Assert.Equal(10.5, gate.Boxes.Min(b => b.Lo.X), 1e-9);
    Assert.Equal(
      ["Bedded", "Level"],
      f.Filling.Shape.Elements.Single(e => e.Name == "Piece2")
        .Children.Select(c => c.Name!)
        .Order()
    );
    Assert.Equal(2, (Pour(f)["pieces"] as JArray)!.Count);
  }

  [Fact]
  public void A_level_is_cut_only_by_feeds_that_enter_no_later_piece() {
    PourRequest r = Request(
      "floor",
      Block(6, 4, 6),
      [At(5, 10, -3), At(19, 10, -1)],
      "chain",
      "alt"
    );

    FillingResult f = Make(r);

    Element one = f.Filling.Shape.Elements.Single(e => e.Name == "Piece1");
    var point = new Vec3(11, 13.75, 1.25);
    Assert.True(
      Covers(one.Children.Single(c => c.Name == "Level"), point),
      "Level1 is cut by the gate into piece 2"
    );
    Assert.False(Covers(one.Children.Single(c => c.Name == "Bedded"), point));
    Assert.Empty(Failing(f, r));
  }

  private static bool Covers(Element group, Vec3 p) =>
    group.Children.Any(c =>
      Enumerable.Range(0, 3).All(a => p[a] > c.From![a] && p[a] < c.To![a])
    );

  [Fact]
  public void Pairs_take_their_gates_from_the_main_runner() {
    PourRequest r = Request(
      "floor",
      Block(6, 4, 6),
      [At(5, 10, -3), At(19, 10, -3)],
      "pairs",
      "pairs2"
    );

    FillingResult f = Make(r);

    FeedLaid gate = Assert.Single(f.Feeds, x => x.Kind == "gate");
    Assert.Equal(16.5, gate.Boxes.Min(b => b.Lo.X), 1e-9);
  }

  [Fact]
  public void A_piece_beside_the_runner_is_reached_at_a_right_angle() {
    PourRequest r = Request("floor", Block(6, 4, 6), [At(5, 10, -3)]);

    FillingResult f = Make(r);

    Assert.Empty(Failing(f, r));
    Assert.Equal(2, f.Feeds.Single(x => x.Kind == "runner").Boxes.Count);
  }

  [Fact]
  public void A_blocked_path_is_refused() {
    PourRequest r = Request(
      "floor",
      Block(6, 4, 6),
      [At(13, 10, -3), At(13, 10, 4)]
    );

    CastGenException e = Assert.Throws<CastGenException>(() => Make(r));

    Assert.Contains("no runner reaches Piece1", e.Message);
  }

  [Fact]
  public void A_piece_short_of_the_runner_is_refused() {
    PourRequest r = Request("floor", Block(6, 4, 6), [At(13, 6, -3)]);

    CastGenException e = Assert.Throws<CastGenException>(() => Make(r));

    Assert.Contains("Piece1 has no metal between y 13.5 and 14", e.Message);
  }

  [Fact]
  public void Pieces_with_zones_that_overlap_are_refused() {
    PourRequest r = Request(
      "floor",
      Block(6, 4, 6),
      [At(9, 10, -3), At(15.5, 10, -3)],
      "chain",
      "one"
    );

    CastGenException e = Assert.Throws<CastGenException>(() => Make(r));

    Assert.Contains("zones of Piece1 and Piece2 overlap", e.Message);
  }

  [Fact]
  public void A_closed_pour_of_two_pieces_is_refused() {
    PourRequest r = Request(
      "long",
      Block(6, 6, 6),
      [At(13, 4, -3), At(13, 4, -20)],
      closed: true
    );

    Assert.Contains(
      "holds one piece",
      Assert.Throws<CastGenException>(() => Make(r)).Message
    );
  }

  [Fact]
  public void An_unknown_station_and_an_empty_pour_are_refused() {
    Assert.Contains(
      "no station \"nowhere\"",
      Assert
        .Throws<CastGenException>(() =>
          Make(Request("nowhere", Block(6, 4, 6), [At(13, 10, -3)]))
        )
        .Message
    );
    Assert.Contains(
      "no piece",
      Assert
        .Throws<CastGenException>(() =>
          Make(Request("floor", Block(6, 4, 6), []))
        )
        .Message
    );
  }

  private static GenContext WithStation(string row) {
    string text = File.ReadAllText(
      Path.Combine(AppContext.BaseDirectory, "fixtures", "settings.json")
    );
    text = text.Replace(
      "\"stations\": {",
      "\"stations\": { \"odd\": { " + row + " },"
    );
    return new GenContext(
      Settings.Read(text),
      (shape, sha) => throw new InvalidOperationException(shape),
      null
    );
  }

  private const string Sand =
    "\"sandFrom\": [2, 2, -14], \"sandTo\": [30, 14, 14], \"floor\": 2, \"margin\": 1.0, \"ladleUnits\": 2.5, \"risers\": {}";

  [Theory]
  [InlineData("\"runnerDepth\": 0.5, \"gateAcross\": 1", false, "has no drop")]
  [InlineData(
    "\"gateAcross\": 1, \"drop\": [15, 13.5, 9, 17, 14, 10]",
    false,
    "no runnerDepth"
  )]
  [InlineData(
    "\"runnerDepth\": 0.5, \"drop\": [15, 13.5, 9, 17, 14, 10]",
    false,
    "no gateAcross"
  )]
  [InlineData(
    "\"runnerDepth\": 0.5, \"gateAcross\": 1, \"parting\": 8",
    true,
    "has no sprue"
  )]
  [InlineData(
    "\"runnerDepth\": 0.5, \"gateAcross\": 1, \"sprue\": [15, 8, 17, 10]",
    true,
    "needs a parting"
  )]
  public void A_station_missing_one_field_of_the_rule_is_refused(
    string row,
    bool closed,
    string message
  ) {
    PourRequest r = Request(
      "odd",
      Block(4, 4, 4),
      [At(2, 9, 2)],
      closed: closed
    );

    Assert.Contains(
      message,
      Assert
        .Throws<CastGenException>(() =>
          Fillings.Make(WithStation(Sand + ", " + row), r, "spec")
        )
        .Message
    );
  }

  [Fact]
  public void An_open_runner_crosses_the_stations_runner_x_from_the_drops_far_edge() {
    PourRequest r = Request("odd", Block(6, 4, 6), [At(13, 10, -3)]);

    FillingResult f = Fillings.Make(
      WithStation(
        Sand
          + ", \"runnerX\": [14, 18], \"runnerDepth\": 0.5, \"gateAcross\": 1, \"drop\": [15, 13.5, 9, 17, 14, 10]"
      ),
      r,
      "spec"
    );

    JToken run = Pour(f)["feeds"]!.Single(x => (string?)x["name"] == "Runner1");
    Assert.Equal(
      [14.0, 13.5, 2.5, 18, 14, 10],
      run["boxes"]![0]!.Select(n => (double)n)
    );
  }

  [Fact]
  public void A_piece_in_the_runner_box_is_refused() {
    PourRequest r = Request("floor", Block(6, 4, 6), [At(13, 10, 5)]);

    Assert.Contains(
      "lies in the runner box",
      Assert.Throws<CastGenException>(() => Make(r)).Message
    );
  }

  [Fact]
  public void A_head_over_16_across_is_fed_whole() {
    List<Leaf> cast = Leaves(Bx("Body", "[0, 0, 0]", "[20, 12.5, 4]"));
    PatternResult p = Patterns.Make(
      Ctx,
      "pattern-castthing",
      cast,
      [],
      null,
      "spec",
      1.5
    );

    FillingResult f = Make(
      Request("pit", cast, [At(6, 0, -2)], pattern: p.Pattern.Shape)
    );

    Assert.Equal(20 * 4 * 1.5 * 2.5, (double)Pour(f)["units"]!["share"]!);
  }

  private static (Shape Pattern, List<Leaf> Cast) Column() {
    List<Leaf> cast =
    [
      .. Leaves(
        Bx("Low", "[0, 0, 0]", "[4, 16, 4]"),
        Bx("High", "[0, 16, 0]", "[4, 25.5, 4]")
      ),
    ];
    PatternResult p = Patterns.Make(
      Ctx,
      "pattern-castthing",
      cast,
      [],
      null,
      "spec",
      1.5
    );
    return (p.Pattern.Shape, cast);
  }

  [Fact]
  public void Head_stands_over_the_top_of_the_pattern() {
    (Shape pattern, _) = Column();

    Element head = pattern.Elements.Single(e => e.Name == "Head");

    Assert.Equal([0.0, 25.5, 0], head.From!);
    Assert.Equal([4.0, 27, 4], head.To!);
  }

  [Fact]
  public void A_pit_piece_gets_its_head_and_passes_the_gates() {
    (Shape pattern, List<Leaf> cast) = Column();
    PourRequest r = Request("pit", cast, [At(14, -13, -2)], pattern: pattern);

    FillingResult f = Make(r);

    Assert.Empty(Failing(f, r));
    FeedLaid head = Assert.Single(f.Feeds, x => x.Kind == "head");
    Assert.Equal(["Head"], head.Pattern);
    Assert.Equal(1, head.Into);
    Assert.True((double)Pour(f)["units"]!["share"]! > 0);
  }

  [Fact]
  public void A_head_station_without_a_head_in_the_pattern_is_refused() {
    (_, List<Leaf> cast) = Column();
    PourRequest r = Request("pit", cast, [At(14, -13, -2)]);

    Assert.Contains(
      "no Head",
      Assert.Throws<CastGenException>(() => Make(r)).Message
    );
  }

  [Fact]
  public void A_closed_pour_has_a_drag_a_cope_a_sprue_and_a_riser() {
    PourRequest r = Request(
      "long",
      Block(6, 6, 6),
      [At(13, 4, -3)],
      closed: true,
      risers: [new RiserAsk("bed", [3, 6, 3])]
    );

    FillingResult f = Make(r);

    Assert.Empty(Failing(f, r));
    Assert.NotNull(f.Cope);
    Assert.Equal("longflask-cope-filling-castthing", f.Cope!.Name);
    Assert.Equal("longflask-filling-castthing", f.Filling.Name);
    Assert.Equal("cope", (string?)f.Cope.Record.Carried["pour"]!["half"]);
    Assert.Contains("BoxFloor", Names(f.Cope.Shape.Elements));
    Assert.DoesNotContain("BoxFloor", Names(f.Filling.Shape.Elements));
    Assert.Equal(
      ["Riser1", "Runner1", "Sprue"],
      f.Feeds.Select(x => x.Name).Order()
    );
    Assert.All(
      f.Filling.Shape.Elements.Where(e => e.Name!.StartsWith("Sand")),
      e => Assert.True(e.To![1] <= 8 + 1e-9)
    );
    Assert.All(
      f.Cope.Shape.Elements.Where(e => e.Name!.StartsWith("Sand")),
      e => Assert.True(e.From![1] >= 8 - 1e-9)
    );
  }

  [Fact]
  public void A_riser_kind_the_station_lacks_is_refused() {
    PourRequest r = Request(
      "long",
      Block(6, 6, 6),
      [At(13, 4, -3)],
      closed: true,
      risers: [new RiserAsk("anvil", [3, 6, 3])]
    );

    Assert.Contains(
      "no riser section \"anvil\"",
      Assert.Throws<CastGenException>(() => Make(r)).Message
    );
  }

  [Fact]
  public void A_core_sits_in_its_group_and_the_sand_leaves_it() {
    List<Leaf> cast =
    [
      .. Leaves(
        Bx("A", "[0, 0, 0]", "[16, 4, 2]"),
        Bx("B", "[0, 0, 5]", "[16, 4, 6]"),
        Bx("D", "[0, 0, 2]", "[16, 1, 5]")
      ),
    ];
    var feature = new CoreHandoff(
      "slot",
      Leaves(Bx("Slot", "[0, 1, 2]", "[16, 4, 5]")),
      null
    );
    GenOutput core = Cores.Make(
      Ctx,
      "drysandcore-castthing",
      "cast-thing",
      feature,
      "spec"
    );
    List<Leaf> coreLeaves =
    [
      .. core.Shape.Elements.Select(e => new Leaf(
        e.Name!,
        e.Name!,
        e.Name!,
        e
      )),
    ];
    PourRequest r = Request(
      "floor",
      cast,
      [At(7, 10, -3)],
      cores: [new CoreSeat("drysandcore-castthing", coreLeaves, At(7, 10, -3))]
    );

    FillingResult f = Make(r);

    Assert.Empty(Failing(f, r, [core]));
    Assert.All(
      f.Filling.Shape.Elements.Where(e => e.Name!.StartsWith("Sand")),
      e =>
        Assert.False(
          e.From![0] < 23 - 1e-9
            && e.To![0] > 7 + 1e-9
            && e.From![1] < 14 - 1e-9
            && e.To![1] > 11 + 1e-9
            && e.From![2] < 2 - 1e-9
            && e.To![2] > -1 + 1e-9,
          $"{e.Name} stands in the core"
        )
    );
    Element group = f.Filling.Shape.Elements.Single(e => e.Name == "Core1");
    Assert.Equal(coreLeaves.Count, group.Children.Count);
    Assert.Equal(
      "drysandcore-castthing",
      (string?)Pour(f)["cores"]![0]!["item"]
    );
  }

  [Fact]
  public void Capacity_units_follow_the_metal_volume() {
    PourRequest r = Request("floor", Block(6, 2, 4), [At(13, 12, -2)]);

    FillingResult f = Make(r);

    Assert.Equal(6 * 2 * 4 * 2.5, (double)Pour(f)["units"]!["perPiece"]!);
    Assert.Equal(0, (double)Pour(f)["units"]!["share"]!);
  }

  [Fact]
  public void Fuzzed_fillings_have_no_impression_failure() {
    var failures = new List<string>();
    for (int seed = 0; seed < 200; seed++) {
      var rng = new Random(seed);
      double Q(double lo, double hi) =>
        lo + Math.Floor(rng.NextDouble() * (hi - lo) * 16) / 16;
      double z1 = Q(1, 7),
        z0 = z1 - Q(2, 6),
        x0 = Q(10, 14.5),
        x1 = Q(17.5, 22),
        h = Q(2, 4);
      var boxes = new List<Box3>
      {
        Bx("Main", $"[{x0}, {14 - h}, {z0}]", $"[{x1}, 14, {z1}]"),
      };
      for (int i = 0; i < rng.Next(1, 4); i++) {
        double ex0 = Q(3, 24),
          ez0 = Q(-12, 4),
          ey0 = Q(3, 10);
        boxes.Add(
          Bx(
            $"Extra{i}",
            $"[{ex0}, {ey0}, {ez0}]",
            $"[{ex0 + Q(0.5, 5)}, {Math.Min(13, ey0 + Q(0.5, 3))}, {ez0 + Q(0.5, 6)}]"
          )
        );
      }
      List<Leaf> leaves = Leaves([.. boxes]);
      for (int i = 0; i < rng.Next(0, 3); i++) {
        double tx = Q(3, 21),
          tz = Q(-12, 3),
          ty = Q(3, 10);
        double angle = rng.Next(2) == 0 ? 22.5 : 45;
        Element e = Reader
          .Read(
            $$"""
            { "elements": [ { "name": "Turn{{i}}", "from": [{{tx}}, {{ty}}, {{tz}}], "to": [{{tx
              + Q(1, 5)}}, {{Math.Min(13, ty + Q(0.5, 3))}}, {{tz + Q(1, 5)}}],
              "rotationY": {{angle}}, "rotationOrigin": [{{tx + 1}}, {{ty}}, {{tz + 1}}],
              "faces": { "up": { "texture": "#c" } } } ] }
            """
          )
          .Elements[0];
        leaves.Add(
          new Leaf($"Turn{i}", $"Turn{i}", $"Turn{i}", e, "cast", "cast")
        );
      }
      PourRequest r = Request("floor", leaves, [Mat4d.Identity]);
      FillingResult f = Make(r);
      var input = new CheckInput(
        f.Filling.Shape,
        Record: f.Filling.Record,
        Settings: Fx.Settings,
        Cast: ItemShape(r.CastLeaves)
      );
      if (new Impression().Run(input).ExitCode != 0)
        failures.Add($"seed {seed}");
    }
    Assert.Empty(failures);
  }

  [Fact]
  public void A_turned_column_is_filled_within_the_band_and_counted_at_both_steps() {
    Element e = Reader
      .Read(
        """
        { "elements": [ { "name": "Col", "from": [-2, 0, -2], "to": [2, 25.5, 2], "rotationY": 45,
          "rotationOrigin": [0, 0, 0], "faces": { "up": { "texture": "#c" } } } ] }
        """
      )
      .Elements[0];
    List<Leaf> cast = [new Leaf("Col", "Col", "Col", e, "cast", "cast")];
    var patternShape = new Shape();
    Element head = Reader
      .Read(
        """{ "elements": [ { "name": "Head", "from": [-2, 25.5, -2], "to": [2, 27, 2], "rotationY": 45, "rotationOrigin": [0, 0, 0], "faces": { "up": { "texture": "#c" } } } ] }"""
      )
      .Elements[0];
    patternShape.Elements.Add(head);

    int Count(double? step) =>
      Make(
        Request(
          "pit",
          cast,
          [At(16, -13, 0)],
          pattern: patternShape,
          step: step
        )
      ).Filling.Shape.Elements.Count;

    PourRequest pour = Request(
      "pit",
      cast,
      [At(16, -13, 0)],
      pattern: patternShape
    );
    FillingResult f = Make(pour);
    CheckResult impression = new Impression().Run(
      new CheckInput(
        f.Filling.Shape,
        Record: f.Filling.Record,
        Settings: Fx.Settings,
        Cast: ItemShape(cast),
        Pattern: patternShape
      )
    );
    output.WriteLine(
      $"turned pit filling elements: {Count(null)} at 0.125, {Count(Fillings.Fine)} at 1/32"
    );

    Assert.Equal(0, impression.ExitCode);
    Assert.True(Count(null) > 0);
  }

  private static string? ShaperStandard() {
    for (
      DirectoryInfo? dir = new(AppContext.BaseDirectory);
      dir != null;
      dir = dir.Parent
    ) {
      string file = Path.Combine(
        dir.FullName,
        "exmods",
        "workbench",
        "shapes-finished",
        "items",
        "cast-shaper-standard-proposed.json"
      );
      if (File.Exists(file))
        return file;
    }
    return null;
  }

  [SkippableTheory]
  [InlineData("0.125", null)]
  [InlineData("1/32", 1.0 / 32)]
  public void The_shaper_standard_pit_filling_is_counted_at_each_step(
    string label,
    double? step
  ) {
    string? file = ShaperStandard();
    Skip.If(
      file is null,
      "the sibling exmods checkout with cast-shaperstandard is absent"
    );
    Shape drawn = Reader.Read(File.ReadAllText(file!));
    List<Leaf> cast = Select.Groups(drawn, ["Standard"]);
    Box bounds = Occupancy.Of(cast).Bounds;
    Mat4d at = At(16 - bounds.Centre.X, 12.5 - bounds.Hi.Y, -bounds.Centre.Z);
    double top = cast.Max(l => l.Box.Hi.Y);
    List<Box> reach =
    [
      .. cast.Where(l => l.Box.Hi.Y >= top - 1e-9).Select(l => l.Box),
    ];
    Shape pattern = Reader.Read(
      $$"""{ "elements": [ { "name": "Head", "from": [{{reach.Min(b => b.Lo.X)}}, {{top}}, {{reach.Min(b => b.Lo.Z)}}], "to": [{{reach.Max(b => b.Hi.X)}}, {{top + 1.5}}, {{reach.Max(b => b.Hi.Z)}}], "faces": { "up": { "texture": "#c" } } } ] }"""
    );
    PourRequest r = Request("pit", cast, [at], pattern: pattern, step: step);

    FillingResult f = Make(r);
    CheckResult g = new Impression().Run(
      new CheckInput(
        f.Filling.Shape,
        Record: f.Filling.Record,
        Settings: Fx.Settings,
        Cast: ItemShape(cast),
        Pattern: pattern
      )
    );
    string gate = string.Join(
      " ",
      g.Lines.Where(l => l.StartsWith("in:") || l.StartsWith("open:"))
        .Select(l =>
          l.Substring(0, l.IndexOf(" vx3", StringComparison.Ordinal) + 4)
        )
    );
    output.WriteLine(
      $"shaper standard pit filling at {label}: {f.Filling.Shape.Elements.Count} elements, impression exit {g.ExitCode}, {gate}"
    );

    Assert.NotEmpty(f.Filling.Shape.Elements);
  }

  [SkippableFact]
  public void The_shaper_standard_pattern_is_made_in_under_30_seconds() {
    string? file = ShaperStandard();
    Skip.If(
      file is null,
      "the sibling exmods checkout with cast-shaperstandard is absent"
    );
    Shape drawn = Reader.Read(File.ReadAllText(file!));
    List<Leaf> cast = Select.Groups(drawn, ["Standard"]);

    var watch = System.Diagnostics.Stopwatch.StartNew();
    var made = System.Threading.Tasks.Task.Run(() =>
      Patterns.Make(Ctx, "pattern-castthing", cast, [], null, "spec", 1.5)
    );
    bool done;
    string outcome;
    try {
      done = made.Wait(TimeSpan.FromSeconds(30));
      outcome = done ? $"{made.Result.Loose.Count} loose pieces" : "not made";
    } catch (AggregateException e) when (e.InnerException is CastGenException c) {
      done = true;
      outcome = c.Message;
    }
    output.WriteLine(
      $"shaper standard pattern: {watch.Elapsed.TotalSeconds:F1} s, {outcome}"
    );

    Assert.True(done);
  }
}
