using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ExpandedLib.Shapes.Core;
using ExpandedLib.Shapes.Generators;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Ops.Tests;

public class ImportTests : IDisposable {
  private readonly string dir = Path.Combine(
    Path.GetTempPath(),
    "shapetool-import-" + Guid.NewGuid().ToString("N")
  );

  public void Dispose() {
    try {
      Directory.Delete(dir, true);
    } catch (IOException) { }
  }

  private string Put(string rel, string text) {
    string path = Path.Combine(dir, rel);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
    return path;
  }

  private static string Cube(
    string name,
    string texture,
    string from,
    string to
  ) =>
    $$"""{ "name": "{{name}}", "from": {{from}}, "to": {{to}}, "faces": { {{string.Join(
      ", ",
      new[] { "north", "east", "south", "west", "up", "down" }.Select(f => $"\"{f}\": {{ \"texture\": \"#{texture}\" }}")
    )}} } }""";

  private static string Group(string name, params string[] children) =>
    $$"""{ "name": "{{name}}", "from": [0, 0, 0], "to": [1, 1, 1], "children": [ {{string.Join(", ", children)}} ] }""";

  private static string Drawing(params string[] roots) =>
    $$"""
      { "textures": { "iron5": "block/metal/sheet-plain/iron5", "cast-iron1": "//host/materials/cast-iron1" },
        "elements": [ {{string.Join(", ", roots)}} ] }
      """;

  private (int Exit, string Out, string Err) Run(params string[] args) {
    var o = new StringWriter();
    var e = new StringWriter();
    int exit = ShapeToolCli.Run(
      args,
      o,
      e,
      name => name == "XDG_STATE_HOME" ? Path.Combine(dir, "state") : null
    );
    return (exit, o.ToString(), e.ToString());
  }

