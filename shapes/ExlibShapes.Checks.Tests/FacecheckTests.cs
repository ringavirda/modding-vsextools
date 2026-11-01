using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class FacecheckTests {
  private static readonly Facecheck Check = new();

  public static TheoryData<string> Names =>
    [.. Fixtures.Parity("facecheck").Keys];

  private static CheckResult RunFixture(string name) =>
    name == "head-small-origin.json"
      ? Check.Run(
        new CheckInput(
          Fixtures.Shape(name),
          Fixtures.Shape("head-small.json"),
          Fixtures.Table()
        )
      )
      : Check.Run(new CheckInput(Fixtures.Shape(name)));

  [Theory]
  [MemberData(nameof(Names))]
  public void Facecheck_parity(string name) {
    Recorded want = Fixtures.Parity("facecheck")[name];
    CheckResult got = RunFixture(name);
    Assert.Equal(want.Exit, got.ExitCode);
    Assert.Equal(want.Enumerated, got.Enumerated);
    Assert.Equal(
      want.Faulted,
      Fixtures.FaultedNames(got, "uv against", "changes")
    );
  }

  [Fact]
  public void Facecheck_parity_planted_origin() {
    CheckResult got = RunFixture("head-small-origin.json");
    Assert.Equal(1, got.ExitCode);
    Assert.Contains(
      "Lug up: created, origin 0,0 on random texture 'iron'",
      got.Lines
    );
    Assert.Contains(
      "uv against shipped: 6 faces created, 0 resized, 54 kept; 1 at fault",
      got.Lines
    );
    Assert.Equal(0, RunFixture("head-small.json").ExitCode);
  }

  [Fact]
  public void Empty_input_fails() {
    CheckResult r = Check.Run(new CheckInput(null));
    Assert.Equal(Verdict.Fail, r.Verdict);
    Assert.Equal(0, r.Enumerated);
    Shape bare = Reader.Read("""{"textures": {}, "elements": []}""");
    Assert.Equal(1, Check.Run(new CheckInput(bare)).ExitCode);
  }

  [Fact]
  public void Visible_untextured_face_fails_and_hidden_one_is_skipped() {
    Shape seen = Fixtures.Edited(
      "flush.json",
      r => Fixtures.Element(r, "A")["faces"]!["west"]!["texture"] = "#none"
    );
    CheckResult r = Check.Run(new CheckInput(seen));
    Assert.Equal(1, r.ExitCode);
    Assert.Contains(r.Lines, l => l.StartsWith("A west: texture '#none'"));
    Shape hidden = Fixtures.Edited(
      "flush.json",
      r => Fixtures.Element(r, "B")["faces"]!["west"]!["texture"] = "#none"
    );
    r = Check.Run(new CheckInput(hidden));
    Assert.Equal(0, r.ExitCode);
    Assert.Contains(r.Lines, l => l.EndsWith("1 at fault, 0 of them visible"));
  }

  [Fact]
  public void Shipped_without_table_is_a_usage_error() {
    Assert.Throws<CheckUsageException>(() =>
      Check.Run(
        new CheckInput(
          Fixtures.Shape("flush.json"),
          Fixtures.Shape("flush.json")
        )
      )
    );
  }

  [Fact]
  public void Boss_ring_is_a_round_run() {
    Shape s = Fixtures.Shape("head-small.json");
    var runs = RoundRuns.Find(new Tree(s), new Frames(s));
    Assert.Single(runs);
    Assert.Equal(8, runs[0].Members.Count);
  }

  private static CheckResult Against(
    string file,
    string table,
    System.Action<JObject> edit
  ) =>
    Check.Run(
      new CheckInput(
        Fixtures.Edited(file, edit),
        Fixtures.Shape(file),
        UvTables.Parse(table)
      )
    );

  private static void AddBox(JObject root, double[] uv, double rotation = 0) {
    JObject faces = [];
    foreach (
      string f in new[] { "north", "east", "south", "west", "up", "down" }
    )
      faces[f] = new JObject {
        ["texture"] = "#iron",
        ["uv"] = new JArray(uv),
        ["rotation"] = rotation,
      };
    ((JArray)root["elements"]!).Add(
      new JObject {
        ["name"] = "C",
        ["from"] = new JArray(10, 0, 0),
        ["to"] = new JArray(14, 4, 4),
        ["faces"] = faces,
      }
    );
  }

  [Fact]
  public void Position_row_holds_created_and_kept_faces_to_their_position_uv() {
    const string table = """{"uv": {"iron": "position"}}""";
    CheckResult created = Against(
      "flush.json",
      table,
      r => AddBox(r, [0, 0, 4, 4])
    );
    Assert.Equal(1, created.ExitCode);
    Assert.Contains(
      created.Lines,
      l => l.StartsWith("C north: created, uv [0, 0, 4, 4]")
    );
    Assert.Contains(
      created.Lines,
      l => l.StartsWith("A north: kept, uv [1, 1, 5, 5]")
    );
  }

  [Fact]
  public void Patterned_row_holds_a_created_face_to_its_origin_and_rotation() {
    const string table = """{"uv": {"iron": [2, 3, [0, 90]]}}""";
    CheckResult off = Against(
      "flush.json",
      table,
      r => AddBox(r, [0, 0, 4, 4])
    );
    Assert.Equal(1, off.ExitCode);
    Assert.Contains(
      off.Lines,
      l =>
        l.StartsWith("C north: created, uv [0, 0, 4, 4] rotation 0")
        && l.EndsWith("off 'iron's aligned 2,3 rotation 0/90")
    );
    CheckResult aligned = Against(
      "flush.json",
      table,
      r => AddBox(r, [2, 3, 6, 7], 90)
    );
    Assert.Equal(0, aligned.ExitCode);
  }

  [Fact]
  public void A_changed_kept_face_faults_unless_a_changes_row_allows_it() {
    static void Edit(JObject r) =>
      Fixtures.Element(r, "A")["faces"]!["west"]!["uv"] = new JArray(
        2,
        2,
        6,
        6
      );
    const string plain = """{"uv": {"iron": "random"}}""";
    CheckResult bad = Against("flush.json", plain, Edit);
    Assert.Equal(1, bad.ExitCode);
    Assert.Contains(
      bad.Lines,
      l => l.StartsWith("A west: shipped face changed: uv [1, 1, 5, 5]")
    );
    CheckResult allowed = Against(
      "flush.json",
      """{"uv": {"iron": "random"}, "changes": [{"element": "A", "face": "west", "reason": "moved"}]}""",
      Edit
    );
    Assert.Equal(0, allowed.ExitCode);
    Assert.Contains(allowed.Lines, l => l.EndsWith("; allowed: moved"));
  }

  [Fact]
  public void A_changes_row_for_an_unchanged_face_is_reported_and_for_a_missing_element_is_usage() {
    CheckResult unused = Against(
      "flush.json",
      """{"uv": {"iron": "random"}, "changes": [{"element": "A", "face": "east", "reason": "x"}]}""",
      _ => { }
    );
    Assert.Equal(0, unused.ExitCode);
    Assert.Contains(
      "changes: A east unused, the face is as shipped",
      unused.Lines
    );
    CheckUsageException e = Assert.Throws<CheckUsageException>(() =>
      Against(
        "flush.json",
        """{"uv": {"iron": "random"}, "changes": [{"element": "Nope", "face": "east"}]}""",
        _ => { }
      )
    );
    Assert.Contains("changes: no element Nope", e.Message);
  }

  [Fact]
  public void A_ring_face_moved_along_its_strip_passes_only_as_a_round_run() {
    const string table = """{"uv": {"iron": "random", "plate": "random"}}""";
    CheckResult all = Against(
      "head-small.json",
      table,
      r => {
        foreach (JToken bar in r["elements"]![1]!["children"]!) {
          var uv = (JArray)bar["faces"]!["up"]!["uv"]!;
          uv[0] = (double)uv[0]! + 1;
          uv[2] = (double)uv[2]! + 1;
        }
      }
    );
    Assert.Equal(0, all.ExitCode);
    CheckResult one = Against(
      "head-small.json",
      table,
      r => {
        r["elements"]![1]!["children"]![0]!["faces"]!["up"]!["uv"] = new JArray(
          2,
          4,
          8,
          6
        );
      }
    );
    Assert.Equal(1, one.ExitCode);
    Assert.Contains(
      one.Lines,
      l => l.StartsWith("Boss/Bar0 up: shipped face changed")
    );
  }
}
