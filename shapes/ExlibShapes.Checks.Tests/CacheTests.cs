using System;
using System.IO;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class CacheTests {
  [Fact]
  public void Moved_directory_reruns_nothing() {
    using var dir = new TempDir();
    var check = new CountingCheck();
    CheckRunner runner = RunFixtures.Runner(dir);
    string a = dir.Sub("before");
    Directory.CreateDirectory(a);
    File.Copy(Fixtures.Path("cast-ok.json"), Path.Combine(a, "cast-ok.json"));
    File.Copy(
      Fixtures.Path("cast-bolt.json"),
      Path.Combine(a, "cast-bolt.json")
    );
    string b = dir.Sub("after");

    CheckJob Of(string root, string name) =>
      new(
        Path.Combine(root, name),
        check,
        new CheckInput(Reader.ReadFile(Path.Combine(root, name)))
      );
    runner.Run([Of(a, "cast-ok.json"), Of(a, "cast-bolt.json")]);
    Directory.Move(a, b);
    var again = runner.Run([Of(b, "cast-ok.json"), Of(b, "cast-bolt.json")]);

    Assert.Equal(2, check.Runs);
    Assert.All(again, r => Assert.True(r.Cached));
  }

  [Fact]
  public void Changing_one_outputs_allowance_reruns_only_its_gates() {
    using var dir = new TempDir();
    var check = new CountingCheck();
    CheckRunner runner = RunFixtures.Runner(dir);
    runner.Run([
      RunFixtures.Job("a", check, "cast-ok.json"),
      RunFixtures.Job("b", check, "cast-bolt.json"),
    ]);
    Assert.Equal(2, check.Runs);

    var again = runner.Run([
      RunFixtures.Job("a", check, "cast-ok.json", allowance: 0.25),
      RunFixtures.Job("b", check, "cast-bolt.json"),
    ]);

    Assert.Equal(3, check.Runs);
    Assert.False(again[0].Cached);
    Assert.True(again[1].Cached);
  }

  [Fact]
  public void A_cached_result_reads_back_as_run() {
    using var dir = new TempDir();
    var cache = new ResultCache(dir.Sub("cache"));
    CheckResult ran = new Footsweep().Run(
      new CheckInput(Fixtures.Shape("cast-ok.json"))
    );
    cache.Put("k", ran);

    Assert.True(cache.TryGet("k", out CheckResult back));
    Assert.Equal(ran.Check, back.Check);
    Assert.Equal(ran.Verdict, back.Verdict);
    Assert.Equal(ran.Enumerated, back.Enumerated);
    Assert.Equal(ran.Lines, back.Lines);
    Assert.False(cache.TryGet("missing", out _));
  }

  [Fact]
  public void A_changed_check_version_reruns() {
    using var dir = new TempDir();
    var check = new CountingCheck();
    var cache = new ResultCache(dir.Sub("cache"));
    CheckJob job = RunFixtures.Job("a", check, "cast-ok.json");
    new CheckRunner(RunFixtures.Slots(dir), cache, _ => "v1").Run([job]);
    new CheckRunner(RunFixtures.Slots(dir), cache, _ => "v2").Run([job]);
    Assert.Equal(2, check.Runs);
  }

  [Fact]
  public void An_unreadable_entry_is_a_miss() {
    using var dir = new TempDir();
    var cache = new ResultCache(dir.Sub("cache"));
    Directory.CreateDirectory(dir.Sub("cache"));
    File.WriteAllText(Path.Combine(dir.Sub("cache"), "k.json"), "{ not json");
    Assert.False(cache.TryGet("k", out _));
  }

  [Fact]
  public void The_cache_dir_is_the_git_root_of_the_input() {
    using var dir = new TempDir();
    Directory.CreateDirectory(dir.Sub(".git"));
    string deep = Path.Combine(dir.Path, "a", "b");
    Directory.CreateDirectory(deep);
    Assert.Equal(
      Path.Combine(dir.Path, ".exmod", "shapes-cache"),
      ResultCache.DefaultDir(Path.Combine(deep, "x.json"))
    );
    Assert.Equal(
      Path.GetFullPath(dir.Sub("elsewhere")),
      ResultCache.DefaultDir(deep, dir.Sub("elsewhere"))
    );
  }

  private static Provenance Rows(TempDir dir, string version, string input) {
    var p = new Provenance(dir.Sub("provenance.jsonl"));
    p.Append(new("items/a.json", "00", "castgen", version, input));
    p.Append(new("items/other.json", "00", "othergen", version, input));
    return p;
  }

  [Fact]
  public void Generator_change_makes_its_outputs_stale() {
    using var dir = new TempDir();
    Provenance p = Rows(dir, "v1", "in");

    Assert.Null(Staleness.Reason(p, "items/a.json", "castgen", "v1", "in"));
    Assert.Contains(
      "now v2",
      Staleness.Reason(p, "items/a.json", "castgen", "v2", "in")
    );
    Assert.Null(Staleness.Reason(p, "items/other.json", "castgen", "v2", "in"));
    Assert.Null(Staleness.Reason(p, "items/new.json", "castgen", "v2", "in"));
  }

  [Fact]
  public void Changed_inputs_make_an_output_stale() {
    using var dir = new TempDir();
    Provenance p = Rows(dir, "v1", "in");
    Assert.Contains(
      "inputs changed",
      Staleness.Reason(p, "items/a.json", "castgen", "v1", "other")
    );
  }

  private static JObject Machine(params string[] items) =>
    new() {
      ["machine"] = "planer",
      ["users"] = new JArray(
        items.Select(i => new JObject {
          ["blank"] = "bed",
          ["worked"] = new JObject { ["item"] = i, ["steps"] = new JArray() },
        })
      ),
    };

  [Fact]
  public void A_stale_output_is_refused_before_any_check() {
    using var dir = new TempDir();
    var check = new CountingCheck();
    CheckRunner runner = RunFixtures.Runner(dir);
    CheckJob fresh = RunFixtures.Job("a", check, "cast-ok.json");
    CheckJob stale = RunFixtures.Job("b", check, "cast-bolt.json") with {
      Stale = () => "items/b.json: made by castgen v1, now v2",
    };

    StaleOutputException e = Assert.Throws<StaleOutputException>(() =>
      runner.Run([fresh, stale])
    );
    Assert.Equal(["items/b.json: made by castgen v1, now v2"], e.Reasons);
    Assert.Equal(0, check.Runs);
  }
}
