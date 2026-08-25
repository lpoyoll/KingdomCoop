using System.Collections.Concurrent;
using System.Text.Json;

namespace KcdMp.Client;

public sealed record CompanionInventoryItem(Guid ItemClass, int Amount, float Health);

public sealed record CompanionIdentity(
    string Name,
    int FaceIndex,
    string Sex,
    string Background);

public sealed record CompanionProgression(
    Dictionary<string, float> Levels,
    DateTime CapturedUtc);

public sealed record CompanionInventorySnapshot(
    int SchemaVersion,
    DateTime SavedUtc,
    string PlayerName,
    string CampaignKey,
    float Money,
    float? Health,
    float? Stamina,
    CompanionInventoryItem[] Items,
    Guid[] Equipped,
    CompanionIdentity? Identity = null,
    CompanionProgression? Progression = null);

public sealed class CompanionProfileStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KCDMP", "CompanionProfiles");

    public string CampaignKey(ClientConfig config)
    {
        string key = string.IsNullOrWhiteSpace(config.CampaignId)
            ? $"{config.ServerHost}_{config.ServerPort}"
            : config.CampaignId;
        return Safe(key);
    }

    public string PlayerKey(ClientConfig config) =>
        Safe(string.IsNullOrWhiteSpace(config.CompanionId)
            ? (config.PlayerName ?? Environment.MachineName)
            : config.CompanionId);

    public string ProfilePath(ClientConfig config) =>
        Path.Combine(Root, CampaignKey(config), PlayerKey(config) + ".json");

    public string RecoveryPath(ClientConfig config) =>
        Path.Combine(Root, "_recovery", PlayerKey(config) + ".json");

    public CompanionInventorySnapshot? LoadProfile(ClientConfig config) => Read(ProfilePath(config));
    public CompanionInventorySnapshot? LoadRecovery(ClientConfig config) => Read(RecoveryPath(config));
    public void SaveProfile(ClientConfig config, CompanionInventorySnapshot value) => Write(ProfilePath(config), value);
    public void SaveRecovery(ClientConfig config, CompanionInventorySnapshot value) => Write(RecoveryPath(config), value);

    public void DeleteRecovery(ClientConfig config)
    {
        try { if (File.Exists(RecoveryPath(config))) File.Delete(RecoveryPath(config)); }
        catch { }
    }

    private static CompanionInventorySnapshot? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CompanionInventorySnapshot>(
                File.ReadAllText(path), Json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[companion-profile] read failed {path}: {ex.Message}");
            return null;
        }
    }

    private static void Write(string path, CompanionInventorySnapshot value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
        Console.WriteLine($"[companion-profile] saved {path}");
    }

    private static string Safe(string text)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) text = text.Replace(c, '_');
        return text.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
    }
}

public sealed class CompanionSnapshotCollector
{
    private sealed class Builder
    {
        public required string Tag { get; init; }
        public float Money { get; set; }
        public float? Health { get; set; }
        public float? Stamina { get; set; }
        public List<CompanionInventoryItem> Items { get; } = [];
        public Dictionary<string, float> Skills { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public TaskCompletionSource<RawSnapshot> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed record RawSnapshot(
        float Money,
        float? Health,
        float? Stamina,
        CompanionInventoryItem[] Items,
        Dictionary<string, float> Skills);

    private readonly ConcurrentDictionary<string, Builder> _builders = new();

    public Task<RawSnapshot> WaitAsync(string tag, CancellationToken ct)
    {
        var b = _builders.GetOrAdd(tag, t => new Builder { Tag = t });
        ct.Register(() => b.Tcs.TrySetCanceled(ct));
        return b.Tcs.Task;
    }

    public void Begin(string tag, float money, float? health, float? stamina)
    {
        var b = _builders.GetOrAdd(tag, t => new Builder { Tag = t });
        b.Money = money; b.Health = health; b.Stamina = stamina;
        b.Items.Clear(); b.Skills.Clear();
    }

    public void Add(string tag, Guid cls, int amount, float health)
    {
        if (_builders.TryGetValue(tag, out var b) && amount > 0)
            b.Items.Add(new CompanionInventoryItem(cls, amount, health));
    }

    public void Skill(string tag, string name, float value)
    {
        if (_builders.TryGetValue(tag, out var b))
            b.Skills[name] = value;
    }

    public void End(string tag)
    {
        if (!_builders.TryRemove(tag, out var b)) return;
        b.Tcs.TrySetResult(new RawSnapshot(
            b.Money, b.Health, b.Stamina, [.. b.Items],
            new Dictionary<string, float>(b.Skills, StringComparer.OrdinalIgnoreCase)));
    }
}
