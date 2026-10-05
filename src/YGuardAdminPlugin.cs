using System.Globalization;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace YGuardAdmin;

/// <summary>
/// In-game admin commands for YGuard servers. On a server rented from the panel
/// the admin and ban lists come from the owner's panel; anywhere else admins
/// are CounterStrikeSharp flag holders and bans are kept in a local file.
/// Ranked pods load idle.
/// </summary>
public partial class YGuardAdminPlugin : BasePlugin, IPluginConfig<YGuardAdminConfig>
{
    public override string ModuleName => "YGuard Admin";
    public override string ModuleVersion => "1.0.3";
    public override string ModuleAuthor => "YGuard";
    public override string ModuleDescription =>
        "slay / slap / kick / ban / bany / respawn, panel admins, timed chat ads";

    public YGuardAdminConfig Config { get; set; } = new();

    private readonly PanelApi _api = new();
    private LocalBans? _localBans;
    private bool _enabled;
    private bool _hosted;
    private HashSet<ulong> _admins = [];
    private Dictionary<ulong, BanEntry> _bans = [];

    private readonly List<string> _chatAds = [];
    private int _chatAdInterval = 120;
    private int _chatAdIndex;
    private string _chatAdColor = "gold";
    private CounterStrikeSharp.API.Modules.Timers.Timer? _chatAdTimer;

