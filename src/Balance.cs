using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    private const int MaxBalancePlayers = 16;

    // The teams from before the veto (SteamID -> side at that time), restored when the match starts on the match map.
    private readonly Dictionary<ulong, int> matchTeams = new();
    private readonly Dictionary<ulong, string> matchTeamNames = new();

    /// <summary>
    /// Splits the T/CT players into the two most even teams by rating. Picks randomly among splits that are
    /// close to the best one (and not identical to last match) so teammates vary between matches.
    /// </summary>
    private bool BalanceTeams()
    {
        var players = TeamPlayers().Where(p => p.SteamID != 0).ToList();
        if (players.Count < 2)
        {
            return false;
        }
        if (players.Count > MaxBalancePlayers)
        {
            PrintAll($"Too many players to balance ({players.Count}).");
            return false;
        }

        int n = players.Count;
        int[] ratings = players.Select(p => GetRating(p.SteamID)).ToArray();
        int sizeA = n / 2;
        bool even = n % 2 == 0;

        // Every split as a bitmask of the players on side A. With an even count, fix player 0 on A to skip mirrors.
        var splits = new List<(int Mask, double Diff)>();
        for (int mask = 0; mask < 1 << n; mask++)
        {
            if (BitOperations.PopCount((uint)mask) != sizeA) continue;
            if (even && (mask & 1) == 0) continue;
            splits.Add((mask, Math.Abs(AverageRating(ratings, mask, true) - AverageRating(ratings, mask, false))));
        }

        double best = splits.Min(s => s.Diff);
        double tolerance = Math.Max(0, EloBalanceTolerance.Value);
        var candidates = splits.Where(s => s.Diff <= best + tolerance).ToList();

        // Avoid repeating the previous match's teams if there is any other fair option.
        var fresh = candidates.Where(s => !IsSameAsLastTeams(players, s.Mask)).ToList();
        if (fresh.Count > 0) candidates = fresh;

        int chosen = candidates[Random.Shared.Next(candidates.Count)].Mask;
        bool InA(int i) => (chosen & (1 << i)) != 0;

        // Put each group on the side that needs the fewest moves.
        int movesIfACt = players.Where((p, i) => InA(i) ? p.TeamNum != TeamCT : p.TeamNum != TeamT).Count();
        int sideA = movesIfACt <= n - movesIfACt ? TeamCT : TeamT;
        int sideB = OtherTeam(sideA);

        matchTeams.Clear();
        matchTeamNames.Clear();
        for (int i = 0; i < n; i++)
        {
            var player = players[i];
            int target = InA(i) ? sideA : sideB;
            matchTeams[player.SteamID] = target;
            matchTeamNames[player.SteamID] = player.PlayerName;
            if (player.TeamNum != target) player.ChangeTeam((CsTeam)target);
        }

        double avgCt = AverageRating(ratings, chosen, sideA == TeamCT);
        double avgT = AverageRating(ratings, chosen, sideA != TeamCT);
        double ctChance = ExpectedScore(avgCt, avgT);
        string Names(int side) => string.Join(", ", players.Where((p, i) => (InA(i) ? sideA : sideB) == side).Select(p => p.PlayerName));

        PrintAll($"{ChatColors.Lime}Teams balanced by rating:");
        PrintAll($"{TeamName(TeamCT)} avg {Hl($"{avgCt:0}")} ({ctChance:P0}): {Names(TeamCT)}");
        PrintAll($"{TeamName(TeamT)} avg {Hl($"{avgT:0}")} ({1 - ctChance:P0}): {Names(TeamT)}");
        return true;
    }

    private static double AverageRating(int[] ratings, int mask, bool sideA)
    {
        double sum = 0;
        int count = 0;
        for (int i = 0; i < ratings.Length; i++)
        {
            if (((mask & (1 << i)) != 0) != sideA) continue;
            sum += ratings[i];
            count++;
        }
        return count == 0 ? 0 : sum / count;
    }

    /// <summary>True if the split puts exactly last match's teammates together (only when all players played last match).</summary>
    private bool IsSameAsLastTeams(List<CCSPlayerController> players, int mask)
    {
        var lastA = eloData.LastTeamA.ToHashSet();
        var lastB = eloData.LastTeamB.ToHashSet();
        var last = players.Select(p => p.SteamID.ToString())
            .Select(id => lastA.Contains(id) ? 0 : lastB.Contains(id) ? 1 : -1)
            .ToList();
        if (last.Contains(-1)) return false;

        // Same grouping if every player on side A shares one previous team and side B the other.
        var groupA = last.Where((_, i) => (mask & (1 << i)) != 0).Distinct().ToList();
        var groupB = last.Where((_, i) => (mask & (1 << i)) == 0).Distinct().ToList();
        return groupA.Count == 1 && groupB.Count == 1 && groupA[0] != groupB[0];
    }

    // ---- Teams from before the veto ----

    /// <summary>Remembers the current T/CT players as the match teams (used when autobalance is off).</summary>
    private void SnapshotMatchTeams()
    {
        matchTeams.Clear();
        matchTeamNames.Clear();
        foreach (var player in TeamPlayers().Where(p => p.SteamID != 0))
        {
            matchTeams[player.SteamID] = player.TeamNum;
            matchTeamNames[player.SteamID] = player.PlayerName;
        }
    }

    /// <summary>
    /// When the match starts on the match map: put everyone from before the veto back with their teammates.
    /// Which side each group ends up on doesn't matter (the knife round decides), so use the one needing fewer moves.
    /// Players who weren't in the teams before the veto stay where they are.
    /// </summary>
    private void RestoreMatchTeams()
    {
        var players = TeamPlayers().Where(p => matchTeams.ContainsKey(p.SteamID)).ToList();
        if (players.Count == 0) return;

        int movesKeep = players.Count(p => p.TeamNum != matchTeams[p.SteamID]);
        int movesSwap = players.Count(p => p.TeamNum != OtherTeam(matchTeams[p.SteamID]));
        bool swap = movesSwap < movesKeep;

        int moved = 0;
        foreach (var player in players)
        {
            int target = swap ? OtherTeam(matchTeams[player.SteamID]) : matchTeams[player.SteamID];
            if (player.TeamNum == target) continue;
            player.ChangeTeam((CsTeam)target);
            moved++;
        }
        if (moved > 0) PrintAll($"Teams restored from before the veto ({moved} player(s) moved).");
    }

    private Timer? autoStartTimer;
    private int autoStartSeconds;

    /// <summary>Match-team players who are not on T/CT right now.</summary>
    private List<string> MissingMatchPlayers()
    {
        var onTeams = TeamPlayers().Select(p => p.SteamID).ToHashSet();
        return matchTeams.Keys.Where(id => !onTeams.Contains(id))
            .Select(id => matchTeamNames.GetValueOrDefault(id, id.ToString())).ToList();
    }

    /// <summary>On the match map: start by itself (after a countdown) once everyone from before the veto is back on a team.</summary>
    private void CheckEveryoneBack()
    {
        if (phase != MatchPhase.WaitingForMatchReady || matchTeams.Count == 0) return;

        var missing = MissingMatchPlayers();
        if (missing.Count == 0 && autoStartTimer == null)
        {
            autoStartSeconds = 10;
            string next = KnifeEnabled.Value ? "The knife round" : "The match";
            PrintAll($"{ChatColors.Lime}Everyone is back!{ChatColors.Default} {next} starts in {autoStartSeconds} seconds.");
            autoStartTimer = AddPhaseTimer(1.0f, () =>
            {
                autoStartSeconds--;
                if (autoStartSeconds > 0)
                {
                    if (autoStartSeconds <= 3 || autoStartSeconds == 5) PrintAll($"Starting in {autoStartSeconds}...");
                    return;
                }
                autoStartTimer?.Kill();
                autoStartTimer = null;
                if (phase == MatchPhase.WaitingForMatchReady) OnAllReady();
            }, repeat: true);
        }
        else if (missing.Count > 0 && autoStartTimer != null)
        {
            autoStartTimer.Kill();
            autoStartTimer = null;
            PrintAll($"{ChatColors.Red}Start cancelled,{ChatColors.Default} waiting for {Hl(string.Join(", ", missing))}.");
        }
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        // Reconnecting players can be put straight back on their team without a team-change event.
        AddTimer(3.0f, () =>
        {
            if (!player.IsValid) return;
            if (IsOnTeam(player)) OnPlayerRejoinedTeam(player);
            if (IsReadyPhase) CheckReady();
        });
        return HookResult.Continue;
    }

    private void CmdBalance(CCSPlayerController player, string[] args)
    {
        if (!RequireAdmin(player)) return;

        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (sub is "on" or "off")
        {
            bool enabled = sub == "on";
            EloAutoBalance.Value = enabled;
            PrintAll($"Auto team balancing {(enabled ? $"{ChatColors.Lime}enabled" : $"{ChatColors.Red}disabled")}{ChatColors.Default}{UntilRestart}.");
            return;
        }
        if (sub.Length > 0)
        {
            Reply(player, "Usage: .balance | .balance on | .balance off");
            return;
        }

        if (!IsReadyPhase)
        {
            Reply(player, "Teams can only be balanced during warmup.");
            return;
        }
        if (!BalanceTeams()) Reply(player, "Need at least 2 players on teams to balance.");
    }
}
