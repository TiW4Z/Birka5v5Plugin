using System.Text;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using Microsoft.Extensions.Logging;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    private const string CfgFolder = "Birka5v5";
    private const string DefaultMapPool = "de_ancient,de_anubis,de_dust2,de_inferno,de_mirage,de_nuke,de_train";

    public FakeConVar<string> ChatPrefix = new("birka_chat_prefix", "Chat prefix for plugin messages", "[Birka]");
    public FakeConVar<int> PlayersRequired = new("birka_players_required", "Ready players (on T/CT) needed to start", 10);
    public FakeConVar<string> AdminFlag = new("birka_admin_flag", "CounterStrikeSharp permission flag for admin commands", "@css/config");
    public FakeConVar<string> Admins = new("birka_admins", "Extra admin SteamID64s, comma separated", "");
    public FakeConVar<bool> VetoEnabled = new("birka_veto_enabled", "Run a map veto after the first ready-up", true);
    public FakeConVar<string> VetoMode = new("birka_veto_mode", "Veto mode: ban (ban until one map is left) or pick (2 picks each, random of 4)", "ban");
    public FakeConVar<string> VetoMaps = new("birka_veto_maps", "Comma separated veto map pool. Workshop maps as name:workshopid", DefaultMapPool);
    public FakeConVar<int> VetoVoteTime = new("birka_veto_vote_time", "Seconds per veto vote", 20);
    public FakeConVar<bool> VetoHud = new("birka_veto_hud", "Show the veto on the center HUD", true);
    public FakeConVar<bool> KnifeEnabled = new("birka_knife_enabled", "Play a knife round to decide sides", true);
    public FakeConVar<int> KnifeDecisionTime = new("birka_knife_decision_time", "Seconds the knife winners have to pick .stay/.switch (then stay)", 60);
    public FakeConVar<bool> DemoEnabled = new("birka_demo_enabled", "Record a GOTV demo of each match", true);
    public FakeConVar<string> DemoPath = new("birka_demo_path", "Demo folder, relative to the game's write folder (csgo/addons/metamod with Metamod)", "birka_demos/");
    public FakeConVar<bool> PauseEnabled = new("birka_pause_enabled", "Allow players to use .pause", true);
    public FakeConVar<int> ReminderInterval = new("birka_reminder_interval", "Seconds between ready/pause chat reminders", 30);
    public FakeConVar<bool> EloEnabled = new("birka_elo_enabled", "Update player ratings after each completed match", true);
    public FakeConVar<bool> EloAutoBalance = new("birka_elo_autobalance", "Auto-create balanced teams by rating when everyone is ready", true);
    public FakeConVar<int> EloBalanceTolerance = new("birka_elo_balance_tolerance", "Rating points from the fairest split that still count as fair (adds team variety)", 25);
    public FakeConVar<int> EloStart = new("birka_elo_start", "Starting rating for new players", 1000);
    public FakeConVar<int> EloK = new("birka_elo_k", "Rating step (K factor)", 32);
    public FakeConVar<int> EloKProvisional = new("birka_elo_k_provisional", "K factor for a player's first 10 rated matches", 48);
    public FakeConVar<float> EloPerfWeight = new("birka_elo_perf_weight", "How much ADR/kills shift points within a team (0 = pure team Elo, max 1)", 0.5f);
    public FakeConVar<int> EloMinTeamSize = new("birka_elo_min_team_size", "Minimum rated players per team for a match to count", 4);
    public FakeConVar<float> EloMinRoundShare = new("birka_elo_min_round_share", "Share of rounds a player must play to be rated", 0.5f);

    private static readonly string[] ConVarNames =
    {
        "birka_chat_prefix", "birka_players_required", "birka_admin_flag", "birka_admins",
        "birka_veto_enabled", "birka_veto_mode", "birka_veto_maps", "birka_veto_vote_time", "birka_veto_hud",
        "birka_knife_enabled", "birka_knife_decision_time", "birka_demo_enabled", "birka_demo_path",
        "birka_pause_enabled", "birka_reminder_interval",
        "birka_elo_enabled", "birka_elo_autobalance", "birka_elo_balance_tolerance", "birka_elo_start", "birka_elo_k",
        "birka_elo_k_provisional", "birka_elo_perf_weight", "birka_elo_min_team_size", "birka_elo_min_round_share",
    };

    private static string CfgDirectory => Path.Combine(Server.GameDirectory, "csgo", "cfg", CfgFolder);
    private static string ConfigFilePath => Path.Combine(CfgDirectory, "config.cfg");

    private const string DefaultConfig = """
        // Birka5v5 settings. Executed when the plugin loads.
        // Keep string values in quotes.

        // Chat prefix for plugin messages
        birka_chat_prefix "[Birka]"

        // Number of ready players on T/CT needed before the veto / match starts.
        // Every player on T/CT must also be ready.
        birka_players_required 10

        // Admin permission flag (CounterStrikeSharp admins.json) for admin commands
        birka_admin_flag "@css/config"
        // Extra admins by SteamID64, comma separated
        birka_admins ""

        // Map veto: 1 = on, 0 = off (in-game: .veto)
        birka_veto_enabled 1
        // "ban"  = teams ban until one map is left
        // "pick" = each team picks 2 maps, a random one of the 4 is played
        birka_veto_mode "ban"
        // Map pool (in-game: .veto add <map> / .veto remove <map>)
        // Workshop maps: name:workshopid, e.g. de_cache:3437809122
        birka_veto_maps "de_ancient,de_anubis,de_dust2,de_inferno,de_mirage,de_nuke,de_train"
        // Seconds each team has to vote on a ban/pick
        birka_veto_vote_time 20
        // Show the veto on the center screen HUD
        birka_veto_hud 1

        // Knife round for sides
        birka_knife_enabled 1
        birka_knife_decision_time 60

        // GOTV demos (requires tv_enable 1 in server.cfg).
        // Saved in csgo/addons/metamod/<birka_demo_path> (the game writes files to the Metamod folder)
        birka_demo_enabled 1
        birka_demo_path "birka_demos/"

        // Allow .pause for players
        birka_pause_enabled 1

        // Seconds between chat reminders
        birka_reminder_interval 30

        // ---- Elo rating (stored in cfg/Birka5v5/elo.json) ----
        // Update ratings after each completed match (1 = on, 0 = off)
        birka_elo_enabled 1
        // Auto-create balanced teams by rating when everyone is ready (1 = on, 0 = off). In-game: .balance on/off
        birka_elo_autobalance 1
        // Splits within this many rating points of the fairest one count as fair; one is picked at random
        // so teammates vary between matches (0 = always the fairest split)
        birka_elo_balance_tolerance 25
        // Starting rating for new players (admins can seed with .elo set <name> <rating>)
        birka_elo_start 1000
        // Rating step per match, and the bigger step for a player's first 10 matches
        birka_elo_k 32
        birka_elo_k_provisional 48
        // How much ADR/kills decide each player's share of the team's gain/loss (0 = everyone equal, max 1)
        birka_elo_perf_weight 0.5
        // A match is only rated if each team has this many players who played at least
        // birka_elo_min_round_share of the rounds
        birka_elo_min_team_size 4
        birka_elo_min_round_share 0.5
        """;

    private void EnsureConfigFiles()
    {
        try
        {
            Directory.CreateDirectory(CfgDirectory);
            if (!File.Exists(ConfigFilePath))
            {
                File.WriteAllText(ConfigFilePath, DefaultConfig + Environment.NewLine);
                Logger.LogInformation("Created default config at {Path}", ConfigFilePath);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not create default config");
        }
    }

    /// <summary>Execs a phase cfg from cfg/Birka5v5, with a minimal inline fallback if the file is missing.</summary>
    private void ExecPhaseCfg(string fileName)
    {
        if (File.Exists(Path.Combine(CfgDirectory, fileName)))
        {
            Server.ExecuteCommand($"exec {CfgFolder}/{fileName}");
            return;
        }

        Logger.LogWarning("cfg/{Folder}/{File} is missing, using built-in fallback", CfgFolder, fileName);
        string fallback = fileName switch
        {
            "warmup.cfg" => "mp_warmup_start;mp_warmup_pausetimer 1;mp_warmuptime 9999;mp_autoteambalance 0;mp_limitteams 0",
            "knife.cfg" => "mp_ct_default_primary \"\";mp_t_default_primary \"\";mp_ct_default_secondary \"\";mp_t_default_secondary \"\";mp_give_player_c4 0;mp_maxmoney 0;mp_free_armor 1;mp_warmup_end",
            _ => "mp_warmup_end",
        };
        Server.ExecuteCommand(fallback);
    }

    /// <summary>Rewrites (or appends) a single setting line in config.cfg so in-game changes persist.</summary>
    private void SaveConfigValue(string name, string value, bool quote)
    {
        try
        {
            EnsureConfigFiles();
            string line = quote ? $"{name} \"{value}\"" : $"{name} {value}";
            string text = File.ReadAllText(ConfigFilePath);
            var regex = new Regex($@"^[ \t]*{Regex.Escape(name)}[ \t].*$", RegexOptions.Multiline);

            if (regex.IsMatch(text))
            {
                text = regex.Replace(text, line.Replace("$", "$$"), 1);
            }
            else
            {
                if (!text.EndsWith('\n')) text += Environment.NewLine;
                text += line + Environment.NewLine;
            }

            File.WriteAllText(ConfigFilePath, text, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not save {Name} to config.cfg", name);
        }
    }

    /// <summary>String convar values may arrive with surrounding quotes depending on how they were set.</summary>
    private static string Clean(string? value) => (value ?? "").Trim().Trim('"').Trim();

    private bool IsPickMode => Clean(VetoMode.Value).Equals("pick", StringComparison.OrdinalIgnoreCase);
}
