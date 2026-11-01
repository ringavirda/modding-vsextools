using System;
using System.Collections.Generic;
using System.Linq;

namespace ExpandedLib.Shapes.Core;

/// <summary>A part record and its shape no longer name the same elements.</summary>
public sealed class ReconcileException(
  string message,
  IReadOnlyList<string> dangling,
  IReadOnlyList<string> added
) : Exception(message) {
  /// <summary>Record entries whose element the shape no longer has.</summary>
  public IReadOnlyList<string> Dangling { get; } = dangling;

  /// <summary>Shape elements the record has no entry for.</summary>
  public IReadOnlyList<string> New { get; } = added;
}

/// <summary>Checks a part record against the shape it describes.</summary>
public static class Reconcile {
  /// <summary>Compares the record's element names with the shape's, at any depth, in ordinal order.
  /// Unnamed elements are ignored. The shape's sha256 does not short-cut the comparison: a rename
  /// that keeps the element count is still caught.</summary>
  /// <param name="record">The record to check.</param>
  /// <param name="shape">The shape it describes.</param>
  /// <param name="shapeSha">Lowercase hex sha256 of the shape file bytes.</param>
  /// <returns>True when <paramref name="shapeSha"/> equals the record's, so the record needs no refresh.</returns>
  /// <exception cref="ReconcileException">The names differ; its message lists them.</exception>
  public static bool Check(PartRecord record, Shape shape, string shapeSha) {
    var names = new HashSet<string>(StringComparer.Ordinal);
    Collect(shape.Elements, names);
    string[] dangling =
    [
      .. record
        .Elements.Keys.Where(k => !names.Contains(k))
        .Order(StringComparer.Ordinal),
    ];
    string[] added =
    [
      .. names
        .Where(n => !record.Elements.ContainsKey(n))
        .Order(StringComparer.Ordinal),
    ];
    if (dangling.Length > 0 || added.Length > 0)
      throw new ReconcileException(
        $"{record.Shape}: record and shape differ; dangling: [{string.Join(", ", dangling)}]; new: [{string.Join(", ", added)}]",
        dangling,
        added
      );
    return string.Equals(record.ShapeSha, shapeSha, StringComparison.Ordinal);
  }

  private static void Collect(List<Element> elements, HashSet<string> names) {
    foreach (Element e in elements) {
      if (e.Name != null)
        names.Add(e.Name);
      Collect(e.Children, names);
    }
  }
}
