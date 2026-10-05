using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    private const int GamePhaseHalftime = 4;
    private const int GamePhasePostGame = 5;

    // True from the moment a pause is requested (it may still be waiting for the next freezetime).
    private bool isPaused;
    private bool unpauseT;
    private bool unpauseCT;
    private Timer? pauseReminderTimer;

    private void ResetPauseState()
    {
        isPaused = false;
        unpauseT = false;
        unpauseCT = false;
        pauseReminderTimer?.Kill();
        pauseReminderTimer = null;
    }

    private void CmdPause(CCSPlayerController player)
    {
        if (!PauseEnabled.Value)
        {
            Reply(player, "Pausing is disabled.");
            return;
        }
        if (!IsOnTeam(player)) return;
        if (!CanPause(player)) return;

        PauseMatch($"{TeamName(player.TeamNum)} ({player.PlayerName})");
    }

    private void CmdForcePause(CCSPlayerController? player)
    {
        if (!RequireAdmin(player)) return;
        if (!CanPause(player)) return;
        PauseMatch($"Admin {Hl(player?.PlayerName ?? "Console")}");
    }

    private bool CanPause(CCSPlayerController? player)
    {
        if (phase != MatchPhase.Live)
        {
            Reply(player, "You can only pause during a live match.");
            return false;
        }
        if (isPaused)
        {
            Reply(player, "The match is already paused.");
            return false;
        }
        var rules = GetGameRules();
        if (rules != null && (rules.GamePhase == GamePhaseHalftime || rules.GamePhase == GamePhasePostGame))
        {
            Reply(player, "You can't pause during halftime or after the match.");
            return false;
        }
        return true;
    }

    private void PauseMatch(string byWho)
    {
        // CS2 applies mp_pause_match immediately in freezetime, otherwise at the start of the next freezetime.
        Server.ExecuteCommand("mp_pause_match");
        isPaused = true;
        unpauseT = false;
        unpauseCT = false;

        bool inFreezetime = GetGameRules()?.FreezePeriod ?? false;
        if (inFreezetime)
        {
            PrintAll($"{byWho} {ChatColors.Red}paused the match.");
        }
        else
        {
            PrintAll($"{byWho} {ChatColors.Red}called a pause.{ChatColors.Default} The match pauses at the start of the next round.");
        }
        PrintAll($"Both teams must type {Hl(".unpause")} to continue.");
        StartPauseReminder();
    }

    private void CmdUnpause(CCSPlayerController player)
    {
        if (phase != MatchPhase.Live || !isPaused)
        {
            Reply(player, "The match is not paused.");
            return;
        }

        if (player.TeamNum == TeamT) unpauseT = true;
        else if (player.TeamNum == TeamCT) unpauseCT = true;
        else return;

        if (unpauseT && unpauseCT)
        {
            PrintAll($"{ChatColors.Lime}Both teams are ready, unpausing!");
            UnpauseMatch();
            return;
        }

        int waitingFor = unpauseT ? TeamCT : TeamT;
        PrintAll($"{TeamName(player.TeamNum)} want to unpause. Waiting for {TeamName(waitingFor)} to type {Hl(".unpause")}.");
    }

    private void CmdForceUnpause(CCSPlayerController? player)
    {
        if (!RequireAdmin(player)) return;
        if (!isPaused)
        {
            Reply(player, "The match is not paused.");
            return;
        }
        PrintAll($"Admin {Hl(player?.PlayerName ?? "Console")} unpaused the match.");
        UnpauseMatch();
    }

    private void UnpauseMatch()
    {
        Server.ExecuteCommand("mp_unpause_match");
        ResetPauseState();
    }

    private void StartPauseReminder()
    {
        pauseReminderTimer?.Kill();
        pauseReminderTimer = AddPhaseTimer(Math.Max(5, ReminderInterval.Value), () =>
        {
            if (!isPaused) return;
            var missing = new List<string>();
            if (!unpauseT) missing.Add(TeamName(TeamT));
            if (!unpauseCT) missing.Add(TeamName(TeamCT));
            PrintAll($"Match is paused. Waiting for {string.Join(" and ", missing)} to type {Hl(".unpause")}.");
        }, repeat: true);
    }
}
