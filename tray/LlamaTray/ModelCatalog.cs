using System.Text.RegularExpressions;

namespace LlamaTray;

/// <summary>Reads model ids out of models.ini so the "Load model" menu has entries before the server is running.</summary>
internal static class ModelCatalog
{
    private static readonly Regex SectionHeader = new(@"^\s*\[(?<name>[^\]]+)\]\s*$", RegexOptions.Compiled);

    public static List<string> ReadModelIds()
    {
        var ids = new List<string>();
        if (!File.Exists(ServerConfig.Current.PresetIni)) return ids;

        foreach (var rawLine in File.ReadAllLines(ServerConfig.Current.PresetIni))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;

            var match = SectionHeader.Match(line);
            if (!match.Success) continue;

            var name = match.Groups["name"].Value.Trim();
            if (name == "*") continue;

            ids.Add(name);
        }

        return ids;
    }
}
