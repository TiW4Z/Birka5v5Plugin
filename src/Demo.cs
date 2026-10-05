using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    private bool demoRecording;

    // Where the engine writes the demo (its first search path, csgo/addons/metamod with Metamod)
    // and where it is moved once finished (csgo/<birka_demo_path>).
    private string activeDemoEnginePath = "";
    private string activeDemoFinalPath = "";

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
        if (GetConVarNumber("tv_autorecord") >= 1)
        {
            Logger.LogWarning("tv_autorecord is 1: GOTV records its own demos and tv_record may be refused");
        }

        string folder = GetDemoFolder();
        string map = string.Concat(Server.MapName.Split(Path.GetInvalidFileNameChars().Append('/').ToArray()));
        string file = $"{folder}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{map}.dem";

        activeDemoFinalPath = Path.Combine(Server.GameDirectory, "csgo", file);
        activeDemoEnginePath = PrepareDemoFolders(folder)
            ? activeDemoFinalPath
            : Path.Combine(EngineWriteDirectory(), file);

        Logger.LogInformation("Starting demo: tv_record \"{File}\" (engine writes to {Path})", file, activeDemoEnginePath);
        Server.ExecuteCommand($"tv_record \"{file}\"");
        demoRecording = true;

        string enginePath = activeDemoEnginePath;
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

        string enginePath = activeDemoEnginePath;
        string finalPath = activeDemoFinalPath;
        void Stop()
        {
            Server.ExecuteCommand("tv_stoprecord");
            AddTimer(5.0f, () => MoveFinishedDemo(enginePath, finalPath));
        }

        if (delay <= 0) Stop();
        else AddTimer(delay, Stop);
    }

    /// <summary>
    /// Creates the demo folders. The engine does not create folders for tv_record, and it writes into its
    /// first search path (csgo/addons/metamod), so that folder is made a symlink to csgo/&lt;birka_demo_path&gt;.
    /// Returns true if the engine's writes land directly in the final folder.
    /// </summary>
    private bool PrepareDemoFolders(string folder)
    {
        string finalDir = Path.Combine(Server.GameDirectory, "csgo", folder).TrimEnd('/', '\\');
        string engineDir = Path.Combine(EngineWriteDirectory(), folder).TrimEnd('/', '\\');

        try
        {
            Directory.CreateDirectory(finalDir);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not create demo folder {Folder}", finalDir);
        }

        if (SamePath(engineDir, finalDir)) return true;
        if (folder.Length == 0) return false; // can't link the engine's root folder; move afterwards

        try
        {
            var existing = new DirectoryInfo(engineDir);
            if (existing.Exists && existing.LinkTarget != null)
            {
                string? linkTarget = existing.ResolveLinkTarget(true)?.FullName;
                if (linkTarget != null && SamePath(linkTarget, finalDir)) return true;
                existing.Delete(); // link to an old location
            }
            else if (existing.Exists)
            {
                // A real folder from older versions: move its demos over, then replace it with the link.
                foreach (var demo in existing.EnumerateFiles())
                {
                    string target = Path.Combine(finalDir, demo.Name);
                    if (!File.Exists(target)) demo.MoveTo(target);
                }
                if (existing.EnumerateFileSystemInfos().Any())
                {
                    Logger.LogWarning("{Folder} is not empty, recording there and moving demos afterwards", engineDir);
                    return false;
                }
                existing.Delete();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(engineDir)!);
            Directory.CreateSymbolicLink(engineDir, finalDir);
            Logger.LogInformation("Linked {Link} -> {Target} so demos are written there directly", engineDir, finalDir);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not link {Link} to {Target}, demos will be moved after recording", engineDir, finalDir);
            CreateFolder(engineDir);
            return false;
        }
    }

    private bool CreateFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not create demo folder {Folder}", dir);
            return false;
        }
    }

    /// <summary>A map change ends the recording without tv_stoprecord.</summary>
    private void OnDemoEndedByMapChange()
    {
        if (!demoRecording) return;
        demoRecording = false;

        string enginePath = activeDemoEnginePath;
        string finalPath = activeDemoFinalPath;
        AddTimer(5.0f, () => MoveFinishedDemo(enginePath, finalPath));
    }

    /// <summary>Moves a finished demo from the engine's write folder to csgo/&lt;birka_demo_path&gt;.</summary>
    private void MoveFinishedDemo(string enginePath, string finalPath)
    {
        if (enginePath.Length == 0 || SamePath(enginePath, finalPath)) return;
        try
        {
            if (!File.Exists(enginePath))
            {
                Logger.LogWarning("Finished demo not found at {Path}", enginePath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            File.Move(enginePath, finalPath, overwrite: false);
            Logger.LogInformation("Demo saved to {Path}", finalPath);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not move demo {Source} to {Target}; it is still at the source", enginePath, finalPath);
        }
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
