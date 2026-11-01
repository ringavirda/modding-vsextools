using System;
using System.Collections.Generic;
using Xunit;

namespace ExpandedLib.Shapes.Core.Tests;

public class EngineTests {
  private const string Chain = """
    {
      "elements": [
        {
          "name": "A", "from": [2, 3, 4], "to": [10, 9, 12],
          "rotationX": 10, "rotationOrigin": [6, 6, 8],
          "children": [
            {
              "name": "B", "from": [1, 1, 1], "to": [5, 4, 6],
              "rotationY": 20, "rotationOrigin": [3, 2, 3],
              "children": [
                {
                  "name": "C", "from": [0, 0, 0], "to": [4, 3, 5],
                  "rotationZ": 15, "rotationOrigin": [2, 1, 2]
                }
              ]
            }
          ]
        }
      ]
    }
    """;

  private static void Near(Vec3 expected, Vec3 actual, double tol = 1e-9) {
    Assert.InRange(actual.X, expected.X - tol, expected.X + tol);
    Assert.InRange(actual.Y, expected.Y - tol, expected.Y + tol);
    Assert.InRange(actual.Z, expected.Z - tol, expected.Z + tol);
  }

  [Fact]
  public void Missing_origin_pivots_at_the_parent_frame() {
    Shape s = Reader.Read(
      """{ "elements": [ { "name": "R", "from": [4, 0, 0], "to": [6, 2, 2], "rotationZ": 90 } ] }"""
    );
    Mat4d m = Engine.WorldMatrices(s)["R"];
    Near(new Vec3(0, 4, 0), m.Mul(Vec3.Zero));
  }

  [Fact]
  public void Scale_is_multiplied() {
    Shape s = Reader.Read(
      """{ "elements": [ { "name": "S", "from": [1, 0, 0], "to": [3, 2, 2], "scaleX": 3 } ] }"""
    );
    Box b = WorldBox.Of(s, "S");
    Near(new Vec3(3, 0, 0), b.Lo);
    Near(new Vec3(9, 2, 2), b.Hi);
  }

  [Fact]
  public void A_three_deep_rotated_chain_moved_in_world_space_lands_on_the_asked_box() {
    Shape s = Reader.Read(Chain);
    var asked = new Box(new Vec3(3, 4, 5), new Vec3(8.5, 8, 11));
    WorldBox.SetWorld(s, "A/B/C", asked);
    Box got = WorldBox.Of(s, "A/B/C");
    Near(asked.Lo, got.Lo);
    Near(asked.Hi, got.Hi);
  }

  [Fact]
  public void SetWorld_leaves_the_ancestors_alone() {
    Shape s = Reader.Read(Chain);
    WorldBox.SetWorld(
      s,
      "A/B/C",
      new Box(new Vec3(3, 4, 5), new Vec3(8.5, 8, 11))
    );
    Assert.Equal([2.0, 3.0, 4.0], s.Elements[0].From);
    Assert.Equal([1.0, 1.0, 1.0], s.Elements[0].Children[0].From);
  }

  [Fact]
  public void SetWorld_refuses_a_box_the_rotation_cannot_fit() {
    Shape s = Reader.Read(Chain);
    Assert.Throws<ArgumentException>(() =>
      WorldBox.SetWorld(
        s,
        "A/B/C",
        new Box(new Vec3(0, 0, 0), new Vec3(0.1, 20, 0.1))
      )
    );
  }

  [Fact]
  public void X_and_Y_rotations_turn_as_the_games_matrices_do() {
    Shape s = Reader.Read(
      """
      { "elements": [
        { "name": "X", "from": [0, 0, 0], "to": [1, 1, 1], "rotationX": 90 },
        { "name": "Y", "from": [0, 0, 0], "to": [1, 1, 1], "rotationY": 90 }
      ] }
      """
    );
    Dictionary<string, Mat4d> world = Engine.WorldMatrices(s);
    Near(new Vec3(0, 0, 1), world["X"].Mul(new Vec3(0, 1, 0)));
    Near(new Vec3(0, 0, -1), world["Y"].Mul(new Vec3(1, 0, 0)));
  }

