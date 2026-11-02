using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Ops.Tests;

/// <summary>A temporary workspace: a git checkout of drawings (<c>shapes/</c>), a spec folder with its blank files and
/// settings (<c>spec/</c>), an output root (<c>out/</c>) and a state folder for the timing log.</summary>
internal sealed class Workspace : IDisposable {
  private static string Cube(string name, string from, string to) =>
    $$"""{ "name": "{{name}}", "from": {{from}}, "to": {{to}}, "faces": { {{string.Join(
      ", ",
      new[] { "north", "east", "south", "west", "up", "down" }.Select(f => $"\"{f}\": {{ \"texture\": \"#cast-iron1\" }}")
    )}} } }""";

  public const string FormDrawing = """
    { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
      CUBE_BASE
    ] } ] }
    """;

  public static readonly string Form = FormDrawing.Replace(
    "CUBE_BASE",
    Cube("Base", "[0, 0, 0]", "[16, 4, 16]")
  );

  public static readonly string Drawing = $$"""
    { "elements": [ { "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1], "children": [
      {{Cube("Base", "[0, 0, 0]", "[16, 4, 16]")}},
      {{Cube("Way", "[2, 3, 2]", "[14, 4, 14]")}},
      {{Cube("Cav", "[6, 0, 6]", "[10, 4, 10]")}}
    ] } ] }
    """;

  public static readonly string DrawingWithMystery = Drawing.Replace(
    Cube("Cav", "[6, 0, 6]", "[10, 4, 10]"),
    Cube("Cav", "[6, 0, 6]", "[10, 4, 10]")
      + ", "
      + Cube("Mystery", "[1, 1, 1]", "[2, 2, 2]")
  );

  public const string FormRecord = """
    { "schema": 1, "elements": { "Root": {}, "Base": { "stage": "cast", "role": "cast" } },
      "features": { "ways": { "elements": ["Base"], "origin": "cast", "faces": ["Base/up"], "step": "plane" } } }
    """;

  public const string Record = """
    { "schema": 1,
      "elements": { "Root": {}, "Base": { "stage": "cast", "role": "cast" }, "Way": { "stage": "worked", "role": "cast" },
                    "Cav": { "stage": "cast", "role": "cast" } },
      "features": { "vways": { "elements": ["Way"], "origin": "planed", "faces": ["Way/up"], "step": "plane" },
                    "cav": { "elements": ["Cav"], "origin": "cored" } },
      "steps": [ { "id": "plane", "faces": ["Base/up"] } ] }
    """;

  public static readonly string MysteryRecord = Record.Replace(
    "\"Root\": {},",
    "\"Root\": {}, \"Mystery\": {},"
  );

  public const string FloorLie =
    """{ "x": { "axis": "x", "offset": 4 }, "y": { "axis": "y", "offset": 10 }, "z": { "axis": "z", "offset": -8 } }""";

  public string Dir { get; } =
    Path.Combine(
      Path.GetTempPath(),
      "shapetool-ops-" + Guid.NewGuid().ToString("N")
    );

  public string Shapes => Path.Combine(Dir, "shapes");
  public string Spec => Path.Combine(Dir, "spec");
  public string Out => Path.Combine(Dir, "out");
  public string SettingsFile => Path.Combine(Spec, "shapetool.json");
  public string State => Path.Combine(Dir, "state");

  public Workspace() {
    Directory.CreateDirectory(Shapes);
    Directory.CreateDirectory(Path.Combine(Spec, "blanks"));
    File.Copy(
      Path.Combine(AppContext.BaseDirectory, "fixtures", "settings.json"),
      Path.Combine(Spec, "shapetool.json")
    );
    Git("init", "-q");
    Git("config", "user.email", "t@example.org");
    Git("config", "user.name", "t");
    Write("b.json", Form, FormRecord);
    Write("m.json", Drawing, Record);
    File.WriteAllText(
      Path.Combine(Spec, "blanks", "castbed.json"),
      """
      { "blank": "castbed", "size": [16, 4, 16], "cutFaces": "from-record",
        "form": { "shape": "b.json", "sha": "HEAD", "groups": ["Root"] } }
      """
    );
  }

  /// <summary>Writes a drawing and its record (with the drawing's sha) in the checkout and commits it.</summary>
  public void Write(
    string shape,
    string drawing,
    string record,
    string? owner = null
  ) {
    string path = Path.Combine(Shapes, shape);
    File.WriteAllText(path, drawing);
    PartRecord r = PartRecord.Read(record);
    r.Shape = shape;
    r.ShapeSha = Provenance.Sha256Of(File.ReadAllBytes(path));
    r.Owner = owner;
    r.WriteFile(Path.Combine(Shapes, shape[..^".json".Length] + ".part.json"));
    Git("add", ".");
    Git("commit", "-q", "-m", "x");
  }

  public string MachineSpec(
    string castings = "",
    string users = "",
    string set = ""
  ) {
    string path = Path.Combine(Spec, "planer.json");
    File.WriteAllText(
      path,
      $$"""
      { "machine": "planer", "source": { "shape": "m.json", "sha": "HEAD" },
        "users": [ {{users}} ], "castings": [ {{castings}} ] {{set}} }
      """
    );
    return path;
  }

  public static string User(int count = 1, string item = "planer-bed") =>
    $$"""
      { "blank": "castbed", "groups": ["Root"], "count": {{count}},
        "worked": { "item": "{{item}}", "from": "castbed",
                    "steps": [ { "step": "plane", "cuts": ["ways"], "features": ["vways"] } ] } }
      """;

  public static string Casting(
    string groups = "[\"Root\"]",
    string item = "cast-planer-bed"
  ) =>
    $$"""
      { "item": "{{item}}", "source": { "groups": {{groups}} },
        "station": "floor", "lie": {{FloorLie}}, "cores": [ { "feature": "cav", "print": 2 } ] }
      """;

  public (int Exit, string Out, string Err) Run(params string[] args) {
    var o = new StringWriter();
    var e = new StringWriter();
    int exit = ShapeToolCli.Run(
      args,
      o,
      e,
      name => name == "XDG_STATE_HOME" ? State : null
    );
    return (exit, o.ToString(), e.ToString());
  }

  public (int Exit, string Out, string Err) Gen(
    string spec,
    params string[] more
  ) => Run(["gen", spec, "--root", Out, "--shapes", Shapes, .. more]);

  /// <summary>The input hash of each output path in the provenance log under <paramref name="root"/>.</summary>
  public static Dictionary<string, string> InputHashes(string root) =>
    File.ReadAllLines(Path.Combine(root, "provenance.jsonl"))
      .Where(l => l.Length > 0)
      .Select(l => JObject.Parse(l))
      .ToDictionary(o => (string)o["path"]!, o => (string)o["inputHash"]!);

  public string Tree() =>
    string.Join(
      "\n",
      Directory
        .EnumerateFiles(Out, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(f =>
          Path.GetRelativePath(Out, f)
          + " "
          + Provenance.Sha256Of(File.ReadAllBytes(f))
        )
    );

  private void Git(params string[] args) {
    var psi = new ProcessStartInfo("git") {
      WorkingDirectory = Shapes,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
    };
    foreach (string a in args)
      psi.ArgumentList.Add(a);
    using Process p = Process.Start(psi)!;
    p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0)
      throw new InvalidOperationException(
        $"git {string.Join(' ', args)} failed"
      );
  }

  public void Dispose() {
    try {
      Directory.Delete(Dir, true);
    } catch (IOException) { }
  }
}