    public void OnConfigParsed(YGuardAdminConfig config)
    {
        config.PollSeconds = Math.Clamp(config.PollSeconds, 5, 600);
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        var serverType = (Environment.GetEnvironmentVariable("SERVER_TYPE") ?? "").Trim();
        _enabled = !serverType.Equals("Ranked", StringComparison.OrdinalIgnoreCase);
        if (!_enabled)
        {
            Logger.LogInformation("YGuardAdmin idle (SERVER_TYPE={Type})", serverType);
            return;
        }

        _localBans = new LocalBans(Path.GetFullPath(
            Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", "YGuardAdmin", "bans.json")));
        _bans = _localBans.Load();

        RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
        RegisterListener<Listeners.OnMapStart>(_ => Refresh());
        AddTimer(Config.PollSeconds, Refresh, TimerFlags.REPEAT);
        Refresh();

        Logger.LogInformation("YGuardAdmin active (SERVER_TYPE={Type})", serverType);
    }

    // ---------------------------------------------------------------- state

    private void Refresh()
    {
        if (!_enabled || !_api.Configured) return;
        Task.Run(async () =>
        {
            PanelState? state;
            try
            {
                state = await _api.GetStateAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[YGuardAdmin] state error: {ex.Message}");
                return;
            }
            if (state == null) return;

            Server.NextFrame(() =>
            {
                var wasHosted = _hosted;
                _hosted = state.Hosted;
                if (!_hosted)
                {
                    if (wasHosted) _bans = _localBans?.Load() ?? [];
                    StopChatAds();
                    PushLocalBansToPanel();
                    return;
                }

                _admins = state.Admins
                    .Select(id => ulong.TryParse(id, out var v) ? v : 0)
                    .Where(v => v != 0)
                    .ToHashSet();
                _bans = state.Bans
                    .Where(b => ulong.TryParse(b.SteamId, out _))
                    .ToDictionary(
                        b => ulong.Parse(b.SteamId),
                        b => new BanEntry { Reason = b.Reason, ExpiresAt = b.ExpiresAt?.ToUniversalTime() });

                ApplyChatAds(state.ChatAds);

                foreach (var player in OnlinePlayers())
                {
                    if (IsBanned(player.SteamID, out var ban)) KickBanned(player, ban);
                }
            });
        });
    }

    private void ApplyChatAds(ChatAdsState? ads)
    {
        var enabled = ads is { Enabled: true } && ads.Messages.Count > 0;
        var messages = enabled
            ? ads!.Messages
                .Select(m => (m ?? "").Trim())
                .Where(m => m.Length > 0)
                .Select(m => m.Length > 220 ? m[..220] : m)
                .Take(5)
                .ToList()
            : [];
        var interval = Math.Clamp(ads?.IntervalSeconds ?? 120, 30, 900);
        var color = NormalizeAdColor(ads?.Color);

        var same =
            enabled
            && interval == _chatAdInterval
            && color == _chatAdColor
            && messages.SequenceEqual(_chatAds);

        if (!enabled)
        {
            StopChatAds();
            return;
        }

        _chatAds.Clear();
        _chatAds.AddRange(messages);
        _chatAdColor = color;
        if (_chatAdIndex >= _chatAds.Count) _chatAdIndex = 0;

        if (same && _chatAdTimer != null) return;

        _chatAdInterval = interval;
        _chatAdTimer?.Kill();
        _chatAdTimer = AddTimer(_chatAdInterval, BroadcastNextChatAd, TimerFlags.REPEAT);
    }

    private void StopChatAds()
    {
        _chatAdTimer?.Kill();
        _chatAdTimer = null;
        _chatAds.Clear();
        _chatAdIndex = 0;
    }

    private void BroadcastNextChatAd()
    {
        if (!_hosted || _chatAds.Count == 0) return;
        var template = _chatAds[_chatAdIndex % _chatAds.Count];
        _chatAdIndex = (_chatAdIndex + 1) % _chatAds.Count;
        try
        {
            var accent = AdChatColor(_chatAdColor);
            var body = ExpandAdPlaceholders(template);
            Server.PrintToChatAll(
                $" {accent}[{ChatColors.Default}AD{accent}]{ChatColors.Default} {body}");
        }
        catch
        {
            // ignore chat failures mid-round
        }
    }

    private static string NormalizeAdColor(string? color)
    {
        var key = (color ?? "gold").Trim().ToLowerInvariant();
        return key switch
        {
            "green" or "blue" or "red" or "purple" or "lightred" or "white" or "grey" or "gray" or "gold"
                => key == "gray" ? "grey" : key,
            _ => "gold",
        };
    }

    private static char AdChatColor(string color) => color switch
    {
        "green" => ChatColors.Green,
        "blue" => ChatColors.Blue,
        "red" => ChatColors.Red,
        "purple" => ChatColors.Purple,
        "lightred" => ChatColors.LightRed,
        "white" => ChatColors.White,
        "grey" => ChatColors.Grey,
        _ => ChatColors.Gold,
    };

    private string ExpandAdPlaceholders(string template)
    {
        var online = OnlinePlayers().ToList();
        var map = NativeAPI.GetMapName() ?? "";
        if (string.IsNullOrWhiteSpace(map))
        {
            try { map = Server.MapName ?? ""; } catch { map = ""; }
        }

        var players = online.Count;
        var maxPlayers = Math.Max(players, Server.MaxPlayers);
        var randomName = online.Count > 0
            ? online[Random.Shared.Next(online.Count)].PlayerName
            : "-";
        var onlineList = string.Join(", ", online.Select(p => p.PlayerName).Take(8));
        if (online.Count > 8) onlineList += "…";

        var now = DateTime.Now;
        return template
            .Replace("{time}", now.ToString("HH:mm", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{map}", map, StringComparison.OrdinalIgnoreCase)
            .Replace("{players}", players.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{maxplayers}", maxPlayers.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{player}", randomName, StringComparison.OrdinalIgnoreCase)
            .Replace("{online}", onlineList, StringComparison.OrdinalIgnoreCase);
    }

    private void OnClientAuthorized(int slot, SteamID steamId)
    {
        if (!IsBanned(steamId.SteamId64, out var ban)) return;
        Server.NextFrame(() =>
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is { IsValid: true }) KickBanned(player, ban);
        });
    }

    private bool IsBanned(ulong steamId, out BanEntry ban)
    {
        if ((_hosted || !Config.HostedOnly)
            && _bans.TryGetValue(steamId, out var found) && found.Active)
        {
            ban = found;
            return true;
        }
        ban = null!;
        return false;
    }

    private bool IsAdmin(CCSPlayerController? player)
    {
        if (player == null) return true;
        if (!player.IsValid || player.IsBot) return false;
        if (_hosted)
        {
            // AuthorizedSteamID can still be null briefly after join; SteamID is set earlier.
            var id = player.AuthorizedSteamID?.SteamId64 ?? player.SteamID;
            return id != 0 && _admins.Contains(id);
        }
        return AdminManager.PlayerHasPermissions(player, Config.AdminFlag)
               || AdminManager.PlayerHasPermissions(player, "@css/root");
    }

    private bool IsAdminSteam(ulong steamId)
    {
        if (_hosted) return _admins.Contains(steamId);
        var online = OnlinePlayers().FirstOrDefault(p => p.SteamID == steamId);
        return online != null && IsAdmin(online);
    }

    // ------------------------------------------------------------- commands

    [ConsoleCommand("css_admin", "List YGuard admin commands")]
    [ConsoleCommand("css_admins", "List YGuard admin commands")]
    public void CmdHelp(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Allowed(caller, info)) return;
        info.ReplyToCommand($"{Prefix} !slay <target> · !slap <target> [damage] · !kick <target> [reason]");
        info.ReplyToCommand($"{Prefix} !ban <target|steamid64> [time: 30m 2h 7d, 0 = permanent] [reason]");
        info.ReplyToCommand($"{Prefix} !bany <steamid64> (unban) · !respawn <target>");
        info.ReplyToCommand($"{Prefix} targets: name, #userid, @all, @t, @ct, @me");
    }

    [ConsoleCommand("css_slay", "Kill a player")]
    [CommandHelper(minArgs: 1, usage: "<target>")]
    public void CmdSlay(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Allowed(caller, info)) return;
        foreach (var target in Targets(caller, info))
        {
            var pawn = target.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;
            pawn.CommitSuicide(false, true);
            Announce($"{AdminName(caller)} slayed {ChatColors.Red}{target.PlayerName}");
        }
    }

