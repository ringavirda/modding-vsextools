using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json;
using Xunit;
using Xunit.Abstractions;

namespace ExpandedLib.Shapes.Core.Tests;

public class ReaderTests(ITestOutputHelper output) {
  private const string Commented = """
    {
      // a comment
      "textureWidth": 16, /* block */
      "textures": { "a": "game:block/x", },
      "elements": [
        {
          "name": "Root", "from": [0, 0, 0], "to": [16, 16, 16],
          "faces": { "north": { "texture": "#a", "uv": [0, 0, 16, 16], }, },
          "children": [],
        },
      ],
    }
    """;

  [Fact]
  public void Reads_a_commented_trailing_comma_shape() {
    Shape s = Reader.Read(Commented);
    Assert.Equal(16, s.TextureWidth);
    Assert.Equal("game:block/x", s.Textures.Single().Value);
    Element root = s.Elements.Single();
    Assert.Equal("Root", root.Name);
    Assert.Equal([16.0, 16, 16], root.To);
    Assert.Equal([0.0, 0, 16, 16], root.Faces.Single().Value.Uv);
  }

  [Fact]
  public void A_timestamp_string_stays_a_string() {
    Shape s = Reader.Read(
      """{ "textures": {}, "elements": [], "stamp": "2026-11-01T10:00:00Z" }"""
    );
    Assert.Contains("2026-11-01T10:00:00Z", CanonicalWriter.Write(s));
  }

  [Fact]
  public void Reads_unquoted_keys_and_single_quoted_strings() {
    Shape s = Reader.Read("{ textures: { a: 'game:block/x' } }");
    Assert.Equal("game:block/x", s.Textures.Single().Value);
  }

  [Fact]
  public void A_missing_rotationOrigin_stays_null_and_a_written_one_is_kept() {
    Shape s = Reader.Read(
      """
      { "elements": [
        { "name": "A", "from": [1,2,3], "to": [4,5,6] },
        { "name": "B", "from": [1,2,3], "to": [4,5,6], "rotationOrigin": [0,0,0], "rotationY": 45, "scaleX": 2 }
      ] }
      """
    );
    Assert.Null(s.Elements[0].RotationOrigin);
    Assert.Null(s.Elements[0].RotationY);
    Assert.Equal([0.0, 0, 0], s.Elements[1].RotationOrigin);
    Assert.Equal(45, s.Elements[1].RotationY);
    Assert.Equal(2, s.Elements[1].ScaleX);
  }

  [Fact]
  public void Unknown_keys_and_key_order_are_kept_at_every_level() {
    Shape s = Reader.Read(
      """
      { "zeta": 1, "elements": [ { "to": [1,1,1], "name": "A", "client": { "k": true },
          "faces": { "up": { "glow": 255, "texture": "#a", "enabled": false } } } ],
        "editor": { "allAngles": true }, "alpha": [1] }
      """
    );
    Assert.Equal(["zeta", "elements", "editor", "alpha"], s.KeyOrder);
    Assert.Equal(["zeta", "alpha"], s.Unknown.Properties().Select(p => p.Name));
    Assert.True((bool)s.Editor!["allAngles"]!);
    Element el = s.Elements.Single();
    Assert.Equal(["to", "name", "client", "faces"], el.KeyOrder);
    Assert.NotNull(el.Unknown["client"]);
    Face face = el.Faces.Single().Value;
    Assert.Equal(["glow", "texture", "enabled"], face.KeyOrder);
    Assert.Equal(255, (int)face.Unknown["glow"]!);
    Assert.False(face.Enabled);
  }

  [Fact]
  public void Reads_clips_with_version_quantity_and_end_action() {
    Shape s = Reader.Read(
      """
      { "animations": [ { "code": "open", "version": 2, "quantityframes": 20,
          "onAnimationEnd": "Hold", "extra": 1,
          "keyframes": [ { "frame": 0, "elements": { "Door": { "rotationY": 0 } } },
                         { "frame": 19, "elements": { "Door": { "rotationY": 90 } } } ] } ] }
      """
    );
    Clip c = s.Animations.Single();
    Assert.Equal("open", c.Code);
    Assert.Equal(2, c.Version);
    Assert.Equal(20, c.Quantity);
    Assert.Equal("Hold", c.OnAnimationEnd);
    Assert.Equal([0, 19], c.Keyframes.Select(k => k.Frame!.Value));
    Assert.Equal("Door", c.Keyframes[1].Elements.Single().Key);
    Assert.NotNull(c.Unknown["extra"]);
  }

