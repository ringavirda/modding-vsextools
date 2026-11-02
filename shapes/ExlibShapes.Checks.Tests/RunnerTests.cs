using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class RunnerTests {
  private static readonly Runner Check = new();

  public static TheoryData<string> Names = [.. Fixtures.Parity("runner").Keys];

  private static string Category(string line) =>
    line.Contains("elements of that name") ? "boxes"
    : line.Contains("top at y") ? "floor"
    : line.Contains("vx3 of sand in its volume") ? "seat"
    : "";

  private static CheckInput Of(string name, string[]? feeds = null) =>
    PourFixtures.Input(
      Fixtures.Shape(name + ".json"),
      PourFixtures.Record(
        [PourFixtures.At(13, 10, -3)],
        feeds ?? [PourFixtures.Runner1]
      )
    );

  private static string Feed(
    double y0,
    double x1,
    double z0,
    string kind = "runner"
  ) =>
    $$"""{ "name": "F", "kind": "{{kind}}", "boxes": [[15, {{y0}}, {{z0}}, {{x1}}, 14, 9]] }""";

  [Theory]
  [MemberData(nameof(Names))]
  public void Runner_parity(string name) {
    Recorded want = Fixtures.Parity("runner")[name];
    CheckResult got = Check.Run(Of(name));
    Assert.Equal(want.Exit, got.ExitCode);
    Assert.True(got.Enumerated >= want.Enumerated);
    Assert.Equal(
      want.Faulted,
      got.Lines.Where(l => Category(l) != "")
        .Select(Category)
        .Distinct()
        .Order()
    );
  }

  [Fact]
  public void A_runner_box_with_a_renamed_wall_is_rejected_by_this_gate_alone() {
    Shape renamed = Fixtures.Edited(
      "fill-ok.json",
      raw => Fixtures.Element(raw, "BoxWallW")["name"] = "BoxWallX"
    );
    var input = PourFixtures.Input(
      renamed,
      PourFixtures.Record([PourFixtures.At(13, 10, -3)], [PourFixtures.Runner1])
    );
    Assert.Equal(["runner"], PourFixtures.Rejecting(input));
    Assert.Contains(
      "BoxWallW: 0 elements of that name, one wanted",
      Check.Run(input).Lines
    );
  }

  [Fact]
  public void The_floor_below_the_sands_top_names_both_heights() {
    CheckResult r = Check.Run(Of("fill-lowered"));
    Assert.Contains(
      "BoxFloor: top at y 13.5, the sand's top is 14",
      r.Lines.Select(l =>
        l[(l.LastIndexOf("BoxFloor", System.StringComparison.Ordinal))..]
      )
    );
  }

  [Fact]
  public void A_cope_holds_the_runner_box_like_an_open_pour() {
    CheckInput input = Of("fill-ok") with {
      Record = CastFixtures.Record(
        "\"kinds\": [\"filling\"], \"pour\": { \"half\": \"cope\", \"pair\": \"drag\" }"
      ),
      Station = PourFixtures.Settings().Stations["floor"],
    };
    Assert.Equal(0, Check.Run(input).ExitCode);
  }

  [Fact]
  public void A_station_with_no_runner_box_holds_no_runner_element() {
    CheckInput input = Of("fill-ok") with {
      Record = PourFixtures.Record(
        [PourFixtures.At(0, 0, 0)],
        [],
        station: "small"
      ),
    };
    CheckResult r = Check.Run(input);
    Assert.Equal(1, r.ExitCode);
    Assert.Equal(
      7,
      r.Lines.Count(l =>
        l.EndsWith(
          "a runner-role element in a filling that holds no runner box"
        )
      )
    );
  }

  [Fact]
  public void A_closed_pours_drag_holds_no_runner_element() {
    CheckInput input = Of("fill-ok") with {
      Record = PourFixtures.Record(
        [PourFixtures.At(13, 3, -3)],
        [],
        station: "long",
        mould: "closed"
      ),
    };
    CheckResult r = Check.Run(input);
    Assert.Equal(1, r.ExitCode);
    Assert.Equal(
      7,
      r.Lines.Count(l =>
        l.EndsWith(
          "a runner-role element in a filling that holds no runner box"
        )
      )
    );
  }

  [Fact]
  public void A_runner_cut_not_the_depth_from_the_top_is_a_fault() {
    CheckResult r = Check.Run(Of("fill-ok", [Feed(13, 17, 2.5)]));
    Assert.Contains("F: y 13 to 14, wanted 0.5 deep from 14", r.Lines);
  }

  [Fact]
  public void A_runner_cut_narrower_than_the_gate_width_is_a_fault() {
    CheckResult r = Check.Run(Of("fill-ok", [Feed(13.5, 15.5, 2.5)]));
    Assert.Contains("F: 0.5 across, at least 1 wanted", r.Lines);
  }

  [Fact]
  public void The_first_runner_not_meeting_the_drop_is_a_fault() {
    CheckResult r = Check.Run(
      Of("fill-ok", [Feed(13.5, 17, 2.5).Replace("14, 9", "14, 8")])
    );
    Assert.Contains("F: does not meet the drop", r.Lines);
  }

  [Fact]
  public void A_closed_pours_runner_hangs_from_the_parting_and_meets_the_sprue() {
    Shape sand = Reader.Read(
      """
      { "textures": { "andesite": "block/stone/sand/andesite" },
        "elements": [ { "name": "Sand1", "from": [2,2,-46], "to": [30,8,14], "faces": { "up": { "texture": "#andesite" } } } ] }
      """
    );
    const string sprue =
      """{ "name": "Sprue", "kind": "sprue", "boxes": [[15, 7.5, 9, 17, 8, 10]] }""";
    const string run =
      """{ "name": "R", "kind": "runner", "boxes": [[15, 7.5, 2.5, 17, 8, 9]] }""";
    PartRecord Closed(string runner) =>
      PourFixtures.Record(
        [PourFixtures.At(13, 3, -3)],
        [runner, sprue],
        station: "long",
        mould: "closed"
      );
    CheckInput good = PourFixtures.Input(sand, Closed(run));
    CheckResult r = Check.Run(good);
    Assert.Equal(0, r.ExitCode);
    CheckResult bad = Check.Run(
      PourFixtures.Input(sand, Closed(run.Replace("8, 9", "8, 8")))
    );
    Assert.Contains("R: does not meet the sprue", bad.Lines);
  }

  [Fact]
  public void A_turned_sand_leaf_in_a_runner_box_counts_its_own_volume() {
    Shape fill = PourFixtures.WithTurned(
      "fill-ok.json",
      PourFixtures.Turned("Chip", 11, 13.25, 12, 13, 13.75, 14)
    );
    CheckResult r = Check.Run(
      PourFixtures.Input(
        fill,
        PourFixtures.Record(
          [PourFixtures.At(13, 10, -3)],
          [PourFixtures.Runner1]
        )
      )
    );
    string line = Assert.Single(
      r.Lines,
      l => l.EndsWith("vx3 of sand in its volume")
    );
    Assert.InRange(
      PourFixtures.Number([line], line[..(line.IndexOf(": ") + 2)]),
      1.9,
      2.1
    );
  }
}
