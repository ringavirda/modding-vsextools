using System.Linq;
using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class FitTests {
  private static readonly Fit Check = new();

  private static CheckResult Run(
    string[] at,
    string[] feeds,
    Shape? filling = null
  ) =>
    Check.Run(
      PourFixtures.Input(
        filling ?? Fixtures.Shape("fill-ok.json"),
        PourFixtures.Record(at, feeds)
      )
    );

  private static Shape Bedded(double lo, double hi) =>
    Reader.Read(
      $$"""
      { "textures": { "andesite": "block/stone/sand/andesite" },
        "elements": [
          { "name": "Piece1", "from": [0,0,0], "to": [0,0,0], "children": [ { "name": "Bedded", "from": [0,0,0], "to": [0,0,0], "children": [
            { "name": "Sand1", "from": [{{lo}}, 2, -3.5], "to": [{{hi}}, 14, 3.5], "faces": { "up": { "texture": "#andesite" } } } ] } ] },
          { "name": "Piece2", "from": [0,0,0], "to": [0,0,0], "children": [ { "name": "Bedded", "from": [0,0,0], "to": [0,0,0], "children": [
            { "name": "Sand2", "from": [12.5, 2, -13.5], "to": [19.5, 14, -6.5], "faces": { "up": { "texture": "#andesite" } } } ] } ] } ] }
      """
    );

  private static readonly string[] Two =
  [
    PourFixtures.At(13, 10, -3),
    PourFixtures.At(13, 10, -13),
  ];

  [Fact]
  public void A_piece_with_its_margin_all_round_passes() {
    CheckResult r = Run([PourFixtures.At(13, 10, -3)], [PourFixtures.Runner1]);
    Assert.Equal(0, r.ExitCode);
    Assert.Contains("fit: 1 clearances read", r.Lines);
  }

  [Fact]
  public void A_piece_past_the_wall_names_the_wall_and_the_gap() {
    CheckResult r = Run([PourFixtures.At(26, 10, -3)], []);
    Assert.Contains("Piece1: -2 from the east wall, margin 1", r.Lines);
  }

  [Fact]
  public void A_piece_nearer_the_floor_than_the_margin_is_a_fault() {
    CheckResult r = Run([PourFixtures.At(13, 2.5, -3)], []);
    Assert.Contains("Piece1: 0.5 from the floor, margin 1", r.Lines);
  }

  [Fact]
  public void Pieces_nearer_than_the_margin_name_both() {
    CheckResult r = Run(
      [PourFixtures.At(13, 10, -3), PourFixtures.At(13, 10, 3.5)],
      [],
      Bedded(12.5, 19.5)
    );
    Assert.Contains("Piece1 and Piece2: 0.5 apart, margin 1", r.Lines);
  }

  [Fact]
  public void A_feed_nearer_a_piece_it_does_not_enter_than_the_margin_is_a_fault() {
    const string near =
      """{ "name": "Near", "kind": "runner", "boxes": [[19.5, 13.5, -3, 20.5, 14, 3]] }""";
    CheckResult r = Run([PourFixtures.At(13, 10, -3)], [near]);
    Assert.Contains("Near and Piece1: 0.5 apart, margin 1", r.Lines);
  }

  [Fact]
  public void A_feed_that_touches_a_piece_enters_it() {
    CheckResult r = Run([PourFixtures.At(13, 10, -3)], [PourFixtures.Runner1]);
    Assert.DoesNotContain(r.Lines, l => l.StartsWith("Runner1"));
  }

  [Fact]
  public void Two_pieces_hold_their_footprint_in_their_bedded_sand() {
    Assert.Equal(0, Run(Two, [], Bedded(12.5, 19.5)).ExitCode);
    CheckResult r = Run(Two, [], Bedded(13, 19.5));
    Assert.Contains(
      "Piece1: its plan grown by 0.5 leaves the Bedded sand by 0.5",
      r.Lines
    );
  }

  [Fact]
  public void A_piece_with_no_bedded_sand_is_a_fault_with_more_than_one_piece() {
    CheckResult r = Run(Two, [], Fixtures.Shape("fill-ok.json"));
    Assert.Contains("Piece1: no Bedded sand to hold its footprint", r.Lines);
  }

  [Fact]
  public void A_margin_too_wide_for_the_piece_is_rejected_by_this_gate_alone() {
    var input = PourFixtures.Input(
      Fixtures.Shape("fill-ok.json"),
      PourFixtures.One(),
      settings: PourFixtures.WithFloorMargin(9)
    );
    Assert.Equal(["fit"], PourFixtures.Rejecting(input));
  }
}
