using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    private bool demoRecording;

    private void StartDemo()
    {
        if (!DemoEnabled.Value || demoRecording) return;

        if (GetConVarNumber("tv_enable") < 1)
        {
            Logger.LogWarning("tv_enable is 0, demo recording needs GOTV enabled in server.cfg");
            PrintAll("Demo recording is on but GOTV is disabled (tv_enable 0). No demo will be recorded.");
            return;
        }

        string folder = GetDemoFolder();
        try
        {
            Directory.CreateDirectory(Path.Combine(Server.GameDirectory, "csgo", folder));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not create demo folder {Folder}, recording to csgo/", folder);
            folder = "";
        }

        // GOTV only joins if tv_enable was on when the map loaded; tv_record fails without it.
        bool gotvConnected = IsGotvConnected();
        if (!gotvConnected)
        {
            Logger.LogWarning("GOTV bot is not on the server. tv_enable must be 1 before the map loads (server.cfg or launch options, then change map)");
        }
        if (GetConVarNumber("tv_autorecord") >= 1)
        {
            Logger.LogWarning("tv_autorecord is 1: GOTV records its own demos (auto*.dem in csgo/) and tv_record may be refused");
        }

        string map = string.Concat(Server.MapName.Split(Path.GetInvalidFileNameChars().Append('/').ToArray()));
        string file = $"{folder}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{map}.dem";
        string fullPath = Path.Combine(Server.GameDirectory, "csgo", file);

        Logger.LogInformation("Starting demo: tv_record \"{File}\" (expected at {Path})", file, fullPath);
        Server.ExecuteCommand($"tv_record \"{file}\"");
        demoRecording = true;

        AddTimer(5.0f, () => VerifyDemoStarted(fullPath, gotvConnected), CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void VerifyDemoStarted(string fullPath, bool gotvConnected)
    {
        if (!demoRecording) return;

        if (File.Exists(fullPath))
        {
            Logger.LogInformation("Demo is recording: {Path}", fullPath);
            return;
        }

        string reason = gotvConnected
            ? "check the server console for the tv_record error"
            : "the GOTV bot is not on the server (tv_enable must be 1 before the map loads)";
        Logger.LogError("Demo file was not created at {Path}: {Reason}", fullPath, reason);
        PrintAll($"Demo recording did not start: {reason}.");
    }

    private static bool IsGotvConnected() =>
        Utilities.FindAllEntitiesByDesignerName<CounterStrikeSharp.API.Core.CCSPlayerController>("cs_player_controller")
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
