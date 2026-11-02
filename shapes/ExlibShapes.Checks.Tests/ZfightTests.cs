using System;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class ZfightTests {
  public static TheoryData<string> Names => [.. Fixtures.Parity("zfight").Keys];

  [Theory]
  [MemberData(nameof(Names))]
  public void Zfight_parity(string name) {
    Recorded want = Fixtures.Parity("zfight")[name];
    var (pairs, faces) = ZfightLint.Pairs(Fixtures.Shape(name));
    var got = pairs
      .Select(p =>
        (
          Name: string.CompareOrdinal(p.Path1, p.Path2) < 0
            ? $"{p.Path1} | {p.Path2}"
            : $"{p.Path2} | {p.Path1}",
          p.Area
        )
      )
      .OrderBy(t => t.Name, StringComparer.Ordinal)
      .ToList();
    Assert.Equal(want.Exit, pairs.Count > 0 ? 1 : 0);
    Assert.Equal(want.Enumerated, faces);
    Assert.Equal(want.Faulted, got.Select(t => t.Name));
    for (int i = 0; i < got.Count; i++)
      Assert.Equal(want.Areas[i], got[i].Area, 1e-4);
  }

  private static Shape TopsAndLid(bool lid) {
    string face = """{"texture": "#x"}""";
    string faces = string.Join(
      ",",
      new[] { "north", "east", "south", "west", "up", "down" }.Select(f =>
        $"\"{f}\": {face}"
      )
    );
    string[] spans =
    [
      "[0,0,0], \"to\": [4,2,4]",
      "[2,1,1], \"to\": [6,2,3]",
      .. lid ? new[] { "[1,2,0], \"to\": [5,3,4]" } : [],
    ];
    string els = string.Join(
      ",",
      spans.Select(
        (s, n) =>
          $"{{\"name\": \"B{n}\", \"from\": {s}, \"faces\": {{{faces}}}}}"
      )
    );
    return Reader.Read($"{{\"textures\": {{}}, \"elements\": [{els}]}}");
  }

  [Fact]
  public void Zfight_holds_a_visible_coplanar_pair_and_passes_a_covered_one() {
    var (seen, _) = ZfightLint.Check(TopsAndLid(false));
    Finding f = Assert.Single(seen);
    Assert.Equal("B0", f.Path);
    Assert.Contains("B1", f.Message);
    Assert.Contains("up face", f.Message);
    var (pairs, _) = ZfightLint.Pairs(TopsAndLid(false));
    Assert.Equal(4.0, Assert.Single(pairs).Area, 0.1);
    Assert.Empty(ZfightLint.Check(TopsAndLid(true)).Findings);
  }

  private static Shape Bars(double rise) {
    static string Bar(string name, double y) =>
      """
        {"name": "NAME", "from": [2, Y0, 2], "to": [12, Y1, 3],
         "rotationOrigin": [2, Y0, 2], "rotationZ": 45,
         "faces": {"north": {"texture": "#x"}}}
        """.Replace("NAME", name).Replace(
        "Y0",
        y.ToString(System.Globalization.CultureInfo.InvariantCulture)
      ).Replace(
        "Y1",
        (y + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
      );
    return Reader.Read(
      "{\"textures\": {}, \"elements\": ["
        + Bar("Low", 4)
        + ","
        + Bar("High", 4 + rise)
        + "]}"
    );
  }

  [Fact]
  public void Zfight_holds_faces_turned_in_their_plane_to_their_exact_overlap() {
    Assert.Empty(ZfightLint.Check(Bars(3.0)).Findings);
    Finding f = Assert.Single(ZfightLint.Check(Bars(0.5)).Findings);
    Assert.Contains("north face", f.Message);
  }

  [Fact]
  public void Zfight_ignores_overlapping_faces_in_different_planes() {
    Shape s = Reader.Read(
      """
      {"textures": {}, "elements": [
        {"name": "Low", "from": [0, 0, 0], "to": [4, 2, 4], "faces": {"up": {"texture": "#x"}}},
        {"name": "High", "from": [2, 0, 1], "to": [6, 2.5, 3], "faces": {"up": {"texture": "#x"}}}]}
      """
    );
    var (findings, faces) = ZfightLint.Check(s);
    Assert.Empty(findings);
    Assert.Equal(2, faces);
  }
}
