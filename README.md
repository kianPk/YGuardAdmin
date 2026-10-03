# YGuard Admin

CounterStrikeSharp admin commands for YGuard / 5Stack servers.

## Commands

Use them in chat with `!` (public) or `/` (silent), or in the console with `css_`.

| Command | What it does |
| --- | --- |
| `!slay <target>` | Kill the player |
| `!slap <target> [damage]` | Knock the player around, optionally with damage |
| `!kick <target> [reason]` | Kick the player |
| `!ban <target\|steamid64> [time] [reason]` | Ban. Time: `30m`, `2h`, `7d`, `2w`, plain minutes, or `0` / nothing for permanent. A SteamID64 also bans players who are offline |
| `!bany <steamid64>` / `!unban` | Remove a ban |
| `!respawn <target>` | Respawn the player |
| `!admin` | List the commands |

Targets: part of a name, `#userid`, `@all`, `@t`, `@ct`, `@me`.

Admins cannot kick or ban each other.

## Who is an admin

- **Servers rented from the panel:** the owner, plus whoever the owner adds on the server's page in the panel (Hosting → the server → In-game admins). Bans are stored in the panel too, so the owner can see them and unban there. Changes are pushed over RCON and re-read every `PollSeconds`.
- **Any other server:** players with the `AdminFlag` CounterStrikeSharp permission (default `@css/ban`) or `@css/root`. Bans go to `configs/plugins/YGuardAdmin/bans.json`.
- **Ranked** pods load the plugin idle.

## Config

`configs/plugins/YGuardAdmin/YGuardAdmin.json`

```json
{
  "ChatPrefix": "YGuard",
  "PollSeconds": 30,
  "AdminFlag": "@css/ban",
  "DefaultSlapDamage": 0
}
```

## Build

```bash
dotnet build -c Release
```

The release zip contains `addons/counterstrikesharp/plugins/YGuardAdmin/YGuardAdmin.dll`.
