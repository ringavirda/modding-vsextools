using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class ContractTests {
  private sealed class Fake(string id, Profile profile) : ICheck {
    public string Id => id;
    public Profile Profile => profile;

    public CheckResult Run(CheckInput input) =>
      CheckResult.Of(id, 1, false, []);
  }

  [Fact]
  public void Filling_kinds_have_empty_rows_and_every_kind_has_one() {
    foreach (string kind in KindTable.Kinds)
      Assert.NotNull(KindTable.Gates(kind));
    Assert.Equal(
      [
        "impression",
        "flood",
        "capacity",
        "fit",
        "runner",
        "pieces",
        "feeding",
        "pit",
        "parting",
        "corefit",
      ],
      KindTable.Gates("filling")
    );
  }

  [Fact]
  public void A_cope_record_skips_the_gates_that_read_the_sand_and_the_cast() {
    var input = new CheckInput(
      null,
      Record: CastFixtures.Record(
        "\"kinds\": [\"filling\"], \"pour\": { \"half\": \"cope\", \"pair\": \"drag\" }"
      )
    );
    Assert.Equal(
      ["runner"],
      Selection
        .For(["filling"], Profile.Structural)
        .Where(c => Selection.Applies(c, input))
        .Select(c => c.Id)
    );
  }

  [Fact]
  public void Feeding_and_pit_apply_only_where_their_rules_do() {
    CheckInput open = StageFixtures.Input(
      Fixtures.Shape("fill-ok.json"),
      StageFixtures.Record(
        "floor",
        [PourFixtures.At(13, 10, -3)],
        [PourFixtures.Runner1]
      )
    );
    CheckInput head = StageFixtures.Input(
      Fixtures.Shape("fill-ok.json"),
      StageFixtures.Record(
        "floor",
        [PourFixtures.At(13, 10, -3)],
        [
          """{ "name": "Head1", "kind": "head", "into": 1, "boxes": [[14, 13, -1, 16, 15, 1]] }""",
        ]
      )
    );
    CheckInput pit = StageFixtures.Input(
      Fixtures.Shape("fill-ok.json"),
      StageFixtures.Record(
        "pit",
        [PourFixtures.At(13, 0, -3)],
        [PourFixtures.Runner1]
      )
    );
    var feeding = new Feeding();
    var tall = new Pit();
    Assert.False(Selection.Applies(feeding, open));
    Assert.True(Selection.Applies(feeding, head));
    Assert.True(Selection.Applies(feeding, StageFixtures.Closed()));
    Assert.False(Selection.Applies(tall, open));
    Assert.True(Selection.Applies(tall, pit));
  }

  [Fact]
  public void Unknown_kind_is_refused_with_the_kinds() {
    ArgumentException e = Assert.Throws<ArgumentException>(() =>
      KindTable.Gates("sculpture")
    );
    Assert.Contains("sculpture", e.Message);
    Assert.Contains("casting", e.Message);
  }

  [Fact]
  public void Period_checks_run_only_under_the_period_profile() {
    var structural = new Fake("a", Profile.Structural);
    var period = new Fake("b", Profile.ExpandedPeriod);
    Assert.True(Selection.Runs(structural, Profile.Structural));
    Assert.False(Selection.Runs(period, Profile.Structural));
    Assert.True(Selection.Runs(period, Profile.ExpandedPeriod));
    Assert.True(Selection.Runs(structural, Profile.ExpandedPeriod));
    Assert.Equal(Profile.Structural, Selection.ParseProfile("structural"));
    Assert.Equal(
      Profile.ExpandedPeriod,
      Selection.ParseProfile("expanded-period")
    );
    Assert.Throws<ArgumentException>(() => Selection.ParseProfile("strict"));
  }

  [Fact]
  public void Round_uv_and_regular_octagon_rulings_are_period_checks() {
    Assert.Equal(
      ["octregular", "uvwrap"],
      Selection
        .All.Where(c => c.Profile == Profile.ExpandedPeriod)
        .Select(c => c.Id)
        .Order(StringComparer.Ordinal)
    );
    Assert.Equal(
      [
        "allowance",
        "blankmatch",
        "capacity",
        "castonly",
        "corefit",
        "draw",
        "facecheck",
        "feeding",
        "fit",
        "flood",
        "flushcheck",
        "footsweep",
        "impression",
        "octcheck",
        "parting",
        "pieces",
        "pit",
        "role",
        "runner",
        "sizes",
        "split",
      ],
      Selection
        .All.Where(c => c.Profile == Profile.Structural)
        .Select(c => c.Id)
        .Order(StringComparer.Ordinal)
    );
  }

  [Fact]
  public void Selection_follows_the_table_rows() {
    Assert.Equal(
      [
        "impression",
        "flood",
        "capacity",
        "fit",
        "runner",
        "pieces",
        "feeding",
        "pit",
        "parting",
        "corefit",
      ],
      Selection
        .For(["filling", "item"], Profile.ExpandedPeriod)
        .Select(c => c.Id)
    );
    Assert.Throws<ArgumentException>(() =>
      Selection.For(["sculpture"], Profile.Structural)
    );
  }

  [Fact]
  public void Result_of_nothing_enumerated_fails_and_exit_codes_hold() {
    CheckResult none = CheckResult.Of("c", 0, false, []);
    Assert.Equal(1, none.ExitCode);
    Assert.Contains("c: enumerated 0, nothing was checked", none.Lines);
    Assert.Equal(0, CheckResult.Of("c", 3, false, []).ExitCode);
    Assert.Equal(1, CheckResult.Of("c", 3, true, []).ExitCode);
    Assert.Equal(3, CheckResult.Ask("c", 3, []).ExitCode);
    Assert.Equal(2, CheckUsageException.ExitCode);
  }

  [Fact]
  public void Each_check_embeds_its_own_version() {
    string[] ids =
    [
      "facecheck",
      "sizes",
      "flushcheck",
      "zfight",
      "footprint",
      "footsweep",
      "octcheck",
      "octregular",
      "uvwrap",
      "castonly",
      "allowance",
      "split",
      "role",
      "draw",
      "blankmatch",
      "lints",
      "impression",
      "flood",
      "capacity",
      "fit",
      "runner",
      "pieces",
      "feeding",
      "pit",
      "parting",
      "corefit",
    ];
    string[] versions = [.. ids.Select(CheckVersions.Of)];
    Assert.All(versions, v => Assert.Matches("^[0-9a-f]{64}$", v));
    Assert.Equal(ids.Length, versions.Distinct().Count());
    Assert.Throws<ArgumentException>(() => CheckVersions.Of("nope"));
  }
}
