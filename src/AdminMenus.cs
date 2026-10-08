using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace YGuardAdmin;

public partial class YGuardAdminPlugin
{
    private readonly Dictionary<int, BaseMenu> _openMenus = [];

    private enum AdminAction
    {
        Slay,
        Slap,
        Kick,
        Respawn,
        Ban,
    }

    private void OpenAdminRootMenu(CCSPlayerController admin)
    {
        var menu = new CenterHtmlMenu("YGuard Admin", this);
        menu.AddMenuOption("Slay", (p, _) => Defer(() => OpenPlayerMenu(p, AdminAction.Slay)));
        menu.AddMenuOption("Slap", (p, _) => Defer(() => OpenPlayerMenu(p, AdminAction.Slap)));
        menu.AddMenuOption("Kick", (p, _) => Defer(() => OpenPlayerMenu(p, AdminAction.Kick)));
        menu.AddMenuOption("Ban", (p, _) => Defer(() => OpenPlayerMenu(p, AdminAction.Ban)));
        menu.AddMenuOption("Respawn", (p, _) => Defer(() => OpenPlayerMenu(p, AdminAction.Respawn)));
        OpenCenter(admin, menu);
        Reply(admin, "Menu open — press 1-9 or type !1 !2 …");
    }

    private void OpenPlayerMenu(CCSPlayerController admin, AdminAction action)
    {
        if (!admin.IsValid) return;

        var title = action switch
        {
            AdminAction.Slay => "Slay — pick player",
            AdminAction.Slap => "Slap — pick player",
            AdminAction.Kick => "Kick — pick player",
            AdminAction.Ban => "Ban — pick player",
            AdminAction.Respawn => "Respawn — pick player",
            _ => "Pick player",
        };

        var menu = new CenterHtmlMenu(title, this);
        var index = 0;
        foreach (var target in OnlinePlayers().OrderBy(p => p.PlayerName, StringComparer.OrdinalIgnoreCase))
        {
            index++;
            var name = string.IsNullOrWhiteSpace(target.PlayerName) ? "player" : target.PlayerName;
            var team = target.Team switch
            {
                CsTeam.Terrorist => "T",
                CsTeam.CounterTerrorist => "CT",
                CsTeam.Spectator => "SPEC",
                _ => "?",
            };
            var steamId = target.SteamID;
            var label = $"{index}. [{team}] {name}";
            menu.AddMenuOption(label, (picker, _) =>
            {
                Defer(() =>
                {
                    if (!picker.IsValid) return;
                    var live = OnlinePlayers().FirstOrDefault(p => p.SteamID == steamId);
                    if (live == null)
                    {
                        Reply(picker, "That player left.");
                        return;
                    }
                    if (action == AdminAction.Ban)
                    {
                        OpenBanDurationMenu(picker, live);
                        return;
                    }
                    ApplyAction(picker, live, action);
                });
            });
        }

        if (index == 0)
        {
            Reply(admin, "No players online.");
            return;
        }

        OpenCenter(admin, menu);
        Reply(admin, $"{title} — press 1-{Math.Min(index, 9)} or type !1 …");
    }

    private void OpenBanDurationMenu(CCSPlayerController admin, CCSPlayerController target)
    {
        var steamId = target.SteamID;
        var name = target.PlayerName ?? "";
        var menu = new CenterHtmlMenu($"Ban {name}", this);
        void Add(string label, int minutes) =>
            menu.AddMenuOption(label, (picker, _) =>
            {
                Defer(() =>
                {
                    if (!picker.IsValid) return;
                    if (IsAdminSteam(steamId) && steamId != (picker.AuthorizedSteamID?.SteamId64 ?? picker.SteamID))
                    {
                        Reply(picker, "You cannot ban another admin.");
                        return;
                    }
                    Ban(picker, steamId, name, minutes, "admin menu");
                });
            });

        Add("30 minutes", 30);
        Add("2 hours", 120);
        Add("1 day", 1440);
        Add("7 days", 10080);
        Add("Permanent", 0);
        menu.AddMenuOption("« Back", (p, _) => Defer(() => OpenPlayerMenu(p, AdminAction.Ban)));
        OpenCenter(admin, menu);
    }