    [ConsoleCommand("css_slap", "Slap a player")]
    [CommandHelper(minArgs: 1, usage: "<target> [damage]")]
    public void CmdSlap(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Allowed(caller, info)) return;
        var damage = Config.DefaultSlapDamage;
        if (info.ArgCount > 2 && int.TryParse(info.GetArg(2), out var parsed)) damage = parsed;
        damage = Math.Clamp(damage, 0, 500);

        foreach (var target in Targets(caller, info))
        {
            var pawn = target.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;

            var velocity = pawn.AbsVelocity;
            pawn.Teleport(null, null, new Vector(
                velocity.X + Random.Shared.Next(-250, 251),
                velocity.Y + Random.Shared.Next(-250, 251),
                velocity.Z + Random.Shared.Next(200, 301)));

            if (damage > 0)
            {
                pawn.Health -= damage;
                Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
                if (pawn.Health <= 0) pawn.CommitSuicide(false, true);
            }
            Announce($"{AdminName(caller)} slapped {ChatColors.Red}{target.PlayerName}"
                     + (damage > 0 ? $"{ChatColors.Default} ({damage} damage)" : ""));
        }
    }

    [ConsoleCommand("css_respawn", "Respawn a player")]
    [CommandHelper(minArgs: 1, usage: "<target>")]
    public void CmdRespawn(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Allowed(caller, info)) return;
        foreach (var target in Targets(caller, info))
        {
            if (target.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist)) continue;
            target.Respawn();
            Announce($"{AdminName(caller)} respawned {ChatColors.Green}{target.PlayerName}");
        }
    }

    [ConsoleCommand("css_kick", "Kick a player")]
    [CommandHelper(minArgs: 1, usage: "<target> [reason]")]
    public void CmdKick(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Allowed(caller, info)) return;
        var reason = JoinArgs(info, 2);
        foreach (var target in Targets(caller, info))
        {
            if (!CanPunish(caller, info, target)) continue;
            Announce($"{AdminName(caller)} kicked {ChatColors.Red}{target.PlayerName}"
                     + (reason.Length > 0 ? $"{ChatColors.Default} ({reason})" : ""));
            Kick(target, reason.Length > 0 ? $"Kicked: {reason}" : "Kicked by an admin");
        }
    }

    [ConsoleCommand("css_ban", "Ban a player")]
    [CommandHelper(minArgs: 1, usage: "<target|steamid64> [time] [reason]")]
    public void CmdBan(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Allowed(caller, info)) return;

        var minutes = 0;
        var reasonFrom = 2;
        if (info.ArgCount > 2 && TryParseMinutes(info.GetArg(2), out var parsed))
        {
            minutes = parsed;
            reasonFrom = 3;
        }
        var reason = JoinArgs(info, reasonFrom);

        var arg = info.GetArg(1).Trim();
        if (SteamIdPattern().IsMatch(arg))
        {
            var steamId = ulong.Parse(arg, CultureInfo.InvariantCulture);
            var online = OnlinePlayers().FirstOrDefault(p => p.SteamID == steamId);
            if (online != null)
            {
                if (CanPunish(caller, info, online)) Ban(caller, info, steamId, online.PlayerName, minutes, reason);
                return;
            }
            if (caller != null && IsAdminSteam(steamId))
            {
                info.ReplyToCommand($"{Prefix} You cannot ban another admin.");
                return;
            }
            Ban(caller, info, steamId, "", minutes, reason);
            return;
        }

        foreach (var target in Targets(caller, info))
        {
            if (CanPunish(caller, info, target)) Ban(caller, info, target.SteamID, target.PlayerName, minutes, reason);
        }
    }

    [ConsoleCommand("css_bany", "Unban a player")]
    [ConsoleCommand("css_unban", "Unban a player")]
    [CommandHelper(minArgs: 1, usage: "<steamid64>")]
    public void CmdUnban(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Allowed(caller, info)) return;
        var arg = info.GetArg(1).Trim();
        if (!SteamIdPattern().IsMatch(arg))
        {
            info.ReplyToCommand($"{Prefix} Usage: !bany <steamid64>");
            return;
        }
        var steamId = ulong.Parse(arg, CultureInfo.InvariantCulture);

        if (!_hosted)
        {
            var removed = _bans.Remove(steamId);
            _localBans?.Save(_bans);
            PushLocalBansToPanel();
            info.ReplyToCommand(removed ? $"{Prefix} {steamId} unbanned." : $"{Prefix} {steamId} was not banned.");
            return;
        }

        var adminId = caller?.AuthorizedSteamID?.SteamId64 ?? 0;
        Task.Run(async () =>
        {
            var error = await _api.UnbanAsync(steamId, adminId);
            Server.NextFrame(() =>
            {
                if (error != null)
                {
                    Reply(caller, $"Unban failed: {error}");
                    return;
                }
                _bans.Remove(steamId);
                Announce($"{AdminName(caller)} unbanned {ChatColors.Green}{steamId}");
            });
        });
    }

    [ConsoleCommand("css_yadmin_reload", "Re-read admins and bans from the panel")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void CmdReload(CCSPlayerController? caller, CommandInfo info)
    {
        Refresh();
        info.ReplyToCommand("[YGuardAdmin] reloading admins and bans");
    }

    // -------------------------------------------------------------- helpers

    private void PushLocalBansToPanel()
    {
        if (!_api.Configured) return;
        var snapshot = _bans
            .Where(kv => kv.Value.Active)
            .Select(kv => (kv.Key, Name: "", kv.Value.Reason, kv.Value.ExpiresAt))
            .ToList();
        _ = _api.SyncBansAsync(snapshot);
    }

    private void Ban(CCSPlayerController? caller, CommandInfo info, ulong steamId, string name, int minutes, string reason)
    {
        var duration = minutes > 0 ? FormatMinutes(minutes) : "permanently";
        var entry = new BanEntry
        {
            Reason = reason,
            ExpiresAt = minutes > 0 ? DateTime.UtcNow.AddMinutes(minutes) : null,
        };

        void Apply()
        {
            _bans[steamId] = entry;
            var label = name.Length > 0 ? name : steamId.ToString();
            Announce($"{AdminName(caller)} banned {ChatColors.Red}{label}{ChatColors.Default} {duration}"
                     + (reason.Length > 0 ? $" ({reason})" : ""));
            var online = OnlinePlayers().FirstOrDefault(p => p.SteamID == steamId);
            if (online != null) KickBanned(online, entry);
        }

        if (!_hosted)
        {
            Apply();
            _localBans?.Save(_bans);
            PushLocalBansToPanel();
            return;
        }

        var adminId = caller?.AuthorizedSteamID?.SteamId64 ?? 0;
        var adminName = caller?.PlayerName ?? "Console";
        Task.Run(async () =>
        {
            var error = await _api.BanAsync(steamId, name, reason, minutes, adminId, adminName);
            Server.NextFrame(() =>
            {
                if (error != null)
                {
                    Reply(caller, $"Ban failed: {error}");
                    return;
                }
                Apply();
            });
        });
    }

    private bool Allowed(CCSPlayerController? caller, CommandInfo info)
    {
        if (!_enabled || (!_hosted && Config.HostedOnly)) return false;
        if (IsAdmin(caller)) return true;
        info.ReplyToCommand($"{Prefix} You are not an admin on this server.");
        return false;
    }

    private bool CanPunish(CCSPlayerController? caller, CommandInfo info, CCSPlayerController target)
    {
        if (caller == null || target.SteamID == caller.SteamID || !IsAdmin(target)) return true;
        info.ReplyToCommand($"{Prefix} {target.PlayerName} is an admin.");
        return false;
    }

    private List<CCSPlayerController> Targets(CCSPlayerController? caller, CommandInfo info)
    {
        var players = info.GetArgTargetResult(1).Players
            .Where(p => p is { IsValid: true, IsHLTV: false })
            .ToList();
        if (players.Count == 0) info.ReplyToCommand($"{Prefix} No player matches \"{info.GetArg(1)}\".");
        return players;
    }

    private static IEnumerable<CCSPlayerController> OnlinePlayers()
        => Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false });

    private void KickBanned(CCSPlayerController player, BanEntry ban)
    {
        var until = ban.ExpiresAt is { } expires
            ? $"until {expires:yyyy-MM-dd HH:mm} UTC"
            : "permanently";
        Kick(player, $"You are banned from this server {until}"
                     + (ban.Reason.Length > 0 ? $": {ban.Reason}" : ""));
    }

    private static void Kick(CCSPlayerController player, string reason)
    {
        if (!player.IsValid || player.UserId == null) return;
        var safe = reason.Replace("\"", "'").Replace(";", ",").Trim();
        if (safe.Length > 120) safe = safe[..120];
        Server.ExecuteCommand($"kickid {player.UserId} {safe}");
    }

    private void Reply(CCSPlayerController? caller, string message)
    {
        if (caller is { IsValid: true }) caller.PrintToChat($"{Prefix} {message}");
        else Console.WriteLine($"[YGuardAdmin] {message}");
    }

    private void Announce(string message) => Server.PrintToChatAll($"{Prefix} {message}");

    private string Prefix => $" {ChatColors.Green}{Config.ChatPrefix}{ChatColors.Default}";

    private static string AdminName(CCSPlayerController? caller)
        => $"{ChatColors.Gold}ADMIN {(caller?.PlayerName ?? "Console")}{ChatColors.Default}";

    private static string JoinArgs(CommandInfo info, int from)
    {
        var parts = new List<string>();
        for (var i = from; i < info.ArgCount; i++) parts.Add(info.GetArg(i));
        return string.Join(' ', parts).Trim().Trim('"');
    }

    private static bool TryParseMinutes(string input, out int minutes)
    {
        minutes = 0;
        var match = DurationPattern().Match(input.Trim().ToLowerInvariant());
        if (!match.Success) return false;
        var n = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var total = match.Groups[2].Value switch
        {
            "h" => n * 60,
            "d" => n * 60 * 24,
            "w" => n * 60 * 24 * 7,
            _ => n,
        };
        minutes = (int)Math.Min(total, 60L * 24 * 365 * 10);
        return true;
    }

    private static string FormatMinutes(int minutes)
    {
        if (minutes % (60 * 24) == 0) return $"for {minutes / (60 * 24)} day(s)";
        if (minutes % 60 == 0) return $"for {minutes / 60} hour(s)";
        return $"for {minutes} minute(s)";
    }

    [GeneratedRegex(@"^7656119\d{10}$")]
    private static partial Regex SteamIdPattern();

    [GeneratedRegex(@"^(\d{1,7})(m|h|d|w)?$")]
    private static partial Regex DurationPattern();
}
