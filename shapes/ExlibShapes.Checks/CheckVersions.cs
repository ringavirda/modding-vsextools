using System;
using System.Linq;
using System.Reflection;

namespace ExpandedLib.Shapes.Checks;

/// <summary>The content version of each check, embedded at build.</summary>
public static class CheckVersions {
  /// <summary>The sha256 of a check's folder, <c>Checks/Common/</c>, the root files and Core, as
  /// lowercase hex; changes whenever any of those sources does.</summary>
  /// <param name="checkId">The check's id, compared to its folder name ignoring case.</param>
  /// <exception cref="ArgumentException">No such check.</exception>
  public static string Of(string checkId) =>
    typeof(CheckVersions)
      .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
      .FirstOrDefault(a =>
        string.Equals(
          a.Key,
          "CheckVersion." + checkId,
          StringComparison.OrdinalIgnoreCase
        )
      )
      ?.Value
    ?? throw new ArgumentException(
      $"no version embedded for check '{checkId}'"
    );
}
