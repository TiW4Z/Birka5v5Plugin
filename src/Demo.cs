using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    private bool demoRecording;

    // Like MatchZy: tv_record with a path relative to the engine's write folder. With Metamod installed
    // that folder is csgo/addons/metamod, so demos end up in csgo/addons/metamod/<birka_demo_path>.
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

        if (delay <= 0)
        {
            Server.ExecuteCommand("tv_stoprecord");
            return;
        }
        AddTimer(delay, () => Server.ExecuteCommand("tv_stoprecord"));
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
