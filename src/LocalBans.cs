using System.Text.Json;

namespace YGuardAdmin;

public sealed class BanEntry
{
    public string Reason { get; set; } = "";
    public DateTime? ExpiresAt { get; set; }

    public bool Active => ExpiresAt == null || ExpiresAt > DateTime.UtcNow;
}

/// <summary>
/// Ban list for servers that are not rented from the panel, where there is no
/// panel-side list to keep them in.
/// </summary>
internal sealed class LocalBans(string path)
{
    private readonly object _lock = new();

    public Dictionary<ulong, BanEntry> Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(path)) return [];
                var loaded = JsonSerializer.Deserialize<Dictionary<ulong, BanEntry>>(File.ReadAllText(path));
                return loaded?.Where(kv => kv.Value.Active).ToDictionary() ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    public void Save(Dictionary<ulong, BanEntry> bans)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(
                bans.Where(kv => kv.Value.Active).ToDictionary(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
