using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>One drawn leaf of a filling: its world box, its role and the piece group it stands in.</summary>
/// <param name="Node">The element.</param>
/// <param name="Box">The world bounds.</param>
/// <param name="Role"><c>sand</c>, <c>runner</c> or <c>core</c>.</param>
/// <param name="Piece">The k of the <c>Piece&lt;k&gt;</c> group above it, 0 when none.</param>
/// <param name="Level">Whether a group named <c>Level</c> is above it.</param>
/// <param name="Bedded">Whether a group named <c>Bedded</c> is above it.</param>
/// <param name="CoreGroup">The name of the <c>Core&lt;i&gt;</c> group above it, null when none.</param>
/// <param name="Solid">The boxes that make up the leaf: <paramref name="Box"/> itself when aligned, else its lattice cells.</param>
/// <param name="Turned">The area of the leaf's faces that are not axis-aligned, in vx2.</param>
internal sealed record FillingLeaf(
  Node Node,
  Box Box,
  string Role,
  int Piece,
  bool Level,
  bool Bedded = false,
  string? CoreGroup = null,
  IReadOnlyList<Box>? Solid = null,
  double Turned = 0
) {
  /// <summary>The boxes that make up the leaf.</summary>
  public IReadOnlyList<Box> Cells => Solid ?? [Box];
}

/// <summary>A filling read into the solids and the metal of its pour, in the filling's space.</summary>
internal sealed class Pouring {
  private static readonly Regex CoreGroup = new(@"^Core\d+$");
  private static readonly Regex PieceGroup = new(@"^Piece(\d+)$");

  public required Pour Pour { get; init; }
  public required Station Station { get; init; }

  /// <summary>The station's sand box.</summary>
  public required Box Plan { get; init; }

  /// <summary>Sand with every piece pressed: each Bedded shown and each Level hidden.</summary>
  public required List<Box> Sand { get; init; }
  public required List<Box> Runner { get; init; }
  public required List<Box> Cores { get; init; }

  /// <summary>The placed cast, per piece in pressing order.</summary>
  public required List<List<Box>> Cast { get; init; }

  /// <summary>The pour's feeds with their placed boxes.</summary>
  public required List<(Feed Feed, List<Box> Boxes)> Feeds { get; init; }

  /// <summary>The station's inlet cuts and drop.</summary>
  public required List<Box> Cuts { get; init; }

  /// <summary>Where the flood starts: the first inlet cut, else the drop.</summary>
  public required Box Seed { get; init; }

  /// <summary>The area of the faces of the cast, the pattern elements a feed names and the cores that are not
  /// axis-aligned, in vx2: where the metal and the sand are read on different lattices.</summary>
  public required double Turned { get; init; }

  public double Top => Station.SandTo[1];

  /// <summary>The metal: the cast, every feed, the inlet cuts and the drop.</summary>
  public IEnumerable<Box> Metal =>
    Cast.SelectMany(c => c).Concat(Feeds.SelectMany(f => f.Boxes)).Concat(Cuts);

  /// <summary>The leaves of <paramref name="shape"/> as a filling reads them.</summary>
  /// <exception cref="CheckUsageException">The settings lack the runner role.</exception>
  public static List<FillingLeaf> Leaves(
    Shape shape,
    CheckInput input,
    Settings settings,
    Lattice? lattice = null
  ) {
    Lattice lat = lattice ?? Lattice.Fine;
    var tree = new Tree(shape);
    var frames = new Frames(shape);
    string runnerKey = RoleTextures.KeyOf(settings, "runner");
    var leaves = new List<FillingLeaf>();
    foreach (Node leaf in tree.Leaves) {
      bool core = false,
        level = false,
        bedded = false;
      string? coreGroup = null;
      int piece = 0;
      for (Node? n = leaf; n != null; n = n.Parent) {
        if (CoreGroup.IsMatch(n.Name)) {
          core = true;
          coreGroup = n.Name;
        }
        level |= n.Name == "Level";
        bedded |= n.Name == "Bedded";
        if (PieceGroup.Match(n.Name) is { Success: true } m)
          piece = int.Parse(m.Groups[1].Value);
      }
      string role;
      string? tagged = Tags.Of(input.Record, leaf)?.Role;
      if (core)
        role = "core";
      else if (tagged is "runner" or "core" or "sand")
        role = tagged;
      else {
        string tex = (
          leaf.Faces.FirstOrDefault().Value?.Texture ?? ""
        ).TrimStart('#');
        role = RoleTextures.Is(shape, settings, runnerKey, tex)
          ? "runner"
          : "sand";
      }
      Mat4d world = frames.Mat(leaf);
      List<Box> solid = lat.Solid(world, leaf.Size);
      leaves.Add(
        new FillingLeaf(
          leaf,
          WorldBox.Of(world, leaf.Size),
          role,
          piece,
          level,
          bedded,
          coreGroup,
          solid,
          Lattice.TurnedArea(world, leaf.Size)
        )
      );
    }
    return leaves;
  }

