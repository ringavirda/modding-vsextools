using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ExpandedLib.Shapes.Checks.Tests;

/// <summary>A check that counts its runs and the most that ran at once.</summary>
internal sealed class CountingCheck(int sleepMs = 0) : ICheck {
  private int runs;
  private int active;
  private int peak;

  public string Id => "counting";

  public Profile Profile => Profile.Structural;

  public int Runs => runs;

  public int Peak => peak;

  public CheckResult Run(CheckInput input) {
    Interlocked.Increment(ref runs);
    int now = Interlocked.Increment(ref active);
    int seen;
    while (
      (seen = peak) < now
      && Interlocked.CompareExchange(ref peak, now, seen) != seen
    ) { }
    if (sleepMs > 0)
      Thread.Sleep(sleepMs);
    Interlocked.Decrement(ref active);
    return CheckResult.Of("counting", 3, false, ["counted"]);
  }
}

internal sealed class ThrowingCheck : ICheck {
  public string Id => "throwing";

  public Profile Profile => Profile.Structural;

  public CheckResult Run(CheckInput input) =>
    throw new CheckUsageException("cannot read the input");
}

internal sealed class TempDir : IDisposable {
  public string Path { get; } =
    Directory
      .CreateDirectory(
        System.IO.Path.Combine(
          System.IO.Path.GetTempPath(),
          "exlib-run-" + Guid.NewGuid().ToString("N")
        )
      )
      .FullName;

  public string Sub(string name) => System.IO.Path.Combine(Path, name);

  public void Dispose() => Directory.Delete(Path, true);
}

internal static class RunFixtures {
  public static Slots Slots(TempDir dir, int count = 4) =>
    new(dir.Sub("slots"), count);

  public static CheckJob Job(
    string output,
    ICheck check,
    string fixture,
    double allowance = 0.125
  ) =>
    new(
      output,
      check,
      new CheckInput(Fixtures.Shape(fixture), Allowance: allowance)
    );

  public static CheckRunner Runner(TempDir dir, int slots = 4) =>
    new(Slots(dir, slots), new ResultCache(dir.Sub("cache")), _ => "v1");
}
