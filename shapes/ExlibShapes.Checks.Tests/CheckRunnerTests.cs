using System;
using System.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class CheckRunnerTests {
  [Fact]
  public void Runner_never_holds_more_slots_than_exist() {
    using var dir = new TempDir();
    var check = new CountingCheck(sleepMs: 60);
    CheckRunner runner = RunFixtures.Runner(dir, slots: 2);
    var jobs = Enumerable
      .Range(0, 8)
      .Select(i =>
        RunFixtures.Job("o" + i, check, "cast-ok.json", allowance: 0.1 + i)
      )
      .ToList();

    var results = runner.Run(jobs);

    Assert.Equal(8, check.Runs);
    Assert.Equal(2, check.Peak);
    Assert.Equal(jobs, results.Select(r => r.Job));
  }

  [Fact]
  public void Slots_are_held_until_disposed() {
    using var dir = new TempDir();
    Slots slots = RunFixtures.Slots(dir, 2);
    IDisposable a = slots.TryTake()!;
    IDisposable b = slots.TryTake()!;
    Assert.NotNull(a);
    Assert.NotNull(b);
    Assert.Null(slots.TryTake());
    a.Dispose();
    IDisposable? c = slots.TryTake();
    Assert.NotNull(c);
    Assert.Null(slots.TryTake());
    b.Dispose();
    c!.Dispose();
  }

  [Fact]
  public void A_usage_error_is_rethrown_by_the_runner() {
    using var dir = new TempDir();
    var runner = RunFixtures.Runner(dir);
    var job = new CheckJob("a", new ThrowingCheck(), new CheckInput(null));
    Assert.Throws<CheckUsageException>(() => runner.Run([job]));
  }
}
