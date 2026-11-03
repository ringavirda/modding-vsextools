using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Ops;

/// <summary>A floor flask lane module's tables as its dumper printed them.</summary>
/// <param name="Module">The module name: lathe, shaper or smallflask.</param>
/// <param name="Tables">Every upper-case module constant that is plain data, by name.</param>
/// <param name="Kinds">The module's casting kinds by code; null when it has none.</param>
/// <param name="Press">The lathe's press.py cross slide entries by code; null for another module.</param>
/// <param name="Fillings">The lane's filling names of the module's casting kinds, <c>longflask-filling-castshaperframe</c>; null when the lane has none.</param>
/// <param name="KindsError">Why the kinds could not be built; null when they were or the module has none.</param>
internal sealed record LaneTables(
  string Module,
  JObject Tables,
  JObject? Kinds,
  JObject? Press,
  IReadOnlyList<string>? Fillings,
  string? KindsError
) {
  public static readonly string[] Modules = ["lathe", "shaper", "smallflask"];

  /// <summary>Runs the dumper under <paramref name="python"/> on <paramref name="lane"/>'s module.</summary>
  /// <param name="python">The interpreter; it needs the lane's packages (numpy).</param>
  /// <param name="lane">The lane directory the module files are in.</param>
  /// <param name="module">One of <see cref="Modules"/>.</param>
  /// <exception cref="InvalidOperationException">The interpreter cannot run or the module fails to import.</exception>
  public static LaneTables Dump(string python, string lane, string module) {
    string script = Path.Combine(
      Path.GetTempPath(),
      "lane-dump-" + Guid.NewGuid().ToString("N") + ".py"
    );
    using (
      Stream s =
        Assembly
          .GetExecutingAssembly()
          .GetManifestResourceStream("lane_dump.py")
        ?? throw new InvalidOperationException("lane_dump.py is not embedded")
    )
    using (FileStream f = File.Create(script))
      s.CopyTo(f);
    try {
      var psi = new ProcessStartInfo(python) {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
      };
      foreach (
        string a in new[] { "-I", script, Path.GetFullPath(lane), module }
      )
        psi.ArgumentList.Add(a);
      Process p;
      try {
        p =
          Process.Start(psi)
          ?? throw new InvalidOperationException($"{python} not started");
      } catch (System.ComponentModel.Win32Exception e) {
        throw new InvalidOperationException(
          $"cannot run {python}: {e.Message}"
        );
      }
      using (p) {
        Task<string> err = p.StandardError.ReadToEndAsync();
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
          throw new InvalidOperationException(
            $"lane module {module} in {lane} did not import under {python}: {LastLine(err.Result)}"
          );
        JObject o = JObject.Parse(text);
        return new LaneTables(
          module,
          (JObject)o["tables"]!,
          o["kinds"] as JObject,
          o["press"] as JObject,
          (o["fillings"] as JArray)?.Select(t => (string)t!).ToList(),
          (string?)o["kindsError"]
        );
      }
    } finally {
      File.Delete(script);
    }
  }

  private static string LastLine(string text) {
    string[] lines = text.Trim().Split('\n');
    return lines[^1].Trim();
  }
}
