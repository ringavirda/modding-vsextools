using System;
using System.IO;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Ops.Tests;

public class CliTests {
  [Fact]
  public void Cli_gen_writes_the_outputs_and_a_second_run_writes_identical_bytes() {
    using var w = new Workspace();
    string spec = w.MachineSpec(Workspace.Casting(), Workspace.User());

    (int exit, string output, string err) = w.Gen(spec);
    string first = w.Tree();
    string provenance = File.ReadAllText(
      Path.Combine(w.Out, "provenance.jsonl")
    );
    (int again, _, _) = w.Gen(spec);

    Assert.True(exit == 0, err);
    Assert.Equal(0, again);
    Assert.Contains(
      "planer-bed [worked, item] items/planer-bed-proposed.json",
      output
    );
    Assert.Contains(
      "cast-planer-bed [casting] items/cast-planer-bed-proposed.json",
      output
    );
    Assert.True(
      File.Exists(
        Path.Combine(w.Out, "items", "cast-planer-bed-proposed.part.json")
      )
    );
    Assert.Equal(first, w.Tree());
    Assert.Equal(
      provenance,
      File.ReadAllText(Path.Combine(w.Out, "provenance.jsonl"))
    );
    Assert.Contains("\"generator\":\"castgen\"", provenance);
  }

  [Fact]
  public void Cli_gen_writes_runtime_shapes_beside_the_editable_ones() {
    using var w = new Workspace();
    string runtime = Path.Combine(w.Dir, "runtime");

    (int exit, _, string err) = w.Gen(
      w.MachineSpec(Workspace.Casting(), Workspace.User()),
      "--runtime",
      runtime
    );

    Assert.True(exit == 0, err);
    string text = File.ReadAllText(
      Path.Combine(runtime, "cast-planer-bed.json")
    );
    Assert.Contains("iiex:block/metal/castiron", text);
  }

  [Fact]
  public void Cli_gen_resolves_the_blank_files_beside_the_spec() {
    using var w = new Workspace();
    string spec = w.MachineSpec("", Workspace.User());
    File.Delete(Path.Combine(w.Spec, "blanks", "castbed.json"));

    (int exit, _, string err) = w.Gen(spec);

    Assert.Equal(1, exit);
    Assert.Contains("blanks/castbed.json", err);
  }

  [Fact]
  public void Cli_gen_refuses_a_record_that_is_for_another_drawing() {
    using var w = new Workspace();
    string spec = w.MachineSpec(Workspace.Casting());
    File.WriteAllText(
      Path.Combine(w.Shapes, "m.part.json"),
      File.ReadAllText(Path.Combine(w.Shapes, "m.part.json"))
        .Replace(Provenance.Sha256Of(Workspace.Drawing), "00ff")
    );

    (int exit, _, string err) = w.Gen(spec);

    Assert.Equal(1, exit);
    Assert.Contains("another drawing", err);
  }

  [Fact]
  public void Cli_gen_does_not_overwrite_a_file_it_does_not_own() {
    using var w = new Workspace();
    string spec = w.MachineSpec(Workspace.Casting());
    Directory.CreateDirectory(Path.Combine(w.Out, "items"));
    File.WriteAllText(
      Path.Combine(w.Out, "items", "cast-planer-bed-proposed.json"),
      "{}"
    );

    (int exit, _, string err) = w.Gen(spec);

    Assert.Equal(1, exit);
    Assert.Contains("not generated", err);
    Assert.Equal(
      "{}",
      File.ReadAllText(
        Path.Combine(w.Out, "items", "cast-planer-bed-proposed.json")
      )
    );
  }

  [Fact]
  public void Cli_show_prints_the_bill() {
    using var w = new Workspace();
    string spec = w.MachineSpec(
      Workspace.Casting(),
      Workspace.User(2),
      """, "set": { "item": "planer-set", "station": "small", "groups": [ { "groups": ["Root/Base"] } ] }"""
    );

    (int exit, string output, _) = w.Run("show", spec);

    Assert.Equal(0, exit);
    Assert.Contains("cast-planer-bed x1, station floor", output);
    Assert.Contains("planer-bed on blank castbed x2", output);
    Assert.Contains("set: planer-set, station small", output);
    Assert.Contains("  castbed x2", output);
    Assert.True(output.Split('\n').Length < 40);
  }

