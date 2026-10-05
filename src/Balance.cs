using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    private const int MaxBalancePlayers = 16;

    // SteamID -> side (CT/T) chosen by the last balance; enforced on the match map until warmup resets it.
    private readonly Dictionary<ulong, int> balancedTeams = new();

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

        balancedTeams.Clear();
        for (int i = 0; i < n; i++)
        {
            var player = players[i];
            int target = InA(i) ? sideA : sideB;
            balancedTeams[player.SteamID] = target;
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

    /// <summary>On the match map: put players back on the side the balance gave them.</summary>
    private void ApplyBalancedTeams()
    {
        if (phase != MatchPhase.WaitingForMatchReady || balancedTeams.Count == 0) return;
        foreach (var player in HumanPlayers())
        {
            if (balancedTeams.TryGetValue(player.SteamID, out int side) && player.TeamNum != side)
            {
                player.ChangeTeam((CsTeam)side);
            }
        }
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        if (phase == MatchPhase.WaitingForMatchReady && balancedTeams.Count > 0)
        {
            AddTimer(1.0f, ApplyBalancedTeams);
        }
        return HookResult.Continue;
    }

    /// <summary>jointeam during WaitingForMatchReady: keep balanced players on their side.</summary>
    private bool BlocksBalancedJoin(CCSPlayerController player, CommandInfo info)
    {
        if (phase != MatchPhase.WaitingForMatchReady) return false;
        if (!balancedTeams.TryGetValue(player.SteamID, out int side)) return false;
        if (!int.TryParse(info.GetArg(1), out int requested)) return false;
        if (requested != TeamT && requested != TeamCT) return false; // spectating is fine
        if (requested == side) return false;

        Reply(player, $"Teams are balanced for this match, you play on {TeamName(side)}.");
        return true;
    }

    private void CmdBalance(CCSPlayerController player, string[] args)
    {
        if (!RequireAdmin(player)) return;

        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (sub is "on" or "off")
        {
            bool enabled = sub == "on";
            EloAutoBalance.Value = enabled;
            SaveConfigValue("birka_elo_autobalance", enabled ? "1" : "0", quote: false);
            PrintAll($"Auto team balancing {(enabled ? $"{ChatColors.Lime}enabled" : $"{ChatColors.Red}disabled")}{ChatColors.Default}.");
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
