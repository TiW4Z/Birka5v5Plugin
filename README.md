# Birka5v5

CounterStrikeSharp plugin for community 5v5 matches:
ready-up → map veto (team vote, center HUD) → map change → ready-up → knife round → live (GOTV demo + round backups), with `.pause`/`.unpause` and admin `.restore`.

## Install

Download a zip from [Releases](../../releases) and extract it into the server's `csgo` folder (the one containing `addons` and `cfg`):

- `Birka5v5-vX.Y.Z.zip`: first install, plugin + `cfg/Birka5v5/`
- `Birka5v5-vX.Y.Z-update.zip`: update, plugin DLL only (keeps your edited cfg files)

For demos, set `tv_enable 1` in `server.cfg` (don't change `tv_enable`/`tv_delay` in warmup/live cfg).

### Building manually

1. `dotnet build -c Release`
2. Copy `bin/Release/net8.0/Birka5v5.dll` to `csgo/addons/counterstrikesharp/plugins/Birka5v5/`
3. Copy the `cfg/Birka5v5/` folder to `csgo/cfg/Birka5v5/`

### Making a release

Push a version tag; GitHub Actions builds both zips and publishes the release:

```
git tag v1.0.1
git push origin v1.0.1
```

Built against CounterStrikeSharp.API 1.0.368 (the last net8.0 release); it also runs on newer CSS builds.

## Flow

| Phase | What happens |
|---|---|
| Warmup | `warmup.cfg`. All players on T/CT type `.ready` (at least `birka_players_required`). |
| Veto | Coin flip for who starts. The acting team votes by typing the map number; most votes wins, ties/no votes are random. Teams are locked. |
| Map change | `changelevel`, or `host_workshop_map` for workshop entries. |
| Match ready | `warmup.cfg` again. Everyone types `.ready`. |
| Knife | `knife.cfg`. The winners type `.stay` or `.switch` (auto-stay after `birka_knife_decision_time`). |
| Live | `live.cfg`, round backups `birka_<matchid>_roundNN.txt`, demo recording to `csgo/birka_demos/`. |
| Match end | Demo stops after the GOTV delay, then the server returns to warmup. |

With `.veto` off, the first ready-up goes straight to the knife round on the current map.

## Commands

Every command works with `.` or `!` (e.g. `.ready` / `!ready`).

| Command | Who | |
|---|---|---|
| `.r` `.ready` / `.ur` `.unready` | everyone | ready / unready |
| `<number>` `.ban <n\|map>` `.pick <n\|map>` | acting team | veto vote |
| `.veto list` | everyone | show pool and mode |
| `.stay` / `.switch` | knife winners | choose sides |
| `.pause` `.p` | T/CT | pause now in freezetime, otherwise at the next freezetime |
| `.unpause` `.up` | T/CT | both teams must type it to resume |
| `.help` | everyone | list commands |
| `.forcestart` | admin | skip the ready check |
| `.veto` / `.veto on` / `.veto off` | admin | toggle the veto (saved to config.cfg) |
| `.veto add <map>` / `.veto add <name> <workshopid>` | admin | add to pool (saved) |
| `.veto remove <map>` | admin | remove from pool (saved) |
| `.veto mode ban\|pick` | admin | change veto mode (saved) |
| `.restore <round>` | admin | replay from the start of that round (1 = first round); pauses until both teams `.unpause` |
| `.forcepause` / `.forceunpause` | admin | pause / unpause without a vote |
| `.reset` | admin | abort and return to warmup |

Server console: `birka_forcestart`, `birka_restore <round>`, `birka_reset`, `birka_status`.

Admins are players with `birka_admin_flag` (default `@css/config`) or `@css/root` in CounterStrikeSharp's `admins.json`, or SteamID64s listed in `birka_admins`.

## Settings

All settings are in `csgo/cfg/Birka5v5/config.cfg` (created with defaults if missing). See the comments in that file.
