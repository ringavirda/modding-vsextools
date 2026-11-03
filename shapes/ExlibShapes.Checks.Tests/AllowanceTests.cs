using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class AllowanceTests {
  private static readonly Allowance Check = new();

  public static TheoryData<string> Names =>
    [.. Fixtures.Parity("allowance").Keys];

  public static TheoryData<string, string, int, string> Faces =>
    new()
    {
      { "east", "to", 0, "hi" },
      { "west", "from", 0, "lo" },
      { "up", "to", 1, "hi" },
      { "down", "from", 1, "lo" },
      { "south", "to", 2, "hi" },
      { "north", "from", 2, "lo" },
    };

  private static CheckInput Input(
    Shape item,
    string steps = CastFixtures.PlaneWay,
    string source = "cast-source.json"
  ) =>
    new(
      item,
      Source: Fixtures.Shape(source),
      SourceRecord: CastFixtures.Record(CastFixtures.CastRecord + "," + steps)
    );

  [Theory]
  [MemberData(nameof(Names))]
  public void Allowance_parity(string name) {
    Recorded want = Fixtures.Parity("allowance")[name];
    CheckResult got = Check.Run(Input(Fixtures.Shape(name)));
    Assert.Equal(want.Exit, got.ExitCode);
    Assert.Equal(want.Enumerated, got.Enumerated);
    Assert.Equal(want.Faulted, CastFixtures.Names(got, "allowance:"));
  }

  [Fact]
  public void One_face_a_short_is_rejected_by_this_gate_alone() {
    CheckInput input = Input(Fixtures.Shape("cast-allow-short.json")) with {
      Record = CastFixtures.Record(CastFixtures.CastRecord),
      Settings = CastFixtures.Settings(),
    };
    Assert.Equal(["allowance"], CastFixtures.Rejecting(input));
    Assert.Contains(
      "Way: box [4, 4, 0]..[12, 5, 8] against [4, 4, 0]..[12, 5.125, 8], the source plus the allowance on [up]",
      Check.Run(input).Lines
    );
  }

  [Theory]
  [MemberData(nameof(Faces))]
  public void Each_face_moves_its_own_way(
    string face,
    string end,
    int axis,
    string side
  ) {
    Shape item = Fixtures.Edited(
      "cast-source.json",
      raw =>
        CastFixtures.Move(
          CastFixtures.Find(raw, "Seat"),
          end,
          axis,
          ((JArray)CastFixtures.Find(raw, "Seat")[end]!)[axis]!.Value<double>()
            + (side == "hi" ? 0.125 : -0.125)
        )
    );
    string steps = $$"""
      "steps": [ { "id": "plane", "faces": ["Seat/{{face}}"] } ]
      """;
    Assert.Equal(0, Check.Run(Input(item, steps)).ExitCode);
    Assert.Equal(1, Check.Run(Input(item)).ExitCode);
  }

  [Fact]
  public void The_parts_of_a_split_count_as_one_element() {
    Shape item = Fixtures.Edited(
      "cast-ok.json",
      raw => {
        JObject seat = CastFixtures.Find(raw, "Seat");
        JObject second = (JObject)seat.DeepClone();
        second["name"] = "Seat_s2";
        CastFixtures.Move(seat, "to", 0, 8);
        CastFixtures.Move(second, "from", 0, 8);
        ((JArray)raw["elements"]![0]!["children"]!).Add(second);
      }
    );
    Assert.Equal(0, Check.Run(Input(item)).ExitCode);
  }

  [Fact]
  public void A_cast_element_missing_and_an_element_outside_the_source_are_named() {
    Shape gone = Fixtures.Edited(
      "cast-ok.json",
      raw => CastFixtures.Find(raw, "Lug").Remove()
    );
    Assert.Contains("Lug: missing from the item", Check.Run(Input(gone)).Lines);
    Shape stray = Fixtures.Edited(
      "cast-ok.json",
      raw => {
        JObject extra = (JObject)CastFixtures.Find(raw, "Lug").DeepClone();
        extra["name"] = "Rib";
        ((JArray)raw["elements"]![0]!["children"]!).Add(extra);
      }
    );
    Assert.Contains("Rib: not in the source", Check.Run(Input(stray)).Lines);
  }

  [Fact]
  public void A_worked_or_fastener_element_of_the_source_is_not_expected_in_the_item() {
    CheckInput input = Input(Fixtures.Shape("cast-ok.json")) with {
      SourceRecord = CastFixtures.Record(
        """
        "elements": { "Bed": { "stage": "cast" }, "Lug": { "stage": "worked" } },
        "steps": [ { "id": "plane", "faces": ["Way/up"] } ]
        """
      ),
    };
    Shape without = Fixtures.Edited(
      "cast-ok.json",
      raw => CastFixtures.Find(raw, "Lug").Remove()
    );
    CheckResult r = Check.Run(input with { Shape = without });
    Assert.Equal(0, r.ExitCode);
    Assert.Equal(2, r.Enumerated);
  }

  [Fact]
  public void A_step_face_of_no_cast_element_is_a_fault_and_a_bad_direction_is_usage() {
    CheckResult r = Check.Run(
      Input(
        Fixtures.Shape("cast-ok.json"),
        """
        "steps": [ { "id": "plane", "faces": ["Way/up", "Ghost/up"] } ]
        """
      )
    );
    Assert.Equal(1, r.ExitCode);
    Assert.Contains(
      "Ghost/up: not a face of a cast element in the source",
      r.Lines
    );
    Assert.Throws<CheckUsageException>(() =>
      Check.Run(
        Input(
          Fixtures.Shape("cast-ok.json"),
          """
          "steps": [ { "id": "plane", "faces": ["Way/top"] } ]
          """
        )
      )
    );
  }

  [Fact]
  public void A_step_face_naming_a_group_moves_only_the_leaves_on_its_bounding_face() {
    string up = """
      "steps": [ { "id": "plane", "faces": ["Bed/up"] } ]
      """;
    Assert.Equal(
      0,
      Check.Run(Input(Fixtures.Shape("cast-ok.json"), up)).ExitCode
    );
    string twice = """
      "steps": [ { "id": "plane", "faces": ["Bed/up", "Way/up"] } ]
      """;
    Assert.Equal(
      0,
      Check.Run(Input(Fixtures.Shape("cast-ok.json"), twice)).ExitCode
    );
    Shape lug = Fixtures.Edited(
      "cast-source.json",
      raw => CastFixtures.Move(CastFixtures.Find(raw, "Lug"), "to", 0, 14.125)
    );
    string east = """
      "steps": [ { "id": "plane", "faces": ["Bed/east"] } ]
      """;
    Assert.Equal(0, Check.Run(Input(lug, east)).ExitCode);
  }

  [Fact]
  public void A_group_face_no_leaf_carries_in_the_world_is_a_fault() {
    Shape turned = Fixtures.Edited(
      "cast-source.json",
      raw => {
        JObject way = CastFixtures.Find(raw, "Way");
        way["rotationX"] = 90;
        way["rotationOrigin"] = new JArray(8, 4.5, 4);
      }
    );
    CheckInput input = Input(Fixtures.Shape("cast-ok.json")) with {
      Source = turned,
      SourceRecord = CastFixtures.Record(
        CastFixtures.CastRecord
          + """
          , "steps": [ { "id": "plane", "faces": ["Bed/up"] } ]
          """
      ),
    };
    Assert.Contains(
      "Bed/up: no leaf of the group lies on its bounding face",
      Check.Run(input).Lines
    );
  }

  [Fact]
  public void The_allowance_is_the_input_s() {
    Shape item = Fixtures.Edited(
      "cast-source.json",
      raw => CastFixtures.Move(CastFixtures.Find(raw, "Way"), "to", 1, 5.25)
    );
    Assert.Equal(1, Check.Run(Input(item)).ExitCode);
    Assert.Equal(0, Check.Run(Input(item) with { Allowance = 0.25 }).ExitCode);
  }

  private static CheckInput Moved(Shape item, params FlushMove[] moves) {
    PartRecord rec = CastFixtures.Record(CastFixtures.CastRecord);
    FlushMoves.Write(rec, moves);
    return Input(item) with { Record = rec };
  }

  private static Shape LugSunk() =>
    Fixtures.Edited(
      "cast-ok.json",
      raw => CastFixtures.Move(CastFixtures.Find(raw, "Lug"), "to", 0, 13.875)
    );

  [Fact]
  public void A_recorded_sink_explains_the_difference_and_the_same_sink_unrecorded_fails() {
    FlushMove sink = new("Lug", "sink", "east", -0.125);
    Assert.Equal(0, Check.Run(Moved(LugSunk(), sink)).ExitCode);
    CheckResult bare = Check.Run(Input(LugSunk()));
    Assert.Equal(1, bare.ExitCode);
    Assert.Contains(
      "Lug: box [12, 2, 2]..[13.875, 4, 4] against [12, 2, 2]..[14, 4, 4], the source plus the allowance on []",
      bare.Lines
    );
    Assert.Equal(1, Check.Run(Moved(LugSunk())).ExitCode);
  }

  [Fact]
  public void A_recorded_move_on_a_cut_face_fails() {
    CheckResult r = Check.Run(
      Moved(
        Fixtures.Shape("cast-ok.json"),
        new FlushMove("Way", "sink", "up", -0.125)
      )
    );
    Assert.Equal(1, r.ExitCode);
    Assert.Contains("Way/up: a sink move on a face a step cuts", r.Lines);
  }

  [Fact]
  public void A_recorded_cap_is_accepted_only_at_its_thickness_and_off_a_cut_face() {
    static Shape Capped(string name) =>
      Fixtures.Edited(
        "cast-ok.json",
        raw => {
          JObject cap = (JObject)CastFixtures.Find(raw, "Lug").DeepClone();
          cap["name"] = name;
          CastFixtures.Move(cap, "from", 1, 4);
          CastFixtures.Move(cap, "to", 1, 4.125);
          if (name == "Way_cap") {
            CastFixtures.Move(cap, "from", 1, 5.125);
            CastFixtures.Move(cap, "to", 1, 5.25);
          }
          ((JArray)raw["elements"]![0]!["children"]!).Add(cap);
        }
      );
    Shape lug = Capped("Lug_cap");
    Assert.Equal(
      0,
      Check
        .Run(
          Moved(lug, new FlushMove("Lug_cap", "cap", "up", 0.125, null, "Lug"))
        )
        .ExitCode
    );
    Assert.Contains(
      "Lug_cap: cap not 0.25 thick beside Lug/up, or on a face a step cuts",
      Check
        .Run(
          Moved(lug, new FlushMove("Lug_cap", "cap", "up", 0.25, null, "Lug"))
        )
        .Lines
    );
    Assert.Contains(
      "Way_cap: cap not 0.125 thick beside Way/up, or on a face a step cuts",
      Check
        .Run(
          Moved(
            Capped("Way_cap"),
            new FlushMove("Way_cap", "cap", "up", 0.125, null, "Way")
          )
        )
        .Lines
    );

    Shape stray = Fixtures.Edited(
      "cast-ok.json",
      raw => {
        JObject cap = (JObject)CastFixtures.Find(raw, "Lug").DeepClone();
        cap["name"] = "Lug_cap";
        CastFixtures.Move(cap, "from", 0, 0);
        CastFixtures.Move(cap, "to", 0, 2);
        CastFixtures.Move(cap, "from", 1, 4);
        CastFixtures.Move(cap, "to", 1, 4.125);
        ((JArray)raw["elements"]![0]!["children"]!).Add(cap);
      }
    );
    Assert.Contains(
      "Lug_cap: cap not 0.125 thick beside Lug/up, or on a face a step cuts",
      Check
        .Run(
          Moved(
            stray,
            new FlushMove("Lug_cap", "cap", "up", 0.125, null, "Lug")
          )
        )
        .Lines
    );
  }

  [Fact]
  public void A_recorded_merge_takes_the_union_and_never_of_a_cut_element() {
    Shape merged = Fixtures.Edited(
      "cast-ok.json",
      raw => {
        CastFixtures.Find(raw, "Lug").Remove();
        CastFixtures.Move(CastFixtures.Find(raw, "Seat"), "to", 0, 14);
      }
    );
    Assert.Equal(
      0,
      Check
        .Run(Moved(merged, new FlushMove("Lug", "merge", "", 0, "Seat")))
        .ExitCode
    );
    Shape wayGone = Fixtures.Edited(
      "cast-ok.json",
      raw => CastFixtures.Find(raw, "Way").Remove()
    );
    Assert.Contains(
      "Way: a merge on an element a step cuts",
      Check
        .Run(Moved(wayGone, new FlushMove("Way", "merge", "", 0, "Seat")))
        .Lines
    );
  }

  [Fact]
  public void Empty_input_fails_and_the_source_is_required() {
    Assert.Equal(Verdict.Fail, Check.Run(new CheckInput(null)).Verdict);
    Assert.Throws<CheckUsageException>(() =>
      Check.Run(new CheckInput(Fixtures.Shape("cast-ok.json")))
    );
    Assert.Throws<CheckUsageException>(() =>
      Check.Run(
        new CheckInput(
          Fixtures.Shape("cast-ok.json"),
          Source: Fixtures.Shape("cast-source.json")
        )
      )
    );
  }
}
