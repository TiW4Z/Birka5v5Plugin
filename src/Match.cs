using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    // Used for the round backup file prefix and the demo name.
    private string matchId = "";

    private string BackupPrefix => $"birka_{matchId}";

    private void GoLive()
    {
        SetPhase(MatchPhase.Live);
        readyPlayers.Clear();
        ResetPauseState();
        matchId = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        ExecPhaseCfg("live.cfg");
        Server.ExecuteCommand($"mp_backup_round_file {BackupPrefix}");
        Server.ExecuteCommand("mp_backup_round_file_pattern \"%prefix%_round%round%.txt\"");
        Server.ExecuteCommand("mp_backup_round_auto 1");
        // Restart on the same map when the match ends, instead of going to the next map in the mapcycle.
        Server.ExecuteCommand("mp_match_end_restart 1");
        Server.ExecuteCommand("mp_warmup_end");
        Server.ExecuteCommand("mp_restartgame 1");

        StartDemo();
        StartEloTracking();

        AddPhaseTimer(2.0f, () =>
        {
            for (int i = 0; i < 3; i++) PrintAll($"{ChatColors.Lime}LIVE! LIVE! LIVE!");
            PrintAll($"GL HF! {Hl(".pause")} pauses at the next freezetime.");
        });
    }

    private HookResult OnMatchEnd(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        if (phase != MatchPhase.Live) return HookResult.Continue;

        var (ct, t) = GetTeamScores();
        PrintAll($"Match over: {TeamName(TeamCT)} {Hl(ct.ToString())} - {Hl(t.ToString())} {TeamName(TeamT)}");

        // Needs the Live phase, so before switching to PostMatch.
        ProcessEloMatchEnd();

        SetPhase(MatchPhase.PostMatch);
        ResetPauseState();

        // Finishing a demo with tv_stoprecord froze the server for ~1 s ("Long frame") and dropped players with
        // "Overflow". So the demo is not stopped here: the map reload below ends the recording while everyone is
        // loading anyway. Waiting tv_delay + 20 s lets GOTV's delayed broadcast reach the end of the match first.
        // The engine's own end-of-match restart is pushed far enough out that it never happens.
        int tvDelay = GetTvDelay();
        int reloadAt = tvDelay + 20;
        Server.ExecuteCommand($"mp_match_restart_delay {reloadAt + 1}");
        PrintAll($"Back to warmup in {reloadAt} seconds (the map reloads).");

        AddPhaseTimer(reloadAt, ReloadMapAfterMatch);
        return HookResult.Continue;
    }

    /// <summary>Reloads the current map; OnMapStart then puts the server back into warmup.</summary>
    private void ReloadMapAfterMatch()
    {
        // Workshop maps need their id; the veto's entry has it when the match map came from the veto.
        MapEntry? current = matchMap != null && (matchMap.IsWorkshop || matchMap.Name.Equals(Server.MapName, StringComparison.OrdinalIgnoreCase))
            ? matchMap
            : new MapEntry(Server.MapName, null);

        Logger.LogInformation("Reloading {Map} after the match", current);
        if (!ChangeMap(current))
        {
            // Couldn't reload (e.g. unknown workshop id): go back to warmup on the running map
            // (this stops the demo with tv_stoprecord instead).
            Logger.LogWarning("Could not reload {Map}, returning to warmup without a map change", current);
            EnterWarmup();
        }
    }
}
