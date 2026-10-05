using System;
using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>Covers each subcommand refusing a <c>--flag</c> it does not read. Every call names a
/// missing shape file, so a flag the check accepts goes on to fail loading it instead.</summary>
public class ProgramArgsTests {
  private const string Missing = "/nonexistent/shape.json";

  private static string? Rejected(Func<string[], int> run, params string[] args) {
    try {
      run([Missing, .. args]);
    } catch (UsageException e) when (e.Message.Contains("does not take")) {
      return e.Message;
    } catch (Exception) { }
    return null;
  }

  [Fact]
  public void RenderRefusesAnglePastItsOwnFlags() {
    string? message = Rejected(
      Program.RunRender,
      "--out=/tmp/x",
      "--angle",
      "90"
    );
    Assert.NotNull(message);
    Assert.StartsWith("render does not take --angle (it takes --out", message);
  }

  [Fact]
  public void UnknownFlagIsRefusedInEqualsFormToo() {
    string? message = Rejected(Program.RunItem, "--out=/tmp/x", "--views=iso");
    Assert.NotNull(message);
    Assert.Contains("item does not take --views", message);
  }

  [Fact]
  public void BareSwitchValueIsNotTakenAsAFlagsValue() {
    Assert.NotNull(
      Rejected(Program.RunBlock, "--full", "--angle=90", "--bogus")
    );
  }

  [Theory]
  [InlineData(
    "render",
    "--out=/tmp/x",
    "--views=iso",
    "--ppu=24",
    "--anim=c",
    "--frames=1",
    "--selective=a",
    "--repo=/r",
    "--only=a",
    "--highlight=b",
    "--no-grid",
    "--no-edges",
    "--game=/g"
  )]
  [InlineData(
    "render",
    "--out",
    "/tmp/x",
    "--views",
    "iso",
    "--ppu",
    "24",
    "--anim",
    "c",
    "--frames",
    "1",
    "--selective",
    "a",
    "--repo",
    "/r",
    "--only",
    "a",
    "b",
    "--highlight",
    "c",
    "--game",
    "/g"
  )]
  [InlineData(
    "schematic",
    "--out=/tmp/x",
    "--views=plan",
    "--angle=90",
    "--layer=all",
    "--ppu=8",
    "--roots=/a",
    "--game=/g"
  )]
  [InlineData(
    "schematic",
    "--out",
    "/tmp/x",
    "--views",
    "plan",
    "--angle",
    "90",
    "--layer",
    "all",
    "--ppu",
    "8",
    "--roots",
    "/a",
    "/b",
    "--game",
    "x"
  )]
  [InlineData(
    "block",
    "--out=/tmp/x",
    "--variant=v",
    "--views=iso",
    "--angle=90",
    "--full",
    "--ppu=24",
    "--selective=a",
    "--transparent",
    "--roots=/a",
    "--game=/g"
  )]
  [InlineData(
    "block",
    "--out",
    "/tmp/x",
    "--variant",
    "v",
    "--views",
    "iso",
    "--angle",
    "90",
    "--ppu",
    "24",
    "--selective",
    "a",
    "--roots",
    "/a",
    "/b",
    "--game",
    "x"
  )]
  [InlineData(
    "item",
    "--out=/tmp/x",
    "--variant=v",
    "--ppu=24",
    "--roots=/a",
    "--game=/g"
  )]
  [InlineData(
    "item",
    "--out",
    "/tmp/x",
    "--variant",
    "v",
    "--ppu",
    "24",
    "--roots",
    "a",
    "b",
    "--game",
    "x"
  )]
  [InlineData("tree", "--group=a", "--game=/g")]
  [InlineData("tree", "--group", "a", "--game", "x")]
  [InlineData("measure", "--group=a", "--cells=0,0,0", "--game=/g")]
  [InlineData("measure", "--group", "a", "--cells", "0,0,0", "--game", "x")]
  public void EveryFlagACommandReadsPassesTheCheck(
    string command,
    params string[] args
  ) {
    Func<string[], int> run = command switch {
      "render" => Program.RunRender,
      "schematic" => Program.RunSchematic,
      "block" => Program.RunBlock,
      "item" => Program.RunItem,
      "tree" => Program.RunTree,
      _ => Program.RunMeasure,
    };
    Assert.Null(Rejected(run, args));
  }
}
