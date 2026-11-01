using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExpandedLib.Shapes.Core;

/// <summary>The tool's settings: texture registry, roles, flask stations, lint profile and step
/// rows. Generators read roles and stations, never texture keys or paths.</summary>
public sealed partial class Settings {
  private static readonly string[] FixedRoles =
  [
    "cast",
    "fastener",
    "pattern",
    "corebox",
    "core",
    "sand",
    "runner",
  ];

  private static readonly string[] LintProfiles =
  [
    "structural",
    "expanded-period",
  ];

  /// <summary>The texture registry by key.</summary>
  public required IReadOnlyDictionary<
    string,
    TextureEntry
  > Textures { get; init; }

  /// <summary>Texture key by role.</summary>
  public required IReadOnlyDictionary<string, string> Roles { get; init; }

  /// <summary>Flask stations by name.</summary>
  public required IReadOnlyDictionary<string, Station> Stations { get; init; }

  /// <summary>The lint profile, <c>structural</c> or <c>expanded-period</c>.</summary>
  public required string LintProfile { get; init; }

  /// <summary>Step rows by step code.</summary>
  public required IReadOnlyDictionary<string, StepRow> Steps { get; init; }

  /// <summary>Reads settings from the file at <paramref name="path"/>.</summary>
  /// <exception cref="FileNotFoundException"><paramref name="path"/> does not exist.</exception>
  /// <exception cref="SettingsException">A key, role, texture reference or value is unknown or malformed.</exception>
  public static Settings ReadFile(string path) {
    if (!File.Exists(path))
      throw new FileNotFoundException($"No such settings file: {path}", path);
    try {
      return Read(File.ReadAllText(path));
    } catch (SettingsException e) {
      throw new SettingsException($"{path}: {e.Message}");
    }
  }

  /// <summary>Reads settings from their JSON text (comments and trailing commas accepted).</summary>
  /// <exception cref="SettingsException">A key, role, texture reference or value is unknown or malformed.</exception>
  public static Settings Read(string text) {
    JToken root;
    try {
      using var reader = new JsonTextReader(new StringReader(text));
      root = JToken.ReadFrom(
        reader,
        new JsonLoadSettings { CommentHandling = CommentHandling.Ignore }
      );
    } catch (JsonException e) {
      throw new SettingsException($"not JSON: {e.Message}");
    }
    var top = new Strict(root, "settings");
    top.Allow("textures", "roles", "stations", "lint", "steps");
    var steps = top.Required("steps").Map(ReadStep);
    var textures = top.Required("textures").Map(ReadTexture);
    var roles = top.Required("roles").Map((key, v) => v.Text());
    foreach ((string role, string key) in roles) {
      if (!RoleKnown(role, steps))
        throw new SettingsException($"unknown role \"{role}\"");
      if (!textures.ContainsKey(key))
        throw new SettingsException(
          $"role \"{role}\" names unknown texture key \"{key}\""
        );
    }
    string lint = top.Required("lint").Text();
    if (!LintProfiles.Contains(lint))
      throw new SettingsException($"unknown lint profile \"{lint}\"");
    return new Settings {
      Textures = textures,
      Roles = roles,
      Stations = top.Required("stations").Map(ReadStation),
      LintProfile = lint,
      Steps = steps,
    };
  }

  /// <summary>The registry entry for <paramref name="key"/>.</summary>
  /// <exception cref="SettingsException">The key is not in the registry.</exception>
  public TextureEntry Texture(string key) =>
    Textures.TryGetValue(key, out TextureEntry? t)
      ? t
      : throw new SettingsException($"unknown texture key \"{key}\"");

  /// <summary>The texture key that fills <paramref name="role"/>.</summary>
  /// <exception cref="SettingsException">The role is unknown or no key is set for it.</exception>
  public string RoleKey(string role) =>
    Roles.TryGetValue(role, out string? key)
      ? key
      : throw new SettingsException(
        RoleKnown(role, Steps)
          ? $"role \"{role}\" has no texture key"
          : $"unknown role \"{role}\""
      );

  /// <summary>The path to write into an editable shape for <paramref name="key"/>, with
  /// variants filled in.</summary>
  /// <exception cref="SettingsException">The key is unknown or the path names an undeclared variant.</exception>
  public string EditablePath(string key) {
    TextureEntry t = Texture(key);
    return Fill(t, t.Editable, v => v.Editable);
  }

  /// <summary>The path to write into a runtime shape of mod domain <paramref name="domain"/>
  /// for <paramref name="key"/>; the domain's entry, else the <c>*</c> entry.</summary>
  /// <exception cref="SettingsException">The key is unknown, no runtime path serves the domain, or the path names an undeclared variant.</exception>
  public string RuntimePath(string key, string domain) {
    TextureEntry t = Texture(key);
    string path =
      t.Runtime.TryGetValue(domain, out string? p)
      || t.Runtime.TryGetValue("*", out p)
        ? p
        : throw new SettingsException(
          $"texture key \"{key}\" has no runtime path for domain \"{domain}\""
        );
    return Fill(t, path, v => v.Runtime);
  }

