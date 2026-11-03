using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ExpandedLib.Shapes.Checks;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Generators;

/// <summary>A core set in a filling.</summary>
/// <param name="Item">The core item's code.</param>
/// <param name="Leaves">The core item's leaves, root-level, in its own model space.</param>
/// <param name="At">The core's model space in the filling's.</param>
public sealed record CoreSeat(
  string Item,
  IReadOnlyList<Leaf> Leaves,
  Mat4d At
);

/// <summary>A riser asked for a closed pour.</summary>
/// <param name="Kind">The riser's key in the station's <c>risers</c>.</param>
/// <param name="At">A point on the cast's top, x, y, z, in the cast item's model space.</param>
/// <param name="Piece">The piece, from 1, the point is placed by.</param>
public sealed record RiserAsk(string Kind, double[] At, int Piece = 1);

/// <summary>What a filling is made from.</summary>
/// <param name="Code">The code the filling's names end in, the cast item's.</param>
/// <param name="Station">The station's name in the settings.</param>
/// <param name="Closed">Whether the pour is closed: a drag and a cope, a sprue and a parting.</param>
/// <param name="Cast">The cast item's code.</param>
/// <param name="Pattern">The pattern item's code.</param>
/// <param name="CastLeaves">The cast item's leaves, root-level, in its model space.</param>
/// <param name="Pieces">Each piece's placement, the cast item's model space in the filling's, in pressing
/// order.</param>
/// <param name="PatternShape">The pattern item's shape; its <c>Head</c> element and its splits <c>Head_s2</c> and on are the head of a station that
/// has a head height.</param>
/// <param name="Cores">The cores set in the casting, in the record's order.</param>
/// <param name="Fill">The fill order, <c>chain</c>, <c>pairs</c> or <c>rows</c>.</param>
/// <param name="Order">The press order the pieces' plan runs in, <c>one</c> for a single piece.</param>
/// <param name="Risers">The risers of a closed pour; none when null.</param>
/// <param name="Step">The grid step on the plane of a turned leaf, in voxels; <see cref="Sands.Step"/> when null.</param>
public sealed record PourRequest(
  string Code,
  string Station,
  bool Closed,
  string Cast,
  string Pattern,
  IReadOnlyList<Leaf> CastLeaves,
  IReadOnlyList<Mat4d> Pieces,
  Shape? PatternShape,
  IReadOnlyList<CoreSeat> Cores,
  string Fill,
  string Order,
  IReadOnlyList<RiserAsk>? Risers = null,
  double? Step = null
);

/// <summary>A filling and, for a closed pour, its cope.</summary>
/// <param name="Filling">The filling that carries the pour: an open pour's, a closed pour's drag.</param>
/// <param name="Cope">A closed pour's cope; null for an open pour.</param>
/// <param name="Feeds">The feeds the record declares.</param>
public sealed record FillingResult(
  GenOutput Filling,
  GenOutput? Cope,
  IReadOnlyList<FeedLaid> Feeds
);

/// <summary>Step 11: the filling of a flask, the sand of one pour.</summary>
public static class Fillings {
  private const double Eps = 1e-9;

  /// <summary>The finest grid a turned leaf is read on, in voxels.</summary>
  public const double Fine = 1.0 / 32;

