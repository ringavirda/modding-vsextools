using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class LintsTests {
  [Fact]
  public void Every_decided_ruling_names_a_check_or_the_fastener_lint() {
    string[] ids =
    [
      .. Selection.All.Select(c => c.Id),
      Catalogue.FastenerDecider,
    ];
    Assert.All(Catalogue.Decided, r => Assert.Contains(r.Decider, ids));
    Assert.All(
      Catalogue.Decided,
      r => Assert.Matches(@"^\d{4}-\d\d-\d\d$", r.Date)
    );
    Assert.All(Catalogue.Decided, r => Assert.NotEqual("", r.Message));
    Assert.Equal(
      Catalogue.Decided.Count,
      Catalogue.Decided.Select(r => r.Id).Distinct().Count()
    );
  }

  [Fact]
  public void The_fastener_ruling_carries_the_lints_own_id_date_and_message() {
    RulingLint r = Assert.Single(Catalogue.Decided, r => r.Id == "fastener");
    Assert.Equal(FastenerLint.Ruling, r.Date);
    Assert.Equal(FastenerLint.Message, r.Message);
  }

  [Fact]
  public void Catalogue_lists_the_decided_rulings_then_the_ones_that_need_an_eye() {
    var lines = Catalogue.Lines();
    int eye = lines.ToList().IndexOf("needs an eye:");
    Assert.Equal(Catalogue.Decided.Count, eye);
    Assert.Equal(Catalogue.Eye.Count, lines.Count - eye - 1);
    Assert.Equal(
      "castings-hold-the-pour  2026-10-19  a cast item holds only the iron one pour makes: no bolts, brasses, keys or holes  [castonly]",
      lines[0]
    );
    Assert.All(lines, l => Assert.DoesNotContain('—', l));
  }

  private static PartRecord Record(string regular = "") =>
    CastFixtures.Record(
      "\"kinds\": [\"casting\"]" + (regular == "" ? "" : ", " + regular)
    );

  [Fact]
  public void Octregular_applies_only_where_the_record_names_regular_globs() {
    var octregular = new OctRegular();
    var shape = Fixtures.Shape("octagon-small.json");
    Assert.False(
      Selection.Applies(octregular, new CheckInput(shape, Record: Record()))
    );
    Assert.False(
      Selection.Applies(
        octregular,
        new CheckInput(shape, Record: Record("\"regular\": []"))
      )
    );
    Assert.False(Selection.Applies(octregular, new CheckInput(shape)));
    var named = new CheckInput(
      shape,
      Record: Record("\"regular\": [\"Bolt*\"]")
    );
    Assert.True(Selection.Applies(octregular, named));
    Assert.Equal(["Bolt*"], Selection.RegularGlobs(named.Record!));
  }

  [Fact]
  public void Uvwrap_applies_only_to_a_shape_with_a_round_run() {
    var uvwrap = new Uvwrap();
    Assert.True(
      Selection.Applies(
        uvwrap,
        new CheckInput(Fixtures.Shape("uvwrap-ring.json"))
      )
    );
    Assert.False(
      Selection.Applies(uvwrap, new CheckInput(Fixtures.Shape("cast-ok.json")))
    );
  }

  [Fact]
  public void Blankmatch_applies_only_to_a_record_naming_its_blank() {
    var shape = Fixtures.Shape("cast-ok.json");
    var check = new Blankmatch();
    Assert.False(
      Selection.Applies(check, new CheckInput(shape, Record: Record()))
    );
    Assert.True(
      Selection.Applies(
        check,
        new CheckInput(shape, Record: Record("\"blank\": \"castbedsection\""))
      )
    );
  }

  [Fact]
  public void The_casting_pattern_and_worked_rows_name_their_gates() {
    Assert.Equal(
      ["castonly", "allowance", "split", "role", "uvwrap", "octregular"],
      KindTable.Gates("casting")
    );
    Assert.Equal(["role", "draw"], KindTable.Gates("pattern"));
    Assert.Equal(["role", "blankmatch"], KindTable.Gates("worked"));
    Assert.Equal(
      [
        "castonly",
        "allowance",
        "split",
        "role",
        "uvwrap",
        "octregular",
        "draw",
      ],
      KindTable.Gates(["casting", "pattern"])
    );
    Assert.Equal(
      [
        "castonly",
        "allowance",
        "split",
        "role",
        "uvwrap",
        "octregular",
        "draw",
      ],
      Selection
        .For(["casting", "pattern"], Profile.ExpandedPeriod)
        .Select(c => c.Id)
    );
    Assert.Equal(
      ["castonly", "allowance", "split", "role", "draw"],
      Selection
        .For(["casting", "pattern"], Profile.Structural)
        .Select(c => c.Id)
    );
  }
}
