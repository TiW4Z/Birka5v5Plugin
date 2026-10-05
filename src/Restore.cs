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
    // The engine writes them to its first "Game" search path (csgo/addons/metamod with Metamod installed),
    // which is searched first, with other likely folders and game/ as fallbacks.
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

        // mp_backup_restore_load_file looks the name up through the engine's search paths,
        // so make sure the file is in the folder the engine reads/writes first.
        string fileName = Path.GetFileName(sourcePath);
        string loadPath = Path.Combine(EngineWriteDirectory(), fileName);
        try
        {
            if (!SamePath(sourcePath, loadPath)) File.Copy(sourcePath, loadPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not copy backup {Source} to {Target}", sourcePath, loadPath);
            Reply(player, $"{ChatColors.Red}Could not prepare the backup file, see server console.");
            return;
        }

        Server.ExecuteCommand($"mp_backup_restore_load_file {fileName}");
        EloDropRoundsFrom(round - 1);

        // Restoring always pauses; both teams have to .unpause (or an admin .forceunpause).
        Server.ExecuteCommand("mp_pause_match");
        ResetPauseState(); // a restore pause replaces any disconnect pause
        isPaused = true;

        PrintAll($"Admin {Hl(player?.PlayerName ?? "Console")} restored the match to the start of {Hl($"round {round}")}.");
        PrintAll($"The match is paused. Both teams must type {Hl(".unpause")} to continue.");
        StartPauseReminder();
    }

    private List<string> BackupSearchDirectories()
    {
        var dirs = new List<string>
        {
            EngineWriteDirectory(),
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

    private static readonly EnumerationOptions RecursiveSearch = new()
    {
        RecurseSubdirectories = true,
        MaxRecursionDepth = 4,
        IgnoreInaccessible = true,
    };

    /// <summary>1-based round number -> backup file path for the current match.</summary>
    private Dictionary<int, string> FindBackupFiles()
    {
        var result = new Dictionary<int, string>();
        if (matchId.Length == 0) return result;

        // Our prefix first; the engine's actual mp_backup_round_file value as a fallback in case ours didn't apply.
        var prefixes = new List<string> { BackupPrefix };
        string enginePrefix = Clean(ConVar.Find("mp_backup_round_file")?.StringValue);
        if (enginePrefix.Length > 0 && !prefixes.Contains(enginePrefix)) prefixes.Add(enginePrefix);

        foreach (var prefix in prefixes)
        {
            // Likely folders first, then the whole game/ folder.
            foreach (var dir in BackupSearchDirectories())
            {
                AddBackupMatches(result, prefix, dir, SearchOption.TopDirectoryOnly);
            }
            if (result.Count == 0)
            {
                AddBackupMatches(result, prefix, Server.GameDirectory, SearchOption.AllDirectories);
            }
            if (result.Count > 0) break;
        }
        return result;
    }

    private static void AddBackupMatches(Dictionary<int, string> result, string prefix, string dir, SearchOption option)
    {
        // Matches both "<prefix>_round03.txt" and longer patterns that contain "_round03".
        var regex = new Regex($@"^{Regex.Escape(prefix)}.*_round(\d+).*\.txt$", RegexOptions.IgnoreCase);
        try
        {
            var files = option == SearchOption.AllDirectories
                ? Directory.EnumerateFiles(dir, $"{prefix}*.txt", RecursiveSearch)
                : Directory.EnumerateFiles(dir, $"{prefix}*.txt");
            foreach (var path in files)
            {
                var match = regex.Match(Path.GetFileName(path));
                if (!match.Success) continue;
                result.TryAdd(int.Parse(match.Groups[1].Value) + 1, path); // first folder wins
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Newest "*round*.txt" files anywhere under game/, for troubleshooting where the engine writes backups.</summary>
    private static List<string> FindRecentRoundFiles(int count)
    {
        try
        {
            return Directory.EnumerateFiles(Server.GameDirectory, "*round*.txt", RecursiveSearch)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(count)
                .Select(f => $"{f.FullName} ({f.LastWriteTime:yyyy-MM-dd HH:mm})")
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    private void ReportMissingBackups(CCSPlayerController? player)
    {
        var dirs = BackupSearchDirectories();
        Logger.LogWarning("No round backups found for prefix {Prefix}. mp_backup_round_file='{File}' mp_backup_round_file_last='{Last}' mp_backup_round_auto={Auto}. Searched: {Dirs} and game/ recursively",
            BackupPrefix,
            Clean(ConVar.Find("mp_backup_round_file")?.StringValue),
            Clean(ConVar.Find("mp_backup_round_file_last")?.StringValue),
            GetConVarNumber("mp_backup_round_auto"),
            string.Join(" | ", dirs));
        foreach (var file in FindRecentRoundFiles(5)) Logger.LogWarning("Recent round file: {File}", file);
        Reply(player, $"No backup files found for {BackupPrefix}. Run birka_status in the server console for details.");
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
