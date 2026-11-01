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
  public void The_allowance_is_the_input_s() {
    Shape item = Fixtures.Edited(
      "cast-source.json",
      raw => CastFixtures.Move(CastFixtures.Find(raw, "Way"), "to", 1, 5.25)
    );
    Assert.Equal(1, Check.Run(Input(item)).ExitCode);
    Assert.Equal(0, Check.Run(Input(item) with { Allowance = 0.25 }).ExitCode);
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
