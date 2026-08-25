namespace KcdMp.Client;

/// <summary>
/// File-system save shield for a joining companion.
///
/// KCD2 still requires a local player/save as a bootstrap shell. While co-op is
/// active the live actor contains companion inventory/progression, so an
/// autosave could otherwise persist that temporary state. The shield snapshots
/// the user's KCD2 save directory before profile mount and restores it after a
/// clean session or on the next launch after a crash.
///
/// This intentionally favours data safety over disk usage. It never deletes the
/// backup until a restore completed.
/// </summary>
public sealed class CompanionSaveShield
{
    public sealed record Shield(string Source, string Backup, DateTime CreatedUtc);

    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KCDMP", "SaveShield");

    public Shield? Begin(string companionId)
    {
        string? source = FindSaveRoot();
        if (source is null) return null;

        string backup = Path.Combine(_root, Safe(companionId),
            DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(backup);
        CopyTree(source, backup);
        File.WriteAllText(Path.Combine(backup, ".source.txt"), source);
        return new(source, backup, DateTime.UtcNow);
    }

    public Shield? FindLatestPending(string companionId)
    {
        string dir = Path.Combine(_root, Safe(companionId));
        if (!Directory.Exists(dir)) return null;

        foreach (var candidate in Directory.GetDirectories(dir)
                     .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string marker = Path.Combine(candidate, ".source.txt");
            if (!File.Exists(marker)) continue;
            string source = File.ReadAllText(marker).Trim();
            if (source.Length > 0)
                return new(source, candidate, Directory.GetCreationTimeUtc(candidate));
        }
        return null;
    }

    public void Restore(Shield shield)
    {
        if (!Directory.Exists(shield.Backup)) return;
        Directory.CreateDirectory(shield.Source);

        // Replace files present in the snapshot, and remove later autosaves
        // created during the protected session.
        foreach (string file in Directory.GetFiles(shield.Source, "*",
                     SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(shield.Source, file);
            if (rel.StartsWith(".kcdmp-", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Delete(file);
        }

        CopyTree(shield.Backup, shield.Source, skipMarker: true);
        Directory.Delete(shield.Backup, recursive: true);
    }

    private static string? FindSaveRoot()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string saved = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        string[] candidates =
        [
            Path.Combine(home, "Saved Games", "kingdomcome2"),
            Path.Combine(home, "Saved Games", "kingdomcome2", "profiles"),
            Path.Combine(saved, "My Games", "KingdomCome2"),
        ];

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static void CopyTree(string source, string target, bool skipMarker = false)
    {
        foreach (string dir in Directory.GetDirectories(source, "*",
                     SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));

        foreach (string file in Directory.GetFiles(source, "*",
                     SearchOption.AllDirectories))
        {
            if (skipMarker && Path.GetFileName(file) == ".source.txt") continue;
            string rel = Path.GetRelativePath(source, file);
            string dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static string Safe(string text)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            text = text.Replace(c, '_');
        return text;
    }
}
