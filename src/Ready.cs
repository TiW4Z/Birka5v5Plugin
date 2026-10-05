using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    // SteamID64s survive reconnects; cleared on each phase change.
    private readonly HashSet<ulong> readyPlayers = new();

    private bool IsReadyPhase => phase is MatchPhase.Warmup or MatchPhase.WaitingForMatchReady;

    private int RequiredPlayers => Math.Max(1, PlayersRequired.Value);

    private int ReadyCount() => TeamPlayers().Count(p => readyPlayers.Contains(p.SteamID));

    private void CmdReady(CCSPlayerController player, string[] args)
    {
        if (!IsReadyPhase)
        {
            Reply(player, "Ready is not available right now.");
            return;
        }
        if (!IsOnTeam(player))
        {
            Reply(player, "Join a team first.");
            return;
        }
        if (!readyPlayers.Add(player.SteamID))
        {
            Reply(player, $"You are already ready. ({ReadyCount()}/{RequiredPlayers})");
            return;
        }

        PrintAll($"{ChatColors.Lime}{player.PlayerName}{ChatColors.Default} is ready ({ReadyCount()}/{RequiredPlayers}).");
        CheckReady();
    }

    private void CmdUnready(CCSPlayerController player, string[] args)
    {
        if (!IsReadyPhase) return;
        if (!readyPlayers.Remove(player.SteamID))
        {
            Reply(player, "You are not ready.");
            return;
        }
        PrintAll($"{ChatColors.Red}{player.PlayerName}{ChatColors.Default} is not ready ({ReadyCount()}/{RequiredPlayers}).");
    }

    private void CmdForceStart(CCSPlayerController? player)
    {
        if (!RequireAdmin(player)) return;
        if (!IsReadyPhase)
        {
            Reply(player, "Nothing to start right now.");
            return;
        }
        PrintAll($"Admin {Hl(player?.PlayerName ?? "Console")} force started.");
        OnAllReady();
    }

    private void CheckReady()
    {
        if (!IsReadyPhase) return;

        var onTeams = TeamPlayers().ToList();
        int ready = onTeams.Count(p => readyPlayers.Contains(p.SteamID));

        if (ready < RequiredPlayers) return;
        if (ready < onTeams.Count) return; // everyone on T/CT must be ready
        if (!onTeams.Any(p => p.TeamNum == TeamT) || !onTeams.Any(p => p.TeamNum == TeamCT)) return;

        PrintAll($"{ChatColors.Lime}All players are ready!");
        OnAllReady();
    }

    private void StartReadyReminder()
    {
        AddPhaseTimer(Math.Max(5, ReminderInterval.Value), () =>
        {
            if (!IsReadyPhase) return;

            var onTeams = TeamPlayers().ToList();
            var notReady = onTeams.Where(p => !readyPlayers.Contains(p.SteamID)).Select(p => p.PlayerName).ToList();
            int ready = onTeams.Count - notReady.Count;

            string message = $"Ready {Hl($"{ready}/{RequiredPlayers}")}. Type {Hl(".ready")}.";
            if (notReady.Count > 0)
            {
                message += $" Not ready: {ChatColors.Red}{string.Join(", ", notReady)}";
            }
            else if (onTeams.Count < RequiredPlayers)
            {
                message += $" Waiting for {RequiredPlayers - onTeams.Count} more player(s).";
            }
            PrintAll(message);
        }, repeat: true);
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid && !player.IsBot)
        {
            readyPlayers.Remove(player.SteamID);
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        // Someone moving to spectator may complete the ready check.
        if (IsReadyPhase) AddTimer(0.2f, CheckReady);
        return HookResult.Continue;
    }
}
