using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace ExpandedLib.Shapes.Checks;

/// <summary>One check on one output.</summary>
/// <param name="Output">The output's path, for reports only; it is not part of the cache key.</param>
/// <param name="Check">The check.</param>
/// <param name="Input">What it reads.</param>
/// <param name="Stale">Why the output is stale, null when fresh; read before anything runs. Null when the output
/// is not generated.</param>
public sealed record CheckJob(
  string Output,
  ICheck Check,
  CheckInput Input,
  Func<string?>? Stale = null
);

/// <summary>A job's result.</summary>
/// <param name="Job">The job.</param>
/// <param name="Result">The verdict.</param>
/// <param name="Cached">Whether it was read from the cache and not run.</param>
public sealed record JobResult(CheckJob Job, CheckResult Result, bool Cached);

/// <summary>Runs checks in parallel inside one process, each holding a machine-wide slot, reading and filling a
/// content-keyed cache.</summary>
/// <param name="slots">The slot pool.</param>
/// <param name="cache">The result cache; null runs everything.</param>
/// <param name="version">A check's version; <see cref="CheckVersions.Of"/> when null.</param>
public sealed class CheckRunner(
  Slots slots,
  ResultCache? cache = null,
  Func<ICheck, string>? version = null
) {
  /// <summary>Runs every job and returns the results in job order.</summary>
  /// <exception cref="StaleOutputException">A job's output is stale; nothing was run.</exception>
  /// <exception cref="CheckUsageException">A check could not read its input; the first one, in job order.</exception>
  public IReadOnlyList<JobResult> Run(IReadOnlyList<CheckJob> jobs) {
    List<string> stale =
    [
      .. jobs.Select(j => j.Stale?.Invoke()).OfType<string>().Distinct(),
    ];
    if (stale.Count > 0)
      throw new StaleOutputException(stale);

    var results = new JobResult?[jobs.Count];
    var errors = new ConcurrentDictionary<int, Exception>();
    Parallel.For(
      0,
      jobs.Count,
      new ParallelOptions { MaxDegreeOfParallelism = jobs.Count },
      i => {
        CheckJob job = jobs[i];
        try {
          string key = ResultCache.Key(
            job.Check,
            (version ?? (c => CheckVersions.Of(c.Id)))(job.Check),
            job.Input
          );
          if (cache != null && cache.TryGet(key, out CheckResult hit)) {
            results[i] = new JobResult(job, hit, true);
            return;
          }
          CheckResult result;
          using (slots.Take())
            result = job.Check.Run(job.Input);
          cache?.Put(key, result);
          results[i] = new JobResult(job, result, false);
        } catch (Exception e) {
          errors[i] = e;
        }
      }
    );
    if (!errors.IsEmpty)
      ExceptionDispatchInfo
        .Capture(errors.OrderBy(kv => kv.Key).First().Value)
        .Throw();
    return [.. results!];
  }
}
