using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class RoleTests {
  private static readonly Role Check = new();

  public static TheoryData<string> Names => [.. Fixtures.Parity("role").Keys];

  private const string PatternRecord = """
    "kinds": ["pattern"],
    "elements": { "Drag": { "stage": "cast", "role": "cast" } }
    """;

  private static Shape Oak() =>
    Fixtures.Edited(
      "pattern-ok.json",
      raw => {
        raw["textures"]!["oak"] = "block/wood/planks/oak1";
        CastFixtures.Find(raw, "CopeBody")["faces"]!["up"]!["texture"] = "#oak";
      }
    );

  [Theory]
  [MemberData(nameof(Names))]
  public void Role_parity(string name) {
    Recorded want = Fixtures.Parity("role")[name];
    CheckResult got = Check.Run(
      new CheckInput(
        Fixtures.Shape(name),
        Record: CastFixtures.Record(CastFixtures.CastRecord),
        Settings: CastFixtures.Settings()
      )
    );
    Assert.Equal(want.Exit, got.ExitCode);
    Assert.Equal(want.Enumerated, got.Enumerated);
    Assert.Equal(want.Faulted, CastFixtures.Names(got, "role:"));
  }

  [Fact]
  public void An_oak_pattern_face_is_rejected_by_this_gate_alone() {
    var input = new CheckInput(
      Oak(),
      Record: CastFixtures.Record(PatternRecord),
      Settings: CastFixtures.Settings(),
      Parting: 8
    );
    Assert.Equal(["role"], CastFixtures.Rejecting(input));
    Assert.Equal(
      ["Cope/CopeBody up: texture 'oak', the pattern role takes 'wood'"],
      Check.Run(input).Lines.Where(l => l.Contains("oak"))
    );
  }

  [Fact]
  public void A_pattern_or_core_box_record_forces_its_role_on_every_element() {
    var pattern = new CheckInput(
      Fixtures.Shape("cast-ok.json"),
      Record: CastFixtures.Record(
        """
        "kinds": ["pattern"],
        "elements": { "Bed": { "stage": "cast", "role": "cast" } }
        """
      ),
      Settings: CastFixtures.Settings()
    );
    Assert.Equal(1, Check.Run(pattern).ExitCode);
    CheckResult box = Check.Run(
      new CheckInput(
        Fixtures.Shape("pattern-ok.json"),
        Record: CastFixtures.Record("\"kinds\": [\"corebox\"]"),
        Settings: CastFixtures.Settings()
      )
    );
    Assert.Equal(0, box.ExitCode);
    Assert.Equal(18, box.Enumerated);
  }

  [Fact]
  public void A_texture_is_the_role_s_by_its_editable_path_too() {
    Shape shape = Fixtures.Edited(
      "pattern-ok.json",
      raw => {
        raw["textures"]!["pine"] = "block/wood/planks/{wood}1";
        CastFixtures.Find(raw, "CopeBody")["faces"]!["up"]!["texture"] =
          "#pine";
      }
    );
    Assert.Equal(
      0,
      Check
        .Run(
          new CheckInput(
            shape,
            Record: CastFixtures.Record(PatternRecord),
            Settings: CastFixtures.Settings()
          )
        )
        .ExitCode
    );
  }

  [Fact]
  public void An_element_with_no_role_in_the_record_is_named() {
    CheckResult r = Check.Run(
      new CheckInput(
        Fixtures.Shape("cast-ok.json"),
        Record: CastFixtures.Record("\"kinds\": [\"casting\"]"),
        Settings: CastFixtures.Settings()
      )
    );
    Assert.Equal(1, r.ExitCode);
    Assert.Contains("Bed/Seat: no role in the record", r.Lines);
    Assert.Equal(18, r.Enumerated);
  }

  [Fact]
  public void Empty_input_fails_and_the_record_and_settings_are_required() {
    Assert.Equal(Verdict.Fail, Check.Run(new CheckInput(null)).Verdict);
    Shape shape = Fixtures.Shape("cast-ok.json");
    Assert.Throws<CheckUsageException>(() =>
      Check.Run(new CheckInput(shape, Settings: CastFixtures.Settings()))
    );
    Assert.Throws<CheckUsageException>(() =>
      Check.Run(
        new CheckInput(shape, Record: CastFixtures.Record("\"kinds\": []"))
      )
    );
    Assert.Throws<CheckUsageException>(() =>
      Check.Run(
        new CheckInput(
          shape,
          Record: CastFixtures.Record(
            "\"elements\": { \"Bed\": { \"role\": \"lacquer\" } }"
          ),
          Settings: CastFixtures.Settings()
        )
      )
    );
  }

  private const string FastenerRecord = """
    "kinds": ["casting"],
    "elements": { "Bed": { "stage": "cast", "role": "cast" },
                  "Bolt": { "stage": "fastener", "role": "fastener" } }
    """;

  [Fact]
  public void Fastener_lint_is_quiet_on_a_wrought_bolt_in_a_cast_body() {
    (var findings, int n) = FastenerLint.Check(
      Fixtures.Shape("cast-bolt.json"),
      CastFixtures.Record(FastenerRecord),
      CastFixtures.Settings()
    );
    Assert.Empty(findings);
    Assert.Equal(4, n);
  }

  [Fact]
  public void Fastener_lint_names_a_fastener_off_the_fastener_texture() {
    Shape cast = Fixtures.Edited(
      "cast-bolt.json",
      raw =>
        CastFixtures.Find(raw, "Bolt")["faces"]!["up"]!["texture"] =
          "#cast-iron1"
    );
    (var findings, _) = FastenerLint.Check(
      cast,
      CastFixtures.Record(FastenerRecord),
      CastFixtures.Settings()
    );
    Finding f = Assert.Single(findings);
    Assert.Equal(
      ("error", "fastener", "fastener_texture"),
      (f.Level, f.Code, f.Kind)
    );
    Assert.Equal("Bed/Bolt", f.Path);
  }

  [Fact]
  public void Fastener_lint_names_a_cast_element_on_the_fastener_texture() {
    (var findings, _) = FastenerLint.Check(
      Fixtures.Shape("cast-iron-face.json"),
      CastFixtures.Record(FastenerRecord),
      CastFixtures.Settings()
    );
    Assert.Equal(6, findings.Count);
    Assert.All(findings, f => Assert.Equal("boss_texture", f.Kind));
    Assert.All(findings, f => Assert.Equal("Bed/Lug", f.Path));
  }

  [Fact]
  public void A_face_a_step_cuts_takes_the_steps_finished_role() {
    Shape planed = Fixtures.Edited(
      "cast-ok.json",
      raw => {
        raw["textures"]!["cast-iron-turned1"] = "block/metal/cast-iron-turned1";
        raw.SelectTokens("$..children[?(@.name == 'Way')]")
          .OfType<JObject>()
          .First()["faces"]!["up"]!["texture"] = "#cast-iron-turned1";
      }
    );
    CheckInput Input(string body) =>
      new(
        planed,
        Record: CastFixtures.Record(body),
        Settings: CastFixtures.Settings()
      );

    CheckResult cut = Check.Run(
      Input(
        CastFixtures.CastRecord.Replace("casting", "worked")
          + ","
          + CastFixtures.PlaneWay
      )
    );
    CheckResult uncut = Check.Run(Input(CastFixtures.CastRecord));
    CheckResult cast = Check.Run(
      Input(CastFixtures.CastRecord + "," + CastFixtures.PlaneWay)
    );

    Assert.Equal(0, cut.ExitCode);
    Assert.Equal(1, uncut.ExitCode);
    Assert.Equal(1, cast.ExitCode);
    Assert.Contains(
      "Way up: texture 'cast-iron-turned1', the cast role takes 'cast-iron1'",
      uncut.Lines.Single(l => l.Contains("Way up"))
    );
  }
}