  /// <summary>Makes the filling of a pour. The shape holds the station's runner box elements by their names (a closed
  /// pour's cope holds them, its drag none), sand <c>Sand&lt;n&gt;</c> of the sand role, set cores as groups
  /// <c>Core&lt;i&gt;</c>, and past one piece the groups <c>Piece&lt;k&gt;</c>, each holding <c>Bedded</c> (its zone
  /// with the cast and every feed cut) and <c>Level</c> (the zone flat to the sand's top, cut only by feeds that
  /// enter no piece or an earlier one). The sand is the station's volume less the metal (the placed cast, the feeds,
  /// the station's inlet and drop), the runner box and the cores. The record's <c>pour</c> slot carries the station,
  /// cast, pattern, each piece's placement, the cores, the feeds, <c>fill</c>, <c>order</c> and the units.</summary>
  /// <param name="ctx">The context; its settings give the station and the roles' textures.</param>
  /// <param name="request">What the pour is made from.</param>
  /// <param name="spec">The spec text the provenance hash is taken over.</param>
  /// <exception cref="CastGenException">The station is unknown or lacks a part of the rule, a head is missing from the
  /// pattern, a piece or a feed lies in the runner box, two pieces' zones overlap, or a feed cannot be laid.</exception>
  public static FillingResult Make(
    GenContext ctx,
    PourRequest request,
    string spec
  ) {
    PourRequest r = request;
    string flask = r.Station + "flask";
    string name = $"{flask}-filling-{r.Code}",
      copeName = $"{flask}-cope-filling-{r.Code}";
    if (!ctx.Settings.Stations.TryGetValue(r.Station, out Station? st))
      throw new CastGenException($"{name}: no station \"{r.Station}\"");
    if (r.Pieces.Count == 0)
      throw new CastGenException($"{name}: the pour has no piece");

    var cast = r.Pieces.Select(at => Occupancy.Of(r.CastLeaves, at)).ToList();
    var heads = new List<Occupancy>();
    if (st.HeadHeight != null) {
      List<Leaf> head =
      [
        .. (r.PatternShape?.Elements ?? [])
          .Where(IsHead)
          .Select(e => new Leaf(e.Name!, e.Name!, e.Name!, e)),
      ];
      if (head.Count == 0)
        throw new CastGenException(
          $"{name}: the station has a head height and the pattern has no Head"
        );
      foreach (Mat4d at in r.Pieces)
        heads.Add(Occupancy.Of(head, at));
    }
    List<IReadOnlyList<Box>> metal =
    [
      .. cast.Select(
        (c, k) =>
          (IReadOnlyList<Box>)
            [.. c.Parts, .. heads.ElementAtOrDefault(k)?.Parts ?? []]
      ),
    ];
    var risers = new List<(string, Vec3)>();
    foreach (RiserAsk ask in r.Risers ?? [])
      risers.Add(
        (
          ask.Kind,
          r.Pieces[ask.Piece - 1].Mul(new Vec3(ask.At[0], ask.At[1], ask.At[2]))
        )
      );
    List<FeedLaid> feeds = Feeds.Lay(name, st, r.Closed, r.Fill, metal, risers);

    var all = new Occupancy();
    foreach (Occupancy c in cast)
      all.Add(c);
    foreach (Occupancy h in heads)
      all.Add(h);
    foreach (FeedLaid f in feeds)
      foreach (Box b in f.Boxes)
        all.Add(b);
    foreach (InletCut c in st.Inlet)
      all.Add(Of(c.From, c.To));
    if (st.Drop != null)
      all.Add(
        Of(
          [st.Drop[0], st.Drop[1], st.Drop[2]],
          [st.Drop[3], st.Drop[4], st.Drop[5]]
        )
      );
    var solid = new Occupancy();
    solid.Add(all);
    foreach (StationBox b in st.RunnerBox)
      solid.Add(Of(b.From, b.To));
    var coreLeaves = new List<List<Leaf>>();
    foreach (CoreSeat c in r.Cores) {
      List<Leaf> placed =
      [
        .. c.Leaves.Select(l =>
          l with
          {
            Element = Select.Flatten(
              l.Element,
              c.At * Engine.Local(l.Element),
              l.SourcePath
            ),
          }
        ),
      ];
      coreLeaves.Add(placed);
      solid.Add(Occupancy.Of(c.Leaves, c.At));
    }

    string sandKey = "#" + ctx.Settings.RoleKey("sand"),
      runnerKey = "#" + ctx.Settings.RoleKey("runner");
    int sandN = 0;
    Leaf Sand(Box b) =>
      Styled($"Sand{++sandN}", b.Lo.ToArray(), b.Hi.ToArray(), sandKey, "sand");
    Box station = Of(st.SandFrom, st.SandTo);
    var dragRoot = new List<(Leaf, string?)>();
    var copeRoot = new List<(Leaf, string?)>();
    List<(Leaf, string?)> runnerRoot = r.Closed ? copeRoot : dragRoot;
    foreach (StationBox b in st.RunnerBox)
      runnerRoot.Add((Styled(b.Name, b.From, b.To, runnerKey, "runner"), null));

    void LayPieces(Box region) {
      double grow = Math.Max(st.Margin / 2, st.Footprint ?? 0);
      var zones = new List<Box>();
      for (int k = 0; k < metal.Count; k++) {
        Rect plan = Rect.Of(metal[k]);
        zones.Add(
          new Box(
            new Vec3(
              Math.Max(plan.X0 - grow, region.Lo.X),
              region.Lo.Y,
              Math.Max(plan.Z0 - grow, region.Lo.Z)
            ),
            new Vec3(
              Math.Min(plan.X1 + grow, region.Hi.X),
              region.Hi.Y,
              Math.Min(plan.Z1 + grow, region.Hi.Z)
            )
          )
        );
      }
      for (int a = 0; a < zones.Count; a++)
        for (int c = a + 1; c < zones.Count; c++)
          if (
            new Rect(
              zones[a].Lo.X,
              zones[a].Lo.Z,
              zones[a].Hi.X,
              zones[a].Hi.Z
            ).Overlaps(
              new Rect(zones[c].Lo.X, zones[c].Lo.Z, zones[c].Hi.X, zones[c].Hi.Z)
            )
          )
            throw new CastGenException(
              $"{name}: the zones of Piece{a + 1} and Piece{c + 1} overlap on the plan, closer than the margin {Fmt.G(st.Margin)}"
            );
      var outside = new Occupancy();
      outside.Add(solid);
      foreach (Box z in zones)
        outside.Add(z);
      foreach (Box b in Sands.Fill(region, outside, r.Step))
        dragRoot.Add((Sand(b), null));
      for (int k = 0; k < zones.Count; k++) {
        var level = new Occupancy();
        foreach (InletCut c in st.Inlet)
          level.Add(Of(c.From, c.To));
        if (st.Drop != null)
          level.Add(
            Of(
              [st.Drop[0], st.Drop[1], st.Drop[2]],
              [st.Drop[3], st.Drop[4], st.Drop[5]]
            )
          );
        foreach (
          FeedLaid f in feeds.Where(f => f.Into == null || f.Into < k + 1)
        )
          foreach (Box b in f.Boxes)
            level.Add(b);
        for (int h = 0; h < k && h < heads.Count; h++)
          level.Add(heads[h]);
        foreach (Box b in Sands.Fill(zones[k], solid, r.Step))
          dragRoot.Add((Sand(b), $"Piece{k + 1}/Bedded"));
        foreach (Box b in Sands.Fill(zones[k], level, r.Step))
          dragRoot.Add((Sand(b), $"Piece{k + 1}/Level"));
      }
    }

    if (r.Closed) {
      double parting = st.Parting!.Value;
      Box drag = new(station.Lo, new Vec3(station.Hi.X, parting, station.Hi.Z)),
        cope = new(new Vec3(station.Lo.X, parting, station.Lo.Z), station.Hi);
      if (r.Pieces.Count == 1)
        foreach (Box b in Sands.Fill(drag, solid, r.Step))
          dragRoot.Add((Sand(b), null));
      else
        LayPieces(drag);
      foreach (Box b in Sands.Fill(cope, solid, r.Step))
        copeRoot.Add((Sand(b), null));
    } else if (r.Pieces.Count == 1) {
      foreach (Box b in Sands.Fill(station, solid, r.Step))
        dragRoot.Add((Sand(b), null));
    } else {
      LayPieces(station);
    }

    foreach (StationBox rb in st.RunnerBox) {
      Box runnerBox = Of(rb.From, rb.To);
      for (int k = 0; k < metal.Count; k++)
        if (metal[k].Any(b => Overlap(b, runnerBox)))
          throw new CastGenException(
            $"{name}: Piece{k + 1} lies in the runner box {rb.Name}"
          );
      foreach (FeedLaid f in feeds)
        if (f.Boxes.Any(b => Overlap(b, runnerBox)))
          throw new CastGenException(
            $"{name}: {f.Name} lies in the runner box {rb.Name}"
          );
    }

    var coreRoot = new List<(Leaf, string?)>();
    for (int i = 0; i < coreLeaves.Count; i++)
      foreach (Leaf l in coreLeaves[i])
        coreRoot.Add((l with { Role = "core" }, $"Core{i + 1}"));

    double units = st.LadleUnits;
    var shared = new Occupancy();
    foreach (Occupancy h in heads)
      shared.Add(h);
    foreach (FeedLaid f in feeds.Where(f => f.Kind is "sprue" or "riser"))
      foreach (Box b in f.Boxes)
        shared.Add(b);
    long perPiece =
        (long)
          Math.Round(
            units * Sands.Volume(cast[0], Fine),
            MidpointRounding.AwayFromZero
          ),
      share =
        (long)
          Math.Round(
            units * Sands.Volume(shared, Fine),
            MidpointRounding.AwayFromZero
          );

    string hash = HashOf(ctx, r, st, spec);
    var pour = new JObject {
      ["station"] = r.Station,
      ["mould"] = r.Closed ? "closed" : "open",
      ["cast"] = r.Cast,
      ["pattern"] = r.Pattern,
      ["pieces"] = new JArray(
        r.Pieces.Select(at => new JObject { ["at"] = Rows(at) })
      ),
      ["cores"] = new JArray(
        r.Cores.Select(c => new JObject {
          ["item"] = c.Item,
          ["at"] = Rows(c.At),
        })
      ),
      ["feeds"] = new JArray(feeds.Select(FeedJson)),
      ["fill"] = r.Fill,
      ["order"] = r.Order,
      ["units"] = new JObject { ["perPiece"] = perPiece, ["share"] = share },
    };
    if (r.Closed)
      pour["pair"] = copeName;

    GenOutput filling = Output(
      ctx,
      name,
      [.. dragRoot, .. coreRoot],
      pour,
      hash
    );
    GenOutput? copeOut = null;
    if (r.Closed)
      copeOut = Output(
        ctx,
        copeName,
        copeRoot,
        new JObject { ["half"] = "cope", ["pair"] = name },
        hash
      );
    return new FillingResult(filling, copeOut, feeds);
  }

