using System.Text.RegularExpressions;

namespace LlamaTray;

/// <summary>
/// Lightweight INI reader that parses models.ini into per-section key-value pairs.
/// Returns a dictionary keyed by section name (e.g. "Qwen3.6-27B-NVFP4") plus "*" for
/// the global defaults section. Values are always strings; callers parse as needed.
/// </summary>
internal static class IniParser
{
    private static readonly Regex SectionHeader = new(
        @"^\s*\[(?<name>[^\]]+)\]\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline
    );
    private static readonly Regex KeyValue = new(
        @"^\s*(?<key>[a-z0-9_-]+)\s*=\s*(?<value>.*)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    /// <summary>
    /// Parse the models.ini preset file. Returns a dictionary of section name → key-value pairs.
    /// The "*" section holds global defaults; named sections hold per-model overrides.
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> Parse(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        if (!File.Exists(path))
            return result;

        var lines = File.ReadAllLines(path);
        string? currentSection = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
            {
                continue;
            }

            var sectionMatch = SectionHeader.Match(line);
            if (sectionMatch.Success)
            {
                currentSection = sectionMatch.Groups["name"].Value.Trim();
                if (!result.ContainsKey(currentSection))
                    result[currentSection] = new Dictionary<string, string>();
                continue;
            }

            var kvMatch = KeyValue.Match(line);
            if (kvMatch.Success && currentSection != null)
            {
                var key = kvMatch.Groups["key"].Value.Trim();
                var value = kvMatch.Groups["value"].Value.Trim();
                result[currentSection][key] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// Resolve the effective value for a given model id and key, falling back to the global
    /// "*" defaults section when the model-specific section does not override it.
    /// Returns null if neither the section nor the defaults contain the key.
    /// </summary>
    public static string? Resolve(
        Dictionary<string, Dictionary<string, string>> presets,
        string modelId,
        string key
    )
    {
        if (
            presets.TryGetValue(modelId, out var section) && section.TryGetValue(key, out var value)
        )
            return value;
        if (
            presets.TryGetValue("*", out var @default)
            && @default.TryGetValue(key, out var defaultValue)
        )
            return defaultValue;
        return null;
    }
}