  private static string? Python() {
    foreach (string p in new[] { "python3", "python" })
      try {
        var psi = new ProcessStartInfo(p, "-c \"import sys\"") {
          RedirectStandardOutput = true,
          RedirectStandardError = true,
        };
        using Process proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode == 0)
          return p;
      } catch (System.ComponentModel.Win32Exception) { }
    return null;
  }

  private string FakeLane() {
    Put("lane/stdfill.py", "");
    Put(
      "lane/press.py",
      """
      CROSSSLIDE = ("castlathecrossslide",)
      KINDS = {"castlathecrossslide": {"item": "/x/editable/cast-lathe-crossslide.json", "axis": 1, "rows": {"floor": (1.0,)}}}
      """
    );
    Put(
      "lane/lathe.py",
      """
      A = 0.125
      COPY = "lathe.json"
      COPY_COMMIT = "abc1234"
      BED_ALLOW = {"Way1": ((1, 1), (2, 0)), "Way2": ((1, 1),), "Plain": ()}
      BORE = {"lathehead": 1}
      RINGS = {"lathehead": ("Ring",)}
      SLIDE_SUNK = {"Fixing": ((2, 0, 1),)}
      def kinds(sf):
          return {
              "lathebed": dict(item="cast-lathe-bed", pieces={"Bed": (None, 0)},
                               lie=[[0, 0, 1, 7.5], [0, -1, 0, 22], [1, 0, 0, -19], [0, 0, 0, 1.0]]),
              "latheframe": dict(item="cast-lathe-frame", pieces={"StandardW": ("StandardW", None), "StandardE": ("StandardE", None)},
                                 lie=[[1, 0, 0, 5.875], [0, 1, 0, 4], [0, 0, 1, -24], [0, 0, 0, 1.0]]),
          }
      """
    );
    return Path.Combine(dir, "lane");
  }

  private string LatheDrawing() =>
    Put(
      "drawings/lathe.json",
      Drawing(
        Group(
          "Root",
          Group(
            "Bed",
            Cube("Way1", "cast-iron1", "[0, 0, 0]", "[4, 4, 4]"),
            Cube("Way2", "cast-iron1", "[4, 0, 0]", "[8, 4, 4]"),
            Cube("Plain", "cast-iron1", "[8, 0, 0]", "[9, 4, 4]")
          ),
          Group("Head", Cube("Ring", "cast-iron1", "[0, 4, 0]", "[4, 8, 4]")),
          Group(
            "Bolts",
            Cube("Bolt1", "iron5", "[0, 0, 5]", "[1, 1, 6]"),
            Cube("Bolt2", "iron5", "[2, 0, 5]", "[3, 1, 6]")
          ),
          Cube("Fixing", "cast-iron1", "[9, 0, 0]", "[10, 1, 1]")
        )
      )
    );

  [Fact]
  public void Import_lists_every_drawing_of_a_block() {
    string drawing = Put(
      "exmods/workbench/shapes/lathe/mach-proposed.json",
      Drawing()
    );
    Put("exmods/workbench/shapes/lathe/mach.json", Drawing());
    Put("exmods/workbench/shapes/lathe/mach-sketch.json", Drawing());
    Put("exmods/workbench/shapes/lathe/old/mach.json", Drawing());
    Put("exmods/mods/iiex/assets/iiex/shapes/block/mach.json", Drawing());
    Put("exmods/workbench/shapes/lathe/other.json", Drawing());
    Put("exmods/workbench/shapes/lathe/mach.part.json", """{ "schema": 1 }""");

    (int exit, string output, _) = Run(
      "import",
      drawing,
      "--out",
      Path.Combine(dir, "out")
    );

    Assert.Equal(0, exit);
    string[] listed = [.. output.Split('\n').Where(l => l.StartsWith("  "))];
    Assert.Equal(
      [
        "  shipped mods/iiex/assets/iiex/shapes/block/mach.json",
        "  proposed workbench/shapes/lathe/mach-proposed.json",
        "  sketch workbench/shapes/lathe/mach-sketch.json",
        "  editable workbench/shapes/lathe/mach.json",
        "  old workbench/shapes/lathe/old/mach.json",
      ],
      listed.Select(l => l.TrimEnd('\r'))
    );
  }

  [Fact]
  public void Iron5_is_a_fastener_guess() {
    string drawing = LatheDrawing();
    string outDir = Path.Combine(dir, "out");

    (int exit, _, _) = Run("import", drawing, "--out", outDir);

    Assert.Equal(0, exit);
    PartRecord r = PartRecord.ReadFile(Path.Combine(outDir, "lathe.part.json"));
    Assert.Equal("fastener", r.Elements["Bolt1"].Stage);
    Assert.Equal("fastener", r.Elements["Bolts"].Stage);
    Assert.Equal("cast", r.Elements["Way1"].Stage);
    Assert.Equal("cast", r.Elements["Root"].Stage);
    Assert.Equal(["machine"], r.Kinds);
    Assert.Equal("stages", (string?)r.Carried["import"]!["unconfirmed"]![0]);
  }

  [SkippableFact]
  public void Import_asks_one_question_per_drawing_group_and_confirms_a_bore_from_the_tables() {
    string? python = Python();
    Skip.If(python == null, "no python3");
    string drawing = LatheDrawing();
    string outDir = Path.Combine(dir, "out");

    (int exit, string output, string err) = Run(
      "import",
      drawing,
      "--lane",
      FakeLane(),
      "--module",
      "lathe",
      "--out",
      outDir,
      "--python",
      python!
    );

    Assert.True(exit == 3, err + output);
    string[] asked =
    [
      .. output.Split('\n').Where(l => l.StartsWith("Question:")),
    ];
    string question = Assert.Single(asked);
    Assert.StartsWith("Question: Bed: ", question);
    Assert.Contains("questions: 1", output);
    PartRecord r = PartRecord.ReadFile(Path.Combine(outDir, "lathe.part.json"));
    Feature bed = r.Features["Bed"];
    Assert.Equal(["Way1", "Way2"], bed.Elements);
    Assert.Equal(["Way1/up", "Way1/north", "Way2/up"], bed.Faces);
    Assert.Null(bed.Origin);
    Assert.Equal(true, (bool?)bed.Unknown["unconfirmed"]);
    Assert.Equal("bored", r.Features["Head"].Origin);
    Assert.False(r.Features.ContainsKey("Bolts"));
    Assert.Equal(
      ["sunk"],
      r.Carried["import"]!["moves"]!["Fixing"]!.Select(t => (string)t!)
    );

    (int answered, _, string answerErr) = Run(
      "answer",
      "lathe.json",
      "Bed",
      "planed",
      "--root",
      outDir
    );
    Assert.True(answered == 0, answerErr);
    Assert.Equal(
      "planed",
      PartRecord
        .ReadFile(Path.Combine(outDir, "lathe.part.json"))
        .Answers.Single()
        .Text
    );
  }

  private (int Exit, string Out) ImportWithLane(string outDir, string python) {
    (int exit, string output, string err) = Run(
      "import",
      LatheDrawing(),
      "--lane",
      FakeLane(),
      "--module",
      "lathe",
      "--out",
      outDir,
      "--python",
      python
    );
    return (exit, output + err);
  }

  [SkippableFact]
  public void A_second_import_applies_the_stored_answer_and_asks_nothing() {
    string? python = Python();
    Skip.If(python == null, "no python3");
    string outDir = Path.Combine(dir, "out");
    ImportWithLane(outDir, python!);
    Run("answer", "lathe.json", "Bed", "planed", "--root", outDir);

    (int exit, string output) = ImportWithLane(outDir, python!);

    Assert.True(exit == 0, output);
    Assert.DoesNotContain("Question:", output);
    PartRecord r = PartRecord.ReadFile(Path.Combine(outDir, "lathe.part.json"));
    Assert.Equal("planed", r.Features["Bed"].Origin);
    Assert.False(r.Features["Bed"].Unknown.ContainsKey("unconfirmed"));
    Assert.Equal("planed", r.Answers.Single().Text);
  }

  [SkippableFact]
  public void A_second_import_asks_again_when_the_stored_answer_is_for_other_elements() {
    string? python = Python();
    Skip.If(python == null, "no python3");
    string outDir = Path.Combine(dir, "out");
    ImportWithLane(outDir, python!);
    Run("answer", "lathe.json", "Bed", "planed", "--root", outDir);
    string recordPath = Path.Combine(outDir, "lathe.part.json");
    PartRecord r = PartRecord.ReadFile(recordPath);
    r.Answers.Single().FeatureSha = "0000";
    r.WriteFile(recordPath);

    (int exit, string output) = ImportWithLane(outDir, python!);

    Assert.Equal(3, exit);
    Assert.Contains("Question: Bed:", output);
    r = PartRecord.ReadFile(recordPath);
    Assert.Empty(r.Answers);
    Assert.Null(r.Features["Bed"].Origin);
  }

  [SkippableFact]
  public void A_second_import_keeps_what_import_did_not_write() {
    string? python = Python();
    Skip.If(python == null, "no python3");
    string outDir = Path.Combine(dir, "out");
    ImportWithLane(outDir, python!);
    string recordPath = Path.Combine(outDir, "lathe.part.json");
    PartRecord r = PartRecord.ReadFile(recordPath);
    r.Elements["Way1"].Stage = "worked";
    r.Steps.Add(new Step { Id = "plane" });
    r.Carried["blank"] = "castbed";
    r.WriteFile(recordPath);

    ImportWithLane(outDir, python!);

    r = PartRecord.ReadFile(recordPath);
    Assert.Equal("worked", r.Elements["Way1"].Stage);
    Assert.Equal("plane", r.Steps.Single().Id);
    Assert.Equal("castbed", (string?)r.Carried["blank"]);
  }

  [SkippableFact]
  public void Import_writes_a_machine_file_entry_per_lane_kind_and_the_cross_slide() {
    string? python = Python();
    Skip.If(python == null, "no python3");
    string outDir = Path.Combine(dir, "out");

    (_, string output, string err) = Run(
      "import",
      LatheDrawing(),
      "--lane",
      FakeLane(),
      "--module",
      "lathe",
      "--out",
      outDir,
      "--python",
      python!
    );

    Assert.Contains("machine file lathe.json: 3 castings", output);
    MachineSpec m = Spec.ReadMachine(
      JObject.Parse(File.ReadAllText(Path.Combine(outDir, "lathe.json")))
    );
    Assert.Equal("lathe", m.Machine);
    Assert.Equal("abc1234", m.Source.Sha);
    Assert.Equal(0.125, m.Allowance);
    Assert.Equal(
      ["cast-lathe-bed", "cast-lathe-crossslide", "cast-lathe-frame"],
      m.Castings.Select(c => c.Item)
    );
    CastingSpec bed = m.Castings[0];
    Assert.Equal(["Bed"], bed.Groups.Single().Groups);
    JObject lie = (JObject)bed.Lie!;
    Assert.Equal("z", (string?)lie["x"]!["axis"]);
    Assert.Equal(7.5, (double)lie["x"]!["offset"]!);
    Assert.Equal("-y", (string?)lie["y"]!["axis"]);
    Assert.Equal("x", (string?)lie["z"]!["axis"]);
    Assert.Equal(
      ["StandardE", "StandardW"],
      m.Castings[2].Groups.Single().Groups
    );
    Assert.Equal(["CrossSlide"], m.Castings[1].Groups.Single().Groups);
    Assert.Equal("", err);
  }

  [SkippableFact]
  public void Import_does_not_overwrite_a_machine_file_it_does_not_own() {
    string? python = Python();
    Skip.If(python == null, "no python3");
    string outDir = Path.Combine(dir, "out");
    string mine = Put("out/lathe.json", """{ "machine": "lathe" }""");

    (int exit, _, string err) = Run(
      "import",
      LatheDrawing(),
      "--lane",
      FakeLane(),
      "--module",
      "lathe",
      "--out",
      outDir,
      "--python",
      python!
    );

    Assert.Equal(1, exit);
    Assert.Contains("not generated", err);
    Assert.Equal("""{ "machine": "lathe" }""", File.ReadAllText(mine));
  }
}
