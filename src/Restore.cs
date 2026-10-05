using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    // Valve writes csgo/<prefix>_roundNN.txt at the start of each round (mp_backup_round_auto 1),
    // where NN is the number of rounds already played. So round X (1-based) is file round{X-1}.
    private void CmdRestore(CCSPlayerController? player, string[] args)
    {
        if (!RequireAdmin(player)) return;

        if (phase != MatchPhase.Live)
        {
            Reply(player, "Restore is only available during a live match.");
            return;
        }

        var available = GetBackupRounds();
        int currentRound = (GetGameRules()?.TotalRoundsPlayed ?? 0) + 1;

        if (args.Length == 0 || !int.TryParse(args[0], out int round) || round < 1)
        {
            Reply(player, $"Usage: .restore <round>. Current round is {currentRound}. Available: {FormatRounds(available)}");
            return;
        }

        string fileName = $"{BackupPrefix}_round{round - 1:D2}.txt";
        if (!available.Contains(round))
        {
            Reply(player, $"{ChatColors.Red}No backup for round {round}.{ChatColors.Default} Available: {FormatRounds(available)}");
            return;
        }

        Server.ExecuteCommand($"mp_backup_restore_load_file {fileName}");

        // Restoring always pauses; both teams have to .unpause (or an admin .forceunpause).
        Server.ExecuteCommand("mp_pause_match");
        isPaused = true;
        unpauseT = false;
        unpauseCT = false;

        PrintAll($"Admin {Hl(player?.PlayerName ?? "Console")} restored the match to the start of {Hl($"round {round}")}.");
        PrintAll($"The match is paused. Both teams must type {Hl(".unpause")} to continue.");
        StartPauseReminder();
    }

    /// <summary>1-based round numbers that have a backup file for the current match.</summary>
    private List<int> GetBackupRounds()
    {
        var rounds = new List<int>();
        if (matchId.Length == 0) return rounds;

        string dir = Path.Combine(Server.GameDirectory, "csgo");
        var regex = new Regex($@"^{Regex.Escape(BackupPrefix)}_round(\d+)\.txt$", RegexOptions.IgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, $"{BackupPrefix}_round*.txt"))
            {
                var match = regex.Match(Path.GetFileName(path));
                if (match.Success) rounds.Add(int.Parse(match.Groups[1].Value) + 1);
            }
        }
        catch (IOException)
        {
        }
        rounds.Sort();
        return rounds;
    }

    private static string FormatRounds(List<int> rounds)
    {
        if (rounds.Count == 0) return "none";
        return rounds.Count <= 2 ? string.Join(", ", rounds) : $"{rounds[0]}-{rounds[^1]}";
    }
}
