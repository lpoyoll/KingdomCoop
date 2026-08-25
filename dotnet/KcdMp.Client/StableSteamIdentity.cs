using System.Text.RegularExpressions;

namespace KcdMp.Client;

public static partial class StableSteamIdentity
{
    public static string? TryGetMostRecentSteam64()
    {
        try
        {
            foreach (string root in CandidateSteamRoots())
            {
                string file = Path.Combine(root, "config", "loginusers.vdf");
                if (!File.Exists(file)) continue;
                string text = File.ReadAllText(file);

                // Split user blocks and prefer the one carrying MostRecent "1".
                foreach (Match m in SteamUserBlockRegex().Matches(text))
                {
                    if (m.Groups["body"].Value.Contains("\"MostRecent\"\t\t\"1\"",
                            StringComparison.OrdinalIgnoreCase)
                        || m.Groups["body"].Value.Contains("\"MostRecent\" \"1\"",
                            StringComparison.OrdinalIgnoreCase))
                        return m.Groups["id"].Value;
                }

                var first = SteamIdRegex().Match(text);
                if (first.Success) return first.Groups[1].Value;
            }
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> CandidateSteamRoots()
    {
        string? pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(pf86))
            yield return Path.Combine(pf86, "Steam");

        string? pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(pf))
            yield return Path.Combine(pf, "Steam");

        // Common custom-library root. Harmless if absent.
        yield return @"D:\SteamLibrary";
    }

    [GeneratedRegex("\"(?<id>[0-9]{17})\"\\s*\\{(?<body>.*?)\\}", RegexOptions.Singleline)]
    private static partial Regex SteamUserBlockRegex();

    [GeneratedRegex("\"([0-9]{17})\"")]
    private static partial Regex SteamIdRegex();
}
