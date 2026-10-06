using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    private enum MapState { Available, Banned, Picked }

    private record VetoStep(int Team, bool IsPick);

    private const int VetoCountdownSeconds = 5;

    private List<MapEntry> vetoPool = new();
    private readonly Dictionary<string, (MapState State, int Team)> vetoMapStates = new(StringComparer.OrdinalIgnoreCase);
    private List<VetoStep> vetoSteps = new();
    private int vetoStepIndex;
    private bool vetoPickMode;
    private bool vetoVoteActive;
    private int vetoSecondsLeft;
    private readonly Dictionary<ulong, string> vetoBallots = new();
    private Timer? vetoVoteTimer;
    private MapEntry? selectedMap;

    // True while the veto holds players frozen with mp_pause_match.
    private bool vetoFrozen;

    // Shown on the HUD when no vote is open (countdown, last result, final map).
    private string vetoHeadline = "";

    private VetoStep? CurrentVetoStep => vetoStepIndex < vetoSteps.Count ? vetoSteps[vetoStepIndex] : null;

    private IEnumerable<MapEntry> AvailableMaps => vetoPool.Where(m => vetoMapStates[m.Name].State == MapState.Available);

    private void ResetVetoState()
    {
        vetoPool = new List<MapEntry>();
        vetoMapStates.Clear();
        vetoSteps = new List<VetoStep>();
        vetoStepIndex = 0;
        vetoVoteActive = false;
        vetoBallots.Clear();
        vetoVoteTimer = null;
        selectedMap = null;
        vetoHeadline = "";
        hudDirty = true;
    }

    private void StartVeto()
    {
        var pool = GetMapPool();
        bool pickMode = IsPickMode;

        if (pool.Count < 2)
        {
            PrintAll($"{ChatColors.Red}The veto map pool needs at least 2 maps. Starting on the current map.");
            StartMatch();
            return;
        }
        if (pickMode && pool.Count < 4)
        {
            PrintAll($"{ChatColors.Red}Pick mode needs at least 4 maps, using ban mode instead.");
            pickMode = false;
        }

        SetPhase(MatchPhase.Veto);
        ResetVetoState();
        readyPlayers.Clear();

        // Send everyone back to spawn and hold them there: ending warmup starts a fresh round,
        // and the pause holds its freezetime until the veto is done.
        Server.ExecuteCommand("mp_warmup_end");
        Server.ExecuteCommand("mp_pause_match");
        vetoFrozen = true;

        vetoPool = pool;
        vetoPickMode = pickMode;
        foreach (var map in pool) vetoMapStates[map.Name] = (MapState.Available, 0);

        int first = Random.Shared.Next(2) == 0 ? TeamCT : TeamT;
        int second = OtherTeam(first);
        if (pickMode)
        {
            vetoSteps = new List<VetoStep>
            {
                new(first, true), new(second, true), new(second, true), new(first, true),
            };
        }
        else
        {
            for (int i = 0; i < pool.Count - 1; i++)
            {
                vetoSteps.Add(new VetoStep(i % 2 == 0 ? first : second, false));
            }
        }

        string modeText = pickMode
            ? "each team picks 2 maps, then one of the 4 is chosen at random"
            : "teams take turns banning until one map is left";
        PrintAll($"{ChatColors.Lime}Map veto:{ChatColors.Default} {modeText}.");
        PrintAll($"Coin flip: {TeamName(first)} go first. Vote by typing the map number in chat.");

        int countdown = VetoCountdownSeconds;
        vetoHeadline = $"<font class='fontSize-m' color='#80c0ff'>MAP VETO STARTS IN {countdown}</font>";
        hudDirty = true;
        AddPhaseTimer(1.0f, () =>
        {
            countdown--;
            if (countdown > 0)
            {
                vetoHeadline = $"<font class='fontSize-m' color='#80c0ff'>MAP VETO STARTS IN {countdown}</font>";
                hudDirty = true;
            }
            else if (countdown == 0)
            {
                StartVetoVote();
            }
        }, repeat: true);
    }

    private void StartVetoVote()
    {
        var step = CurrentVetoStep;
        if (step == null)
        {
            FinishVeto();
            return;
        }

        vetoBallots.Clear();
        vetoSecondsLeft = Math.Max(5, VetoVoteTime.Value);
        vetoVoteActive = true;
        hudDirty = true;

        string action = step.IsPick ? $"{ChatColors.Lime}PICK" : $"{ChatColors.Red}BAN";
        PrintAll($"{TeamName(step.Team)} {action}{ChatColors.Default} a map ({vetoSecondsLeft}s). Type the number:");
        PrintVetoMapList();

        vetoVoteTimer = AddPhaseTimer(1.0f, VetoVoteTick, repeat: true);
    }

    private void VetoVoteTick()
    {
        if (!vetoVoteActive) return;
        vetoSecondsLeft--;
        hudDirty = true;

        if (!VetoHud.Value && (vetoSecondsLeft == 10 || vetoSecondsLeft == 5))
        {
            PrintAll($"{vetoSecondsLeft} seconds left to vote.");
        }
        if (vetoSecondsLeft <= 0)
        {
            CloseVetoVote();
        }
    }

    private void PrintVetoMapList()
    {
        var lines = vetoPool.Select((m, i) =>
        {
            var (state, team) = vetoMapStates[m.Name];
            return state switch
            {
                MapState.Banned => $"{ChatColors.Grey}{i + 1}. {m.Label} (banned){ChatColors.Default}",
                MapState.Picked => $"{ChatColors.Lime}{i + 1}. {m.Label} ({TeamShort(team)} pick){ChatColors.Default}",
                _ => $"{Hl($"{i + 1}.")} {m.Label}",
            };
        });
        PrintAll(string.Join("  ", lines));
    }

    private void CmdVetoVote(CCSPlayerController player, string[] args)
    {
        if (args.Length == 0)
        {
            Reply(player, "Usage: .ban <number|map> or .pick <number|map>");
            return;
        }
        CastVetoVote(player, string.Join(' ', args));
    }

    private void CastVetoVote(CCSPlayerController player, string input)
    {
        var step = CurrentVetoStep;
        if (phase != MatchPhase.Veto || !vetoVoteActive || step == null)
        {
            Reply(player, "There is no veto vote right now.");
            return;
        }
        if (player.TeamNum != step.Team)
        {
            Reply(player, $"It's not your team's turn. {TeamName(step.Team)} are voting.");
            return;
        }

        var map = FindMap(vetoPool, input);
        if (map == null)
        {
            Reply(player, $"Unknown map '{input}'. Type the map number.");
            return;
        }
        if (vetoMapStates[map.Name].State != MapState.Available)
        {
            Reply(player, $"{map.Label} is already {(vetoMapStates[map.Name].State == MapState.Banned ? "banned" : "picked")}.");
            return;
        }

        vetoBallots[player.SteamID] = map.Name;
        hudDirty = true;
        Reply(player, $"You voted to {(step.IsPick ? "pick" : "ban")} {Hl(map.Label)}.");

        // Close early once the whole acting team has voted.
        var voters = TeamPlayers(step.Team).ToList();
        if (voters.Count > 0 && voters.All(p => vetoBallots.ContainsKey(p.SteamID)))
        {
            vetoVoteActive = false;
            vetoVoteTimer?.Kill();
            AddPhaseTimer(1.0f, CloseVetoVote);
        }
    }

    private void CloseVetoVote()
    {
        var step = CurrentVetoStep;
        if (step == null) return;

        vetoVoteActive = false;
        vetoVoteTimer?.Kill();
        vetoVoteTimer = null;

        var available = AvailableMaps.ToList();
        var teamIds = TeamPlayers(step.Team).Select(p => p.SteamID).ToHashSet();
        var counts = vetoBallots
            .Where(b => teamIds.Contains(b.Key) && available.Any(m => m.Name == b.Value))
            .GroupBy(b => b.Value)
            .Select(g => (Map: g.Key, Votes: g.Count()))
            .ToList();

        MapEntry chosen;
        string how;
        if (counts.Count == 0)
        {
            chosen = available[Random.Shared.Next(available.Count)];
            how = " (no votes, random)";
        }
        else
        {
            int max = counts.Max(c => c.Votes);
            var tied = counts.Where(c => c.Votes == max).ToList();
            var pickedName = tied[Random.Shared.Next(tied.Count)].Map;
            chosen = available.First(m => m.Name == pickedName);
            how = tied.Count > 1 ? $" (tie, random of {tied.Count})" : $" ({max} vote{(max == 1 ? "" : "s")})";
        }

        vetoMapStates[chosen.Name] = (step.IsPick ? MapState.Picked : MapState.Banned, step.Team);
        vetoBallots.Clear();

        string verb = step.IsPick ? $"{ChatColors.Lime}picked" : $"{ChatColors.Red}banned";
        PrintAll($"{TeamName(step.Team)} {verb}{ChatColors.Default} {Hl(chosen.Label)}{how}.");

        string color = step.IsPick ? "#40e070" : "#ff4040";
        vetoHeadline = $"<font class='fontSize-m' color='{color}'>{TeamShort(step.Team)} {(step.IsPick ? "PICKED" : "BANNED")} {chosen.Label.ToUpperInvariant()}</font>";
        hudDirty = true;

        vetoStepIndex++;
        if (CurrentVetoStep == null)
        {
            AddPhaseTimer(2.0f, FinishVeto);
        }
        else
        {
            AddPhaseTimer(2.0f, StartVetoVote);
        }
    }

    private void FinishVeto()
    {
        if (vetoPickMode)
        {
            var picks = vetoPool.Where(m => vetoMapStates[m.Name].State == MapState.Picked).ToList();
            selectedMap = picks[Random.Shared.Next(picks.Count)];
            PrintAll($"Picked maps: {string.Join(", ", picks.Select(m => m.Label))}. Random choice...");
        }
        else
        {
            selectedMap = AvailableMaps.First();
            vetoMapStates[selectedMap.Name] = (MapState.Picked, 0);
        }

        var map = selectedMap;
        PrintAll($"{ChatColors.Lime}The match will be played on {Hl(map.Label.ToUpperInvariant())}!");
        vetoHeadline = $"<font class='fontSize-l' color='#40e070'>MAP: {map.Label.ToUpperInvariant()}</font>";
        hudDirty = true;

        AddPhaseTimer(6.0f, () => ChangeToSelectedMap(map));
    }

    private void UnfreezeVeto()
    {
        if (!vetoFrozen) return;
        vetoFrozen = false;
        Server.ExecuteCommand("mp_unpause_match");
    }

    private void ChangeToSelectedMap(MapEntry map)
    {
        ClearVetoHud();
        UnfreezeVeto();

        matchMap = map;
        matchMapLoadedName = null;
        if (!map.IsWorkshop && Server.MapName.Equals(map.Name, StringComparison.OrdinalIgnoreCase))
        {
            matchMapLoadedName = Server.MapName;
            EnterMatchReady();
            return;
        }

        SetPhase(MatchPhase.MapChangePending);
        PrintAll($"Changing map to {Hl(map.Label)}...");
        if (!ChangeMap(map))
        {
            PrintAll($"{ChatColors.Red}Map {map.Name} was not found on the server. Check birka_veto_maps.");
            EnterWarmup();
        }
    }

    // ---- .veto admin command ----

    private void CmdVeto(CCSPlayerController player, string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        if (sub == "list" || sub == "maps")
        {
            var pool = GetMapPool();
            string state = VetoEnabled.Value ? $"{ChatColors.Lime}on" : $"{ChatColors.Red}off";
            Reply(player, $"Veto is {state}{ChatColors.Default}, mode {Hl(IsPickMode ? "pick" : "ban")}. Pool ({pool.Count}): {string.Join(", ", pool.Select(m => m.ToString()))}");
            return;
        }

        if (!RequireAdmin(player)) return;

        if (sub is "add" or "remove" or "rem" or "del" or "mode" && phase == MatchPhase.Veto)
        {
            Reply(player, "Can't change the veto while it is running.");
            return;
        }

        switch (sub)
        {
            case "":
            case "on":
            case "off":
            {
                bool enabled = sub == "" ? !VetoEnabled.Value : sub == "on";
                VetoEnabled.Value = enabled;
                PrintAll($"Map veto {(enabled ? $"{ChatColors.Lime}enabled" : $"{ChatColors.Red}disabled")}{ChatColors.Default}{UntilRestart}.");
                break;
            }
            case "add":
                VetoAdd(player, args.Skip(1).ToArray());
                break;
            case "remove":
            case "rem":
            case "del":
                VetoRemove(player, args.Skip(1).ToArray());
                break;
            case "mode":
            {
                string mode = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                if (mode != "ban" && mode != "pick")
                {
                    Reply(player, "Usage: .veto mode ban|pick");
                    return;
                }
                VetoMode.Value = mode;
                PrintAll($"Veto mode set to {Hl(mode)}{UntilRestart}.");
                if (mode == "pick" && GetMapPool().Count < 4)
                {
                    Reply(player, $"{ChatColors.Red}Pick mode needs at least 4 maps in the pool.");
                }
                break;
            }
            default:
                Reply(player, "Usage: .veto | .veto list | .veto add <map> [workshopid] | .veto remove <map> | .veto mode ban|pick");
                break;
        }
    }

    private void VetoAdd(CCSPlayerController player, string[] args)
    {
        if (args.Length == 0)
        {
            Reply(player, "Usage: .veto add <map> or .veto add <map> <workshopid>");
            return;
        }

        MapEntry? entry;
        if (args.Length > 1 && args[1].All(char.IsDigit))
        {
            entry = new MapEntry(args[0], args[1]);
        }
        else
        {
            entry = MapEntry.Parse(args[0]);
            if (entry is { IsWorkshop: false })
            {
                string name = entry.Name;
                if (!Server.IsMapValid(name) && !name.Contains('_') && Server.IsMapValid($"de_{name}"))
                {
                    name = $"de_{name}";
                }
                if (!Server.IsMapValid(name))
                {
                    Reply(player, $"{ChatColors.Red}Map '{entry.Name}' not found on the server. For workshop maps use .veto add <name> <workshopid>");
                    return;
                }
                entry = new MapEntry(name, null);
            }
        }
        if (entry == null) return;

        var pool = GetMapPool();
        if (pool.Any(m => m.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Reply(player, $"{entry.Name} is already in the pool.");
            return;
        }

        pool.Add(entry);
        SetMapPool(pool);
        PrintAll($"Added {Hl(entry.Name)} to the veto pool ({pool.Count} maps){UntilRestart}.");
    }

    private void VetoRemove(CCSPlayerController player, string[] args)
    {
        if (args.Length == 0)
        {
            Reply(player, "Usage: .veto remove <map>");
            return;
        }

        var pool = GetMapPool();
        var map = FindMap(pool, args[0]);
        if (map == null)
        {
            Reply(player, $"'{args[0]}' is not in the pool.");
            return;
        }
        if (pool.Count <= 2)
        {
            Reply(player, $"{ChatColors.Red}The pool needs at least 2 maps.");
            return;
        }

        pool.Remove(map);
        SetMapPool(pool);
        PrintAll($"Removed {Hl(map.Name)} from the veto pool ({pool.Count} maps){UntilRestart}.");
        if (IsPickMode && pool.Count < 4)
        {
            Reply(player, $"{ChatColors.Red}Pick mode needs at least 4 maps; ban mode will be used until more are added.");
        }
    }
}
