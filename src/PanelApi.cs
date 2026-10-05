using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YGuardAdmin;

public sealed class PanelState
{
    [JsonPropertyName("hosted")]
    public bool Hosted { get; set; }

    [JsonPropertyName("admins")]
    public List<string> Admins { get; set; } = [];

    [JsonPropertyName("bans")]
    public List<PanelBan> Bans { get; set; } = [];

    [JsonPropertyName("chat_ads")]
    public ChatAdsState? ChatAds { get; set; }
}

public sealed class ChatAdsState
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("interval_seconds")]
    public int IntervalSeconds { get; set; } = 120;

    [JsonPropertyName("color")]
    public string Color { get; set; } = "gold";

    [JsonPropertyName("messages")]
    public List<string> Messages { get; set; } = [];
}

public sealed class PanelBan
{
    [JsonPropertyName("steam_id")]
    public string SteamId { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("expires_at")]
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// Talks to the panel's /hosted-servers/plugin endpoints. The pod proves who it
/// is with SERVER_ID + SERVER_API_PASSWORD, which 5Stack injects into every
/// game server it runs.
/// </summary>
internal sealed class PanelApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly string? _serverId = Environment.GetEnvironmentVariable("SERVER_ID");
    private readonly string? _password = Environment.GetEnvironmentVariable("SERVER_API_PASSWORD");
    private readonly string? _baseUrl;

    public PanelApi()
    {
        var api = Environment.GetEnvironmentVariable("API_DOMAIN");
        if (!string.IsNullOrWhiteSpace(api))
        {
            _baseUrl = api.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? api.TrimEnd('/')
                : $"https://{api.TrimEnd('/')}";
        }
    }

    public bool Configured =>
        !string.IsNullOrWhiteSpace(_serverId)
        && !string.IsNullOrWhiteSpace(_password)
        && _baseUrl != null;

    public async Task<PanelState?> GetStateAsync()
    {
        if (!Configured) return null;
        using var req = Request(
            HttpMethod.Get,
            $"/hosted-servers/plugin/state?server_id={Uri.EscapeDataString(_serverId!)}");
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"[YGuardAdmin] state HTTP {(int)resp.StatusCode}");
            return null;
        }
        return await resp.Content.ReadFromJsonAsync<PanelState>();
    }

    public Task<string?> BanAsync(
        ulong steamId, string name, string reason, int minutes, ulong adminSteamId, string adminName)
        => PostAsync("/hosted-servers/plugin/ban", new Dictionary<string, object?>
        {
            ["server_id"] = _serverId,
            ["steam_id"] = steamId.ToString(),
            ["name"] = name,
            ["reason"] = reason,
            ["minutes"] = minutes,
            ["admin_steam_id"] = adminSteamId == 0 ? "" : adminSteamId.ToString(),
            ["admin_name"] = adminName,
        });

    public Task<string?> UnbanAsync(ulong steamId, ulong adminSteamId)
        => PostAsync("/hosted-servers/plugin/unban", new Dictionary<string, object?>
        {
            ["server_id"] = _serverId,
            ["steam_id"] = steamId.ToString(),
            ["admin_steam_id"] = adminSteamId == 0 ? "" : adminSteamId.ToString(),
        });

    /// <summary>Push the local ban list so the site can show it on Server details.</summary>
    public async Task SyncBansAsync(
        IEnumerable<(ulong SteamId, string Name, string Reason, DateTime? ExpiresAt)> bans)
    {
        if (!Configured) return;
        var payload = bans.Select(b => new Dictionary<string, object?>
        {
            ["steam_id"] = b.SteamId.ToString(),
            ["name"] = b.Name,
            ["reason"] = b.Reason,
            ["expires_at"] = b.ExpiresAt?.ToUniversalTime().ToString("o"),
        }).ToList();
        await PostAsync("/hosted-servers/plugin/server-bans/sync", new Dictionary<string, object?>
        {
            ["server_id"] = _serverId,
            ["bans"] = payload,
        });
    }

    /// <returns>null on success, otherwise an error message for the admin.</returns>
    private async Task<string?> PostAsync(string path, Dictionary<string, object?> body)
    {
        if (!Configured) return "panel API is not configured on this server";
        try
        {
            using var req = Request(HttpMethod.Post, path);
            req.Content = JsonContent.Create(body);
            using var resp = await Http.SendAsync(req);
            if (resp.IsSuccessStatusCode) return null;
            var text = await resp.Content.ReadAsStringAsync();
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("message", out var message))
                    return message.ValueKind == JsonValueKind.String
                        ? message.GetString()
                        : message.ToString();
            }
            catch (JsonException)
            {
            }
            return $"HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, _baseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _password);
        return req;
    }
}
