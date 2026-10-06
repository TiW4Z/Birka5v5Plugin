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
| Match ready | `warmup.cfg` again. Players join any team. Starts when everyone types `.ready`, an admin uses `.forcestart`, or by itself (10 s countdown) once everyone from before the veto is back on a team. The teams from before the veto are restored as it starts. |
| Knife | `knife.cfg`. The winners type `.stay` or `.switch` (auto-stay after `birka_knife_decision_time`). |
| Live | `live.cfg`, round backups `birka_<matchid>_roundNN.txt`, demo recording to `csgo/addons/metamod/birka_demos/` (the game writes files to the Metamod folder, same as MatchZy). |
| Match end | Demo stops after the GOTV delay, then the server returns to warmup. |

With `.veto` off, the first ready-up goes straight to the knife round on the current map.

During a live match:
- **Damage report:** after every round each player sees `To: [dmg / hits] From: [dmg / hits] - opponent (hp)` for every opponent (`birka_damage_report`).
- **Auto-pause on disconnect:** if a player drops, the match pauses at the next freezetime. When everyone is back on a team it unpauses after 10 seconds. If they're not back within `birka_disconnect_pause_time` (default 5 min), both teams must type `.unpause` to continue (`birka_disconnect_pause`).

## Elo rating and balanced teams

- **Ratings** are stored in `csgo/cfg/Birka5v5/elo.json` (new players start at 1000). Every completed match is logged to `elo_history.jsonl` next to it.
- **After each match** the winning team gains and the losing team loses points (team Elo: beating a stronger team gives more, bigger round margins count a bit more). Within a team, ADR and kills decide each player's share, so a strong player on a losing team loses less. A team's total change isn't affected by performance, so ratings don't inflate.
- **First 10 matches** use a bigger step (shown as "provisional"). Admins can seed known skill levels with `.elo set <name> <rating>`.
- **Balancing** (`birka_elo_autobalance 1`): when everyone is ready in warmup, players are split into the two most even teams before the veto. Among splits that are nearly as fair, one is picked at random (and last match's exact teams are avoided) so teammates vary. On the match map players can join any team; the balanced teams are restored when the knife round starts.
- A match only counts if it ends normally (not `.reset`) and each team has at least `birka_elo_min_team_size` players who played most of the rounds.

## Commands

Every command works with `.` or `!` (e.g. `.ready` / `!ready`).

| Command | Who | |
|---|---|---|
| `.r` `.ready` / `.ur` `.unready` `.notready` | everyone | ready / unready |
| `<number>` `.ban <n\|map>` `.pick <n\|map>` | acting team | veto vote |
| `.veto list` | everyone | show pool and mode |
| `.stay` / `.switch` `.swap` | knife winners | choose sides |
| `.pause` `.p` | T/CT | pause now in freezetime, otherwise at the next freezetime |
| `.unpause` `.up` | T/CT | both teams must type it to resume |
| `.elo` `.rank` / `.elo <name>` | everyone | rating, W-L-D and rank |
| `.top` | everyone | top 10 ratings (including seeded players) |
| `.help` | everyone | list commands |
| `.forcestart` | admin | skip the ready check |
| `.veto` / `.veto on` / `.veto off` | admin | toggle the veto (until restart) |
| `.veto add <map>` / `.veto add <name> <workshopid>` | admin | add to pool (until restart) |
| `.veto remove <map>` | admin | remove from pool (until restart) |
| `.veto mode ban\|pick` | admin | change veto mode (until restart) |
| `.restore <round>` | admin | replay from the start of that round (1 = first round); pauses until both teams `.unpause` |
| `.forcepause` `.fp` / `.forceunpause` `.fup` | admin | pause / unpause without a vote |
| `.reset` `.restart` `.rr` `.endmatch` `.forceend` | admin | abort and return to warmup |
| `.map <name>` / `.map <pool number>` / `.map <workshop id>` | admin | change map (not during a match); the new map starts in warmup |
| `.balance` | admin | balance teams by rating now (warmup) |
| `.balance on` / `.balance off` | admin | toggle auto-balancing (until restart) |
| `.elo set <name> <rating>` | admin | seed or correct a player's rating |

Server console: `birka_forcestart`, `birka_restore <round>`, `birka_reset`, `birka_status`.

Admins are players with `birka_admin_flag` (default `@css/config`) or `@css/root` in CounterStrikeSharp's `admins.json`, or SteamID64s listed in `birka_admins`.

## Settings

All settings are in `csgo/cfg/Birka5v5/config.cfg` (created with defaults if missing). See the comments in that file.

The file is read when the server or plugin starts and is never written by the plugin. Admin changes in-game (`.veto`, `.veto add/remove/mode`, `.balance on/off`) last until the next server restart (or `css_plugins reload Birka5v5`); to make a change permanent, edit `config.cfg`.
