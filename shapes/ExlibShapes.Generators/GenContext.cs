using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Generators;

/// <summary>A shape read at a pinned commit or file sha, with the part record beside it.</summary>
/// <param name="Shape">The shape.</param>
/// <param name="Record">Its part record; an empty record when the drawing has none.</param>
/// <param name="ShapeSha">Lowercase hex sha256 of the shape file bytes.</param>
public sealed record ResolvedSource(
  Shape Shape,
  PartRecord Record,
  string ShapeSha
);

/// <summary>What a generator run sees: settings, the stored answers, a resolver for pinned
/// sources and seeded randomness. Nothing in it touches the file system.</summary>
/// <param name="settings">The tool settings.</param>
/// <param name="resolve">Reads (shape path, pin) into a source; throws when it cannot.</param>
/// <param name="answers">The stored answers of the part being generated.</param>
/// <param name="log">A sink for progress lines; null drops them.</param>
public sealed class GenContext(
  Settings settings,
  Func<string, string, ResolvedSource> resolve,
  IReadOnlyList<Answer>? answers = null,
  Action<string>? log = null
) {
  /// <summary>The tool settings.</summary>
  public Settings Settings { get; } = settings;

  /// <summary>The stored answers of the part being generated.</summary>
  public IReadOnlyList<Answer> Answers { get; } = answers ?? [];

  /// <summary>The shape at <paramref name="shape"/> pinned at <paramref name="sha"/>.</summary>
  /// <param name="shape">The shape path as the spec writes it.</param>
  /// <param name="sha">The commit or file sha it is pinned at.</param>
  public ResolvedSource Resolve(string shape, string sha) =>
    resolve(shape, sha);

  /// <summary>Writes a progress line.</summary>
  public void Log(string line) => log?.Invoke(line);

  /// <summary>A random sequence fixed by its four parts: the same parts give the same sequence on
  /// every run and machine.</summary>
  public Random Rng(
    string generatorId,
    string output,
    string element,
    string face
  ) {
    byte[] h = SHA256.HashData(
      Encoding.UTF8.GetBytes(
        string.Join('\0', generatorId, output, element, face)
      )
    );
    return new Random(BitConverter.ToInt32(h, 0));
  }
}
