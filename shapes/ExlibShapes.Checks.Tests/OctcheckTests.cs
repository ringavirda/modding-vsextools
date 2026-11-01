using System.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class OctcheckTests {
  private static readonly Octcheck Check = new();

  public static TheoryData<string> Names =>
    [.. Fixtures.Parity("octcheck").Keys];

  private static CheckResult RunFixture(string name) =>
    name switch {
      "shaft-small.json" => Check.Run(
        new CheckInput(Fixtures.Shape(name), Regular: ["*"])
      ),
      "shaft-small.json (no regular)" => Check.Run(
        new CheckInput(Fixtures.Shape("shaft-small.json"))
      ),
      _ => Check.Run(new CheckInput(Fixtures.Shape(name))),
    };

  private static string[] FaultPaths(CheckResult r) =>
    [
      .. r
        .Lines.Where(l => l.StartsWith("  FAULT "))
        .Select(l => l[8..l.IndexOf(": ", System.StringComparison.Ordinal)]),
    ];

  [Theory]
  [MemberData(nameof(Names))]
  public void Octcheck_parity(string name) {
    Recorded want = Fixtures.Parity("octcheck")[name];
    CheckResult got = RunFixture(name);
    Assert.Equal(want.Exit, got.ExitCode);
    Assert.Equal(want.Enumerated, got.Enumerated);
    Assert.Equal(want.Faulted, FaultPaths(got));
  }

  [Fact]
  public void Octcheck_faults_a_missing_chamfer() {
    CheckResult good = RunFixture("octagon-small.json");
    Assert.Equal(0, good.ExitCode);
    Assert.Contains(
      "Bar  axis y 0.000..6.000  across 4.0000 x 4.0000 on the axes, 4.2426 on the diagonals, corners 2.236 out, worst 0.002",
      good.Lines
    );
    CheckResult bad = RunFixture("octagon-small-missing-chamfer.json");
    Assert.Equal(1, bad.ExitCode);
    Assert.Contains("  FAULT Bar: 3 chamfers turned 45 deg, not 4", bad.Lines);
    Assert.Contains("  FAULT Bar: a hole 0.705 deep at 315.00 deg", bad.Lines);
  }

  [Fact]
  public void Octcheck_regular_glob_holds_every_bar_regular() {
    Assert.Equal(0, RunFixture("shaft-small.json").ExitCode);
    CheckResult halfArms = RunFixture("shaft-small.json (no regular)");
    Assert.Contains(
      "  FAULT Bar: arms 1.6568 wide on 4.0000 across, not 2.0000 (half its across)",
      halfArms.Lines
    );
    CheckResult chamfered = Check.Run(
      new CheckInput(Fixtures.Shape("octagon-small.json"), Regular: ["*"])
    );
    Assert.Equal(0, chamfered.ExitCode);
    Assert.DoesNotContain(chamfered.Lines, l => l.Contains("arms"));
  }

  [Fact]
  public void Octcheck_holds_a_chamfered_rectangular_bar_without_the_arms_rule() {
    CheckResult r = RunFixture("rect-bar-small.json");
    Assert.Equal(0, r.ExitCode);
    Assert.Contains(
      "Rod  axis y 0.000..6.000  across 4.0000 x 3.4000 on the axes, 4.5255 on the diagonals, corners 2.334 out, worst 0.003",
      r.Lines
    );
  }

  [Fact]
  public void Octcheck_rectangle_recogniser_wants_equal_inset_and_outset() {
    CheckResult r = RunFixture("rect-bar-small-unequal-inset.json");
    Assert.Equal(1, r.ExitCode);
    Assert.Contains("  FAULT Rod: 0 transposed boxes, not 1", r.Lines);
  }

  [Fact]
  public void Regular_globs_match_the_whole_path_and_octcheck_leaves_them_to_octregular() {
    CheckResult other = Check.Run(
      new CheckInput(Fixtures.Shape("octagon-small.json"), Regular: ["Bolt*"])
    );
    Assert.Equal(0, other.ExitCode);
    CheckResult named = Check.Run(
      new CheckInput(Fixtures.Shape("octagon-small.json"), Regular: ["B?r"])
    );
    Assert.Equal(0, named.ExitCode);
    Assert.DoesNotContain(named.Lines, l => l.Contains("arms"));
    Assert.Equal(
      1,
      new OctRegular()
        .Run(
          new CheckInput(Fixtures.Shape("octagon-small.json"), Regular: ["B?r"])
        )
        .ExitCode
    );
  }

  [Fact]
  public void Octcheck_does_not_hold_the_shipped_octagons() {
    CheckResult r = Check.Run(
      new CheckInput(
        Fixtures.Shape("octagon-small-missing-chamfer.json"),
        Fixtures.Shape("octagon-small-missing-chamfer.json")
      )
    );
    Assert.Equal(0, r.ExitCode);
    Assert.Contains(
      r.Lines,
      l =>
        l.EndsWith(
          "(fallen's, shipped)  across",
          System.StringComparison.Ordinal
        ) || l.Contains("(fallen's, shipped)")
    );
    Assert.Contains("0 octagons the copy builds; no fault", r.Lines);
  }

  [Fact]
  public void Octcheck_passes_a_bored_gear_hub_unheld() {
    CheckResult r = RunFixture("gearbevel-mitre-proposed.json");
    Assert.Equal(0, r.ExitCode);
    Assert.Contains("Hub1  bored gear hub (eye chamfers), not held", r.Lines);
  }

  [Fact]
  public void Octcheck_reads_a_plate_ring_and_finds_its_bore() {
    CheckResult r = RunFixture("ring-small.json");
    Assert.Equal(0, r.ExitCode);
    Assert.Contains(
      "Ring  plate ring of 8 on y 8.000..12.000  across 4.0000 on the flats, 4.2426 on the diagonals, bore 2.0000, worst 0.002",
      r.Lines
    );
    CheckResult regular = RunFixture("ring-regular-small.json");
    Assert.Contains(
      "Ring  regular plate ring of 8 on y 8.000..12.000  across 4.0000 on the flats, 4.0000 on the diagonals, bore 2.0000, worst 0.002",
      regular.Lines
    );
  }

  [Fact]
  public void Octcheck_shows_the_hole_a_missing_plate_leaves() {
    CheckResult r = RunFixture("ring-small-dropped-plate.json");
    Assert.Equal(1, r.ExitCode);
    Assert.Contains(
      "  FAULT Ring: open on 1 bearings, from 315.00 deg",
      r.Lines
    );
    Assert.Contains("  FAULT Ring: a hole 0.699 deep at 314.75 deg", r.Lines);
  }

  [Fact]
  public void Octcheck_faults_a_plate_turned_off_the_45_deg_steps() {
    CheckResult r = RunFixture("ring-small-off-step.json");
    Assert.Equal(1, r.ExitCode);
    Assert.Contains(
      "  FAULT Ring/P1: a plate turned 75 deg, off the 45 deg steps",
      r.Lines
    );
  }

  [Theory]
  [InlineData(new[] { 0, 45, 90, 135, 180, 200, 250, 290 })]
  [InlineData(new[] { 0, 45, 90, 120, 150 })]
  public void Octcheck_plate_ring_stray_guards(int[] turns) {
    var shape = Fixtures.Edited(
      "ring-small.json",
      raw => {
        var ring = Fixtures.Element(raw, "Ring");
        var kids = (Newtonsoft.Json.Linq.JArray)ring["children"]!;
        while (kids.Count > turns.Length)
          kids.RemoveAt(kids.Count - 1);
        for (int i = 0; i < turns.Length; i++)
          kids[i]["rotationY"] = (double)turns[i];
      }
    );
    CheckResult r = Check.Run(new CheckInput(shape));
    Assert.Contains("0 octagons the copy builds; no fault", r.Lines);
    Assert.Equal(0, r.Enumerated);
  }

  [Fact]
  public void Octcheck_skips_a_16_gon_by_name() {
    var shape = Fixtures.Edited(
      "ring-small.json",
      raw => {
        var kids = (Newtonsoft.Json.Linq.JArray)
          Fixtures.Element(raw, "Ring")["children"]!;
        for (int i = 0; i < 8; i++) {
          var copy = (Newtonsoft.Json.Linq.JObject)kids[i].DeepClone();
          copy["name"] = "Q" + i;
          copy["rotationY"] = 45.0 * i + 22.5;
          kids.Add(copy);
        }
      }
    );
    CheckResult r = Check.Run(new CheckInput(shape));
    Assert.Contains(
      "SKIPPED Ring  16 plates, a 16-gon: held by facetcheck, not by octcheck",
      r.Lines
    );
  }

  [Fact]
  public void Empty_input_fails() {
    CheckResult r = Check.Run(new CheckInput(null));
    Assert.Equal(Verdict.Fail, r.Verdict);
    Assert.Equal(0, r.Enumerated);
    var bare = Fixtures.Edited(
      "octagon-small.json",
      raw => raw["elements"] = new Newtonsoft.Json.Linq.JArray()
    );
    Assert.Equal(1, Check.Run(new CheckInput(bare)).ExitCode);
  }
}