    private void ApplyAction(CCSPlayerController? admin, CCSPlayerController target, AdminAction action)
    {
        if (admin != null && !CanPunishPlayer(admin, target)) return;

        switch (action)
        {
            case AdminAction.Slay:
            {
                var pawn = target.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE)
                {
                    Reply(admin, $"{target.PlayerName} is not alive.");
                    return;
                }
                pawn.CommitSuicide(false, true);
                Announce($"{AdminName(admin)} slayed {ChatColors.Red}{target.PlayerName}");
                break;
            }
            case AdminAction.Slap:
            {
                var pawn = target.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE)
                {
                    Reply(admin, $"{target.PlayerName} is not alive.");
                    return;
                }
                var damage = Math.Clamp(Config.DefaultSlapDamage, 0, 500);
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
                Announce($"{AdminName(admin)} slapped {ChatColors.Red}{target.PlayerName}"
                         + (damage > 0 ? $"{ChatColors.Default} ({damage} damage)" : ""));
                break;
            }
            case AdminAction.Kick:
                Announce($"{AdminName(admin)} kicked {ChatColors.Red}{target.PlayerName}");
                Kick(target, "Kicked by an admin");
                break;
            case AdminAction.Respawn:
                if (target.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
                {
                    Reply(admin, $"{target.PlayerName} is not on T/CT.");
                    return;
                }
                target.Respawn();
                Announce($"{AdminName(admin)} respawned {ChatColors.Green}{target.PlayerName}");
                break;
        }
    }

    private bool CanPunishPlayer(CCSPlayerController? caller, CCSPlayerController target)
    {
        if (caller == null || target.SteamID == caller.SteamID || !IsAdmin(target)) return true;
        Reply(caller, $"{target.PlayerName} is an admin.");
        return false;
    }

    private void OpenCenter(CCSPlayerController player, CenterHtmlMenu menu)
    {
        menu.PostSelectAction = PostSelectAction.Close;
        try { MenuManager.CloseActiveMenu(player); } catch { /* ignore */ }
        _openMenus[player.Slot] = menu;
        MenuManager.OpenCenterHtmlMenu(this, player, menu);
    }

    private void Defer(Action action) => AddTimer(0.05f, () =>
    {
        try { action(); }
        catch (Exception ex) { Logger.LogWarning(ex, "admin menu action failed"); }
    });

    private void HookMenuNumberPicks()
    {
        AddCommandListener("say", OnMenuSay, HookMode.Pre);
        AddCommandListener("say_team", OnMenuSay, HookMode.Pre);
    }

    private HookResult OnMenuSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player is not { IsValid: true }) return HookResult.Continue;
        if (!_openMenus.ContainsKey(player.Slot)) return HookResult.Continue;

        var raw = (info.GetArg(1) ?? "").Trim().Trim('"');
        if (raw.Length is < 1 or > 3) return HookResult.Continue;

        var digits = raw.TrimStart('!', '/', '.');
        if (!int.TryParse(digits, out var choice) || choice is < 1 or > 9)
            return HookResult.Continue;

        if (!TrySelectMenu(player, choice))
            return HookResult.Continue;

        return HookResult.Handled;
    }

    private bool TrySelectMenu(CCSPlayerController player, int oneBased)
    {
        if (!_openMenus.TryGetValue(player.Slot, out var menu) || menu == null)
            return false;

        if (oneBased < 1 || oneBased > menu.MenuOptions.Count)
        {
            _openMenus.Remove(player.Slot);
            try { MenuManager.CloseActiveMenu(player); } catch { /* ignore */ }
            Reply(player, "Menu closed.");
            return true;
        }

        var option = menu.MenuOptions[oneBased - 1];
        if (option.Disabled)
        {
            _openMenus.Remove(player.Slot);
            return true;
        }

        var onSelect = option.OnSelect;
        _openMenus.Remove(player.Slot);
        try { MenuManager.CloseActiveMenu(player); } catch { /* ignore */ }
        onSelect?.Invoke(player, option);
        return true;
    }

    private static bool WantsPlayerMenu(CommandInfo info)
        => info.ArgCount < 2 || string.IsNullOrWhiteSpace(info.GetArg(1));
}