  /// <summary>Reads the solids and the metal of the pour <paramref name="input"/> holds.</summary>
  /// <exception cref="CheckUsageException">The record has no pour or an input the pour names is missing.</exception>
  public static Pouring Of(
    CheckInput input,
    string gate,
    Lattice? lattice = null
  ) {
    Lattice lat = lattice ?? Lattice.Fine;
    Shape shape = input.Shape!;
    Pour pour = Pour.Read(input.Record);
    Settings settings =
      input.Settings
      ?? throw new CheckUsageException(
        $"{gate} reads the settings, none given"
      );
    Station station = input.Station ?? StationOf(settings, pour.Station);
    Shape cast =
      input.Cast
      ?? throw new CheckUsageException($"{gate} reads the cast, none given");
    List<FillingLeaf> leaves = Leaves(shape, input, settings, lat);
    List<Box> sand = SandOf(leaves),
      runner = RoleOf(leaves, "runner"),
      cores = RoleOf(leaves, "core");
    if (pour.Closed) {
      double parting =
        station.Parting
        ?? throw new CheckUsageException(
          $"{gate}: a closed pour needs a station with a parting"
        );
      Shape pair =
        input.Pair
        ?? throw new CheckUsageException(
          $"{gate} reads the cope of a closed pour, none given"
        );
      List<FillingLeaf> cope = Leaves(pair, input, settings, lat);
      sand =
      [
        .. Clip(sand, double.NegativeInfinity, parting),
        .. Clip(SandOf(cope), parting, double.PositiveInfinity),
      ];
      runner = RoleOf(cope, "runner");
      cores = [.. cores, .. RoleOf(cope, "core")];
    }
    var placedCast = new List<List<Box>>();
    var castFrames = new Frames(cast);
    List<Node> castLeaves = [.. new Tree(cast).Leaves];
    double turned = RoleLeaves(leaves, "core").Sum(l => l.Turned);
    if (pour.Closed)
      turned += RoleLeaves(Leaves(input.Pair!, input, settings, lat), "core")
        .Sum(l => l.Turned);
    foreach (Mat4d at in pour.Pieces) {
      turned += castLeaves.Sum(l =>
        Lattice.TurnedArea(at * castFrames.Mat(l), l.Size)
      );
      placedCast.Add([
        .. castLeaves.SelectMany(l =>
          lat.Solid(at * castFrames.Mat(l), l.Size)
        ),
      ]);
    }
    var feeds = new List<(Feed, List<Box>)>();
    Tree? pattern = null;
    Frames? patternFrames = null;
    foreach (Feed f in pour.Feeds) {
      var boxes = new List<Box>(f.Boxes);
      if (f.Patterns.Count > 0) {
        Shape pat =
          input.Pattern
          ?? throw new CheckUsageException(
            $"{gate}: feed '{f.Name}' names pattern elements, no pattern given"
          );
        pattern ??= new Tree(pat);
        patternFrames ??= new Frames(pat);
        int into =
          f.Into
          ?? throw new CheckUsageException(
            $"{gate}: feed '{f.Name}' names pattern elements and enters no piece"
          );
        if (into < 1 || into > pour.Pieces.Count)
          throw new CheckUsageException(
            $"{gate}: feed '{f.Name}' enters piece {into}, the pour has {pour.Pieces.Count}"
          );
        Mat4d at = Mat4d.Translate(pour.Pieces[into - 1].Mul(Vec3.Zero));
        foreach (Node n in pattern.Leaves)
          if (
            f.Patterns.Any(g =>
              Sweep.GlobMatches(Tree.Bare(n.Path), g)
              || Sweep.GlobMatches(n.Name, g)
            )
          ) {
            boxes.AddRange(lat.Solid(at * patternFrames.Mat(n), n.Size));
            turned += Lattice.TurnedArea(at * patternFrames.Mat(n), n.Size);
          }
      }
      feeds.Add((f, boxes));
    }
    var cuts = new List<Box>();
    foreach (InletCut c in station.Inlet)
      cuts.Add(Of(c.From, c.To));
    Box seed;
    if (station.Inlet.Count > 0)
      seed = cuts[0];
    else
      seed = Of(
        station.Drop
          ?? throw new CheckUsageException(
            $"{gate}: station '{pour.Station}' has no inlet and no drop"
          )
      );
    if (station.Drop != null)
      cuts.Add(Of(station.Drop));
    return new Pouring {
      Pour = pour,
      Station = station,
      Plan = Of(station.SandFrom, station.SandTo),
      Sand = sand,
      Runner = runner,
      Cores = cores,
      Cast = placedCast,
      Feeds = feeds,
      Cuts = cuts,
      Seed = seed,
      Turned = turned,
    };
  }

  /// <summary>The station called <paramref name="name"/>.</summary>
  /// <exception cref="CheckUsageException">The settings have no such station.</exception>
  public static Station StationOf(Settings settings, string name) =>
    settings.Stations.TryGetValue(name, out Station? s)
      ? s
      : throw new CheckUsageException($"no station '{name}' in the settings");

  public static Box Of(double[] from, double[] to) =>
    new(new Vec3(from[0], from[1], from[2]), new Vec3(to[0], to[1], to[2]));

  public static Box Of(double[] box) =>
    Of([box[0], box[1], box[2]], [box[3], box[4], box[5]]);

  private static List<Box> SandOf(List<FillingLeaf> leaves) =>
    [
      .. leaves
        .Where(l => l.Role == "sand" && !l.Level)
        .SelectMany(l => l.Cells),
    ];

  private static IEnumerable<FillingLeaf> RoleLeaves(
    List<FillingLeaf> leaves,
    string role
  ) => leaves.Where(l => l.Role == role);

  private static List<Box> RoleOf(List<FillingLeaf> leaves, string role) =>
    [.. leaves.Where(l => l.Role == role).SelectMany(l => l.Cells)];

  private static IEnumerable<Box> Clip(
    IEnumerable<Box> boxes,
    double lo,
    double hi
  ) =>
    boxes
      .Select(b => new Box(
        new Vec3(b.Lo.X, Math.Max(b.Lo.Y, lo), b.Lo.Z),
        new Vec3(b.Hi.X, Math.Min(b.Hi.Y, hi), b.Hi.Z)
      ))
      .Where(b => b.Hi.Y > b.Lo.Y);
}
