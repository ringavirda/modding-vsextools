using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Generators.Tests;

public class BlankTests {
  private static readonly string Form = $$"""
    { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
      {{Fx.Cube("Base", "[0, 0, 0]", "[16, 4, 16]")}},
      {{Fx.Cube("Top", "[0, 4, 0]", "[16, 6, 16]")}},
      {{Fx.Cube("Slot", "[2, 1, 2]", "[6, 3, 6]")}},
      {{Fx.Cube("Far", "[40, 0, 0]", "[56, 4, 16]")}}
    ] } ] }
    """;

  private const string FormRecord = """
    { "schema": 1,
      "elements": {
        "Base": { "stage": "cast" }, "Top": { "stage": "cast" },
        "Slot": { "stage": "cast" }, "Far": { "stage": "cast" } },
      "features": {
        "slot": { "elements": ["Slot"], "origin": "planed", "faces": ["Slot/up"], "step": "plane" },
        "ways": { "elements": ["Base"], "origin": "cast", "faces": ["Base/up", "Base/east"], "step": "plane" },
        "ends": { "elements": ["Top"], "origin": "cast", "faces": ["Top/west"], "step": "plane" } } }
    """;

  private static BlankSpec Spec(string cutFaces = "\"from-record\"") =>
    ExpandedLib.Shapes.Generators.Spec.ReadBlank(
      JObject.Parse(
        $$"""
        { "blank": "castbedsection", "size": [16, 10, 16], "perPour": 3, "cutFaces": {{cutFaces}},
          "form": { "shape": "d.json", "sha": "0000000", "groups": ["Root"],
                    "window": [0, 0, 0, 16, 6, 16], "stretch": [ { "axis": "y", "plane": 3.0 } ] } }
        """
      )
    );

  private static BlankResult Build(BlankSpec spec) =>
    Blank.Build(Fx.Ctx(Form, FormRecord), spec);

  [Fact]
  public void A_windowed_stretched_blank_has_the_asked_size_and_no_worked_feature() {
    BlankResult b = Build(Spec());

    Assert.Empty(b.Questions);
    Box e = b.Extent;
    Fx.Near(16, e.Size.X);
    Fx.Near(10, e.Size.Y);
    Fx.Near(16, e.Size.Z);
    Assert.Equal(["Base", "Top"], b.Cast.Select(l => l.Name).Order());
  }

  [Fact]
  public void Stretch_moves_only_faces_past_the_plane() {
    BlankResult b = Build(Spec());

    Box bs = b.Cast.Single(l => l.Name == "Base").Box;
    Box top = b.Cast.Single(l => l.Name == "Top").Box;
    Fx.Near(0, bs.Lo.Y);
    Fx.Near(8, bs.Hi.Y);
    Fx.Near(8, top.Lo.Y);
    Fx.Near(10, top.Hi.Y);
  }

  [Fact]
  public void A_box_form_is_one_cast_element_of_that_size() {
    BlankSpec spec = ExpandedLib.Shapes.Generators.Spec.ReadBlank(
      JObject.Parse(
        """{ "blank": "castplate", "size": [16, 1.5, 16], "form": { "box": [16, 1.5, 16] } }"""
      )
    );

    BlankResult b = Build(spec);

    Leaf l = Assert.Single(b.Cast);
    Fx.Near(new Vec3(16, 1.5, 16), l.Box.Size);
    Assert.Equal("#cast-iron1", l.Element.Faces[0].Value.Texture);
  }

  [Fact]
  public void Cut_faces_come_from_the_record_steps_by_default() {
    BlankResult b = Build(Spec());

    Assert.Equal(["ends", "ways"], b.CutFeatures.Order());
    Assert.Contains("Base/up", b.CutFaces);
    Assert.Contains("Top/west", b.CutFaces);
  }

  [Fact]
  public void Named_cut_features_replace_the_default_and_must_exist() {
    BlankResult b = Build(Spec("[\"ways\"]"));

    Assert.Equal(["ways"], b.CutFeatures);
    Assert.Equal(["Base/up", "Base/east"], b.CutFaces);
    var e = Assert.Throws<CastGenException>(() => Build(Spec("[\"ghost\"]")));
    Assert.Contains("ghost", e.Message);
  }

  [Fact]
  public void An_untagged_form_element_is_a_question_and_no_form() {
    string record = FormRecord.Replace("\"Top\": { \"stage\": \"cast\" },", "");
    BlankResult b = Blank.Build(Fx.Ctx(Form, record), Spec());

    Assert.Equal("ends", Assert.Single(b.Questions).Feature);
    Assert.Empty(b.Cast);
  }
}