  private static Box Of(double[] from, double[] to) =>
    new(new Vec3(from[0], from[1], from[2]), new Vec3(to[0], to[1], to[2]));

  private static Leaf Styled(
    string name,
    double[] from,
    double[] to,
    string key,
    string role
  ) {
    var e = new Element {
      Name = name,
      From = [.. from],
      To = [.. to],
    };
    foreach (string face in FaceAxes.Names)
      e.Faces.Add(new(face, new Face { Texture = key }));
    return new Leaf(name, name, name, e, "cast", role);
  }

  private static JArray Rows(Mat4d m) =>
    new(
      Enumerable.Range(0, 16).Select(i => (JToken)new JValue(m[i / 4, i % 4]))
    );

  private static JObject FeedJson(FeedLaid f) {
    var o = new JObject { ["name"] = f.Name, ["kind"] = f.Kind };
    if (f.Pattern.Count > 0)
      o["pattern"] = new JArray(f.Pattern);
    else
      o["boxes"] = new JArray(
        f.Boxes.Select(b => new JArray(
          b.Lo.X,
          b.Lo.Y,
          b.Lo.Z,
          b.Hi.X,
          b.Hi.Y,
          b.Hi.Z
        ))
      );
    if (f.Into is int into)
      o["into"] = into;
    return o;
  }

