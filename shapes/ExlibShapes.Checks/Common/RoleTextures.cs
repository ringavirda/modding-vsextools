using System.Linq;
using ExpandedLib.Shapes.Core;

namespace ExpandedLib.Shapes.Checks;

/// <summary>Whether a face's texture is the one a role takes.</summary>
internal static class RoleTextures {
  /// <summary>The registry key <paramref name="role"/> takes.</summary>
  /// <exception cref="CheckUsageException">The role has no key in the settings.</exception>
  public static string KeyOf(Settings settings, string role) {
    try {
      return settings.RoleKey(role);
    } catch (SettingsException e) {
      throw new CheckUsageException(e.Message);
    }
  }

  /// <summary>Whether the shape's texture <paramref name="name"/> is the registry key <paramref name="key"/>,
  /// by name or by the key's editable path.</summary>
  public static bool Is(Shape shape, Settings settings, string key, string name) {
    if (name == key)
      return true;
    string? path = shape
      .Textures.Where(t => t.Key == name)
      .Select(t => t.Value)
      .FirstOrDefault();
    return path != null
      && settings.Textures.TryGetValue(key, out TextureEntry? entry)
      && path == entry.Editable;
  }
}
