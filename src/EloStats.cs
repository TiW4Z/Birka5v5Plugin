using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    private class RoundStats
    {
        public readonly Dictionary<ulong, int> Damage = new();
        public readonly Dictionary<ulong, int> Kills = new();
        public readonly Dictionary<ulong, int> Team = new(); // 0 = team A, 1 = team B
    }

    // Keyed by round index (rounds already played at round start). A replayed round simply overwrites its entry.
    private readonly Dictionary<int, RoundStats> eloRounds = new();
    private readonly Dictionary<ulong, string> eloNames = new();
    private HashSet<ulong> rosterA = new();
    private HashSet<ulong> rosterB = new();
    private int currentEloRound = -1;
    private bool eloTracking;

    /// <summary>Called when the match goes live: team A is whoever is on CT now (sides may still swap).</summary>
    private void StartEloTracking()
    {
        eloRounds.Clear();
        eloNames.Clear();
        currentEloRound = -1;
        rosterA = TeamPlayers(TeamCT).Select(p => p.SteamID).ToHashSet();
        rosterB = TeamPlayers(TeamT).Select(p => p.SteamID).ToHashSet();
        eloTracking = true;
    }

    private void StopEloTracking()
    {
        eloTracking = false;
        eloRounds.Clear();
    }

    private bool EloTrackingActive => eloTracking && phase == MatchPhase.Live;

    /// <summary>Side (CT/T) team A is on right now, decided by where most of its roster is.</summary>
    private int SideOfTeamA()
    {
        int SideOf(HashSet<ulong> roster)
        {
            var online = TeamPlayers().Where(p => roster.Contains(p.SteamID)).ToList();
            int ct = online.Count(p => p.TeamNum == TeamCT);
            int t = online.Count(p => p.TeamNum == TeamT);
            return ct == t ? 0 : ct > t ? TeamCT : TeamT;
        }

        int a = SideOf(rosterA);
        if (a != 0) return a;
        int b = SideOf(rosterB);
        return b != 0 ? OtherTeam(b) : 0;
    }

    private void EloRoundStart()
    {
        if (!EloTrackingActive) return;
        currentEloRound = GetGameRules()?.TotalRoundsPlayed ?? currentEloRound + 1;
        eloRounds[currentEloRound] = new RoundStats();
    }

    private void EloRoundEnd()
    {
        if (!EloTrackingActive || !eloRounds.TryGetValue(currentEloRound, out var round)) return;

        int sideA = SideOfTeamA();
        if (sideA == 0) return;
        foreach (var player in TeamPlayers())
        {
            if (player.SteamID == 0) continue;
            round.Team[player.SteamID] = player.TeamNum == sideA ? 0 : 1;
            eloNames[player.SteamID] = player.PlayerName;
        }
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (attacker == null || victim == null || !attacker.IsValid || !victim.IsValid) return HookResult.Continue;
        if (attacker == victim || attacker.TeamNum == victim.TeamNum) return HookResult.Continue;

        // Shared with the damage report; only counts damage up to the victim's remaining health.
        int damage = RecordHit(attacker, victim, @event.DmgHealth);

        if (!EloTrackingActive || !eloRounds.TryGetValue(currentEloRound, out var round)) return HookResult.Continue;
        if (!attacker.IsBot && attacker.SteamID != 0)
        {
            round.Damage[attacker.SteamID] = round.Damage.GetValueOrDefault(attacker.SteamID) + damage;
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (!EloTrackingActive || !eloRounds.TryGetValue(currentEloRound, out var round)) return HookResult.Continue;

        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (attacker == null || victim == null || !attacker.IsValid || !victim.IsValid) return HookResult.Continue;
        if (attacker == victim || attacker.TeamNum == victim.TeamNum || attacker.IsBot || attacker.SteamID == 0) return HookResult.Continue;

        round.Kills[attacker.SteamID] = round.Kills.GetValueOrDefault(attacker.SteamID) + 1;
        return HookResult.Continue;
    }

    /// <summary>After .restore: forget the rounds that will be replayed.</summary>
    private void EloDropRoundsFrom(int roundIndex)
    {
        foreach (var key in eloRounds.Keys.Where(k => k >= roundIndex).ToList())
        {
            eloRounds.Remove(key);
        }
    }

    /// <summary>Called at match end while still Live. Rates the match if it qualifies.</summary>
    private void ProcessEloMatchEnd()
    {
        if (!EloTrackingActive) return;
        eloTracking = false;

        if (!EloEnabled.Value) return;

        // Ignore stale entries past the final round (left over from before a restore).
        int totalPlayed = GetGameRules()?.TotalRoundsPlayed ?? int.MaxValue;
        var rounds = eloRounds.Where(r => r.Key < totalPlayed && r.Value.Team.Count > 0).Select(r => r.Value).ToList();
        if (rounds.Count == 0)
        {
            Logger.LogWarning("Match not rated: no round data");
            return;
        }

        int sideA = SideOfTeamA();
        if (sideA == 0)
        {
            PrintAll("Match not rated: couldn't tell the teams apart.");
            return;
        }
        var (ct, t) = GetTeamScores();
        int scoreA = sideA == TeamCT ? ct : t;
        int scoreB = sideA == TeamCT ? t : ct;

        // Each player counts for the team they played most rounds for.
        double minShare = Math.Clamp(EloMinRoundShare.Value, 0, 1);
        var players = new List<MatchPlayer>();
        foreach (var steamId in rounds.SelectMany(r => r.Team.Keys).Distinct())
        {
            int roundsA = rounds.Count(r => r.Team.TryGetValue(steamId, out int team) && team == 0);
            int roundsB = rounds.Count(r => r.Team.TryGetValue(steamId, out int team) && team == 1);
            int played = roundsA + roundsB;
            if (played < minShare * rounds.Count) continue;

            players.Add(new MatchPlayer(
                steamId,
                eloNames.GetValueOrDefault(steamId, steamId.ToString()),
                roundsA >= roundsB ? 0 : 1,
                played,
                rounds.Sum(r => r.Damage.GetValueOrDefault(steamId)),
                rounds.Sum(r => r.Kills.GetValueOrDefault(steamId))));
        }

        int minTeam = Math.Max(1, EloMinTeamSize.Value);
        if (players.Count(p => p.Team == 0) < minTeam || players.Count(p => p.Team == 1) < minTeam)
        {
            PrintAll($"Match not rated: each team needs at least {minTeam} player(s) who played most of the match.");
            return;
        }

        ApplyMatchRatings(players, scoreA, scoreB);
    }
}
