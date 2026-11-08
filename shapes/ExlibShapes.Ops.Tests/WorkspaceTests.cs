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

  [Fact]
  public void RootOf_keeps_a_root_that_holds_mods() {
    Directory.CreateDirectory(Path.Combine(w, "mods"));
    string drawing = Put("workbench/x.json", "{}");
    Assert.Equal(w, BlockDrawings.RootOf(drawing));
  }

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
