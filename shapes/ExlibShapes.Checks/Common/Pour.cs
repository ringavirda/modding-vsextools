using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>A cut in the sand the metal runs through: boxes in the filling's space, or the pattern elements named by
/// globs placed by the piece it enters.</summary>
internal sealed record Feed(
  string Name,
  string Kind,
  IReadOnlyList<Box> Boxes,
  IReadOnlyList<string> Patterns,
  int? Into
);

/// <summary>The <c>pour</c> slot of a filling's part record.</summary>
internal sealed class Pour {
  public string Station { get; private init; } = "";
  public bool Closed { get; private init; }
  public bool Cope { get; private init; }
  public string? Pair { get; private init; }
  public string Cast { get; private init; } = "";
  public string Pattern { get; private init; } = "";
  public IReadOnlyList<Mat4d> Pieces { get; private init; } = [];
  public IReadOnlyList<(string Item, Mat4d At)> Cores { get; private init; } =
  [];
  public IReadOnlyList<Feed> Feeds { get; private init; } = [];
  public double? PerPiece { get; private init; }
  public double? Share { get; private init; }

  /// <summary>Whether the record is a closed pour's cope, whose slot holds only <c>half</c> and <c>pair</c>.</summary>
  public static bool IsCope(PartRecord? record) =>
    record?.Carried["pour"] is JObject o && (string?)o["half"] == "cope";

  /// <summary>Reads the slot.</summary>
  /// <exception cref="CheckUsageException">The record has no <c>pour</c>, or a field is malformed.</exception>
  public static Pour Read(PartRecord? record) {
    if (record?.Carried["pour"] is not JObject o)
      throw new CheckUsageException("the filling's record has no pour slot");
    try {
      if ((string?)o["half"] == "cope")
        return new Pour { Cope = true, Pair = (string?)o["pair"] };
      return new Pour {
        Station = (string?)o["station"] ?? "",
        Closed = (string?)o["mould"] == "closed",
        Pair = (string?)o["pair"],
        Cast = (string?)o["cast"] ?? "",
        Pattern = (string?)o["pattern"] ?? "",
        Pieces = [.. (o["pieces"] as JArray ?? []).Select(p => At(p["at"]))],
        Cores =
        [
          .. (o["cores"] as JArray ?? []).Select(c =>
            ((string?)c["item"] ?? "", At(c["at"]))
          ),
        ],
        Feeds = [.. (o["feeds"] as JArray ?? []).Select(ReadFeed)],
        PerPiece = (double?)o["units"]?["perPiece"],
        Share = (double?)o["units"]?["share"],
      };
    } catch (Exception e)
        when (e
            is ArgumentException
              or InvalidCastException
              or NullReferenceException
        ) {
      throw new CheckUsageException($"the pour slot is malformed: {e.Message}");
    }
  }

  private static Mat4d At(JToken? t) =>
    Mat4d.FromRows([.. (t as JArray ?? []).Select(v => (double)v)]);

  private static Feed ReadFeed(JToken t) =>
    new(
      (string?)t["name"] ?? "",
      (string?)t["kind"] ?? "",
      [
        .. (t["boxes"] as JArray ?? []).Select(b =>
        {
          double[] v = [.. b.Select(n => (double)n)];
          if (v.Length != 6)
            throw new ArgumentException("a feed box needs 6 numbers");
          return new Box(
            new Vec3(v[0], v[1], v[2]),
            new Vec3(v[3], v[4], v[5])
          );
        }),
      ],
      [.. (t["pattern"] as JArray ?? []).Select(g => (string)g!)],
      (int?)t["into"]
    );
}
