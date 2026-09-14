using System;
using System.Collections.Generic;
using System.Numerics;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Covers Geometry's rotation convention and composition order.</summary>
public class GeometryTests {
  private static Vector3 RotVec(float rx, float ry, float rz, Vector3 v) =>
    Vector3.Transform(v, Geometry.RotateByXyz(rx, ry, rz));

  private static void AssertClose(Vector3 expected, Vector3 actual, float atol) {
    Assert.True(MathF.Abs(expected.X - actual.X) <= atol, $"X: expected {expected.X}, got {actual.X}");
    Assert.True(MathF.Abs(expected.Y - actual.Y) <= atol, $"Y: expected {expected.Y}, got {actual.Y}");
    Assert.True(MathF.Abs(expected.Z - actual.Z) <= atol, $"Z: expected {expected.Z}, got {actual.Z}");
  }

  [Fact]
  public void Rotation_convention_is_right_handed() {
    AssertClose(new Vector3(0.7071f, 0, -0.7071f), RotVec(0, 45, 0, new Vector3(1, 0, 0)), 1e-3f);
    AssertClose(new Vector3(0.7071f, 0.7071f, 0), RotVec(0, 0, -45, new Vector3(0, 1, 0)), 1e-3f);
    AssertClose(new Vector3(0, 0.7071f, 0.7071f), RotVec(45, 0, 0, new Vector3(0, 1, 0)), 1e-3f);
  }

  [Fact]
  public void Rotate_by_xyz_composes_x_after_y_after_z() {
    // "after" in the column-vector sense the docstring states: Rx . Ry . Rz applied to a point
    // applies Rz first. float32 gives ~1e-6 of slack.
    Matrix4x4 m = Geometry.RotateByXyz(30, 40, 50);
    // The game's Rx@Ry@Rz@v applies Rz first, then Ry, then Rx; System.Numerics' row-vector
    // A*B applies A first, so the matching composition order here is reversed: z, y, x.
    Matrix4x4 expected =
      Geometry.RotateByXyz(0, 0, 50) * Geometry.RotateByXyz(0, 40, 0) * Geometry.RotateByXyz(30, 0, 0);
    Assert.True(MatrixClose(m, expected, 1e-5f));
  }

  private static bool MatrixClose(Matrix4x4 a, Matrix4x4 b, float atol) =>
    MathF.Abs(a.M11 - b.M11) <= atol
    && MathF.Abs(a.M12 - b.M12) <= atol
    && MathF.Abs(a.M13 - b.M13) <= atol
    && MathF.Abs(a.M21 - b.M21) <= atol
    && MathF.Abs(a.M22 - b.M22) <= atol
    && MathF.Abs(a.M23 - b.M23) <= atol
    && MathF.Abs(a.M31 - b.M31) <= atol
    && MathF.Abs(a.M32 - b.M32) <= atol
    && MathF.Abs(a.M33 - b.M33) <= atol;

  private static LoadedShape ShapeFromJson(string json) {
    Shape raw = JsonConvert.DeserializeObject<Shape>(json)!;
    return ShapeFile.FromRaw(raw, null);
  }

  [Fact]
  public void Child_coordinates_are_relative_to_parent_from() {
    string json = """
      {"elements": [{"name": "Lid", "from": [-12, 17, -12], "to": [4, 18, 4], "faces": {},
        "children": [{"name": "Cube53", "from": [29, 0, 0], "to": [40, 1, 4],
          "faces": {"up": {"texture": "#a", "uv": [0, 0, 11, 4]}}}]}]}
      """;
    LoadedShape shape = ShapeFromJson(json);
    var mats = Geometry.WorldMatrices(shape);
    Node cube53 = shape.Find("Lid/Cube53")!;
    var (lo, hi) = Geometry.Aabb(Geometry.Corners(mats[cube53.Path], (Vector3)cube53.Size));
    Assert.Equal(17, MathF.Round(lo.X));
    Assert.Equal(28, MathF.Round(hi.X));
    Assert.Equal(17, MathF.Round(lo.Y));
    Assert.Equal(18, MathF.Round(hi.Y));
  }

  [Fact]
  public void Rotation_about_origin_pivots_the_far_edge() {
    // a 1.4-long plate pivoted at (4,0,5) by +45 about y runs toward (5,0,4)
    string json = """
      {"elements": [{"name": "P", "from": [4, 0, 5], "to": [5.4, 12, 6], "rotationOrigin": [4, 0, 5],
        "rotationY": 45, "faces": {"up": {"texture": "#a", "uv": [0, 0, 1.4, 1]}}}]}
      """;
    LoadedShape shape = ShapeFromJson(json);
    Matrix4x4 m = Geometry.WorldMatrices(shape)["P"];
    Vector3[] c = Geometry.Corners(m, (Vector3)shape.Elements[0].Size);
    Vector3 far = c[4]; // x=size, y=0, z=0 corner
    AssertClose(new Vector3(4 + 1.4f * 0.7071f, 0, 5 - 1.4f * 0.7071f), far, 1e-3f);
  }