  [Fact]
  public void Reads_the_texture_tables() {
    Shape s = Reader.Read(
      """{ "textureSizes": { "a": [32, 16] }, "textureWidth": 32, "textureHeight": 16 }"""
    );
    Assert.Equal([32.0, 16], s.TextureSizes.Single().Value);
    Assert.Equal(16, s.TextureHeight);
  }

  [Fact]
  public void A_non_object_document_is_refused() {
    Assert.Throws<JsonException>(() => Reader.Read("[1, 2]"));
  }

  [Fact]
  public void A_wrongly_typed_value_is_refused_with_its_key() {
    var e = Assert.Throws<JsonException>(() =>
      Reader.Read("""{ "elements": [ { "from": "x" } ] }""")
    );
    Assert.Contains("\"from\"", e.Message);
  }

  [Fact]
  public void Keys_match_without_regard_to_case_and_numbers_may_be_strings() {
    Shape s = Reader.Read(
      """{ "Elements": [ { "Name": "A", "From": ["1", 2, 3], "To": [4, 5, 6], "RotationY": "45" } ] }"""
    );
    Element el = s.Elements.Single();
    Assert.Equal("A", el.Name);
    Assert.Equal([1.0, 2, 3], el.From);
    Assert.Equal(45, el.RotationY);
    Assert.Empty(el.Unknown);
  }

  [Fact]
  public void A_null_value_is_an_absent_key() {
    Shape s = Reader.Read(
      """{ "textureWidth": null, "elements": [ { "name": "A", "from": null, "rotationX": null, "faces": null } ] }"""
    );
    Assert.Null(s.TextureWidth);
    Element el = s.Elements.Single();
    Assert.Null(el.From);
    Assert.Null(el.RotationX);
    Assert.Empty(el.Faces);
  }

  [Fact]
  public void A_clip_reads_its_name() {
    Shape s = Reader.Read("""{ "animations": [ { "name": "open" } ] }""");
    Assert.Equal("open", s.Animations.Single().Name);
    Assert.Null(s.Animations.Single().Code);
  }

  [Fact]
  public void Core_references_neither_VintagestoryAPI_nor_SkiaSharp() {
    string[] names = typeof(Reader)
      .Assembly.GetReferencedAssemblies()
      .Select(a => a.Name!)
      .ToArray();
    Assert.DoesNotContain(
      names,
      n => n.StartsWith("VintagestoryAPI", StringComparison.Ordinal)
    );
    Assert.DoesNotContain(
      names,
      n => n.StartsWith("SkiaSharp", StringComparison.Ordinal)
    );
    Assert.Contains("Newtonsoft.Json", names);
  }

  [Fact]
  public void Core_project_file_references_only_Newtonsoft() {
    string? csproj = WorkspacePath.Of(
      "shapes/ExlibShapes.Core/ExlibShapes.Core.csproj"
    );
    Assert.NotNull(csproj);
    XElement project = XDocument.Load(csproj!).Root!;
    Assert.Equal(
      ["Newtonsoft.Json"],
      project
        .Descendants("PackageReference")
        .Select(r => (string)r.Attribute("Include")!)
    );
    Assert.Empty(project.Descendants("Reference"));
    Assert.Empty(project.Descendants("ProjectReference"));
  }

  [SkippableFact]
  public void Reads_every_finished_and_iiex_shape() {
    string? workbench = WorkspacePath.Of("workbench");
    string? exmods = WorkspacePath.Of("exmods");
    Skip.If(
      workbench is null && exmods is null,
      "no workbench or exmods checkout beside this one"
    );
    static List<string> ShapeFiles(string root) =>
      Directory.Exists(root)
        ? Directory
          .EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
          .Where(f =>
            f.Replace('\\', '/').Contains("/shapes/")
            || f.Replace('\\', '/').Contains("/shapes-finished/")
          )
          .ToList()
        : [];
    List<string> finished = ShapeFiles(
      Path.Combine(workbench ?? "", "shapes-finished")
    );
    // Mutation: dropping this floor lets a missing workbench pass on iiex's own shapes.
    Assert.True(
      finished.Count >= 400,
      $"only {finished.Count} finished shapes found; clone workbench beside exmods"
    );
    var files = new List<string>(finished);
    if (exmods is not null)
      files.AddRange(ShapeFiles(Path.Combine(exmods, "mods", "iiex")));
    var unreadable = new List<string>();
    foreach (string f in files)
      try {
        Reader.ReadFile(f);
      } catch (JsonException e) {
        unreadable.Add($"{f}: {e.Message}");
      }
    output.WriteLine(
      $"read {files.Count} files, {unreadable.Count} unreadable"
    );
    foreach (string u in unreadable.Take(20))
      output.WriteLine(u);
    Assert.True(files.Count > 1000, $"only {files.Count} files found");
    Assert.Empty(unreadable);
  }
}