  [Fact]
  public void Cli_show_prints_a_record_with_its_gates_and_stays_under_40_lines() {
    using var w = new Workspace();
    w.Gen(w.MachineSpec(Workspace.Casting(), Workspace.User()));

    (int exit, string output, string err) = w.Run(
      "show",
      Path.Combine(w.Out, "items", "cast-planer-bed-proposed.json")
    );

    Assert.True(exit == 0, err);
    Assert.Contains("[casting]", output);
    Assert.Contains("gates:", output);
    Assert.True(output.Split('\n').Length < 40);
  }

  [Fact]
  public void Gen_asks_exit_3_with_Question_lines_and_Answered_feature_asks_nothing() {
    using var w = new Workspace();
    w.Write(
      "m.json",
      Workspace.DrawingWithMystery,
      Workspace.MysteryRecord,
      owner: "import"
    );
    string spec = w.MachineSpec(
      Workspace.Casting("[\"Root/Base\"]", "cast-planer-base")
        + ", "
        + Workspace.Casting(
          "[\"Root/Base\", \"Root/Mystery\"]",
          "cast-planer-mystery"
        )
    );

    (int exit, string output, string genErr) = w.Gen(spec);
    Assert.True(exit == 3, genErr);
    Assert.Contains("Question: Mystery:", output);
    Assert.True(
      File.Exists(
        Path.Combine(w.Out, "items", "cast-planer-base-proposed.json")
      )
    );
    Assert.False(
      File.Exists(
        Path.Combine(w.Out, "items", "cast-planer-mystery-proposed.json")
      )
    );

    (int answered, string said, string err) = w.Run(
      "answer",
      "m.json",
      "Mystery",
      "cast",
      "--root",
      w.Out,
      "--shapes",
      w.Shapes
    );
    Assert.True(answered == 0, err);
    Assert.Contains("answered Mystery for m.json: cast", said);

    PartRecord record = PartRecord.ReadFile(
      Path.Combine(w.Shapes, "m.part.json")
    );
    Answer a = Assert.Single(record.Answers);
    Assert.Equal(("Mystery", "cast", "fallen"), (a.Feature, a.Text, a.By));
    Assert.Matches(@"^\d{4}-\d\d-\d\d$", a.Date);
    Assert.Equal("import", record.Owner);

    (int rerun, string again, string rerunErr) = w.Gen(spec);
    Assert.True(rerun == 0, rerunErr);
    Assert.DoesNotContain("Question:", again);
    Assert.True(
      File.Exists(
        Path.Combine(w.Out, "items", "cast-planer-mystery-proposed.json")
      )
    );
    Assert.Equal(
      "[]",
      JArray
        .Parse(File.ReadAllText(Path.Combine(w.Out, "questions.json")))
        .ToString(Newtonsoft.Json.Formatting.None)
    );
  }

  [Fact]
  public void Cli_answer_refuses_an_option_the_question_does_not_offer_and_an_unasked_feature() {
    using var w = new Workspace();
    w.Write("m.json", Workspace.DrawingWithMystery, Workspace.MysteryRecord);
    w.Gen(
      w.MachineSpec(
        Workspace.Casting(
          "[\"Root/Base\", \"Root/Mystery\"]",
          "cast-planer-mystery"
        )
      )
    );

    (int bad, _, string badErr) = w.Run(
      "answer",
      "m.json",
      "Mystery",
      "banana",
      "--root",
      w.Out,
      "--shapes",
      w.Shapes
    );
    (int none, _, string noneErr) = w.Run(
      "answer",
      "m.json",
      "Other",
      "cast",
      "--root",
      w.Out,
      "--shapes",
      w.Shapes
    );

    Assert.Equal(2, bad);
    Assert.Contains("not one of", badErr);
    Assert.Equal(1, none);
    Assert.Contains("no open question", noneErr);
  }

