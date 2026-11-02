using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class CorefitTests {
  private static readonly Corefit Check = new();

  public static TheoryData<string> Names =>
    [.. Fixtures.Parity("corefit").Keys];

  private static string Leaf(
    string name,
    double[] lo,
    double[] hi,
    bool grain = false
  ) {
    string faces = grain
      ? $$"""
        "up": { "texture": "#wood", "uv": [0, 0, {{hi[0] - lo[0]}}, {{hi[2]
          - lo[2]}}], "rotation": 0 },
        "down": { "texture": "#wood", "uv": [0, 0, {{hi[0] - lo[0]}}, {{hi[2]
          - lo[2]}}], "rotation": 0 }
        """
      : """ "up": { "texture": "#wood" } """;
    return $$"""
      { "name": "{{name}}", "from": [{{string.Join(
        ", ",
        lo
      )}}], "to": [{{string.Join(", ", hi)}}], "faces": { {{faces}} } }
      """;
  }

  private static Shape Group(string name, params string[] leaves) =>
    Reader.Read(
      $$"""
      { "textures": { "wood": "block/wood/patternpine" },
        "elements": [ { "name": "{{name}}", "from": [0, 0, 0], "to": [0, 0, 0], "children": [{{string.Join(
        ", ",
        leaves
      )}}] } ] }
      """
    );

  private static PartRecord CoreRecord(string run = "x") =>
    CastFixtures.Record(
      $$"""
      "kinds": ["core"],
      "core": { "of": "cast-bed", "feature": "slot", "run": "{{run}}", "prints": ["Print*"] }
      """
    );

  private static PartRecord SourceRecord() =>
    CastFixtures.Record(
      """
      "kinds": ["casting"],
      "features": { "slot": { "elements": ["Slot"], "origin": "cored" } }
      """
    );

  private static Shape Source(string size = "22, 2, 3") =>
    Group(
      "Bed",
      Leaf("Slot", [2, 0, 0], [.. size.Split(", ").Select(double.Parse)])
    );

  /// <summary>The bed's core: a body of three planks from x 2 to 22 and a print of 2 at each end.</summary>
  private static Shape Core(
    double short_ = 0,
    double gap = 0,
    double rise = 0,
    bool eastPrint = true
  ) {
    double end = 22 - short_,
      top = 2 + rise;
    var leaves = new List<string>
    {
      Leaf("Plank1", [2, 0, 0], [10, top, 3]),
      Leaf("Plank2", [10 + gap, 0, 0], [18, top, 3]),
      Leaf("Plank3", [18, 0, 0], [end, top, 3]),
      Leaf("PrintW", [0, 0, 0], [2, top, 3]),
    };
    if (eastPrint)
      leaves.Add(Leaf("PrintE", [end, 0, 0], [end + 2, top, 3]));
    return Group("Core", [.. leaves]);
  }

  private static CheckInput CoreInput(Shape core, string run = "x") =>
    new(
      core,
      Record: CoreRecord(run),
      Source: Source(),
      SourceRecord: SourceRecord(),
      Settings: CastFixtures.Settings()
    );

  private static string[] Faulted(CheckInput input) =>
    [.. Fixtures.FaultedNames(Check.Run(input), "corefit").Distinct()];

  private static IEnumerable<string> Tray(
    string tray,
    double ox,
    double across,
    double depth,
    double floorMore,
    bool grain = true,
    double westOut = 0
  ) {
    double w = 24 + 2,
      z = across + 2,
      top = 1 + depth;
    return
    [
      Leaf(tray + "Floor", [ox, 0, 0], [ox + w + floorMore, 1, z], grain),
      Leaf(tray + "W", [ox - westOut, 1, 0], [ox + 1, top, z], grain),
      Leaf(tray + "E", [ox + w - 1, 1, 0], [ox + w, top, z], grain),
      Leaf(tray + "S", [ox + 1, 1, 0], [ox + w - 1, top, 1], grain),
      Leaf(tray + "N", [ox + 1, 1, z - 1], [ox + w - 1, top, z], grain),
    ];
  }

  private static Shape BoxShape(
    double across = 2,
    double depth = 1.5,
    double floorMore = 0,
    bool grain = true
  ) =>
    Reader.Read(
      $$"""
      { "textures": { "wood": "block/wood/patternpine" },
        "elements": [ { "name": "Box", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
          { "name": "Tray1", "from": [0, 0, 0], "to": [0, 0, 0], "children": [{{string.Join(
        ", ",
        Tray("T1", 0, across, depth, 0, grain)
      )}}] },
          { "name": "Tray2", "from": [0, 0, 0], "to": [0, 0, 0], "children": [{{string.Join(
        ", ",
        Tray("T2", 40, across, depth, floorMore, grain)
      )}}] } ] } ] }
      """
    );

  private static CheckInput BoxInput(Shape box) =>
    new(
      box,
      Record: CastFixtures.Record(
        """
        "kinds": ["corebox"],
        "corebox": { "core": "core-bed", "trays": ["Tray1", "Tray2"] }
        """
      ),
      Settings: CastFixtures.Settings(),
      Cores: [Core()]
    );

  [Theory]
  [MemberData(nameof(Names))]
  public void Corefit_parity(string name) {
    Recorded want = Fixtures.Parity("corefit")[name];
    double[] a = want.Areas;
    string[] core = Faulted(
      CoreInput(Core(short_: a[0], gap: a[1], rise: a[2]))
    );
    string[] box = Faulted(
      BoxInput(
        BoxShape(across: 2 + a[3], depth: 1.5 - a[4] / 2, floorMore: a[5])
      )
    );
    Assert.Equal(
      want.Faulted.Where(f => f is "length" or "section").Order().ToArray(),
      core
    );
    Assert.Equal(
      want.Faulted.Where(f => f is "pocket" or "extent").Order().ToArray(),
      box
    );
  }

  [Fact]
  public void Corefit_the_nominal_core_and_box_pass() {
    CheckResult core = Check.Run(CoreInput(Core()));
    Assert.Equal(0, core.ExitCode);
    Assert.Equal(5, core.Enumerated);
    CheckResult box = Check.Run(BoxInput(BoxShape()));
    Assert.Equal(0, box.ExitCode);
    Assert.Equal(2 + 2 + 1 + 20, box.Enumerated);
  }

  [Fact]
  public void Corefit_pocket_equals_core() {
    CheckInput input = BoxInput(BoxShape());
    Assert.Equal([], Faulted(input));
    Shape longer = Reader.Read(
      $$"""
      { "textures": { "wood": "block/wood/patternpine" },
        "elements": [ { "name": "Box", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
          { "name": "Tray1", "from": [0, 0, 0], "to": [0, 0, 0], "children": [{{string.Join(
        ", ",
        LongTray("T1", 0)
      )}}] },
          { "name": "Tray2", "from": [0, 0, 0], "to": [0, 0, 0], "children": [{{string.Join(
        ", ",
        LongTray("T2", 40)
      )}}] } ] } ] }
      """
    );
    CheckResult r = Check.Run(BoxInput(longer));
    Assert.Equal(1, r.ExitCode);
    Assert.Equal(["pocket"], Faulted(BoxInput(longer)));
    Assert.Contains(
      r.Lines,
      l =>
        l.StartsWith(
          "pocket: the trays make 24.125 x 3 x 2",
          StringComparison.Ordinal
        )
    );
  }

  private static IEnumerable<string> LongTray(string tray, double ox) {
    double w = 24.125 + 2,
      z = 4,
      top = 2.5;
    return
    [
      Leaf(tray + "Floor", [ox, 0, 0], [ox + w, 1, z], true),
      Leaf(tray + "W", [ox, 1, 0], [ox + 1, top, z], true),
      Leaf(tray + "E", [ox + w - 1, 1, 0], [ox + w, top, z], true),
      Leaf(tray + "S", [ox + 1, 1, 0], [ox + w - 1, top, 1], true),
      Leaf(tray + "N", [ox + 1, 1, z - 1], [ox + w - 1, top, z], true),
    ];
  }

  [Fact]
  public void Corefit_a_pocket_that_is_no_box_is_named() {
    Shape bar = Reader.Read(
      $$"""
      { "textures": { "wood": "block/wood/patternpine" },
        "elements": [ { "name": "Box", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
          { "name": "Tray1", "from": [0, 0, 0], "to": [0, 0, 0], "children": [{{string.Join(
        ", ",
        Tray("T1", 0, 2, 1.5, 0)
          .Append(Leaf("Post", [10, 1, 1], [12, 2.5, 2], true))
      )}}] },
          { "name": "Tray2", "from": [0, 0, 0], "to": [0, 0, 0], "children": [{{string.Join(", ", Tray("T2", 40, 2, 1.5, 0))}}] } ] } ] }
      """
    );
    CheckResult r = Check.Run(BoxInput(bar));
    Assert.Equal(1, r.ExitCode);
    Assert.Contains("pocket: Tray1 pocket is not a box", r.Lines);
  }

  [Fact]
  public void Corefit_extent_wants_walls_of_one_round_each_pocket() {
    Shape thin = Reader.Read(
      $$"""
          { "textures": { "wood": "block/wood/patternpine" },
            "elements": [ { "name": "Box", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
              { "name": "Tray1", "from": [0, 0, 0], "to": [0, 0, 0], "children": [{{string.Join(
          ", ",
          Tray("T1", 0, 2, 1.5, 0, true, 0.5)
        )}}] },
              { "name": "Tray2", "from": [0, 0, 0], "to": [0, 0, 0], "children": [{{string.Join(", ", Tray("T2", 40, 2, 1.5, 0))}}] } ] } ] }
          """
    );
    Assert.Equal(["extent"], Faulted(BoxInput(thin)));
  }

  [Fact]
  public void Corefit_trays_of_different_across_are_not_alike() {
    Shape odd = Reader.Read(
      $$"""
      { "textures": { "wood": "block/wood/patternpine" },
        "elements": [ { "name": "Box", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
          { "name": "Tray1", "from": [0, 0, 0], "to": [0, 0, 0], "children": [{{string.Join(
        ", ",
        Tray("T1", 0, 2, 1.5, 0)
      )}}] },
          { "name": "Tray2", "from": [0, 0, 0], "to": [0, 0, 0], "children": [{{string.Join(
        ", ",
        Tray("T2", 40, 2.5, 1.5, 0)
      )}}] } ] } ] }
      """
    );
    CheckResult r = Check.Run(BoxInput(odd));
    Assert.Equal(1, r.ExitCode);
    Assert.Contains(
      r.Lines,
      l =>
        l.StartsWith(
          "extent: Tray2 pocket is 24 x 2.5 across",
          StringComparison.Ordinal
        )
    );
  }

  [Fact]
  public void Corefit_grain_runs_boards_along_the_pockets_long_side() {
    Shape turned = BoxShape();
    Assert.Equal([], Faulted(BoxInput(turned)));
    Shape bare = BoxShape(grain: false);
    CheckResult r = Check.Run(BoxInput(bare));
    Assert.Equal(1, r.ExitCode);
    Assert.Equal(["grain"], Faulted(BoxInput(bare)));
    Shape wrong = Reader.Read(
      $$"""
      { "textures": { "wood": "block/wood/patternpine" },
        "elements": [ { "name": "Box", "from": [0, 0, 0], "to": [0, 0, 0], "children": [
          { "name": "Tray1", "from": [0, 0, 0], "to": [0, 0, 0], "children": [{{string.Join(
        ", ",
        Tray("T1", 0, 2, 1.5, 0).Select(l => l.Replace("\"rotation\": 0", "\"rotation\": 90"))
      )}}] },
          { "name": "Tray2", "from": [0, 0, 0], "to": [0, 0, 0], "children": [{{string.Join(", ", Tray("T2", 40, 2, 1.5, 0))}}] } ] } ] }
      """
    );
    Assert.Equal(["grain"], Faulted(BoxInput(wrong)));
  }

  [Fact]
  public void Corefit_length_names_a_body_off_the_feature_and_a_gap_in_it() {
    CheckResult shortBody = Check.Run(CoreInput(Core(short_: 0.125)));
    Assert.Equal(1, shortBody.ExitCode);
    Assert.Contains(
      shortBody.Lines,
      l =>
        l.StartsWith(
          "length: the body runs x 2 to 21.875, the feature 2 to 22",
          StringComparison.Ordinal
        )
    );
    CheckResult gap = Check.Run(CoreInput(Core(gap: 0.125)));
    Assert.Contains(
      gap.Lines,
      l =>
        l.StartsWith(
          "length: a gap in the body on x from 10 to 10.125",
          StringComparison.Ordinal
        )
    );
  }

  [Fact]
  public void Corefit_section_is_judged_on_the_axes_across_the_run() {
    Assert.Equal(["section"], Faulted(CoreInput(Core(rise: 0.125))));
  }

  [Fact]
  public void Corefit_a_core_without_its_east_print_fails_only_prints() {
    CheckResult r = Check.Run(CoreInput(Core(eastPrint: false)));
    Assert.Equal(["prints"], Faulted(CoreInput(Core(eastPrint: false))));
    Assert.Contains("prints: no print at the high end of x", r.Lines);
  }

  [Fact]
  public void Corefit_reads_the_run_it_is_told() {
    Shape alongZ = Group(
      "Core",
      Leaf("Plank", [0, 0, 2], [3, 2, 22]),
      Leaf("PrintW", [0, 0, 0], [3, 2, 2]),
      Leaf("PrintE", [0, 0, 22], [3, 2, 24])
    );
    Shape source = Group("Bed", Leaf("Slot", [0, 0, 2], [3, 2, 22]));
    var input = new CheckInput(
      alongZ,
      Record: CoreRecord("z"),
      Source: source,
      SourceRecord: SourceRecord(),
      Settings: CastFixtures.Settings()
    );
    Assert.Equal(0, Check.Run(input).ExitCode);
    Assert.Contains("prints", Faulted(input with { Record = CoreRecord("x") }));
  }

  private static Shape Sand(params string[] boxes) => StageFixtures.Sand(boxes);

  private static string Block(
    string name,
    double x0,
    double y0,
    double z0,
    double x1,
    double y1,
    double z1
  ) => StageFixtures.Box(name, x0, y0, z0, x1, y1, z1);

  private static CheckInput Seated(
    double westTo = 8,
    double eastFrom = 20,
    params string[] extra
  ) {
    Shape sand = Sand([
      Block("West", 2, 2, -14, westTo, 14, 14),
      Block("East", eastFrom, 2, -14, 30, 14, 14),
      Block("Under", 8, 2, -14, 20, 4, 14),
      Block("Over", 8, 6, -14, 20, 14, 14),
      Block("South", 8, 4, -14, 20, 6, 0),
      Block("North", 8, 4, 2, 20, 6, 14),
      .. extra,
    ]);
    PartRecord record = CastFixtures.Record(
      $$"""
      "kinds": ["filling"],
      "pour": { "station": "floor", "mould": "open", "cast": "fill-cast",
        "pieces": [{ "at": {{PourFixtures.At(13, 10, -3)}} }],
        "cores": [{ "item": "core-bed", "at": {{PourFixtures.At(10, 4, 0)}} }],
        "feeds": [], "units": { "perPiece": 360, "share": 0 } }
      """
    );
    return new CheckInput(
      sand,
      Record: record,
      Settings: PourFixtures.Settings(),
      Cast: Fixtures.Shape("fill-cast.json"),
      Cores:
      [
        Group(
          "Core",
          Leaf("Body", [0, 0, 0], [8, 2, 2]),
          Leaf("PrintW", [-2, 0, 0], [0, 2, 2]),
          Leaf("PrintE", [8, 0, 0], [10, 2, 2])
        ),
      ],
      CoreRecords: [CoreRecord()]
    );
  }

  [Fact]
  public void Corefit_seat_prints_in_clean_seats_pass() {
    CheckResult r = Check.Run(Seated());
    Assert.Equal(0, r.ExitCode);
    Assert.Equal(4, r.Enumerated);
  }

  [Fact]
  public void Corefit_seat_sand_in_a_print_fails_only_the_hold() {
    CheckResult r = Check.Run(Seated(8, 20, Block("Plug", 8, 4, 0, 9, 6, 2)));
    Assert.Equal(1, r.ExitCode);
    Assert.Equal(
      ["seat"],
      Faulted(Seated(8, 20, Block("Plug", 8, 4, 0, 9, 6, 2)))
    );
    Assert.Contains("seat: core-bed PrintW holds sand", r.Lines);
    Assert.DoesNotContain(r.Lines, l => l.Contains("no sand within"));
  }

  [Fact]
  public void Corefit_seat_end_within_a_32nd() {
    Assert.Equal(0, Check.Run(Seated(westTo: 8 - 1.0 / 32)).ExitCode);
    CheckResult r = Check.Run(Seated(westTo: 7.9));
    Assert.Equal(1, r.ExitCode);
    Assert.Contains(
      r.Lines,
      l =>
        l.StartsWith(
          "seat: core-bed PrintW has no sand within 0.03125 of its end at x 8",
          StringComparison.Ordinal
        )
    );
    Assert.DoesNotContain(r.Lines, l => l.Contains("holds sand"));
  }

  [Fact]
  public void Corefit_applies_to_cores_boxes_and_pours_with_cores_and_has_rows() {
    Assert.Equal(["facecheck", "corefit"], KindTable.Gates("core"));
    Assert.Equal(["facecheck", "corefit"], KindTable.Gates("corebox"));
    Assert.True(Selection.Applies(Check, Seated()));
    Assert.True(Selection.Applies(Check, CoreInput(Core())));
    Assert.True(Selection.Applies(Check, BoxInput(BoxShape())));
    Assert.False(
      Selection.Applies(
        Check,
        PourFixtures.Input(Fixtures.Shape("fill-ok.json"), PourFixtures.One())
      )
    );
  }

  [Fact]
  public void Corefit_without_a_core_slot_is_a_usage_error() {
    var input = new CheckInput(
      Core(),
      Record: CastFixtures.Record("\"kinds\": [\"core\"]")
    );
    Assert.Throws<CheckUsageException>(() => Check.Run(input));
  }

  [Fact]
  public void Corefit_seat_sand_turned_clear_of_a_print_holds_none_and_over_it_holds() {
    CheckResult clear = Check.Run(
      Seated(8, 20, PourFixtures.Turned("Plug", 7.1, 4, -0.9, 8.1, 6, 0.1))
    );
    Assert.Equal(0, clear.ExitCode);
    CheckResult over = Check.Run(
      Seated(8, 20, PourFixtures.Turned("Plug", 7.7, 4, -0.3, 8.7, 6, 0.7))
    );
    Assert.Contains("seat: core-bed PrintW holds sand", over.Lines);
  }

  [Fact]
  public void Corefit_seat_a_turned_print_holds_sand_by_its_corners_not_its_bounds() {
    CheckInput input = Seated(
      8,
      20.02,
      Block("Corner", 19.85, 4, 0.3, 19.95, 6, 0.4)
    ) with {
      Cores =
      [
        Group(
          "Core",
          Leaf("Body", [0, 0, 0], [8, 2, 2]),
          Leaf("PrintW", [-2, 0, 0], [0, 2, 2]),
          PourFixtures.Turned("PrintC", 8.7929, 0, 0.5, 9.7929, 2, 1.5, "wood")
        ),
      ],
    };
    CheckResult r = Check.Run(input);
    Assert.Equal(0, r.ExitCode);
    Assert.Equal(4, r.Enumerated);
  }
}
