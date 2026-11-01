using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Core;

/// <summary>Prints a JSON tree in one of the tool's layouts.</summary>
internal sealed class JsonLayout {
  /// <summary>Two-space indent, LF; objects and arrays of plain values on one line; numbers rounded
  /// to 1e-4 and written without a needless fraction.</summary>
  public static readonly JsonLayout Canonical = new() {
    Indent = "  ",
    Newline = "\n",
    Inline = true,
    Round = true,
    WholeFloats = false,
    UnsignedZero = true,
  };

  /// <summary>The canonical layout with numbers as held, for a part record.</summary>
  public static readonly JsonLayout Record = new() {
    Indent = "  ",
    Newline = "\n",
    Inline = true,
    Round = false,
    WholeFloats = false,
    UnsignedZero = true,
  };

  /// <summary>One-space indent, LF, every array expanded: Python's <c>json.dump(indent=1)</c>; numbers
  /// as held, a whole float written with <c>.0</c>.</summary>
  public static readonly JsonLayout Python = new() {
    Indent = " ",
    Newline = "\n",
    Inline = false,
    Round = false,
    WholeFloats = true,
    LowerExponent = true,
  };

  /// <summary>Tabs, CRLF, plain-valued objects and arrays on one line, a blank line between clips;
  /// numbers as held.</summary>
  public static readonly JsonLayout Vsmc = new() {
    Indent = "\t",
    Newline = "\r\n",
    Inline = true,
    Round = false,
    WholeFloats = true,
    BlankBetweenClips = true,
  };

  private string Indent { get; init; } = "";
  private string Newline { get; init; } = "";
  private bool Inline { get; init; }
  private bool Round { get; init; }
  private bool WholeFloats { get; init; }
  private bool LowerExponent { get; init; }
  private bool UnsignedZero { get; init; }
  private bool BlankBetweenClips { get; init; }

  /// <summary>The text of <paramref name="root"/> without a final line break.</summary>
  public string Print(JToken root) {
    var sb = new StringBuilder();
    Write(sb, root, 0, null, false);
    return sb.ToString();
  }

  private void Write(
    StringBuilder sb,
    JToken t,
    int depth,
    string? key,
    bool inMap
  ) {
    switch (t) {
      case JObject o:
        WriteContainer(
          sb,
          o.Properties().Select(p => ((string?)p.Name, p.Value)).ToList(),
          '{',
          '}',
          depth,
          key,
          true,
          inMap
        );
        break;
      case JArray a:
        WriteContainer(
          sb,
          a.Select(v => ((string?)null, v)).ToList(),
          '[',
          ']',
          depth,
          key,
          false,
          false
        );
        break;
      default:
        Scalar(sb, (JValue)t);
        break;
    }
  }

  private void WriteContainer(
    StringBuilder sb,
    System.Collections.Generic.List<(string? Name, JToken Value)> items,
    char open,
    char close,
    int depth,
    string? key,
    bool named,
    bool inMap
  ) {
    if (items.Count == 0) {
      sb.Append(open).Append(close);
      return;
    }
    bool plain = items.All(i => i.Value is JValue || IsPlainArray(i.Value));
    bool oneLine =
      Inline && plain && (named ? inMap : items.All(i => i.Value is JValue));
    bool mapOfObjects = named && items.All(i => i.Value is JObject);
    if (oneLine) {
      sb.Append(open).Append(' ');
      for (int i = 0; i < items.Count; i++) {
        if (i > 0)
          sb.Append(", ");
        Item(sb, items[i], depth, named, false);
      }
      sb.Append(' ').Append(close);
      return;
    }
    sb.Append(open).Append(Newline);
    for (int i = 0; i < items.Count; i++) {
      sb.Append(string.Concat(Enumerable.Repeat(Indent, depth + 1)));
      Item(
        sb,
        items[i],
        depth + 1,
        named,
        mapOfObjects,
        open == '[' ? key : null
      );
      if (i < items.Count - 1) {
        sb.Append(',');
        if (BlankBetweenClips && open == '[' && key == "animations")
          sb.Append(Newline);
      }
      sb.Append(Newline);
    }
    sb.Append(string.Concat(Enumerable.Repeat(Indent, depth))).Append(close);
  }

  private void Item(
    StringBuilder sb,
    (string? Name, JToken Value) item,
    int depth,
    bool named,
    bool inMap,
    string? arrayKey = null
  ) {
    if (named)
      sb.Append(JsonConvert.ToString(item.Name!)).Append(": ");
    Write(sb, item.Value, depth, named ? item.Name : arrayKey, inMap);
  }

  private static bool IsPlainArray(JToken t) =>
    t is JArray a && a.All(v => v is JValue);

  private void Scalar(StringBuilder sb, JValue v) {
    switch (v.Type) {
      case JTokenType.Integer:
        sb.Append(
          Convert
            .ToInt64(v.Value, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture)
        );
        break;
      case JTokenType.Float:
        sb.Append(Float((double)v));
        break;
      case JTokenType.String:
        sb.Append(JsonConvert.ToString((string)v!));
        break;
      case JTokenType.Boolean:
        sb.Append((bool)v ? "true" : "false");
        break;
      case JTokenType.Null:
        sb.Append("null");
        break;
      default:
        throw new JsonException($"cannot write a {v.Type} value");
    }
  }

  private string Float(double d) {
    if (Round)
      d = Math.Round(d, 4);
    if (UnsignedZero && d == 0)
      d = 0;
    string s = d.ToString("R", CultureInfo.InvariantCulture);
    if (LowerExponent)
      s = s.Replace('E', 'e');
    if (WholeFloats && s.All(c => c == '-' || char.IsDigit(c)))
      s += ".0";
    return s;
  }
}
