using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

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

        // Give GOTV time to flush the broadcast delay before stopping the demo (same timing as Get5/MatchZy).
        int tvDelay = GetTvDelay();
        int restartDelay = (int)GetConVarNumber("mp_match_restart_delay");
        int requiredDelay = tvDelay + 15 + (tvDelay > 0 ? 10 : 0);
        if (requiredDelay > restartDelay)
        {
            Server.ExecuteCommand($"mp_match_restart_delay {requiredDelay}");
            restartDelay = requiredDelay;
        }

        StopDemo(tvDelay + 14.5f);

        // Back to warmup just after the engine has restarted the match.
        AddPhaseTimer(restartDelay + 1, EnterWarmup);
        return HookResult.Continue;
    }
}
