using System.Collections.Generic;
using ExpandedLib.Shapes.Core;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Generators;

/// <summary>An eye call a generator cannot make: the output it blocks is not written until the
/// question is answered.</summary>
/// <param name="Feature">The feature id (or element name) the question is about.</param>
/// <param name="Text">The question.</param>
/// <param name="Options">The answers it accepts.</param>
/// <param name="Suggested">The option to offer first; one of <paramref name="Options"/>.</param>
public sealed record Question(
  string Feature,
  string Text,
  IReadOnlyList<string> Options,
  string Suggested
) {
  /// <summary>The source shape path the question is about, as the spec writes it; empty when the resolver gave no path.</summary>
  public string Part { get; init; } = "";

  /// <summary>The fingerprint an answer to this question is stored against (<c>featureSha</c> of the part record).</summary>
  public string FeatureSha { get; init; } = "";
}

/// <summary>One shape a generator produced, with its part record.</summary>
/// <param name="Name">The output name (no extension).</param>
/// <param name="Kinds">The record's kinds.</param>
/// <param name="Shape">The generated shape.</param>
/// <param name="Record">The generated part record.</param>
/// <param name="InputHash">The provenance input hash of the output.</param>
public sealed record GenOutput(
  string Name,
  IReadOnlyList<string> Kinds,
  Shape Shape,
  PartRecord Record,
  string InputHash
);

/// <summary>A drawing a generator read, kept so the gates of its outputs can read it: the cast form a casting was
/// made from, a blank's form, a core's cored feature.</summary>
/// <param name="Name">The input's name; an output's record slot names it.</param>
/// <param name="Shape">The drawing.</param>
/// <param name="Record">The drawing's record, the tags and features the gates read.</param>
public sealed record GenInput(string Name, Shape Shape, PartRecord Record);

/// <summary>What a generator run returns. An output named by a question is absent from
/// <see cref="Outputs"/>.</summary>
/// <param name="Outputs">The outputs written.</param>
/// <param name="Questions">The eye calls left open.</param>
public sealed record GenResult(
  IReadOnlyList<GenOutput> Outputs,
  IReadOnlyList<Question> Questions
) {
  /// <summary>One <c>redraw: ...</c> row per machine drawing that differs from its blank; empty when none.</summary>
  public IReadOnlyList<string> Redraws { get; init; } = [];

  /// <summary>The drawings the outputs' gates read, one per name; empty when none.</summary>
  public IReadOnlyList<GenInput> Inputs { get; init; } = [];
}

/// <summary>A generator: pure over its context and spec, no file I/O.</summary>
public interface IGenerator {
  /// <summary>The generator's id, the first part of every <see cref="GenContext.Rng"/> seed.</summary>
  string Id { get; }

  /// <summary>The sha256 of the generator's sources and of Core's.</summary>
  string Version { get; }

  /// <summary>The JSON-schema document describing the spec.</summary>
  JObject SpecSchema { get; }

  /// <summary>Runs the generator on one spec entry.</summary>
  /// <param name="ctx">The context.</param>
  /// <param name="spec">The spec entry as written.</param>
  /// <exception cref="SpecException">The spec is malformed.</exception>
  /// <exception cref="CastGenException">The inputs cannot make the output.</exception>
  GenResult Run(GenContext ctx, JObject spec);
}