  private static GenOutput Output(
    GenContext ctx,
    string item,
    List<(Leaf Leaf, string? Group)> placed,
    JObject pour,
    string hash
  ) {
    List<Leaf> loose =
    [
      .. placed
        .Where(p => !p.Group?.StartsWith("Core") ?? true)
        .Select(p => p.Leaf),
    ];
    List<Leaf> assigned = Uvs.Assign(ctx, item, loose);
    var byName = assigned.ToDictionary(l => l.Name);

    var rec = new PartRecord { Shape = item + ".json", Owner = "generated" };
    rec.Kinds.Add("filling");
    var shape = new Shape();
    var groups = new Dictionary<string, Element>();
    Element Group(string path) {
      if (groups.TryGetValue(path, out Element? g))
        return g;
      string[] parts = path.Split('/');
      g = new Element {
        Name = parts[^1],
        From = [0, 0, 0],
        To = [0, 0, 0],
      };
      if (parts.Length == 1)
        shape.Elements.Add(g);
      else
        Group(string.Join('/', parts[..^1])).Children.Add(g);
      groups[path] = g;
      return g;
    }
    foreach ((Leaf leaf, string? group) in placed) {
      Leaf use =
        group != null && group.StartsWith("Core") ? leaf : byName[leaf.Name];
      Element e = Cores.Named(use);
      if (group == null)
        shape.Elements.Add(e);
      else
        Group(group).Children.Add(e);
      if (group == null || !group.StartsWith("Core"))
        rec.Elements[use.Name] = new ElementPart {
          Stage = "cast",
          Role = use.Role,
        };
      else
        rec.Elements[group.Split('/')[0]] = new ElementPart {
          Stage = "cast",
          Role = "core",
        };
    }
    Cores.Textures(
      ctx,
      shape,
      placed.Select(p =>
        p.Group?.StartsWith("Core") == true ? p.Leaf : byName[p.Leaf.Name]
      )
    );
    rec.Carried["pour"] = pour;
    return new GenOutput(item, rec.Kinds, shape, rec, hash);
  }

