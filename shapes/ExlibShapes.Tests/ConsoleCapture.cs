using Xunit;

namespace ExpandedLib.Shapes.Tests;

/// <summary>The collection for tests that redirect <see cref="System.Console.Out"/>; xunit runs it after
/// every parallel collection, with nothing beside it.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCapture {
  /// <summary>The collection's name, for <c>[Collection]</c>.</summary>
  public const string Name = "Console capture";
}
