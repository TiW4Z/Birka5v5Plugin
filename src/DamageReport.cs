using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    // Per-round hits, keyed by player slot so bots work too (reset every round, so slot reuse is harmless).
    private readonly Dictionary<int, int> roundVictimHealth = new();
    private readonly Dictionary<(int Attacker, int Victim), (int Damage, int Hits)> roundHits = new();

    // Set once freezetime ends, so restarts (going live, .restore) don't print an empty report.
    private bool roundPlayed;

    private void ResetRoundDamage()
    {
        roundVictimHealth.Clear();
        roundHits.Clear();
        roundPlayed = false;
    }

    /// <summary>Records a hit and returns the damage that counts (capped by the victim's remaining health).</summary>
    private int RecordHit(CCSPlayerController attacker, CCSPlayerController victim, int rawDamage)
    {
        int remaining = roundVictimHealth.GetValueOrDefault(victim.Slot, 100);
        int damage = Math.Clamp(rawDamage, 0, remaining);
        roundVictimHealth[victim.Slot] = remaining - damage;

        var key = (attacker.Slot, victim.Slot);
        var (total, hits) = roundHits.GetValueOrDefault(key);
        roundHits[key] = (total + damage, hits + 1);
        return damage;
    }

    /// <summary>After a live round: each player sees damage given to / taken from every opponent.</summary>
    private void PrintDamageReport()
    {
        if (phase != MatchPhase.Live || !DamageReport.Value || !roundPlayed) return;

        var players = TeamPlayers().ToList();
        foreach (var player in players.Where(p => !p.IsBot))
        {
            var opponents = TeamPlayers(OtherTeam(player.TeamNum)).ToList();
            if (opponents.Count == 0) continue;

            player.PrintToChat($"{Prefix} Damage report:");
            foreach (var opponent in opponents)
            {
                var (given, givenHits) = roundHits.GetValueOrDefault((player.Slot, opponent.Slot));
                var (taken, takenHits) = roundHits.GetValueOrDefault((opponent.Slot, player.Slot));
                int hp = opponent.PawnIsAlive ? Math.Max(0, opponent.PlayerPawn.Value?.Health ?? 0) : 0;

                string to = given > 0 ? $"{ChatColors.Lime}{given}" : $"{ChatColors.Grey}0";
                string from = taken > 0 ? $"{ChatColors.Red}{taken}" : $"{ChatColors.Grey}0";
                player.PrintToChat(
                    $" To: [{to}{ChatColors.Default} / {givenHits} {Hits(givenHits)}] " +
                    $"From: [{from}{ChatColors.Default} / {takenHits} {Hits(takenHits)}] - " +
                    $"{opponent.PlayerName} ({hp} hp)");
            }
        }
    }

    private static string Hits(int count) => count == 1 ? "hit" : "hits";
}
