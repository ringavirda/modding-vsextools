using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes;

/// <summary>A typed or mechanical port a scene cell carries.</summary>
/// <param name="Face">The face letter n, e, s, w, u or d the port opens on; null for a mechanical
/// port with no face.</param>
/// <param name="Type">The network type, or <c>mechanical</c>.</param>
/// <param name="Joins">The key of the network or line the port joins; null when it joins none.</param>
public sealed record ScenePort(string? Face, string Type, string? Joins);

/// <summary>One cell of a scene, in world coordinates.</summary>
/// <param name="At">The cell position.</param>
/// <param name="Code">The block code.</param>
/// <param name="Id">The setup id holding the cell; null for a filler or an unnamed cell.</param>
/// <param name="Role">core, filler, port, block or fluid.</param>
/// <param name="Principal">The core a filler or port belongs to; null for any other role.</param>
/// <param name="Network">The network key the cell is on; null when on none.</param>
/// <param name="Line">The mechanical line key the cell is on; null when on none.</param>
/// <param name="Port">The port this cell is; null unless <paramref name="Role"/> is port.</param>
/// <param name="Open">The faces the cell's connector answers on that no neighbour joins.</param>
public sealed record SceneCell(
  Offset At,
  string Code,
  string? Id,
  string Role,
  Offset? Principal,
  string? Network,
  string? Line,
  ScenePort? Port,
  IReadOnlyList<string> Open
);

/// <summary>A pipe or gas network of a scene.</summary>
/// <param name="Key">The network key cells and ports name.</param>
/// <param name="Type">The network type, such as <c>steam</c>.</param>
/// <param name="Nodes">The number of cells on the network; a count, not a list.</param>
/// <param name="Ids">The setup ids of the network's cells in cell order, a filler or port counted
/// under its principal's id, then the principal ids of the ports that join it; each once.</param>
public sealed record SceneNetwork(
  string Key,
  string Type,
  int Nodes,
  IReadOnlyList<string> Ids
);

/// <summary>A mechanical line of a scene.</summary>
/// <param name="Key">The line key cells and ports name.</param>
/// <param name="Members">The number of cells on the line; a count, not a list.</param>
/// <param name="Ids">The setup ids of the line's cells in cell order, a filler or port counted
/// under its principal's id, then the principal ids of the ports that join it; each once.</param>
public sealed record SceneLine(
  string Key,
  int Members,
  IReadOnlyList<string> Ids
);

/// <summary>
/// A setup scene as the harness dumps it: every non-air cell of a built setup, with its network or
/// line key, ports and open faces. The format is <c>setup-scene/1</c>.
/// </summary>
/// <param name="Setup">The setup's name.</param>
/// <param name="Variant">The variant's name; null when the setup has none.</param>
/// <param name="Game">The game version the dump was made on; null when absent.</param>
/// <param name="Cells">Every cell, in file order.</param>
/// <param name="Networks">The pipe and gas networks.</param>
/// <param name="Lines">The mechanical lines.</param>
public sealed record SceneFile(
  string Setup,
  string? Variant,
  string? Game,
  IReadOnlyList<SceneCell> Cells,
  IReadOnlyList<SceneNetwork> Networks,
  IReadOnlyList<SceneLine> Lines
) {
  /// <summary>The value the file's <c>format</c> must hold.</summary>
  public const string Format = "setup-scene/1";

  /// <summary>Reads a scene file.</summary>
  /// <param name="path">A <c>.scene.json</c> file.</param>
  /// <exception cref="InvalidDataException">The file is not JSON, its format is not
  /// <see cref="Format"/>, a cell lacks its position or code, or a position or count is not an
  /// integer.</exception>
  /// <exception cref="IOException">The file cannot be read.</exception>
  public static SceneFile Load(string path) => Parse(File.ReadAllText(path));

  /// <summary>Reads a scene from its JSON text; see <see cref="Load"/>.</summary>
  public static SceneFile Parse(string json) {
    JObject root;
    try {
      root = JObject.Parse(json);
    } catch (Newtonsoft.Json.JsonException e) {
      throw new InvalidDataException($"not a scene file: {e.Message}", e);
    }
    string? format = (string?)root["format"];
    if (format != Format)
      throw new InvalidDataException(
        $"scene format {format ?? "(none)"} is not {Format}"
      );
    try {
      return new SceneFile(
        Required(root, "setup"),
        (string?)root["variant"],
        (string?)root["game"],
        [.. Items(root, "cells").Select(Cell)],
        [
          .. Items(root, "networks")
            .Select(n => new SceneNetwork(
              Required(n, "key"),
              Required(n, "type"),
              (int?)n["nodes"] ?? 0,
              Strings(n["ids"])
            )),
        ],
        [
          .. Items(root, "lines")
            .Select(l => new SceneLine(
              Required(l, "key"),
              (int?)l["members"] ?? 0,
              Strings(l["ids"])
            )),
        ]
      );
    } catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException) {
      throw new InvalidDataException($"not a scene file: {e.Message}", e);
    }
  }

  private static SceneCell Cell(JObject c) =>
    new(
      At(c["at"]) ?? throw new InvalidDataException("a cell has no position"),
      Required(c, "code"),
      (string?)c["id"],
      (string?)c["role"] ?? "block",
      At(c["principal"]),
      (string?)c["network"],
      (string?)c["line"],
      c["port"] is JObject p
        ? new ScenePort(
          (string?)p["face"],
          (string?)p["type"] ?? "mechanical",
          (string?)p["joins"]
        )
        : null,
      Strings(c["open"])
    );

  private static IEnumerable<JObject> Items(JObject root, string key) =>
    root[key] is JArray list ? list.OfType<JObject>() : [];

  private static string Required(JObject o, string key) =>
    (string?)o[key] ?? throw new InvalidDataException($"missing \"{key}\"");

  private static IReadOnlyList<string> Strings(JToken? token) =>
    token is JArray list ? [.. list.Select(t => (string)t!)] : [];

  private static Offset? At(JToken? token) =>
    token is JArray { Count: 3 } a
    && a.All(t => t.Type == JTokenType.Integer)
      ? new Offset((int)a[0], (int)a[1], (int)a[2])
      : token is JArray { Count: 3 }
        ? throw new InvalidDataException("a position is not three integers")
        : null;
}