  /// <summary>Checks that <paramref name="machine"/> is in the row of <paramref name="step"/>.</summary>
  /// <exception cref="SettingsException">The step is unknown or the machine is outside its row.</exception>
  public void CheckMachine(string step, string machine) {
    if (!Steps.TryGetValue(step, out StepRow? row))
      throw new SettingsException($"unknown step \"{step}\"");
    if (!row.Machines.Contains(machine) && row.Hand != machine)
      throw new SettingsException(
        $"machine \"{machine}\" is outside the row of step \"{step}\""
      );
  }

  private static string Fill(
    TextureEntry t,
    string path,
    Func<TextureVariant, string> pick
  ) {
    foreach (Match m in Placeholder().Matches(path))
      if (!t.Variants.ContainsKey(m.Groups[1].Value))
        throw new SettingsException(
          $"texture key \"{t.Key}\" path names undeclared variant \"{m.Value}\""
        );
    foreach ((string name, TextureVariant v) in t.Variants)
      path = path.Replace("{" + name + "}", pick(v));
    return path;
  }

  [GeneratedRegex(@"\{(\w+)\}")]
  private static partial Regex Placeholder();

  private static bool RoleKnown(
    string role,
    IReadOnlyDictionary<string, StepRow> steps
  ) =>
    FixedRoles.Contains(role)
    || (
      role.StartsWith("finished.")
      && steps.ContainsKey(role["finished.".Length..])
    );

  private static StepRow ReadStep(string code, Strict s) {
    s.Allow("machines", "hand");
    return new StepRow {
      Machines = s.Required("machines").List(x => x.Text()),
      Hand = s.Optional("hand")?.Text(),
    };
  }

  private static TextureEntry ReadTexture(string key, Strict s) {
    s.Allow("editable", "runtime", "class", "variants");
    Strict rt = s.Required("runtime");
    IReadOnlyDictionary<string, string> runtime = rt.IsString
      ? new Dictionary<string, string> { ["*"] = rt.Text() }
      : rt.Map((_, v) => v.Text());
    string cls = s.Required("class").Text();
    if (
      !Enum.TryParse(cls, true, out TextureClass klass)
      || !Enum.IsDefined(klass)
    )
      throw new SettingsException(
        $"texture \"{key}\": unknown class \"{cls}\""
      );
    return new TextureEntry {
      Key = key,
      Editable = s.Required("editable").Text(),
      Runtime = runtime,
      Class = klass,
      Variants =
        s.Optional("variants")?.Map(ReadVariant)
        ?? new Dictionary<string, TextureVariant>(),
    };
  }

  private static TextureVariant ReadVariant(string name, Strict s) {
    s.Allow("editable", "runtime");
    return new TextureVariant {
      Editable = s.Required("editable").Text(),
      Runtime = s.Required("runtime").Text(),
    };
  }

  private static Station ReadStation(string name, Strict s) {
    s.Allow(
      "sandFrom",
      "sandTo",
      "floor",
      "parting",
      "margin",
      "footprint",
      "runnerX",
      "runnerDepth",
      "gateAcross",
      "risers",
      "headHeight",
      "drop",
      "runnerBox",
      "inlet",
      "sprue",
      "ladleUnits"
    );
    return new Station {
      SandFrom = s.Required("sandFrom").Numbers(3),
      SandTo = s.Required("sandTo").Numbers(3),
      Floor = s.Required("floor").Number(),
      Parting = s.Optional("parting")?.Number(),
      Margin = s.Required("margin").Number(),
      Footprint = s.Optional("footprint")?.Number(),
      RunnerX = s.Optional("runnerX")?.Numbers(2),
      RunnerDepth = s.Optional("runnerDepth")?.Number(),
      GateAcross = s.Optional("gateAcross")?.Number(),
      Risers =
        s.Optional("risers")?.Map((_, v) => v.Numbers(2))
        ?? new Dictionary<string, double[]>(),
      HeadHeight = s.Optional("headHeight")?.Number(),
      Drop = s.Optional("drop")?.Numbers(6),
      RunnerBox =
        s.Optional("runnerBox")
          ?.List(b => {
            b.Allow("name", "from", "to");
            return new StationBox {
              Name = b.Required("name").Text(),
              From = b.Required("from").Numbers(3),
              To = b.Required("to").Numbers(3),
            };
          })
        ?? [],
      Inlet =
        s.Optional("inlet")
          ?.List(b => {
            b.Allow("name", "kind", "from", "to");
            return new InletCut {
              Name = b.Required("name").Text(),
              Kind = b.Required("kind").Text(),
              From = b.Required("from").Numbers(3),
              To = b.Required("to").Numbers(3),
            };
          })
        ?? [],
      Sprue = s.Optional("sprue")?.Numbers(4),
      LadleUnits = s.Required("ladleUnits").Number(),
    };
  }
}
