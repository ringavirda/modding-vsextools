using System;
using System.IO;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Covers <c>render</c> refusing an <c>--only</c> or <c>--highlight</c> name the loaded
/// shape does not hold, instead of writing an empty image.</summary>
public class RenderElementNamesTests {
  private static (int Exit, UsageException? Refused, string[] Written) Run(
    params string[] flags
  ) {
    string outDir = Path.Combine(
      Path.GetTempPath(),
      "exlib-shapes-names-" + Guid.NewGuid()
    );
    try {
      string[] args =
      [
        FixturePath.Of("anim/slide-clip.json"),
        "--out",
        outDir,
        "--views",
        "south",
        "--ppu",
        "8",
        .. flags,
      ];
      try {
        int exit = Program.RunRender(args);
        return (exit, null, Directory.GetFiles(outDir));
      } catch (UsageException e) {
        string[] written = Directory.Exists(outDir)
          ? Directory.GetFiles(outDir)
          : [];
        return (2, e, written);
      }
    } finally {
      if (Directory.Exists(outDir))
        Directory.Delete(outDir, true);
    }
  }

  // Fails if the --only check is removed: the unknown name renders an empty image.
  [Fact]
  public void OnlyWithAnUnknownNameIsRefusedAndWritesNothing() {
    (int _, UsageException? refused, string[] written) = Run("--only", "Pig");
    Assert.NotNull(refused);
    Assert.Equal("--only: no element Pig in slide-clip.json", refused.Message);
    Assert.Empty(written);
  }

  // Fails if the --highlight check is removed; the --only name here is known, so only that guard
  // rejects it.
  [Fact]
  public void HighlightWithAnUnknownNameIsRefusedAndWritesNothing() {
    (int _, UsageException? refused, string[] written) = Run(
      "--only",
      "C",
      "--highlight",
      "Pig"
    );
    Assert.NotNull(refused);
    Assert.Equal(
      "--highlight: no element Pig in slide-clip.json",
      refused.Message
    );
    Assert.Empty(written);
  }

  // Fails if a known name is refused.
  [Fact]
  public void KnownNamesStillRender() {
    (int exit, UsageException? refused, string[] written) = Run(
      "--only",
      "C",
      "--highlight",
      "C"
    );
    Assert.Null(refused);
    Assert.Equal(0, exit);
    Assert.Single(written);
  }
}
