using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Checks;

/// <summary>Which checks run for a part: those its kinds name, under the lint profile in force.</summary>
public static class Selection {
  /// <summary>Every check the tool holds.</summary>
  public static IReadOnlyList<ICheck> All { get; } =
  [
    new Facecheck(),
    new SizesCheck(),
    new Flushcheck(),
    new Footsweep(),
    new Octcheck(),
    new OctRegular(),
    new Uvwrap(),
    new Castonly(),
    new Allowance(),
    new Split(),
    new Role(),
    new Draw(),
    new Blankmatch(),
    new Impression(),
    new Flood(),
    new Capacity(),
    new Fit(),
    new Runner(),
    new Pieces(),
    new Feeding(),
    new Pit(),
    new Parting(),
    new Corefit(),
  ];

  /// <summary>Whether <paramref name="check"/> runs under <paramref name="profile"/>: structural checks always,
  /// period checks only under <see cref="Profile.ExpandedPeriod"/>.</summary>
  public static bool Runs(ICheck check, Profile profile) =>
    check.Profile == Profile.Structural || profile == Profile.ExpandedPeriod;

  /// <summary>The globs of the octagons a record holds regular, from its <c>regular</c> slot; empty when it has
  /// none.</summary>
  public static IReadOnlyList<string> RegularGlobs(PartRecord record) =>
    record.Carried["regular"] is JArray a
      ? [.. a.Select(t => (string?)t ?? "").Where(g => g != "")]
      : [];

  /// <summary>Whether <paramref name="check"/> has anything to read in <paramref name="input"/>. octregular needs
  /// the record to name regular globs (<see cref="RegularGlobs"/>), uvwrap a round run in the shape, blankmatch a
  /// <c>blank</c> slot in the record; impression, flood, capacity, fit, pieces and parting not a closed pour's
  /// cope, whose record holds only its pair; feeding a closed pour or a <c>head</c> feed, pit a station with a head
  /// height, corefit a record with a <c>core</c> or <c>corebox</c> slot or a pour that sets cores; every other check always applies. Without these a check enumerates nothing
  /// and fails.</summary>
  public static bool Applies(ICheck check, CheckInput input) =>
    check.Id switch {
      "octregular" => input.Record != null
        && RegularGlobs(input.Record).Count > 0,
      "uvwrap" => input.Shape != null
        && RoundRuns.Find(new Tree(input.Shape), new Frames(input.Shape)).Count
          > 0,
      "blankmatch" => input.Record?.Carried.ContainsKey("blank") == true,
      "impression" or "flood" or "capacity" or "fit" or "pieces" or "parting" =>
        !Pour.IsCope(input.Record),
      "feeding" => PourOf(input.Record) is not { } f
        || f.Closed
        || f.Feeds.Any(x => x.Kind == "head"),
      "corefit" => Corefit.Reads(input.Record),
      "pit" => !Pour.IsCope(input.Record)
        && (
          PourOf(input.Record) is not { } q
          || StationOf(input, q) is not { } s
          || s.HeadHeight != null
        ),
      _ => true,
    };

  private static Pour? PourOf(PartRecord? record) {
    try {
      return Pour.Read(record);
    } catch (CheckUsageException) {
      return null;
    }
  }

  private static Station? StationOf(CheckInput input, Pour pour) =>
    input.Station
    ?? (
      input.Settings?.Stations.TryGetValue(pour.Station, out Station? s) == true
        ? s
        : null
    );

  /// <summary>Parses a settings lint profile name.</summary>
  /// <exception cref="ArgumentException">The name is neither <c>structural</c> nor <c>expanded-period</c>.</exception>
  public static Profile ParseProfile(string name) =>
    name switch {
      "structural" => Profile.Structural,
      "expanded-period" => Profile.ExpandedPeriod,
      _ => throw new ArgumentException($"unknown lint profile '{name}'"),
    };

  /// <summary>The checks of <paramref name="kinds"/>' gates in <paramref name="candidates"/> that run under
  /// <paramref name="profile"/>, in the table's order.</summary>
  /// <exception cref="ArgumentException">A kind is unknown, or a gate id has no check in the candidates.</exception>
  public static IReadOnlyList<ICheck> For(
    IEnumerable<string> kinds,
    Profile profile,
    IReadOnlyList<ICheck>? candidates = null
  ) {
    candidates ??= All;
    return
    [
      .. KindTable
        .Gates(kinds)
        .Select(id =>
          candidates.FirstOrDefault(c => c.Id == id)
          ?? throw new ArgumentException($"no check '{id}'")
        )
        .Where(c => Runs(c, profile)),
    ];
  }
}
