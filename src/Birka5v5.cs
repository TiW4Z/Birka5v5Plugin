using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace Birka5v5;

public enum MatchPhase
{
    Warmup,                // players ready up for the veto
    Veto,                  // map veto running
    MapChangePending,      // veto done, changing to the chosen map
    WaitingForMatchReady,  // on the match map, players ready up for the knife round
    Knife,                 // knife round in progress
    KnifeDecision,         // knife winners choose .stay / .switch
    Live,                  // match is live
    PostMatch,             // match ended, waiting to return to warmup
}

[MinimumApiVersion(300)]
public partial class Birka5v5Plugin : BasePlugin
{
    public override string ModuleName => "Birka5v5";
    // Set from the git tag by the release workflow (dotnet build -p:Version=...).
    public override string ModuleVersion => typeof(Birka5v5Plugin).Assembly.GetName().Version?.ToString(3) ?? "dev";
    public override string ModuleAuthor => "Birka";
    public override string ModuleDescription => "Ready-up, map veto, knife, demos, pause and restore for community 5v5 matches";

    private MatchPhase phase = MatchPhase.Warmup;

    // Timers that belong to the current phase; killed on every phase change.
    private readonly List<Timer> phaseTimers = new();

    public override void Load(bool hotReload)
    {
        EnsureConfigFiles();
        Server.ExecuteCommand($"exec {CfgFolder}/config.cfg");

        RegisterCommands();

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnTick>(OnTick);

        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);

        // On a fresh server start OnMapStart handles this; on a hot reload the map is already running.
        if (hotReload)
        {
            AddTimer(1.0f, EnterWarmup);
        }

        Logger.LogInformation("Birka5v5 loaded");
    }

    public override void Unload(bool hotReload)
    {
        ClearVetoHud();
    }

    private void OnMapStart(string mapName)
    {
        // Any recording ends when the map changes.
        demoRecording = false;

        AddTimer(1.0f, () =>
        {
            if (phase == MatchPhase.MapChangePending)
            {
                EnterMatchReady();
            }
            else
            {
                EnterWarmup();
            }
        });
    }

    private void SetPhase(MatchPhase newPhase)
    {
        KillPhaseTimers();
        phase = newPhase;
    }

    private Timer AddPhaseTimer(float seconds, Action callback, bool repeat = false)
    {
        var flags = TimerFlags.STOP_ON_MAPCHANGE;
        if (repeat) flags |= TimerFlags.REPEAT;
        var timer = AddTimer(seconds, callback, flags);
        phaseTimers.Add(timer);
        return timer;
    }

    private void KillPhaseTimers()
    {
        foreach (var timer in phaseTimers)
        {
            timer.Kill();
        }
        phaseTimers.Clear();
        pauseReminderTimer = null;
        vetoVoteTimer = null;
    }

    // ---- Phase entry points ----

    /// <summary>Full reset: back to warmup on the current map, ready up for the veto.</summary>
    private void EnterWarmup()
    {
        if (demoRecording) StopDemo(0);
        if (isPaused) Server.ExecuteCommand("mp_unpause_match");
        UnfreezeVeto();

        SetPhase(MatchPhase.Warmup);
        readyPlayers.Clear();
        ResetPauseState();
        ResetVetoState();
        ClearVetoHud();

        ExecPhaseCfg("warmup.cfg");
        StartReadyReminder();

        string next = VetoEnabled.Value ? "the map veto" : "the match";
        PrintAll($"Warmup. Type {Hl(".ready")} to start {next}.");
    }

    /// <summary>On the match map after the veto: ready up for the knife round / live.</summary>
    private void EnterMatchReady()
    {
        SetPhase(MatchPhase.WaitingForMatchReady);
        readyPlayers.Clear();
        ResetPauseState();
        ClearVetoHud();
        UnfreezeVeto();

        ExecPhaseCfg("warmup.cfg");
        StartReadyReminder();

        string next = KnifeEnabled.Value ? "the knife round" : "the match";
        PrintAll($"Map {Hl(Server.MapName)} loaded. Type {Hl(".ready")} to start {next}.");
    }

    /// <summary>Called when everyone is ready (or .forcestart) in one of the ready phases.</summary>
    private void OnAllReady()
    {
        if (phase == MatchPhase.Warmup && VetoEnabled.Value)
        {
            StartVeto();
            return;
        }
        StartMatch();
    }

    private void StartMatch()
    {
        if (KnifeEnabled.Value)
        {
            StartKnife();
        }
        else
        {
            GoLive();
        }
    }
}