  private const string Octagon = """
    { "elements": [ { "name": "O", "from": [0, 0, 0], "to": [4, 4, 4], "rotationY": 45 } ] }
    """;

  [Fact]
  public void An_element_at_45_degrees_can_be_moved_without_resizing() {
    Shape s = Reader.Read(Octagon);
    Box now = WorldBox.Of(s, "O");
    var asked = new Box(
      now.Lo + new Vec3(3, 1, -2),
      now.Hi + new Vec3(3, 1, -2)
    );
    WorldBox.SetWorld(s, "O", asked);
    Box got = WorldBox.Of(s, "O");
    Near(asked.Lo, got.Lo);
    Near(asked.Hi, got.Hi);
    Assert.Equal([4.0, 4, 4], Engine.Size(s.Elements[0]).ToArray());
  }

  [Fact]
  public void An_element_at_45_degrees_refuses_a_resize() {
    Shape s = Reader.Read(Octagon);
    ArgumentException e = Assert.Throws<ArgumentException>(() =>
      WorldBox.SetWorld(s, "O", new Box(new Vec3(0, 0, 0), new Vec3(9, 4, 9)))
    );
    Assert.Contains("degenerate", e.Message);
  }

  [Fact]
  public void A_clip_with_only_a_name_is_found_by_it() {
    Shape s = Reader.Read(
      """{ "animations": [ { "name": "open", "quantityframes": 4 } ] }"""
    );
    Assert.Equal("open", Poses.Find(s, "open").Name);
  }

  [Fact]
  public void Faces_are_matched_by_first_letter_without_regard_to_case() {
    Shape s = Reader.Read(
      """
      { "elements": [ { "name": "F", "from": [0,0,0], "to": [1,1,1],
          "faces": { "North": { "texture": "#a" }, "e": { "texture": "#a" }, "Up": { "texture": "#a" }, "x": { "texture": "#a" } } } ] }
      """
    );
    Assert.Equal(
      ["north", "east", "up"],
      Engine
        .FaceQuads(s.Elements[0], "F", Mat4d.Identity)
        .ConvertAll(q => q.Face)
    );
  }

  [Fact]
  public void Repeated_sibling_names_take_a_suffix_in_the_path() {
    Shape s = Reader.Read(
      """{ "elements": [ { "name": "P", "from": [0,0,0], "to": [1,1,1], "children": [ { "name": "k" }, { "name": "k" } ] } ] }"""
    );
    Assert.Equal(["P", "P/k", "P/k#2"], Engine.Walk(s).ConvertAll(e => e.Path));
  }

  [Fact]
  public void A_pose_between_two_keyframes_is_interpolated() {
    Shape s = Reader.Read(Swing(version: 0));
    Pose p = Poses.At(s, "swing", 2.5)["Arm"];
    Assert.Equal(45, p.Rotation.Z, 9);
    Assert.Equal(1, p.Stretch.X, 9);
  }

  [Fact]
  public void A_version_1_clip_is_refused_with_its_name() {
    Shape s = Reader.Read(Swing(version: 1));
    NotSupportedException e = Assert.Throws<NotSupportedException>(() =>
      Poses.At(s, "swing", 0)
    );
    Assert.Contains("swing", e.Message);
  }

  [Fact]
  public void Union_volume_counts_an_overlap_once() {
    Shape s = Reader.Read(
      """
      { "elements": [
        { "name": "a", "from": [0,0,0], "to": [2,2,2] },
        { "name": "b", "from": [1,0,1], "to": [3,2,3] }
      ] }
      """
    );
    Assert.Equal(14, WorldBox.UnionVolume(s), 9);
  }

  private static string Swing(int version) =>
    $$"""
      {
        "elements": [ { "name": "Arm", "from": [0,0,0], "to": [2,2,2] } ],
        "animations": [ {
          "code": "swing", "version": {{version}}, "quantityframes": 10,
          "keyframes": [
            { "frame": 0, "elements": { "Arm": { "rotationZ": 0 } } },
            { "frame": 5, "elements": { "Arm": { "rotationZ": 90 } } }
          ]
        } ]
      }
      """;
}