  [Fact]
  public void Corner_index_bits() {
    Vector3[] c = Geometry.Corners(Matrix4x4.Identity, new Vector3(2, 3, 5));
    AssertClose(new Vector3(2, 0, 0), c[4], 1e-6f);
    AssertClose(new Vector3(0, 3, 0), c[2], 1e-6f);
    AssertClose(new Vector3(0, 0, 5), c[1], 1e-6f);
  }

  [Fact]
  public void Touching_tolerance() {
    (Vector3, Vector3) a = (Vector3.Zero, Vector3.One);
    Assert.True(Geometry.Touching(a, (new Vector3(1.04f, 0, 0), new Vector3(2, 1, 1))));
    Assert.False(Geometry.Touching(a, (new Vector3(1.06f, 0, 0), new Vector3(2, 1, 1))));
  }

  [Fact]
  public void Face_quads_of_a_unit_cube_have_outward_normals() {
    string faces = string.Join(
      ",",
      Array.ConvertAll(Geometry.Faces, f => $"\"{f}\": {{\"texture\": \"#a\", \"uv\": [0,0,1,1]}}")
    );
    string json =
      "{\"elements\": [{\"name\": \"C\", \"from\": [0,0,0], \"to\": [1,1,1], \"faces\": {"
      + faces
      + "}}]}";
    LoadedShape shape = ShapeFromJson(json);
    Node el = shape.Elements[0];
    var quads = Geometry.FaceQuads(el, Matrix4x4.Identity);
    Assert.Equal(6, quads.Count);
    foreach (Geometry.Quad q in quads) {
      Vector3 centre = (q.Points[0] + q.Points[1] + q.Points[2] + q.Points[3]) / 4f;
      Assert.True(Vector3.Dot(q.Normal, centre - new Vector3(0.5f)) > 0.49f);
    }
  }

  [Fact]
  public void Owner_lathed_cylinder_chords_land_on_the_flats() {
    LoadedShape shape = ShapeFile.Load(FixturePath.Of("items/machined/item-lathed-cylinder.json"));
    var mats = Geometry.WorldMatrices(shape);
    Node target = shape.Find("Cylinder/Cube4/Cube2")!;
    var (lo, _) = Geometry.Aabb(Geometry.Corners(mats[target.Path], (Vector3)target.Size));
    // the north-west chord spans from the west flat (x 4) to the north flat (z 4)
    Assert.True(MathF.Abs(lo.X - 4) < 0.05f);
    Assert.True(MathF.Abs(lo.Z - 4) < 0.05f);
  }

  // Geometry's public WorldMatrices/FaceQuads (float32, System.Numerics) has no production
  // caller: the renderer keeps its own double-precision mirror (Renderer.WorldMatricesD /
  // FaceQuadsD) to survive a strict z-test at exactly coincident faces, and RendererTests' pixel
  // comparison only exercises that mirror. This cross-checks every leaf and quad of a real,
  // multi-element fixture between the two chains, so a bug in the public API that a hand-picked
  // unit fixture would miss cannot pass silently.
  [Fact]
  public void Public_world_matrices_and_face_quads_agree_with_the_verified_double_chain() {
    LoadedShape shape = ShapeFile.Load(FixturePath.Of("items/machined/item-shaped-gearpinion.json"));
    Dictionary<string, Matrix4x4> floatMats = Geometry.WorldMatrices(shape);
    Dictionary<string, Renderer.Mat4d> doubleMats = Renderer.WorldMatricesD(shape, null);

    foreach (Node leaf in shape.Leaves()) {
      Matrix4x4 fm = floatMats[leaf.Path];
      List<Geometry.Quad> floatQuads = Geometry.FaceQuads(leaf, fm);
      List<Renderer.QuadD> doubleQuads = Renderer.FaceQuadsD(leaf, doubleMats[leaf.Path]);
      Assert.Equal(doubleQuads.Count, floatQuads.Count);

      for (int i = 0; i < floatQuads.Count; i++) {
        Geometry.Quad fq = floatQuads[i];
        Renderer.QuadD dq = doubleQuads[i];
        Assert.Equal(dq.Texture, fq.Texture);
        for (int p = 0; p < 4; p++)
          AssertClose(
            new Vector3((float)dq.Points[p].X, (float)dq.Points[p].Y, (float)dq.Points[p].Z),
            fq.Points[p],
            1e-3f
          );
        AssertClose(new Vector3((float)dq.Normal.X, (float)dq.Normal.Y, (float)dq.Normal.Z), fq.Normal, 1e-4f);
      }
    }
  }
}
