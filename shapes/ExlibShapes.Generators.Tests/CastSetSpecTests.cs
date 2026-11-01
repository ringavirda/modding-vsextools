using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Generators.Tests;

public class CastSetSpecTests {
  private const string Machine = """
    { "machine": "planer", "source": { "shape": "machines/planer.json", "sha": "ab12" }, "allowance": 0.25,
      "users": [ { "blank": "castbedsection", "groups": ["Root/Bed/WallE"], "window": [0, 0, 0, 16, 8, 16],
                   "at": { "offset": [1, 2, 3] }, "count": 3,
                   "worked": { "item": "planer-bedsection", "from": "castbedsection",
                     "steps": [ { "step": "plane", "machine": "planer", "cuts": ["ways", "ends"], "features": ["vways"] }, "drill" ] } } ],
      "joined": [ { "item": "lathe-bed", "from": ["a", "b"], "steps": ["plane"] } ],
      "castings": [
        { "item": "cast-planer-table", "source": { "groups": ["Root/Table"] }, "station": "floor", "lie": { "axis": "-z" }, "route": "bored" },
        { "item": "cast-planer-kit", "groups": [ { "groups": ["Root/A"], "at": { "rotate": [0, 90, 0] } }, { "groups": ["Root/B"] } ], "count": 2, "allowance": 0.5 } ],
      "set": { "item": "cast-planer-brackets", "station": "small", "groups": [ { "groups": ["Root/Br1"], "at": { "offset": [0, 0, 0] } } ] } }
    """;

  [Fact]
  public void A_machine_file_reads_every_section() {
    MachineSpec m = Spec.ReadMachine(JObject.Parse(Machine));

    Assert.Equal("planer", m.Machine);
    Assert.Equal("ab12", m.Source.Sha);
    UserSpec u = Assert.Single(m.Users);
    Assert.Equal(3, u.Count);
    Assert.Equal([1.0, 2.0, 3.0], u.At!.Offset);
    Assert.Equal("planer", u.Worked.Steps[0].Machine);
    Assert.Equal(["ways", "ends"], u.Worked.Steps[0].Cuts);
    Assert.Equal("drill", u.Worked.Steps[1].Step);
    Assert.Equal(["a", "b"], Assert.Single(m.Joined).From);
    Assert.Equal(2, m.Castings.Count);
    Assert.Equal("cast-planer-brackets", m.Set!.Item);
  }

  [Fact]
  public void A_casting_inherits_the_machine_source_allowance_and_route() {
    MachineSpec m = Spec.ReadMachine(JObject.Parse(Machine));

    CastingSpec table = m.Castings[0];
    CastingSpec kit = m.Castings[1];
    Assert.Equal(["Root/Table"], table.Groups[0].Groups);
    Assert.Equal(0.25, table.Allowance);
    Assert.Equal("bored", table.Route);
    Assert.Equal(0.5, kit.Allowance);
    Assert.Equal(2, kit.Groups.Count);
    Assert.Equal([0.0, 90.0, 0.0], kit.Groups[0].At!.Rotate);
  }

  [Fact]
  public void The_allowance_defaults_to_an_eighth() {
    BlankSpec b = Spec.ReadBlank(
      JObject.Parse(
        """{ "blank": "x", "size": [1, 1, 1], "form": { "box": [1, 1, 1] } }"""
      )
    );

    Assert.Equal(0.125, b.Allowance);
    Assert.Null(b.CutFaces);
    Assert.Equal(1, b.PerPour);
  }

  [Fact]
  public void An_unknown_key_is_refused_by_name() {
    string bad = Machine.Replace("\"count\": 3,", "\"count\": 3, \"cnt\": 4,");

    var e = Assert.Throws<SpecException>(() =>
      Spec.ReadMachine(JObject.Parse(bad))
    );
    Assert.Contains("cnt", e.Message);
  }

  [Fact]
  public void A_blank_stretching_one_axis_twice_is_refused() {
    var e = Assert.Throws<SpecException>(() =>
      Spec.ReadBlank(
        JObject.Parse(
          """{ "blank": "x", "size": [1, 1, 1], "form": { "shape": "s", "sha": "1", "groups": ["R"], "stretch": [ { "axis": "y", "plane": 1 }, { "axis": "y", "plane": 2 } ] } }"""
        )
      )
    );
    Assert.Contains("twice", e.Message);
  }

  [Fact]
  public void A_box_form_with_a_window_is_refused() {
    Assert.Throws<SpecException>(() =>
      Spec.ReadBlank(
        JObject.Parse(
          """{ "blank": "x", "size": [1, 1, 1], "form": { "box": [1, 1, 1], "window": [0, 0, 0, 1, 1, 1] } }"""
        )
      )
    );
  }

  [Fact]
  public void The_schema_names_both_file_kinds() {
    Assert.Equal(2, ((JArray)Spec.Schema["oneOf"]!).Count);
  }
}
