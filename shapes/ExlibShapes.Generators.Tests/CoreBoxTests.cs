using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;
using Xunit;
using static ExpandedLib.Shapes.Generators.Tests.FlushFixtures;

namespace ExpandedLib.Shapes.Generators.Tests;

public class CoreBoxTests {
  private static GenOutput Box(GenOutput core) =>
    CoreBoxes.Make(
      CoreFixtures.Ctx,
      "corebox-castthing",
      "drysandcore-castthing",
      core,
      "spec"
    );

  private static CheckResult Fit(GenOutput box, GenOutput core) =>
    new Corefit().Run(
      new CheckInput(
        box.Shape,
        Record: box.Record,
        Settings: Fx.Settings,
        Cores: [core.Shape]
      )
    );

  private static GenOutput Core(string to, JToken? print = null) =>
    CoreFixtures.Core(print, Bx("Slot", "[2, 0, 0]", to));

  [Fact]
  public void CoreBox_walls_are_one() {
    GenOutput core = Core("[10, 2, 3]");
    GenOutput box = Box(core);

    CheckResult r = Fit(box, core);

    Assert.Equal([], r.Lines.Where(l => !l.StartsWith("corefit:")));
    Assert.Equal(0, r.ExitCode);
    Assert.Equal(2 + 2 + 1 + 20, r.Enumerated);
    Assert.Equal(["Tray1", "Tray2"], box.Shape.Elements.Select(e => e.Name!));
  }

  [Fact]
  public void CoreBox_pocket_equals_the_core_on_a_long_core() {
    GenOutput core = Core("[44, 4, 9]");
    GenOutput box = Box(core);

    Assert.Equal(0, Fit(box, core).ExitCode);
  }

  [Fact]
  public void CoreBox_pieces_stay_within_16() {
    GenOutput core = Core("[44, 4, 9]");
    GenOutput box = Box(core);

    List<Element> leaves = [.. CoreFixtures.Walk(box.Shape.Elements)];

    Assert.All(
      leaves,
      e =>
        Assert.All(
          Enumerable.Range(0, 3),
          a => Assert.True(e.To![a] - e.From![a] <= 16 + 1e-9)
        )
    );
    Assert.Contains(leaves, e => e.Name!.Contains("_s2"));
  }

  [Fact]
  public void CoreBox_boards_run_along_the_long_side() {
    GenOutput core = Core("[10, 2, 3]");

    CheckResult r = Fit(Box(core), core);

    Assert.DoesNotContain(r.Lines, l => l.StartsWith("grain"));
    Assert.Equal(0, r.ExitCode);
  }

  [Fact]
  public void CoreBox_boards_turn_when_the_pocket_is_square() {
    GenOutput core = CoreFixtures.Core(
      JObject.Parse("""{ "length": 1, "run": "y" }"""),
      Bx("Slot", "[0, 0, 0]", "[4, 2, 2]")
    );
    GenOutput box = Box(core);

    CheckResult r = Fit(box, core);

    Assert.Equal(0, r.ExitCode);
    Element floor = CoreFixtures
      .Walk(box.Shape.Elements)
      .First(e => e.Name == "Tray1Floor");
    Assert.Equal(90, floor.Faces.First(f => f.Key == "up").Value.Rotation);
  }

  [Fact]
  public void CoreBox_is_wood_and_carries_its_slot() {
    GenOutput core = Core("[10, 2, 3]");
    GenOutput box = Box(core);

    CheckResult roles = new Role().Run(
      new CheckInput(box.Shape, Record: box.Record, Settings: Fx.Settings)
    );

    Assert.Equal(0, roles.ExitCode);
    Assert.Equal(
      "drysandcore-castthing",
      (string?)box.Record.Carried["corebox"]!["core"]
    );
    Assert.Equal(
      ["Tray1", "Tray2"],
      box.Record.Carried["corebox"]!["trays"]!.Select(t => (string?)t)
    );
  }
}