  [Fact]
  public void Cli_check_exits_1_on_a_failed_gate_and_a_second_run_is_cached() {
    using var w = new Workspace();
    w.Gen(w.MachineSpec(Workspace.Casting()));
    string filling = Path.Combine(
      w.Out,
      "items",
      "floorflask-filling-cast-planer-bed-proposed.json"
    );

    (int exit, string output, string err) = w.Run(
      "check",
      filling,
      "--cache",
      Path.Combine(w.Dir, "cache"),
      "--settings",
      w.SettingsFile
    );
    (_, string second, _) = w.Run(
      "check",
      filling,
      "--cache",
      Path.Combine(w.Dir, "cache"),
      "--settings",
      w.SettingsFile
    );

    Assert.True(exit == 1, output + err);
    Assert.Contains("PASS impression", output);
    Assert.Contains("FAIL corefit", output);
    Assert.Contains("SKIP draw", output);
    Assert.DoesNotContain(", cached", output);
    Assert.Contains(", cached", second);
  }

  [Fact]
  public void Cli_check_skips_the_gates_that_need_a_source_and_says_so() {
    using var w = new Workspace();
    w.Gen(w.MachineSpec(Workspace.Casting()));

    (int exit, string output, string err) = w.Run(
      "check",
      Path.Combine(w.Out, "items", "cast-planer-bed-proposed.json"),
      "--settings",
      w.SettingsFile
    );

    Assert.True(exit == 0, output + err);
    Assert.Contains("SKIP allowance", output);
    Assert.Contains("pass --source SHAPE", output);
    Assert.Contains("PASS castonly", output);
  }

  [Fact]
  public void Cli_check_refuses_an_output_made_by_another_generator_version() {
    using var w = new Workspace();
    w.Gen(w.MachineSpec(Workspace.Casting()));
    string prov = Path.Combine(w.Out, "provenance.jsonl");
    string text = File.ReadAllText(prov);
    string version = JObject.Parse(text.Split('\n')[0])["version"]!.ToString();
    File.WriteAllText(prov, text.Replace(version, "deadbeef" + version[8..]));

    (int exit, _, string err) = w.Run(
      "check",
      Path.Combine(
        w.Out,
        "items",
        "floorflask-filling-cast-planer-bed-proposed.json"
      ),
      "--settings",
      w.SettingsFile
    );

    Assert.Equal(1, exit);
    Assert.Contains("stale outputs", err);
  }

  [Fact]
  public void Cli_check_catalogue_and_mutate_and_unknown_verbs() {
    using var w = new Workspace();

    (int cat, string list, _) = w.Run("check", "--catalogue");
    (int mutate, _, string mutateErr) = w.Run("check", "x.json", "--mutate");
    (int verb, _, string verbErr) = w.Run("frobnicate");
    (int noRoot, _, string rootErr) = w.Run("gen", "spec.json");

    Assert.Equal(0, cat);
    Assert.Contains("needs an eye:", list);
    Assert.Equal(2, mutate);
    Assert.Contains("M3", mutateErr);
    Assert.Equal(2, verb);
    Assert.Contains("no such verb", verbErr);
    Assert.Equal(2, noRoot);
    Assert.Contains("--root", rootErr);
  }

  [Fact]
  public void Cli_appends_one_timing_line_per_command() {
    using var w = new Workspace();

    w.Run("check", "--catalogue");
    w.Run("frobnicate");

    string[] lines = File.ReadAllLines(
      Path.Combine(w.State, "exlib-shapes", "runs.jsonl")
    );
    Assert.Equal(2, lines.Length);
    Assert.Equal(
      ("check", 0),
      (
        (string)JObject.Parse(lines[0])["command"]!,
        (int)JObject.Parse(lines[0])["exit"]!
      )
    );
    Assert.Equal(2, (int)JObject.Parse(lines[1])["exit"]!);
  }
}
