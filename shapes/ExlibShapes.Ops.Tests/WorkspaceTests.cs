using System;
using System.IO;
using Xunit;

namespace ExpandedLib.Shapes.Ops.Tests;

public class WorkspaceTests : IDisposable {
  private readonly string w = Path.Combine(
    Path.GetTempPath(),
    "shapetool-ws-" + Guid.NewGuid().ToString("N")
  );

  public void Dispose() {
    try {
      Directory.Delete(w, true);
    } catch (IOException) { }
  }

  private string Put(string rel, string text) {
    string path = Path.Combine(w, rel);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
    return path;
  }

  private string Tree() {
    Directory.CreateDirectory(Path.Combine(w, "exmods", "mods"));
    Put("exmod.workspace.json", "{}");
    return Put("workbench/shapes-finished/x.json", "{}");
  }

  // Fails when the sibling-exmods branch of ModsRoot.Of is removed: RootOf answers W, not W/exmods.
  [Fact]
  public void RootOf_a_workbench_drawing_is_the_sibling_exmods() {
    string drawing = Tree();
    Assert.Equal(Path.Combine(w, "exmods"), BlockDrawings.RootOf(drawing));
  }

  // Fails when the first guard of ModsRoot.Of (a root holding mods/ stays) is removed: RootOf answers W/exmods.
  [Fact]
  public void RootOf_keeps_a_root_that_holds_mods() {
    Tree();
    Directory.CreateDirectory(Path.Combine(w, "lane", "mods"));
    string drawing = Put("lane/workbench/x.json", "{}");
    Assert.Equal(Path.Combine(w, "lane"), BlockDrawings.RootOf(drawing));
  }

  // Fails when `? exmods : root` in ModsRoot.Of answers exmods without checking it holds mods/: RootOf answers W/exmods.
  [Fact]
  public void RootOf_keeps_the_root_when_the_workspace_exmods_holds_no_mods() {
    Put("exmod.workspace.json", "{}");
    Directory.CreateDirectory(Path.Combine(w, "exmods"));
    string drawing = Put("workbench/x.json", "{}");
    Assert.Equal(w, BlockDrawings.RootOf(drawing));
  }

  // Fails when Find stops at the mods root: the W/workbench sketch is not listed.
  [Fact]
  public void Find_lists_the_workbench_drawings_beside_the_mods_root() {
    string drawing = Tree();
    string sketch = Put("workbench/shapes-sketch/x-sketch.json", "{}");
    Put("exmods/mods/m/assets/m/shapes/x/x.json", "{}");
    var found = BlockDrawings.Find(drawing, BlockDrawings.RootOf(drawing));
    Assert.Contains(found, d => d.Kind == "sketch" && d.Path == sketch);
    Assert.Contains(found, d => d.Kind == "shipped");
  }

  // Fails when LoadSettings drops the workbench/shapetool.json argument to FindUp: it throws CliUsageException.
  [Fact]
  public void LoadSettings_finds_workbench_settings_beside_the_workspace() {
    string drawing = Tree();
    Put(
      "workbench/shapetool.json",
      "{\"steps\": {}, \"textures\": {}, \"roles\": {}, \"stations\": {}, \"lint\": \"structural\"}"
    );
    Assert.NotNull(
      ExpandedLib.Shapes.Ops.Workspace.LoadSettings(
        null,
        Path.GetDirectoryName(drawing)!
      )
    );
  }

  // Fails when FindUp skips the ancestors of startDir: the workbench settings are not found.
  [Fact]
  public void FindUp_finds_workbench_settings_beside_the_workspace() {
    string drawing = Tree();
    string settings = Put("workbench/shapetool.json", "{}");
    Assert.Equal(
      settings,
      ExpandedLib.Shapes.Ops.Workspace.FindUp(
        Path.GetDirectoryName(drawing)!,
        "shapetool.json",
        Path.Combine("workbench", "shapetool.json")
      )
    );
  }
}
