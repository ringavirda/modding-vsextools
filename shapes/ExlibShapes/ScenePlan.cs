using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;

namespace ExpandedLib.Shapes;

/// <summary>
/// Draws a <see cref="SceneFile"/> as one plan-grid SVG per Y layer: a cell per structure column,
/// filled in its network's or line's colour, labelled with its setup id over its key, with fillers
/// hatched in their principal's fill, ports as arrows and open faces as ticks.
/// </summary>
public static class ScenePlan {
  /// <summary>The colour of an unjoined port's arrow and of an open face's tick.</summary>
  public const string Unjoined = "#DC2828";

  /// <summary>The fill of a fluid-only cell.</summary>
  public const string Fluid = "#9DC3E6";

  /// <summary>The fill of a cell on no network or line.</summary>
  public const string Plain = "#999999";

  private const int Cell = 32;
  private const int Margin = 24;
  private const int LegendRow = 14;

  /// <summary>
  /// The colour of each network and line key: the schematic palette in order with the red entry
  /// skipped, networks first and then lines in file order.
  /// </summary>
  public static IReadOnlyDictionary<string, string> KeyColors(SceneFile scene) {
    string[] palette = [.. Schematic.Palette.Where(c => c != "#C44E52")];
    var colors = new Dictionary<string, string>(StringComparer.Ordinal);
    int i = 0;
    foreach (
      string key in scene
        .Networks.Select(n => n.Key)
        .Concat(scene.Lines.Select(l => l.Key))
    ) {
      colors[key] = i < palette.Length ? palette[i] : Schematic.ColorFor(i + 1);
      i++;
    }
    return colors;
  }

  /// <summary>The Y layers any cell of the scene stands on, ascending.</summary>
  public static IReadOnlyList<int> Layers(SceneFile scene) =>
    [.. scene.Cells.Select(c => c.At.Y).Distinct().OrderBy(y => y)];

  /// <summary>
  /// One Y layer as a plan grid. Every layer shares the grid extents of the whole scene. A cell's
  /// label is its id cut to 5 characters, a filler's or port's the principal's id; a fluid-only
  /// cell is <see cref="Fluid"/> and unlabelled. The legend under the grid lists each key with its
  /// type and ids.
  /// </summary>
  /// <param name="scene">The scene to draw.</param>
  /// <param name="y">The layer; see <see cref="Layers"/>.</param>
  public static string Svg(SceneFile scene, int y) {
    IReadOnlyDictionary<string, string> colors = KeyColors(scene);
    var byAt = new Dictionary<Offset, SceneCell>();
    foreach (SceneCell c in scene.Cells.Where(c => c.Role != "fluid"))
      byAt.TryAdd(c.At, c);

    string ColorOf(SceneCell c) {
      string? key = c.Network ?? c.Line;
      return key != null && colors.TryGetValue(key, out string? color)
        ? color
        : Plain;
    }

    string FillOf(SceneCell c) =>
      c.Role is "filler" or "port"
      && c.Principal is { } p
      && byAt.TryGetValue(p, out SceneCell? core)
        ? ColorOf(core)
        : ColorOf(c);

    string LabelOf(SceneCell c) {
      string? id = c.Role is "filler" or "port"
        ? c.Principal is { } p && byAt.TryGetValue(p, out SceneCell? core)
          ? core.Id
          : null
        : c.Id;
      return id == null ? ""
        : id.Length > 5 ? id[..5]
        : id;
    }

    string KeyOf(SceneCell c) {
      if (
        c.Role is "filler" or "port"
        && c.Principal is { } p
        && byAt.TryGetValue(p, out SceneCell? core)
      )
        return core.Network ?? core.Line ?? "";
      return c.Network ?? c.Line ?? "";
    }

    int x0 = scene.Cells.Min(c => c.At.X),
      x1 = scene.Cells.Max(c => c.At.X);
    int z0 = scene.Cells.Min(c => c.At.Z),
      z1 = scene.Cells.Max(c => c.At.Z);
    int width = (x1 - x0 + 1) * Cell;
    int height = (z1 - z0 + 1) * Cell;
    int Px(int x) => (x - x0) * Cell;
    int Pz(int z) => (z - z0) * Cell;

    List<(string Color, string Text)> legend = [];
    foreach (SceneNetwork n in scene.Networks)
      legend.Add(
        (colors[n.Key], $"{n.Key} {n.Type}: {string.Join(", ", n.Ids)}")
      );
    foreach (SceneLine l in scene.Lines)
      legend.Add(
        (colors[l.Key], $"{l.Key} mechanical: {string.Join(", ", l.Ids)}")
      );
    legend.Add((Unjoined, "red arrow: unjoined port, red tick: open face"));

    string caption = Schematic.LayerCaption(y);
    int canvas =
      Math.Max(
        width,
        Math.Max(
          Schematic.CaptionWidth(caption),
          legend.Max(r => Schematic.CaptionWidth(r.Text) + 16)
        )
      )
      + 2 * Margin;
    int left = (canvas - width) / 2;
    var sb = new StringBuilder();
    sb.Append(
      $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{canvas}\" "
        + $"height=\"{height + 2 * Margin + Schematic.CaptionSpace + legend.Count * LegendRow}\" "
        + $"font-family=\"sans-serif\" font-size=\"{Schematic.FontSize}\" style=\"background-color:#f0f0ec\">"
    );
    sb.Append("<defs>");
    sb.Append(Schematic.Hatch);
    var arrows = new SortedSet<string>(StringComparer.Ordinal) { Unjoined };
    foreach (string color in colors.Values)
      arrows.Add(color);
    foreach (string color in arrows)
      sb.Append(
        $"<marker id=\"arrow{color[1..]}\" markerWidth=\"6\" markerHeight=\"6\" refX=\"3\" refY=\"3\" "
          + $"orient=\"auto\"><path d=\"M0,0 L6,3 L0,6 z\" fill=\"{color}\" /></marker>"
      );
    sb.Append("</defs>");
    sb.Append($"<g transform=\"translate({left},{Margin})\">");

    foreach (SceneCell c in scene.Cells) {
      if (c.At.Y != y)
        continue;
      if (c.Role == "fluid" && byAt.ContainsKey(c.At))
        continue;
      if (c.Role == "fluid") {
        sb.Append(
          $"<rect class=\"cell fluid\" x=\"{Px(c.At.X)}\" y=\"{Pz(c.At.Z)}\" width=\"{Cell}\" height=\"{Cell}\" "
            + $"fill=\"{Fluid}\" stroke=\"black\" stroke-width=\"1\" />"
        );
        continue;
      }
      bool filler = c.Role is "filler" or "port";
      string fill = FillOf(c);
      string cls =
        c.Role == "core" ? "cell anchor"
        : filler ? "cell filler"
        : "cell";
      sb.Append(
        $"<rect class=\"{cls}\" x=\"{Px(c.At.X)}\" y=\"{Pz(c.At.Z)}\" width=\"{Cell}\" height=\"{Cell}\" "
          + $"fill=\"{fill}\" stroke=\"black\" stroke-width=\"{(c.Role == "core" ? 3 : 1)}\" />"
      );
      if (filler)
        sb.Append(
          $"<rect class=\"hatched\" x=\"{Px(c.At.X)}\" y=\"{Pz(c.At.Z)}\" width=\"{Cell}\" height=\"{Cell}\" "
            + "fill=\"url(#hatch)\" />"
        );
      string label = LabelOf(c);
      string key = KeyOf(c);
      double cx = Px(c.At.X) + Cell / 2.0;
      double cz = Pz(c.At.Z) + Cell / 2.0;
      string ink = filler ? "black" : Schematic.Ink(fill);
      if (label != "")
        sb.Append(
          $"<text class=\"number\" x=\"{Schematic.Svg(cx)}\" y=\"{Schematic.Svg(cz - 1)}\" "
            + $"text-anchor=\"middle\" fill=\"{ink}\">{SecurityElement.Escape(label)}</text>"
        );
      if (key != "")
        sb.Append(
          $"<text class=\"key\" x=\"{Schematic.Svg(cx)}\" y=\"{Schematic.Svg(cz + 10)}\" "
            + $"text-anchor=\"middle\" fill=\"{ink}\">{SecurityElement.Escape(key)}</text>"
        );
      if (c.Port is { Face: { } face } port) {
        string color =
          port.Joins != null
          && colors.TryGetValue(port.Joins, out string? joined)
            ? joined
            : Unjoined;
        Arrow(sb, face, cx, cz, color);
      }
      foreach (string open in c.Open)
        Tick(sb, open, Px(c.At.X), Pz(c.At.Z));
    }

    Schematic.Edges(sb, width, height, null);
    sb.Append(
      $"<text class=\"caption\" x=\"{Schematic.Svg(width / 2.0)}\" y=\"{height + Schematic.CaptionSpace - 4}\" "
        + $"text-anchor=\"middle\">{caption}</text>"
    );
    for (int i = 0; i < legend.Count; i++) {
      int ty = height + Schematic.CaptionSpace + 12 + i * LegendRow;
      sb.Append(
        $"<rect class=\"swatch\" x=\"0\" y=\"{ty - 9}\" width=\"10\" height=\"10\" fill=\"{legend[i].Color}\" stroke=\"black\" />"
      );
      sb.Append(
        $"<text class=\"legend\" x=\"16\" y=\"{ty}\">{SecurityElement.Escape(legend[i].Text)}</text>"
      );
    }
    sb.Append("</g></svg>");
    return sb.ToString();
  }

