using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    private bool demoRecording;
    private string activeDemoEnginePath = "";

    // tv_record writes relative to the engine's write folder (csgo/addons/metamod with Metamod installed);
    // finished demos are moved to csgo/<birka_demo_path> so they are all in one place.
    private string EngineDemoDir => Path.Combine(EngineWriteDirectory(), GetDemoFolder()).TrimEnd('/', '\\');
    private string FinalDemoDir => Path.Combine(Server.GameDirectory, "csgo", GetDemoFolder()).TrimEnd('/', '\\');

    private void StartDemo()
    {
        if (!DemoEnabled.Value || demoRecording) return;

        if (GetConVarNumber("tv_enable") < 1)
        {
            Logger.LogWarning("tv_enable is 0, demo recording needs GOTV enabled in server.cfg");
            PrintAll("Demo recording is on but GOTV is disabled (tv_enable 0). No demo will be recorded.");
            return;
        }

        // GOTV only joins if tv_enable was on when the map loaded; tv_record fails without it.
        bool gotvConnected = IsGotvConnected();
        if (!gotvConnected)
        {
            Logger.LogWarning("GOTV bot is not on the server. tv_enable must be 1 before the map loads (server.cfg or launch options, then change map)");
        }

        string folder = GetDemoFolder();
        string map = string.Concat(Server.MapName.Split(Path.GetInvalidFileNameChars().Append('/').ToArray()));
        string file = $"{folder}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{map}.dem";
        string enginePath = Path.Combine(EngineWriteDirectory(), file);

        // The engine may not create missing folders for tv_record.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(enginePath)!);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not create demo folder for {Path}", enginePath);
        }

        Logger.LogInformation("Starting demo: tv_record \"{File}\" -> {Path}", file, enginePath);
        Server.ExecuteCommand($"tv_record \"{file}\"");
        demoRecording = true;
        activeDemoEnginePath = enginePath;

        AddTimer(5.0f, () => VerifyDemoStarted(enginePath, gotvConnected), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void VerifyDemoStarted(string enginePath, bool gotvConnected)
    {
        if (!demoRecording) return;

        if (File.Exists(enginePath))
        {
            Logger.LogInformation("Demo is recording: {Path}", enginePath);
            return;
        }

        string reason = gotvConnected
            ? "check the server console for the tv_record error"
            : "the GOTV bot is not on the server (tv_enable must be 1 before the map loads)";
        Logger.LogError("Demo file was not created at {Path}: {Reason}", enginePath, reason);
        PrintAll($"Demo recording did not start: {reason}.");
    }

    private static bool IsGotvConnected() =>
        Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Any(p => p.IsValid && p.IsHLTV);

    private void StopDemo(float delay)
    {
        if (!demoRecording) return;
        demoRecording = false;

        string finished = activeDemoEnginePath;
        void Stop()
        {
            Server.ExecuteCommand("tv_stoprecord");
            AddTimer(10.0f, () => MoveDemosToFinalFolder(finished));
        }

        if (delay <= 0) Stop();
        else AddTimer(delay, Stop);
    }

    /// <summary>A map change ends the recording without tv_stoprecord.</summary>
    private void OnDemoEndedByMapChange()
    {
        string? ended = demoRecording ? activeDemoEnginePath : null;
        demoRecording = false;
        AddTimer(10.0f, () => MoveDemosToFinalFolder(ended));
    }

    /// <summary>
    /// Moves finished demos from the engine's write folder to csgo/&lt;birka_demo_path&gt;. Also picks up demos left
    /// behind earlier. Files written to in the last 2 minutes are skipped (they may still be recording),
    /// except <paramref name="finishedFile"/>, which is known to be closed.
    /// </summary>
    private void MoveDemosToFinalFolder(string? finishedFile = null)
    {
        string engineDir = EngineDemoDir;
        string finalDir = FinalDemoDir;
        try
        {
            if (!Directory.Exists(engineDir) || SameDirectory(engineDir, finalDir)) return;
            Directory.CreateDirectory(finalDir);

            foreach (var file in Directory.EnumerateFiles(engineDir, "*.dem").ToList())
            {
                bool isFinished = finishedFile != null && SamePath(file, finishedFile);
                if (demoRecording && SamePath(file, activeDemoEnginePath)) continue;
                if (!isFinished && File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddMinutes(-2)) continue;

                try
                {
                    string target = UniqueFilePath(finalDir, Path.GetFileName(file));
                    File.Move(file, target);
                    Logger.LogInformation("Demo saved to {Path}", target);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Could not move demo {File} to {Folder}", file, finalDir);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not move demos from {From} to {To}", engineDir, finalDir);
        }
    }

    /// <summary>True if both point at the same folder, also when one is a link to the other (v1.0.5 created one).</summary>
    private static bool SameDirectory(string a, string b)
    {
        var info = new DirectoryInfo(a);
        string resolved = info.LinkTarget != null ? info.ResolveLinkTarget(true)?.FullName ?? a : a;
        return SamePath(resolved.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'));
    }

    private static string UniqueFilePath(string folder, string fileName)
    {
        string path = Path.Combine(folder, fileName);
        string name = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name}_{i}{ext}");
        return path;
    }

    /// <summary>birka_demo_path, normalised to a safe relative folder ending in '/'.</summary>
    private string GetDemoFolder()
    {
        string folder = Clean(DemoPath.Value).Replace('\\', '/').Trim('/');
        if (folder.Contains("..") || folder.Contains(':')) folder = "birka_demos";
        return folder.Length == 0 ? "" : folder + "/";
    }

    private static int GetTvDelay()
    {
        if (GetConVarNumber("tv_enable") < 1) return 0;
        int delay = (int)GetConVarNumber("tv_delay");
        if (GetConVarNumber("tv_enable1") >= 1)
        {
            delay = Math.Max(delay, (int)GetConVarNumber("tv_delay1"));
        }
        return delay;
    }
}
