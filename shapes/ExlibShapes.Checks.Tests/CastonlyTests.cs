using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class CastonlyTests {
  private static readonly Castonly Check = new();

  public static TheoryData<string> Names =>
    [.. Fixtures.Parity("castonly").Keys];

  private static string RecordFor(string name) =>
    name == "cast-bolt.json"
      ? """
        "kinds": ["casting"],
        "elements": { "Bed": { "stage": "cast", "role": "cast" },
                      "Bolt": { "stage": "cast", "role": "fastener" } }
        """
      : CastFixtures.CastRecord;

  [Theory]
  [MemberData(nameof(Names))]
  public void Castonly_parity(string name) {
    Recorded want = Fixtures.Parity("castonly")[name];
    CheckResult got = Check.Run(
      new CheckInput(
        Fixtures.Shape(name),
        Record: CastFixtures.Record(RecordFor(name))
      )
    );
    Assert.Equal(want.Exit, got.ExitCode);
    Assert.Equal(want.Enumerated, got.Enumerated);
    Assert.Equal(want.Faulted, CastFixtures.Names(got, "castonly:"));
  }

  [Fact]
  public void Castonly_rejects_a_bolt_tagged_cast_and_no_other_gate_does() {
    var input = new CheckInput(
      Fixtures.Shape("cast-bolt.json"),
      Record: CastFixtures.Record(RecordFor("cast-bolt.json")),
      Settings: CastFixtures.Settings(),
      Source: Fixtures.Edited(
        "cast-source.json",
        raw =>
          ((JArray)raw["elements"]![0]!["children"]!).Add(
            CastFixtures.Find(
              JObject.Parse(Fixtures.Text("cast-bolt.json")),
              "Bolt"
            )
          )
      ),
      SourceRecord: CastFixtures.Record(
        RecordFor("cast-bolt.json") + "," + CastFixtures.PlaneWay
      )
    );
    Assert.Equal(["castonly"], CastFixtures.Rejecting(input));
    Assert.Contains(
      "Bed/Bolt: tagged fastener, not cast",
      Check.Run(input).Lines
    );
  }

  [Theory]
  [InlineData("fastener")]
  [InlineData("worked")]
  public void Castonly_rejects_a_stage_tag_through_its_group(string stage) {
    CheckResult r = Check.Run(
      new CheckInput(
        Fixtures.Shape("cast-ok.json"),
        Record: CastFixtures.Record(
          $$"""
          "elements": { "Bed": { "stage": "{{stage}}" } }
          """
        )
      )
    );
    Assert.Equal(1, r.ExitCode);
    Assert.Contains($"Bed/Seat: tagged {stage}, not cast", r.Lines);
  }

  [Fact]
  public void Empty_input_fails_and_a_record_is_required() {
    Assert.Equal(Verdict.Fail, Check.Run(new CheckInput(null)).Verdict);
    Assert.Throws<CheckUsageException>(() =>
      Check.Run(new CheckInput(Fixtures.Shape("cast-ok.json")))
    );
  }
}