  private static void Arrow(
    StringBuilder sb,
    string face,
    double cx,
    double cz,
    string color
  ) {
    (int dx, int dz) = face switch {
      "n" => (0, -1),
      "s" => (0, 1),
      "e" => (1, 0),
      "w" => (-1, 0),
      _ => (0, 0),
    };
    if (dx == 0 && dz == 0) {
      sb.Append(
        $"<circle class=\"connector\" cx=\"{Schematic.Svg(cx)}\" cy=\"{Schematic.Svg(cz)}\" r=\"4\" "
          + $"fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" />"
      );
      return;
    }
    sb.Append(
      $"<line class=\"connector\" x1=\"{Schematic.Svg(cx)}\" y1=\"{Schematic.Svg(cz)}\" "
        + $"x2=\"{Schematic.Svg(cx + dx * Cell * 0.4)}\" y2=\"{Schematic.Svg(cz + dz * Cell * 0.4)}\" "
        + $"stroke=\"{color}\" stroke-width=\"2\" marker-end=\"url(#arrow{color[1..]})\" />"
    );
  }

  private static void Tick(StringBuilder sb, string face, int px, int pz) {
    (int x1, int y1, int x2, int y2) = face switch {
      "n" => (px + 6, pz + 2, px + Cell - 6, pz + 2),
      "s" => (px + 6, pz + Cell - 2, px + Cell - 6, pz + Cell - 2),
      "e" => (px + Cell - 2, pz + 6, px + Cell - 2, pz + Cell - 6),
      "w" => (px + 2, pz + 6, px + 2, pz + Cell - 6),
      _ => (0, 0, 0, 0),
    };
    if (x1 == x2 && y1 == y2)
      return;
    sb.Append(
      $"<line class=\"open\" x1=\"{x1}\" y1=\"{y1}\" x2=\"{x2}\" y2=\"{y2}\" stroke=\"{Unjoined}\" stroke-width=\"3\" />"
    );
  }
}
