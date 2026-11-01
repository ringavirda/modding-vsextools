using System.IO;
using Xunit;

namespace ExpandedLib.Shapes.Core.Tests;

public class ProvenanceTests {
  private const string ShapeText =
    """{ "textures": {}, "elements": [ { "name": "A", "from": [0,0,0], "to": [1,1,1] } ] }""";

  private static void InDir(System.Action<string, Provenance, Shape> body) {
    string dir = Directory.CreateTempSubdirectory().FullName;
    try {
      body(
        dir,
        new Provenance(Path.Combine(dir, "provenance.jsonl")),
        Reader.Read(ShapeText)
      );
    } finally {
      Directory.Delete(dir, true);
    }
  }

  private static ProvenanceRow RowFor(string path) =>
    new(
      path,
      Provenance.Sha256Of(File.ReadAllBytes(path)),
      "castgen",
      "v1",
      "in"
    );

  [Fact]
  public void Write_to_owned_file_is_refused() {
    InDir(
      (dir, store, shape) => {
        string path = Path.Combine(dir, "owned.json");
        File.WriteAllText(path, ShapeText);
        var e = Assert.Throws<ShapeWriteException>(() =>
          Ownership.WriteGenerated(store, path, shape)
        );
        Assert.Contains("owned.json", e.Message);
        Assert.Equal(ShapeText, File.ReadAllText(path));
      }
    );
  }

  [Fact]
  public void Write_to_file_edited_after_its_row_is_refused() {
    InDir(
      (dir, store, shape) => {
        string path = Path.Combine(dir, "gen.json");
        Ownership.WriteGenerated(store, path, shape);
        store.Append(RowFor(path));
        File.AppendAllText(path, " ");
        Assert.Throws<ShapeWriteException>(() =>
          Ownership.WriteGenerated(store, path, shape)
        );
      }
    );
  }

  [Fact]
  public void Generated_file_and_new_file_are_writable() {
    InDir(
      (dir, store, shape) => {
        string path = Path.Combine(dir, "sub", "gen.json");
        Ownership.WriteGenerated(store, path, shape);
        store.Append(RowFor(path));
        Ownership.WriteGenerated(store, path, shape);
        Assert.Equal(CanonicalWriter.Write(shape), File.ReadAllText(path));
        Assert.Equal("sub/gen.json", store.Find(path)!.Path);
      }
    );
  }

  [Fact]
  public void Rows_survive_reopening() {
    InDir(
      (dir, store, shape) => {
        string path = Path.Combine(dir, "gen.json");
        Ownership.WriteGenerated(store, path, shape);
        ProvenanceRow row = RowFor(path);
        store.Append(row);
        var again = new Provenance(store.FilePath);
        ProvenanceRow found = Assert.IsType<ProvenanceRow>(
          again.Find("gen.json")
        );
        Assert.Equal(row with { Path = "gen.json" }, found);
        Assert.True(Ownership.CanWrite(again, path));
      }
    );
  }

  [Fact]
  public void A_relative_path_is_judged_against_the_store_directory() {
    InDir(
      (dir, store, shape) => {
        string path = Path.Combine(dir, "owned.json");
        File.WriteAllText(path, "hand drawn");
        Assert.False(Ownership.CanWrite(store, "owned.json"));
      }
    );
  }

  [Fact]
  public void Input_hash_depends_on_each_input() {
    string h = Provenance.InputHash("spec", "shape", "rec", "set");
    Assert.Equal(h, Provenance.InputHash("spec", "shape", "rec", "set"));
    Assert.NotEqual(h, Provenance.InputHash("spec2", "shape", "rec", "set"));
    Assert.NotEqual(h, Provenance.InputHash("spec", "shape2", "rec", "set"));
    Assert.NotEqual(h, Provenance.InputHash("spec", "shape", "rec2", "set"));
    Assert.NotEqual(h, Provenance.InputHash("spec", "shape", "rec", "set2"));
  }
}
