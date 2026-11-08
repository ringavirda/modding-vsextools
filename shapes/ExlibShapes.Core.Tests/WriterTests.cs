using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace ExpandedLib.Shapes.Core.Tests;

public partial class WriterTests(ITestOutputHelper output) {
  private static string Fixture(string name) =>
    Path.Combine(AppContext.BaseDirectory, "fixtures", "writers", name);

  private static string Text(string name) => File.ReadAllText(Fixture(name));

  private static Settings Registry() =>
    Settings.ReadFile(
      Path.Combine(AppContext.BaseDirectory, "fixtures", "settings.json")
    );

  private static readonly Dictionary<string, string> Ends = new() {
    ["spin"] = "Repeat",
    ["open"] = "Hold",
    ["work"] = "Repeat",
  };

  private static string Runtime(string editable) =>
    RuntimeWriter.Write(Reader.Read(Text(editable)), Registry(), "iiex", Ends);

  /// <summary>A registry that maps each of the shape's texture keys to <c>game:&lt;key&gt;</c>.</summary>
  private static Settings RegistryFor(Shape shape) {
    JObject doc = JObject.Parse(
      File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "settings.json")
      ),
      new JsonLoadSettings { CommentHandling = CommentHandling.Ignore }
    );
    var textures = (JObject)doc["textures"]!;
    foreach (var (key, _) in shape.Textures)
      textures[key] = new JObject {
        ["editable"] = key,
        ["runtime"] = "game:" + key,
        ["class"] = "plain",
      };
    return Settings.Read(doc.ToString());
  }

  private static string Geometry(Shape s) {
    s.Textures.Clear();
    s.Animations.Clear();
    s.Editor = null;
    s.TextureSizes.Clear();
    return CanonicalWriter.Write(s);
  }

  [Theory]
  [InlineData("plain")]
  [InlineData("root")]
  [InlineData("items")]
  [InlineData("repeat")]
  public void Runtime_output_equals_the_converters_bytes(string name) =>
    Assert.Equal(Text(name + ".runtime.json"), Runtime(name + ".json"));

  [Theory]
  [InlineData("plain")]
  [InlineData("root")]
  [InlineData("items")]
  public void Editable_to_runtime_to_editable_is_stable_for_geometry(
    string name
  ) {
    string runtime = Runtime(name + ".json");
    Shape back = Reader.Read(runtime);
    Assert.Equal(runtime, RuntimeWriter.Write(back, Registry(), "iiex", Ends));
    Assert.Equal(
      Geometry(Reader.Read(Text(name + ".runtime.json"))),
      Geometry(back)
    );
    if (name == "plain")
      Assert.Equal(Geometry(Reader.Read(Text("plain.json"))), Geometry(back));
  }

  [Fact]
  public void Root_group_children_are_lifted_with_the_groups_origin_folded_in() {
    Shape back = Reader.Read(Runtime("root.json"));
    Assert.Equal(["Cube1", "Cube2", "Free"], back.Elements.Select(e => e.Name));
    Assert.Equal([17.0, 2, 19.5], back.Elements[0].From);
    Assert.Equal([18.0, 3, 20.5], back.Elements[0].RotationOrigin);
    Assert.Equal([16.5, 0, 16.5], back.Elements[1].From);
  }

  [Fact]
  public void Items_group_and_its_keyframe_entries_are_dropped() {
    Shape back = Reader.Read(Runtime("items.json"));
    Assert.Empty(back.Elements.Single().Children);
    string[] posed = back
      .Animations.Single()
      .Keyframes.SelectMany(k => k.Elements.Select(e => e.Key))
      .ToArray();
    Assert.Equal(["Wheel", "Wheel"], posed);
  }

  [Fact]
  public void Items_group_is_kept_when_not_asked_to_drop() {
    string text = RuntimeWriter.Write(
      Reader.Read(Text("items.json")),
      Registry(),
      "iiex",
      dropItems: false
    );
    Assert.Equal(
      "Items",
      Reader.Read(text).Elements.Single().Children.Single().Name
    );
  }

  [Fact]
  public void Repeat_clip_wrap_is_closed_and_a_whole_turn_flagged() {
    var clip = JObject.Parse(Runtime("repeat.json"))["animations"]![0]!;
    JToken last = clip["keyframes"]!.Last!["elements"]!;
    Assert.Equal(true, (bool?)last["Shaft"]!["rotShortestDistanceX"]);
    Assert.Equal(1, (int)last["Arm"]!["offsetY"]!);
  }

  [Fact]
  public void Unmapped_key_leaves_no_file() {
    Shape s = Reader.Read(Text("plain.json"));
    s.Textures.Add(new("mystery", "//x/mystery"));
    string dir = Directory.CreateTempSubdirectory().FullName;
    string target = Path.Combine(dir, "out.json");
    var e = Assert.Throws<ShapeWriteException>(() =>
      RuntimeWriter.WriteFile(target, s, Registry(), "iiex")
    );
    Assert.Contains("mystery", e.Message);
    Assert.Empty(Directory.GetFileSystemEntries(dir));

    File.WriteAllText(target, "kept");
    Assert.Throws<ShapeWriteException>(() =>
      RuntimeWriter.WriteFile(target, s, Registry(), "iiex")
    );
    Assert.Equal("kept", File.ReadAllText(target));
    Directory.Delete(dir, true);
  }

  [Fact]
  public void Every_unmapped_key_is_named_in_one_message() {
    Shape s = Reader.Read(Text("plain.json"));
    s.Textures.Add(new("mystery", "x"));
    s.Textures.Add(new("riddle", "y"));
    var e = Assert.Throws<ShapeWriteException>(() =>
      RuntimeWriter.Write(s, Registry(), "iiex")
    );
    Assert.Contains("mystery", e.Message);
    Assert.Contains("riddle", e.Message);
  }

  [Fact]
  public void Vsmc_saved_fixture_is_written_back_byte_for_byte() {
    string text = Text("vsmc.json");
    Assert.Equal(text, VsmcWriter.Write(Reader.Read(text)));
    Assert.Equal(text + "\r\n", VsmcWriter.Write(Reader.Read(text + "\r\n")));
  }

  [Fact]
  public void Vsmc_layout_shows_only_an_edit_in_the_diff() {
    string text = Text("vsmc.json");
    Shape s = Reader.Read(text);
    s.Elements[0].To![0] = 12.0;
    string[] before = text.Split("\r\n");
    string[] after = VsmcWriter.Write(s).Split("\r\n");
    Assert.Equal(before.Length, after.Length);
    int[] changed = Enumerable
      .Range(0, before.Length)
      .Where(i => before[i] != after[i])
      .ToArray();
    Assert.Single(changed);
    Assert.Equal("\t\t\t\"to\": [ 12.0, 1.123456, 10.0 ],", after[changed[0]]);
  }

  [Fact]
  public void Canonical_output_has_fixed_order_and_rounded_numbers() {
    string text = CanonicalWriter.Write(Reader.Read(Text("vsmc.json")));
    Assert.EndsWith("\n", text);
    Assert.DoesNotContain("\r", text);
    Assert.Contains("\"to\": [ 11, 1.1235, 10 ],", text);
    Assert.True(
      text.IndexOf("\"textureWidth\"", StringComparison.Ordinal)
        < text.IndexOf("\"textures\"", StringComparison.Ordinal)
    );
    Assert.True(
      text.IndexOf("\"rotationX\"", StringComparison.Ordinal)
        < text.IndexOf("\"rotationZ\"", StringComparison.Ordinal)
    );
    Assert.Equal(text, CanonicalWriter.Write(Reader.Read(text)));
  }

  [Fact]
  public void Atomic_write_replaces_the_file_and_leaves_no_temporary() {
    string dir = Directory.CreateTempSubdirectory().FullName;
    string path = Path.Combine(dir, "sub", "a.json");
    AtomicFile.WriteAllText(path, "one");
    AtomicFile.WriteAllText(path, "two\r\n");
    Assert.Equal("two\r\n", File.ReadAllText(path));
    Assert.Equal(
      [path],
      Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
    );
    Directory.Delete(dir, true);
  }

  [Fact]
  public void Atomic_write_that_fails_leaves_no_temporary() {
    string dir = Directory.CreateTempSubdirectory().FullName;
    string path = Path.Combine(dir, "a.json");
    Directory.CreateDirectory(path);
    Assert.ThrowsAny<IOException>(() => AtomicFile.WriteAllText(path, "x"));
    Assert.Empty(Directory.GetFiles(dir));
    Directory.Delete(dir, true);
  }

  [SkippableFact]
  public void Every_vsmc_saved_workspace_file_is_written_back_byte_for_byte() {
    string? root = WorkspacePath.Of("workbench");
    Skip.If(root is null, "no workbench checkout beside this one");
    var files = Directory
      .EnumerateFiles(root!, "*.json", SearchOption.AllDirectories)
      .Where(f => {
        string text = File.ReadAllText(f);
        return text.Contains("\r\n") && !EmptiedSizes().IsMatch(text);
      })
      .ToList();
    var different = files
      .Where(f => {
        string text = File.ReadAllText(f);
        return VsmcWriter.Write(Reader.Read(text)) != text;
      })
      .ToList();
    output.WriteLine($"{files.Count} CRLF files, {different.Count} differ");
    foreach (string f in different.Take(20))
      output.WriteLine(f);
    Assert.True(files.Count > 50, $"only {files.Count} CRLF files found");
    Assert.Empty(different);
  }

  [SkippableTheory]
  [InlineData("items/cast-lathe-head-proposed.json")]
  [InlineData("furnaces/coke-oven/furnace-megablock-chargedoor-proposed.json")]
  [InlineData("machine-tools/lathe/machine-mp-megablock-lathe-proposed.json")]
  public void Finished_shape_is_stable_for_geometry_through_runtime(string rel) {
    string? path = WorkspacePath.Of("workbench/shapes-finished/" + rel);
    Skip.If(path is null, "no workbench checkout beside this one");
    Shape shape = Reader.ReadFile(path!);
    Settings registry = RegistryFor(shape);
    string runtime = RuntimeWriter.Write(shape, registry, "iiex");
    Shape back = Reader.Read(runtime);
    Assert.Equal(runtime, RuntimeWriter.Write(back, registry, "iiex"));
    Assert.DoesNotContain("\"Items\"", runtime);
    foreach (PathedElement pe in Engine.Walk(shape)) {
      string at = pe.Path;
      if (at == "Items" || at.StartsWith("Items/"))
        continue;
      if (at == "Root" && pe.Element.Children.Count > 0)
        continue;
      string lifted =
        shape.Elements.Any(e => e.Name == "Root" && e.Children.Count > 0)
        && at.StartsWith("Root/")
          ? at["Root/".Length..]
          : at;
      Box was = WorldBox.Of(shape, at);
      Box now = WorldBox.Of(back, lifted);
      Assert.True(
        Near(was.Lo, now.Lo) && Near(was.Hi, now.Hi),
        $"{at}: {was} vs {now}"
      );
    }
    output.WriteLine($"{rel}: {runtime.Length} bytes");
  }

  private static bool Near(Vec3 a, Vec3 b) =>
    Math.Abs(a.X - b.X) < 1e-4
    && Math.Abs(a.Y - b.Y) < 1e-4
    && Math.Abs(a.Z - b.Z) < 1e-4;

  [Fact]
  public void Runtime_writer_keeps_every_decimal() {
    Shape s = Reader.Read(
      """{ "textures": {}, "elements": [ { "name": "A", "from": [0,0,0], "to": [46.666666666666664, 1, 1] } ] }"""
    );
    string runtime = RuntimeWriter.Write(s, Registry(), "iiex");
    Assert.Contains("46.666666666666664", runtime);
  }

  [SkippableFact]
  public void Repeat_finished_shape_equals_the_converters_runtime() {
    string? path = WorkspacePath.Of(
      "workbench/shapes-finished/pumps/engine-pump/machine-pipe-block-engine-fluidpump-improved-proposed.json"
    );
    string? tool = WorkspacePath.Of("exmods/infra/tools/convert-shape.py");
    string? python = new[]
    {
      "/usr/bin/python3",
      "/usr/local/bin/python3",
    }.FirstOrDefault(File.Exists);
    Skip.If(
      path is null || tool is null || python is null,
      "no workbench or exmods checkout or python3"
    );

    Shape shape = Reader.ReadFile(path!);
    string dir = Directory.CreateTempSubdirectory().FullName;
    string script = Path.Combine(dir, "run.py");
    string keys = string.Join(
      ", ",
      shape.Textures.Select(t => $"\"{t.Key}\": \"game:{t.Key}\"")
    );
    File.WriteAllText(
      script,
      $$"""
      import importlib.util, os, sys
      spec = importlib.util.spec_from_file_location("cs", sys.argv[1])
      m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
      m.TEXTURES.clear()
      m.TEXTURES.update({ {{keys}} })
      m.editable_path = lambda name: sys.argv[2]
      m.convert("x", sys.argv[3])
      """
    );
    string expectedPath = Path.Combine(dir, "expected.json");
    var psi = new ProcessStartInfo(python!) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
    };
    foreach (string a in new[] { "-I", script, tool!, path!, expectedPath })
      psi.ArgumentList.Add(a);
    using (Process p = Process.Start(psi)!) {
      p.StandardOutput.ReadToEnd();
      string err = p.StandardError.ReadToEnd();
      p.WaitForExit();
      Assert.True(p.ExitCode == 0, err);
    }
    string expectedText = File.ReadAllText(expectedPath);
    var expected = JObject.Parse(expectedText);
    var ends = ((JArray)expected["animations"]!).ToDictionary(
      c => (string?)c["code"] ?? (string)c["name"]!,
      c => (string)c["onAnimationEnd"]!
    );
    Assert.Contains("Repeat", ends.Values);

    string actual = RuntimeWriter.Write(
      shape,
      RegistryFor(shape),
      "iiex",
      ends
    );
    Directory.Delete(dir, true);
    Assert.Equal(expectedText, actual);
  }

  /// <summary>Model Creator writes a <c>textureSizes</c> map that was emptied as an expanded empty
  /// object; the writer writes <c>{}</c>.</summary>
  [GeneratedRegex(@"""textureSizes"": \{\r\n\t\}")]
  private static partial Regex EmptiedSizes();
}
