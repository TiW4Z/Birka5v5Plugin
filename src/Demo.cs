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

        string map = string.Concat(Server.MapName.Split(Path.GetInvalidFileNameChars().Append('/').ToArray()));
        string file = $"{folder}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{map}.dem";

        Server.ExecuteCommand($"tv_record \"{file}\"");
        demoRecording = true;
        Logger.LogInformation("Recording demo to csgo/{File}", file);
    }

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