  private static bool Overlap(Box a, Box b) =>
    a.Hi.X > b.Lo.X + 1e-9
    && b.Hi.X > a.Lo.X + 1e-9
    && a.Hi.Y > b.Lo.Y + 1e-9
    && b.Hi.Y > a.Lo.Y + 1e-9
    && a.Hi.Z > b.Lo.Z + 1e-9
    && b.Hi.Z > a.Lo.Z + 1e-9;

  private static bool IsHead(Element e) =>
    e.Name is "Head" || (e.Name?.StartsWith("Head_s") ?? false);

  private static string HashOf(
    GenContext ctx,
    PourRequest r,
    Station st,
    string spec
  ) {
    var text = new System.Text.StringBuilder(
      CanonicalWriter.Write(Flush.ShapeOf(r.CastLeaves))
    );
    foreach (Mat4d at in r.Pieces)
      text.Append('\n').Append(Invariant(Rows(at)));
    foreach (CoreSeat c in r.Cores) {
      text.Append('\n').Append(c.Item).Append(Invariant(Rows(c.At)));
      text.Append(CanonicalWriter.Write(Flush.ShapeOf(c.Leaves)));
    }
    foreach (Element head in (r.PatternShape?.Elements ?? []).Where(IsHead))
      text.Append(
        CanonicalWriter.Write(
          new Shape { Elements = { Select.Clone(head, false) } }
        )
      );
    text.Append(
      $"\n{r.Station} {r.Closed} {r.Fill} {r.Order} {r.Cast} {r.Pattern} {r.Code}"
    );
    foreach (RiserAsk a in r.Risers ?? [])
      text.Append(
        $"\n{a.Kind} {string.Join(",", a.At.Select(Invariant))} {a.Piece}"
      );
    text.Append('\n').Append(JsonConvert.SerializeObject(st));
    return Provenance.InputHash(
      spec,
      "",
      Provenance.Sha256Of(text.ToString()),
      Provenance.Sha256Of(
        ctx.Settings.RoleKey("sand") + ctx.Settings.RoleKey("runner")
      )
    );
  }

  private static string Invariant(double v) =>
    v.ToString(CultureInfo.InvariantCulture);

  private static string Invariant(JArray a) =>
    string.Join(",", a.Select(t => Invariant((double)t)));
}
