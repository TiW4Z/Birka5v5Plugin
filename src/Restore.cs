using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    // Valve writes <prefix>_roundNN.txt at the start of each round (mp_backup_round_auto 1),
    // where NN is the number of rounds already played. So round X (1-based) is file round{X-1}.
    // The folder it writes to depends on the server's working directory, so several places are searched,
    // and the file is copied into csgo/ (where mp_backup_restore_load_file reads from) before loading.
    private void CmdRestore(CCSPlayerController? player, string[] args)
    {
        if (!RequireAdmin(player)) return;

        if (phase != MatchPhase.Live)
        {
            Reply(player, "Restore is only available during a live match.");
            return;
        }

        var backups = FindBackupFiles();
        var available = backups.Keys.OrderBy(r => r).ToList();
        int currentRound = (GetGameRules()?.TotalRoundsPlayed ?? 0) + 1;

        if (args.Length == 0 || !int.TryParse(args[0], out int round) || round < 1)
        {
            Reply(player, $"Usage: .restore <round>. Current round is {currentRound}. Available: {FormatRounds(available)}");
            if (available.Count == 0) ReportMissingBackups(player);
            return;
        }

        if (!backups.TryGetValue(round, out string? sourcePath))
        {
            Reply(player, $"{ChatColors.Red}No backup for round {round}.{ChatColors.Default} Available: {FormatRounds(available)}");
            if (available.Count == 0) ReportMissingBackups(player);
            return;
        }

        string fileName = Path.GetFileName(sourcePath);
        string csgoPath = Path.Combine(Server.GameDirectory, "csgo", fileName);
        try
        {
            if (!SamePath(sourcePath, csgoPath)) File.Copy(sourcePath, csgoPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not copy backup {Source} to {Target}", sourcePath, csgoPath);
            Reply(player, $"{ChatColors.Red}Could not prepare the backup file, see server console.");
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

    private List<string> BackupSearchDirectories()
    {
        var dirs = new List<string>
        {
            Path.Combine(Server.GameDirectory, "csgo"),
            Directory.GetCurrentDirectory(),
            Server.GameDirectory,
            Path.Combine(Server.GameDirectory, "csgo", "backup"),
            Path.Combine(Server.GameDirectory, "bin", "linuxsteamrt64"),
            Path.Combine(Server.GameDirectory, "bin", "win64"),
        };

        // If the engine reports the last backup it wrote, search its folder first.
        string last = Clean(ConVar.Find("mp_backup_round_file_last")?.StringValue);
        if (last.Length > 0)
        {
            string? lastDir = Path.GetDirectoryName(Path.IsPathRooted(last) ? last : Path.Combine(Directory.GetCurrentDirectory(), last));
            if (!string.IsNullOrEmpty(lastDir)) dirs.Insert(0, lastDir);
        }

        return dirs.Select(Path.GetFullPath).Distinct().Where(Directory.Exists).ToList();
    }

    /// <summary>1-based round number -> backup file path for the current match.</summary>
    private Dictionary<int, string> FindBackupFiles()
    {
        var result = new Dictionary<int, string>();
        if (matchId.Length == 0) return result;

        // Matches both "<prefix>_round03.txt" and longer patterns that contain "_round03".
        var regex = new Regex($@"^{Regex.Escape(BackupPrefix)}.*_round(\d+).*\.txt$", RegexOptions.IgnoreCase);
        foreach (var dir in BackupSearchDirectories())
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(dir, $"{BackupPrefix}*.txt"))
                {
                    var match = regex.Match(Path.GetFileName(path));
                    if (!match.Success) continue;
                    int round = int.Parse(match.Groups[1].Value) + 1;
                    result.TryAdd(round, path); // first directory wins (csgo/ is searched early)
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return result;
    }

    private void ReportMissingBackups(CCSPlayerController? player)
    {
        var dirs = BackupSearchDirectories();
        string last = Clean(ConVar.Find("mp_backup_round_file_last")?.StringValue);
        Logger.LogWarning("No round backups found for prefix {Prefix}. mp_backup_round_file_last='{Last}'. Searched: {Dirs}",
            BackupPrefix, last, string.Join(" | ", dirs));
        Reply(player, $"No backup files found for {BackupPrefix}. Details were written to the server console.");
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string FormatRounds(List<int> rounds)
    {
        if (rounds.Count == 0) return "none";
        return rounds.Count <= 2 ? string.Join(", ", rounds) : $"{rounds[0]}-{rounds[^1]}";
    }
}
