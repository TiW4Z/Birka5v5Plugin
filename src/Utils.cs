using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;

namespace Birka5v5;

/// <summary>A veto pool entry. Workshop maps are written as name:workshopid in the cfg.</summary>
public record MapEntry(string Name, string? WorkshopId)
{
    public bool IsWorkshop => WorkshopId != null;

    /// <summary>Short label for chat/HUD: "de_mirage" -> "mirage".</summary>
    public string Label => Name.StartsWith("de_", StringComparison.OrdinalIgnoreCase) ? Name[3..] : Name;

    public override string ToString() => IsWorkshop ? $"{Name}:{WorkshopId}" : Name;

    public static MapEntry? Parse(string raw)
    {
        raw = raw.Trim().Trim('"').Trim();
        if (raw.Length == 0) return null;

        int colon = raw.IndexOf(':');
        if (colon > 0)
        {
            string name = raw[..colon].Trim();
            string id = raw[(colon + 1)..].Trim();
            if (name.Length > 0 && id.Length > 0 && id.All(char.IsDigit)) return new MapEntry(name, id);
        }
        // A bare workshop id: use the id as the name too.
        if (raw.All(char.IsDigit)) return new MapEntry(raw, raw);
        return new MapEntry(raw, null);
    }
}

public partial class Birka5v5Plugin
{
    // ---- Chat ----

    private string Prefix => $" {ChatColors.Green}{Clean(ChatPrefix.Value)}{ChatColors.Default}";

    private static string Hl(string text) => $"{ChatColors.Gold}{text}{ChatColors.Default}";

    private void PrintAll(string message) => Server.PrintToChatAll($"{Prefix} {message}");

    private void Reply(CCSPlayerController? player, string message)
    {
        if (player == null || !player.IsValid)
        {
            Server.PrintToConsole($"[Birka5v5] {message}");
            return;
        }
        player.PrintToChat($"{Prefix} {message}");
    }

    // ---- Admin ----

    private bool IsAdmin(CCSPlayerController? player)
    {
        if (player == null) return true; // server console
        if (!player.IsValid) return false;

        string flag = Clean(AdminFlag.Value);
        if (flag.Length > 0 && AdminManager.PlayerHasPermissions(player, flag)) return true;
        if (AdminManager.PlayerHasPermissions(player, "@css/root")) return true;

        string steamId = player.SteamID.ToString();
        return Clean(Admins.Value)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(steamId);
    }

    private bool RequireAdmin(CCSPlayerController? player)
    {
        if (IsAdmin(player)) return true;
        Reply(player, $"{ChatColors.Red}You don't have permission to use this command.");
        return false;
    }

    // ---- Players / teams ----

    private const int TeamT = (int)CsTeam.Terrorist;
    private const int TeamCT = (int)CsTeam.CounterTerrorist;

    private static IEnumerable<CCSPlayerController> HumanPlayers() =>
        Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false }
                                          && p.Connected == PlayerConnectedState.Connected);

    private static bool IsOnTeam(CCSPlayerController player) => player.TeamNum == TeamT || player.TeamNum == TeamCT;

    private static IEnumerable<CCSPlayerController> TeamPlayers() => HumanPlayers().Where(IsOnTeam);

    private static IEnumerable<CCSPlayerController> TeamPlayers(int team) => HumanPlayers().Where(p => p.TeamNum == team);

    private static int OtherTeam(int team) => team == TeamT ? TeamCT : TeamT;

    private static string TeamName(int team) => team switch
    {
        TeamCT => $"{ChatColors.LightBlue}Counter-Terrorists{ChatColors.Default}",
        TeamT => $"{ChatColors.Orange}Terrorists{ChatColors.Default}",
        _ => "Spectators",
    };

    private static string TeamShort(int team) => team == TeamCT ? "CT" : team == TeamT ? "T" : "SPEC";

    // ---- Game state ----

    private static CCSGameRules? GetGameRules() =>
        Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;

    private static (int ct, int t) GetTeamScores()
    {
        int ct = 0, t = 0;
        foreach (var team in Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager"))
        {
            if (team.TeamNum == TeamCT) ct = team.Score;
            else if (team.TeamNum == TeamT) t = team.Score;
        }
        return (ct, t);
    }

    /// <summary>Reads a numeric convar regardless of whether the engine stores it as bool/int/float.</summary>
    private static float GetConVarNumber(string name)
    {
        var cvar = ConVar.Find(name);
        if (cvar == null) return 0;
        return cvar.Type switch
        {
            ConVarType.Bool => cvar.GetPrimitiveValue<bool>() ? 1 : 0,
            ConVarType.Int16 => cvar.GetPrimitiveValue<short>(),
            ConVarType.UInt16 => cvar.GetPrimitiveValue<ushort>(),
            ConVarType.Int32 => cvar.GetPrimitiveValue<int>(),
            ConVarType.UInt32 => cvar.GetPrimitiveValue<uint>(),
            ConVarType.Int64 => cvar.GetPrimitiveValue<long>(),
            ConVarType.UInt64 => cvar.GetPrimitiveValue<ulong>(),
            ConVarType.Float32 => cvar.GetPrimitiveValue<float>(),
            ConVarType.Float64 => (float)cvar.GetPrimitiveValue<double>(),
            _ => float.TryParse(cvar.StringValue, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0,
        };
    }

    // ---- Map pool ----

    private List<MapEntry> GetMapPool()
    {
        var pool = new List<MapEntry>();
        foreach (var part in Clean(VetoMaps.Value).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = MapEntry.Parse(part);
            if (entry != null && !pool.Any(m => m.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)))
            {
                pool.Add(entry);
            }
        }
        return pool;
    }

    private void SetMapPool(List<MapEntry> pool)
    {
        string value = string.Join(",", pool.Select(m => m.ToString()));
        VetoMaps.Value = value;
        SaveConfigValue("birka_veto_maps", value, quote: true);
    }

    /// <summary>Finds a map in a list by 1-based number, exact name, name without de_, or unique prefix.</summary>
    private static MapEntry? FindMap(IReadOnlyList<MapEntry> maps, string input)
    {
        input = input.Trim();
        if (input.Length == 0) return null;

        if (int.TryParse(input, out int number))
        {
            if (number >= 1 && number <= maps.Count) return maps[number - 1];
            var byId = maps.FirstOrDefault(m => m.WorkshopId == input);
            return byId;
        }

        var exact = maps.FirstOrDefault(m => m.Name.Equals(input, StringComparison.OrdinalIgnoreCase)
                                             || m.Label.Equals(input, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        var prefixed = maps.Where(m => m.Label.StartsWith(input, StringComparison.OrdinalIgnoreCase)).ToList();
        return prefixed.Count == 1 ? prefixed[0] : null;
    }

    /// <summary>Changes level. Returns false if the map can't be loaded.</summary>
    private bool ChangeMap(MapEntry map)
    {
        if (map.IsWorkshop)
        {
            Server.ExecuteCommand($"host_workshop_map {map.WorkshopId}");
            return true;
        }
        if (Server.IsMapValid(map.Name))
        {
            Server.ExecuteCommand($"changelevel {map.Name}");
            return true;
        }
        return false;
    }
}
