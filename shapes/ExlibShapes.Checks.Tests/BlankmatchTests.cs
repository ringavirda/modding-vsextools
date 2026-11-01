using System;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class BlankmatchTests {
  private static readonly Blankmatch Check = new();

  private const string BlankBody = """
    "kinds": ["casting"],
    "elements": { "Bed": { "stage": "cast", "role": "cast" } },
    "features": {
      "lug": { "elements": ["Lug"], "origin": "cast", "form": "lug", "faces": [] },
      "ways": { "elements": ["Way"], "origin": "planed", "faces": ["Way/up"], "step": "plane" }
    },
    "steps": [ { "id": "plane", "tool": "planer", "faces": ["Way/up"] } ]
    """;

  private const string UserFeatures = """
    "lug": { "elements": ["Lug"], "origin": "cast", "form": "lug", "faces": [] },
    "slot": { "elements": ["Slot"], "origin": "drilled", "faces": [], "step": "drill" }
    """;

  private const string LineX07 =
    "x: spans 7 of 10 (0.7), the band is 0.75 to 1";

  private static string Answer(
    string feature = "blankmatch/x",
    string line = LineX07
  ) =>
    $$"""
      "answers": [ { "feature": "{{feature}}", "question": "q", "answer": "ruled",
                     "by": "fallen", "date": "2026-11-02", "featureSha": "{{Provenance.Sha256Of(
        line
      )}}" } ]
      """;

  private static string UserBody(
    string features = UserFeatures,
    string steps = "\"Way/up\"",
    string extra = ""
  ) =>
    $$"""
      "kinds": ["worked"],
      "elements": { "Bed": { "stage": "cast", "role": "cast" },
                    "Slot": { "stage": "worked", "role": "cast" } },
      "features": { {{features}} },
      "steps": [ { "id": "plane", "tool": "planer", "faces": [{{steps}}] } ]
      {{(extra == "" ? "" : "," + extra)}}
      """;

  private static Shape UserShape(Action<JObject>? edit = null) =>
    Fixtures.Edited(
      "cast-ok.json",
      raw => {
        ((JArray)raw["elements"]![0]!["children"]!).Add(
          JObject.Parse(
            """
            { "name": "Slot", "from": [6, 2.5, 2], "to": [8, 3.5, 6],
              "faces": { "down": { "texture": "#cast-iron1" } } }
            """
          )
        );
        edit?.Invoke(raw);
      }
    );

  private static CheckResult Run(
    Shape? shape = null,
    string? user = null,
    string[]? cutFaces = null
  ) =>
    Check.Run(
      new CheckInput(
        shape ?? UserShape(),
        Record: CastFixtures.Record(user ?? UserBody()),
        Source: Fixtures.Shape("cast-ok.json"),
        SourceRecord: CastFixtures.Record(BlankBody),
        CutFaces: cutFaces
      )
    );

  private static Shape Narrow(double lugTo = 11) =>
    UserShape(raw => {
      CastFixtures.Move(CastFixtures.Find(raw, "Seat"), "to", 0, 9);
      CastFixtures.Move(CastFixtures.Find(raw, "Way"), "to", 0, 9);
      CastFixtures.Move(CastFixtures.Find(raw, "Lug"), "from", 0, 9);
      CastFixtures.Move(CastFixtures.Find(raw, "Lug"), "to", 0, lugTo);
    });

  [Fact]
  public void Blankmatch_passes_a_user_that_matches() {
    CheckResult r = Run();
    Assert.Equal(0, r.ExitCode);
    Assert.Equal(6, r.Enumerated);
    Assert.Contains("blankmatch: 6 things match the blank", r.Lines);
  }

  [Fact]
  public void Blankmatch_a_boss_more_fails_and_nothing_else_does() {
    CheckResult r = Run(
      user: UserBody(
        UserFeatures
          + ", \"lug2\": { \"elements\": [\"Lug\"], \"origin\": \"cast\", \"form\": \"lug\", \"faces\": [] }"
      )
    );
    Assert.Equal(1, r.ExitCode);
    Assert.Equal(["form 'lug': 2, the blank has 1"], r.Lines.Take(1));
    Assert.Single(r.Lines);
  }

  [Fact]
  public void Blankmatch_a_cut_outside_the_cut_faces_fails() {
    CheckResult r = Run(user: UserBody(steps: "\"Way/up\", \"Seat/up\""));
    Assert.Equal(1, r.ExitCode);
    Assert.Equal(["Seat/up: not a cut face of the blank"], r.Lines.Take(1));
    Assert.Single(r.Lines);
  }

  [Fact]
  public void Blankmatch_a_slot_past_the_blanks_end_fails() {
    CheckResult r = Run(
      UserShape(raw =>
        CastFixtures.Move(CastFixtures.Find(raw, "Slot"), "to", 0, 15)
      )
    );
    Assert.Equal(1, r.ExitCode);
    Assert.StartsWith("slot: lies outside the blank", r.Lines[0]);
    Assert.Single(r.Lines);
  }

  [Fact]
  public void Blankmatch_out_of_band_asks() {
    CheckResult r = Run(Narrow());
    Assert.Equal(3, r.ExitCode);
    Assert.Equal(["x: spans 7 of 10 (0.7), the band is 0.75 to 1"], r.Lines);
  }

  [Fact]
  public void Blankmatch_an_answer_passes_the_axis_it_names() {
    CheckResult r = Run(Narrow(), UserBody(extra: Answer()));
    Assert.Equal(0, r.ExitCode);
    Assert.Contains(
      "x: spans 7 of 10 (0.7), the band is 0.75 to 1, answered",
      r.Lines
    );
    Assert.Equal(
      3,
      Run(Narrow(), UserBody(extra: Answer("blankmatch/y"))).ExitCode
    );
  }

  [Fact]
  public void Blankmatch_stale_answer_asks() {
    CheckResult r = Run(Narrow(11.2), UserBody(extra: Answer()));
    Assert.Equal(3, r.ExitCode);
    Assert.Equal(["x: spans 7.2 of 10 (0.72), the band is 0.75 to 1"], r.Lines);
  }

  [Fact]
  public void Blankmatch_larger_than_the_blank_asks() {
    CheckResult r = Run(
      UserShape(raw =>
        CastFixtures.Move(CastFixtures.Find(raw, "Lug"), "to", 0, 14.6)
      )
    );
    Assert.Equal(3, r.ExitCode);
    Assert.Equal(
      ["x: spans 10.6 of 10 (1.06), the band is 0.75 to 1"],
      r.Lines
    );
  }

  [Fact]
  public void Blankmatch_a_failure_wins_over_a_question() {
    CheckResult r = Run(Narrow(), UserBody(steps: "\"Seat/up\""));
    Assert.Equal(1, r.ExitCode);
    Assert.Equal(2, r.Lines.Count);
    Assert.Contains(r.Lines, l => l.StartsWith("x: spans 7 of 10"));
  }

  [Fact]
  public void Blankmatch_cut_faces_are_the_features_it_names() {
    Assert.Equal(0, Run(cutFaces: ["ways"]).ExitCode);
    Assert.Equal(1, Run(cutFaces: ["lug"]).ExitCode);
    Assert.Throws<CheckUsageException>(() => Run(cutFaces: ["rails"]));
  }

  [Fact]
  public void Empty_input_fails_and_the_records_and_the_blank_are_required() {
    Assert.Equal(Verdict.Fail, Check.Run(new CheckInput(null)).Verdict);
    Shape shape = UserShape();
    PartRecord record = CastFixtures.Record(UserBody());
    Assert.Throws<CheckUsageException>(() =>
      Check.Run(new CheckInput(shape, Source: shape, SourceRecord: record))
    );
    Assert.Throws<CheckUsageException>(() =>
      Check.Run(new CheckInput(shape, Record: record, SourceRecord: record))
    );
    Assert.Throws<CheckUsageException>(() =>
      Check.Run(new CheckInput(shape, Record: record, Source: shape))
    );
  }
}
