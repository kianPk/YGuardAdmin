using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace YGuardAdmin;

public class YGuardAdminConfig : BasePluginConfig
{
    [JsonPropertyName("ChatPrefix")]
    public string ChatPrefix { get; set; } = "YGuard";

    /// <summary>How often the admin and ban lists are re-read from the panel.</summary>
    [JsonPropertyName("PollSeconds")]
    public int PollSeconds { get; set; } = 30;

    /// <summary>
    /// Only used on servers that are not rented from the panel: players holding
    /// this CounterStrikeSharp flag (or @css/root) may use the commands.
    /// </summary>
    [JsonPropertyName("AdminFlag")]
    public string AdminFlag { get; set; } = "@css/ban";

    [JsonPropertyName("DefaultSlapDamage")]
    public int DefaultSlapDamage { get; set; } = 0;
}
