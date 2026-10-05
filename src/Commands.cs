using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    private Dictionary<string, Action<CCSPlayerController, string[]>> chatCommands = new();

    private void RegisterCommands()
    {
        chatCommands = new Dictionary<string, Action<CCSPlayerController, string[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["r"] = CmdReady,
            ["ready"] = CmdReady,
            ["ur"] = CmdUnready,
            ["unready"] = CmdUnready,
            ["notready"] = CmdUnready,
            ["forcestart"] = (p, _) => CmdForceStart(p),
            ["veto"] = CmdVeto,
            ["ban"] = CmdVetoVote,
            ["pick"] = CmdVetoVote,
            ["stay"] = (p, _) => CmdKnifeChoice(p, swap: false),
            ["switch"] = (p, _) => CmdKnifeChoice(p, swap: true),
            ["swap"] = (p, _) => CmdKnifeChoice(p, swap: true),
            ["pause"] = (p, _) => CmdPause(p),
            ["p"] = (p, _) => CmdPause(p),
            ["unpause"] = (p, _) => CmdUnpause(p),
            ["up"] = (p, _) => CmdUnpause(p),
            ["forcepause"] = (p, _) => CmdForcePause(p),
            ["fp"] = (p, _) => CmdForcePause(p),
            ["forceunpause"] = (p, _) => CmdForceUnpause(p),
            ["fup"] = (p, _) => CmdForceUnpause(p),
            ["restore"] = (p, args) => CmdRestore(p, args),
            ["reset"] = (p, _) => CmdReset(p),
            ["help"] = (p, _) => CmdHelp(p),
        };

        AddCommandListener("say", OnPlayerSay);
        AddCommandListener("say_team", OnPlayerSay);
        AddCommandListener("jointeam", OnJoinTeam);

        // Only admins may change the plugin settings from a client console.
        foreach (var name in ConVarNames)
        {
            AddCommandListener(name, (player, _) =>
                player != null && !IsAdmin(player) ? HookResult.Handled : HookResult.Continue);
        }
    }

    private HookResult OnPlayerSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        string text = (info.ArgCount > 2 ? info.ArgString : info.GetArg(1)).Trim().Trim('"').Trim();
        if (text.Length == 0) return HookResult.Continue;

        // During a veto vote a bare number in chat from the acting team is a vote.
        if (vetoVoteActive && player.TeamNum == CurrentVetoStep?.Team && int.TryParse(text, out _))
        {
            Server.NextFrame(() => { if (player.IsValid) CastVetoVote(player, text); });
            return HookResult.Continue;
        }

        if (text[0] != '.' && text[0] != '!') return HookResult.Continue;

        string[] parts = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !chatCommands.TryGetValue(parts[0], out var handler)) return HookResult.Continue;

        string[] args = parts.Skip(1).ToArray();
        // Run next frame so the chat line shows before any reply.
        Server.NextFrame(() => { if (player.IsValid) handler(player, args); });
        return HookResult.Continue;
    }

    private HookResult OnJoinTeam(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return HookResult.Continue;

        bool locked = phase is MatchPhase.Veto or MatchPhase.Knife or MatchPhase.KnifeDecision;
        // Players without a team (e.g. reconnecting) may still join one.
        if (locked && IsOnTeam(player))
        {
            Reply(player, "Teams are locked right now.");
            return HookResult.Handled;
        }
        return HookResult.Continue;
    }

    private void CmdHelp(CCSPlayerController player)
    {
        Reply(player, $"{Hl(".ready")}/{Hl(".unready")}, {Hl(".pause")}/{Hl(".unpause")}, {Hl(".veto list")}, vote with a number during the veto, {Hl(".stay")}/{Hl(".switch")} after knife.");
        if (IsAdmin(player))
        {
            Reply(player, $"Admin: {Hl(".forcestart")}, {Hl(".veto")} (toggle), {Hl(".veto add/remove <map>")}, {Hl(".veto mode ban|pick")}, {Hl(".restore <round>")}, {Hl(".forcepause")}, {Hl(".forceunpause")}, {Hl(".reset")}");
        }
    }

    private void CmdReset(CCSPlayerController? player)
    {
        if (!RequireAdmin(player)) return;
        PrintAll($"{ChatColors.Red}Admin reset the match.");
        EnterWarmup();
    }

    // ---- Server console versions of the admin commands ----

    [ConsoleCommand("birka_forcestart", "Skip the ready check")]
    public void ConsoleForceStart(CCSPlayerController? player, CommandInfo info) => CmdForceStart(player);

    [ConsoleCommand("birka_restore", "Replay a round: birka_restore <round>")]
    public void ConsoleRestore(CCSPlayerController? player, CommandInfo info) =>
        CmdRestore(player, info.ArgCount > 1 ? new[] { info.GetArg(1) } : Array.Empty<string>());

    [ConsoleCommand("birka_reset", "Abort the match and return to warmup")]
    public void ConsoleReset(CCSPlayerController? player, CommandInfo info) => CmdReset(player);

    [ConsoleCommand("birka_status", "Print the plugin state")]
    public void ConsoleStatus(CCSPlayerController? player, CommandInfo info)
    {
        info.ReplyToCommand($"[Birka5v5] phase={phase} ready={ReadyCount()}/{PlayersRequired.Value} veto={(VetoEnabled.Value ? Clean(VetoMode.Value) : "off")} pool={Clean(VetoMaps.Value)} paused={isPaused} demo={demoRecording}");
        info.ReplyToCommand($"[Birka5v5] tv_enable={GetConVarNumber("tv_enable")} tv_autorecord={GetConVarNumber("tv_autorecord")} gotv_connected={IsGotvConnected()} demo_folder={Path.Combine(Server.GameDirectory, "csgo", GetDemoFolder())}");
        if (matchId.Length > 0)
        {
            var backups = FindBackupFiles();
            info.ReplyToCommand($"[Birka5v5] backups prefix={BackupPrefix} found={backups.Count} cwd={Directory.GetCurrentDirectory()}");
            foreach (var path in backups.OrderBy(b => b.Key).Select(b => b.Value).Take(3)) info.ReplyToCommand($"  {path}");
        }
    }
}
