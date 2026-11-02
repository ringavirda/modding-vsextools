using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>
/// The columns <see cref="Draw"/> reads, held so that leaves can be left out one at a time without reading the shape
/// again: the plan is cut once at every edge of every leaf, and the broken area is the plan area of the cells whose
/// column does not run unbroken from the parting, summed over both halves, in vx2. A leaf left out changes only the
/// cells it stands in, so <see cref="Without"/> reads those cells alone, and a cell is read again only after a leaf
/// standing in it is removed.
/// </summary>
public sealed class DrawField {
  private readonly double _parting;
  private readonly double[] _xs,
    _zs;
  private readonly List<(Box Box, int Owner)>?[,] _columns;
  private readonly Dictionary<string, int> _owners = [];
  private readonly List<List<(int I, int K)>> _footprint = [];
  private readonly double[,] _area;
  private readonly int[,] _version;
  private readonly (int Version, double Area)[][] _memo;
  private readonly bool[] _removed;

  /// <summary>Reads <paramref name="shape"/> as <see cref="Draw"/> does.</summary>
  /// <param name="shape">The shape; each leaf is named by its element name, the names distinct.</param>
  /// <param name="parting">The y of the parting, in voxels.</param>
  public DrawField(Shape shape, double parting) {
    _parting = parting;
    var tree = new Tree(shape);
    var frames = new Frames(shape);
    var boxes = new List<(Box Box, int Owner)>();
    foreach (Node leaf in tree.Leaves) {
      int owner = _owners.Count;
      _owners.Add(leaf.Name, owner);
      _footprint.Add([]);
      foreach (Box b in Lattice.Fine.Solid(frames.Mat(leaf), leaf.Size))
        boxes.Add((b, owner));
    }
    _removed = new bool[_owners.Count];
    List<Box> all = [.. boxes.Select(b => b.Box)];
    _xs = Draw.Breaks(all, 0);
    _zs = Draw.Breaks(all, 2);
    _columns = new List<(Box, int)>?[
      Math.Max(_xs.Length - 1, 0),
      Math.Max(_zs.Length - 1, 0)
    ];
    var seen = new HashSet<(int, int)>[_owners.Count];
    for (int o = 0; o < seen.Length; o++)
      seen[o] = [];
    foreach ((Box b, int owner) in boxes) {
      (int i0, int i1) = Draw.Span(_xs, b.Lo.X, b.Hi.X);
      (int k0, int k1) = Draw.Span(_zs, b.Lo.Z, b.Hi.Z);
      for (int i = i0; i < i1; i++)
        for (int k = k0; k < k1; k++) {
          (_columns[i, k] ??= []).Add((b, owner));
          if (seen[owner].Add((i, k)))
            _footprint[owner].Add((i, k));
        }
    }
    _memo = [.. _footprint.Select(f => new (int, double)[f.Count])];
    foreach ((int Version, double Area)[] row in _memo)
      Array.Fill(row, (-1, 0.0));
    _version = new int[_columns.GetLength(0), _columns.GetLength(1)];
    _area = new double[_columns.GetLength(0), _columns.GetLength(1)];
    for (int i = 0; i < _area.GetLength(0); i++)
      for (int k = 0; k < _area.GetLength(1); k++) {
        _area[i, k] = Area(i, k, -1);
        Broken += _area[i, k];
      }
  }

  /// <summary>The broken area of the leaves not yet removed, in vx2; 0 when every column draws.</summary>
  public double Broken { get; private set; }

  /// <summary>Leaves a leaf out for good.</summary>
  /// <param name="name">The leaf's element name.</param>
  /// <exception cref="KeyNotFoundException">No leaf has that name.</exception>
  public void Remove(string name) {
    int owner = _owners[name];
    _removed[owner] = true;
    foreach ((int i, int k) in _footprint[owner]) {
      _version[i, k]++;
      double now = Area(i, k, -1);
      Broken += now - _area[i, k];
      _area[i, k] = now;
    }
  }

  /// <summary>The broken area that would remain were <paramref name="name"/> also left out, in vx2; nothing changes.</summary>
  /// <param name="name">The leaf's element name.</param>
  /// <exception cref="KeyNotFoundException">No leaf has that name.</exception>
  public double Without(string name) {
    int owner = _owners[name];
    double total = Broken;
    List<(int I, int K)> foot = _footprint[owner];
    (int Version, double Area)[] memo = _memo[owner];
    for (int n = 0; n < foot.Count; n++) {
      (int i, int k) = foot[n];
      if (memo[n].Version != _version[i, k])
        memo[n] = (_version[i, k], Area(i, k, owner));
      total += memo[n].Area - _area[i, k];
    }
    return total;
  }

  private double Area(int i, int k, int skip) {
    if (_columns[i, k] is not { } over)
      return 0;
    List<Box> live =
    [
      .. over.Where(c => !_removed[c.Owner] && c.Owner != skip)
        .Select(c => c.Box),
    ];
    if (live.Count == 0)
      return 0;
    double broken = 0;
    foreach (int dir in new[] { -1, 1 }) {
      (int total, int run) = Draw.Column(live, _parting, dir);
      if (total != 0 && total != run)
        broken += (_xs[i + 1] - _xs[i]) * (_zs[k + 1] - _zs[k]);
    }
    return broken;
  }
}
