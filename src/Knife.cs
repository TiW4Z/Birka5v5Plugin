using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    // The restart before the knife round fires its own round_end; only count the round once freezetime has ended.
    private bool knifeRoundStarted;
    private int knifeWinner;

    private void StartKnife()
    {
        SetPhase(MatchPhase.Knife);
        readyPlayers.Clear();
        knifeRoundStarted = false;
        knifeWinner = 0;

        ExecPhaseCfg("knife.cfg");
        Server.ExecuteCommand("mp_warmup_end");
        Server.ExecuteCommand("mp_restartgame 1");

        AddPhaseTimer(2.0f, () =>
        {
            for (int i = 0; i < 3; i++) PrintAll($"{ChatColors.Red}KNIFE ROUND!");
        });
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        EloRoundStart();
        if (phase == MatchPhase.Knife) knifeRoundStarted = false;
        return HookResult.Continue;
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        if (phase == MatchPhase.Knife) knifeRoundStarted = true;
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        EloRoundEnd();
        if (phase != MatchPhase.Knife || !knifeRoundStarted) return HookResult.Continue;

        int winner = @event.Winner;
        if (winner != TeamT && winner != TeamCT)
        {
            // Draw / time ran out: the team with more players alive wins, otherwise a coin flip.
            int aliveT = TeamPlayers(TeamT).Count(p => p.PawnIsAlive);
            int aliveCT = TeamPlayers(TeamCT).Count(p => p.PawnIsAlive);
            winner = aliveT > aliveCT ? TeamT
                : aliveCT > aliveT ? TeamCT
                : Random.Shared.Next(2) == 0 ? TeamT : TeamCT;
        }

        SetPhase(MatchPhase.KnifeDecision);
        knifeWinner = winner;

        Server.NextFrame(() =>
        {
            ExecPhaseCfg("warmup.cfg");
            AnnounceKnifeDecision();
        });

        int decisionTime = Math.Max(10, KnifeDecisionTime.Value);
        AddPhaseTimer(decisionTime, () =>
        {
            PrintAll("No side was chosen in time, staying.");
            DecideSides(swap: false);
        });
        AddPhaseTimer(15.0f, AnnounceKnifeDecision, repeat: true);

        return HookResult.Continue;
    }

    private void AnnounceKnifeDecision()
    {
        if (phase != MatchPhase.KnifeDecision) return;
        PrintAll($"{TeamName(knifeWinner)} won the knife round! Type {Hl(".stay")} or {Hl(".switch")}.");
    }

    private void CmdKnifeChoice(CCSPlayerController player, bool swap)
    {
        if (phase != MatchPhase.KnifeDecision) return;
        if (player.TeamNum != knifeWinner)
        {
            Reply(player, "Only the team that won the knife round can choose.");
            return;
        }
        PrintAll($"{Hl(player.PlayerName)} chose to {(swap ? "switch" : "stay")}.");
        DecideSides(swap);
    }

    private void DecideSides(bool swap)
    {
        if (phase != MatchPhase.KnifeDecision) return;
        if (swap) Server.ExecuteCommand("mp_swapteams");
        GoLive();
    }
}
