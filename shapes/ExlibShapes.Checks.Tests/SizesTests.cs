using System.IO;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Xunit;

namespace ExpandedLib.Shapes.Checks.Tests;

public class SizesTests {
  private static readonly SizesCheck Check = new();

  private static string SpecText =>
    File.ReadAllText(Fixtures.Path("sizes-spec.json"));

  public static TheoryData<string> LintNames =>
    [.. Fixtures.Parity("sizeslint").Keys];

  [Fact]
  public void Sizes_parity() {
    Recorded want = Fixtures.Parity("sizes")["head-small.json"];
    CheckResult got = Check.Run(
      new CheckInput(
        Fixtures.Shape("head-small.json"),
        Sizes: SizesSpec.Parse(SpecText)
      )
    );
    Assert.Equal(want.Exit, got.ExitCode);
    Assert.Equal(want.Enumerated, got.Enumerated);
    Assert.Equal(want.Faulted, Fixtures.FaultedNames(got, "sizes:"));
    Assert.Contains("Head: spans 6 voxels on y, the spec says 5", got.Lines);
    Assert.Contains("Nope: no such element in the shape", got.Lines);
  }

  [Fact]
  public void A_group_spans_its_children() {
    var rows = new[] { new SizeRow("Boss", "x", 6) };
    CheckResult r = Check.Run(
      new CheckInput(Fixtures.Shape("head-small.json"), Sizes: rows)
    );
    Assert.Equal(0, r.ExitCode);
  }

  [Theory]
  [MemberData(nameof(LintNames))]
  public void Sizes_lint_parity(string name) {
    Recorded want = Fixtures.Parity("sizeslint")[name];
    (var findings, int n) = SizesLint.Check(Fixtures.Shape(name));
    Assert.Equal(want.Enumerated, n);
    Assert.Equal(want.Exit, findings.Any(f => f.Level == "error") ? 1 : 0);
    Assert.Equal(
      want.Faulted,
      findings
        .Select(f => f.Kind + " " + f.Path)
        .Order(System.StringComparer.Ordinal)
        .ToArray()
    );
  }

  [Fact]
  public void Empty_input_fails() {
    Assert.Equal(Verdict.Fail, Check.Run(new CheckInput(null)).Verdict);
    CheckResult noRows = Check.Run(
      new CheckInput(Fixtures.Shape("head-small.json"), Sizes: [])
    );
    Assert.Equal(1, noRows.ExitCode);
    Assert.Equal(0, noRows.Enumerated);
  }

  [Fact]
  public void Bad_axis_and_missing_section_are_usage_errors() {
    Assert.Throws<CheckUsageException>(() =>
      SizesSpec.Parse(
        """{"sizes": [{"element": "Head", "axis": "w", "voxels": 1}]}"""
      )
    );
    Assert.Throws<CheckUsageException>(() =>
      SizesSpec.Parse("""{"sizes": []}""")
    );
    Assert.Throws<CheckUsageException>(() =>
      SizesSpec.Parse("""{"sizes": [{"element": "Head", "axis": "x"}]}""")
    );
    Assert.Throws<CheckUsageException>(() =>
      Check.Run(
        new CheckInput(
          Fixtures.Shape("head-small.json"),
          Sizes: [new SizeRow("Head", "w", 1)]
        )
      )
    );
  }
}
