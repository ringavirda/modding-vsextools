using System;

namespace ExpandedLib.Shapes.Core;

/// <summary>The settings file or a lookup against it is invalid. The message names the offending
/// key, role, step or machine.</summary>
public sealed class SettingsException(string message) : Exception(message);
