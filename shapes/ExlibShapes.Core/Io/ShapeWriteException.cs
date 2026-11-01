using System;

namespace ExpandedLib.Shapes.Core;

/// <summary>A shape cannot be written; nothing was written.</summary>
public sealed class ShapeWriteException(string message) : Exception(message);
