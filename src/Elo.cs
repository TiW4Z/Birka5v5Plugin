using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace Birka5v5;

public class EloPlayer
{
    public string Name { get; set; } = "";
    public int Rating { get; set; }
    public int Matches { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int Draws { get; set; }
    public DateTime? LastPlayed { get; set; }
}

public class EloData
{
    public Dictionary<string, EloPlayer> Players { get; set; } = new();

    // Teams of the last rated match (SteamID64s), so balancing can avoid repeating them.
    public List<string> LastTeamA { get; set; } = new();
    public List<string> LastTeamB { get; set; } = new();
}

public partial class Birka5v5Plugin
{
    private const int ProvisionalMatches = 10;

    private static readonly JsonSerializerOptions EloJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions EloHistoryJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private EloData eloData = new();

    // Set if elo.json exists but can't be read, so we never overwrite it with an empty file.
    private bool eloLoadFailed;

    private static string EloFilePath => Path.Combine(CfgDirectory, "elo.json");
    private static string EloHistoryPath => Path.Combine(CfgDirectory, "elo_history.jsonl");

    // ---- Storage ----

    private void LoadElo()
    {
        eloLoadFailed = false;
        try
        {
            if (!File.Exists(EloFilePath))
            {
                eloData = new EloData();
                SaveElo();
                return;
            }
            eloData = JsonSerializer.Deserialize<EloData>(File.ReadAllText(EloFilePath), EloJsonOptions) ?? new EloData();
            Logger.LogInformation("Loaded {Count} player ratings", eloData.Players.Count);
        }
        catch (Exception ex)
        {
            eloLoadFailed = true;
            eloData = new EloData();
            Logger.LogError(ex, "Could not read {Path}. Ratings will not be saved until it is fixed and the plugin reloaded", EloFilePath);
        }
    }

    private void SaveElo()
    {
        if (eloLoadFailed)
        {
            Logger.LogError("Not saving ratings: {Path} could not be read at load", EloFilePath);
            return;
        }
        try
        {
            Directory.CreateDirectory(CfgDirectory);
            string temp = EloFilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(eloData, EloJsonOptions));
            File.Move(temp, EloFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not save {Path}", EloFilePath);
        }
    }

    private void AppendEloHistory(object entry)
    {
        try
        {
            File.AppendAllText(EloHistoryPath, JsonSerializer.Serialize(entry, EloHistoryJsonOptions) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not write {Path}", EloHistoryPath);
        }
    }

    // ---- Lookups ----

    private int StartRating => Math.Max(0, EloStart.Value);

    private int GetRating(ulong steamId) =>
        eloData.Players.TryGetValue(steamId.ToString(), out var p) ? p.Rating : StartRating;

    private EloPlayer GetOrCreateEloPlayer(ulong steamId, string name)
    {
        string key = steamId.ToString();
        if (!eloData.Players.TryGetValue(key, out var player))
        {
            player = new EloPlayer { Rating = StartRating };
            eloData.Players[key] = player;
        }
        if (name.Length > 0) player.Name = name;
        return player;
    }

    private static string FormatRating(int rating, int matches) =>
        matches < ProvisionalMatches ? $"{rating} (provisional)" : rating.ToString();

    /// <summary>Elo expected score of a team with average rating <paramref name="ra"/> against <paramref name="rb"/>.</summary>
    private static double ExpectedScore(double ra, double rb) => 1.0 / (1.0 + Math.Pow(10, (rb - ra) / 400.0));

    // ---- Rating update ----

    private record MatchPlayer(ulong SteamId, string Name, int Team, int Rounds, int Damage, int Kills);

    /// <summary>
    /// Team Elo with a round-margin multiplier. Each team's total change is fixed by the result;
    /// personal performance (ADR/KPR vs the lobby) only decides how that total is split within the team.
    /// </summary>
    private void ApplyMatchRatings(List<MatchPlayer> players, int scoreA, int scoreB)
    {
        var teamA = players.Where(p => p.Team == 0).ToList();
        var teamB = players.Where(p => p.Team == 1).ToList();

        double ratingA = teamA.Average(p => GetRating(p.SteamId));
        double ratingB = teamB.Average(p => GetRating(p.SteamId));
        double expectedA = ExpectedScore(ratingA, ratingB);
        double resultA = scoreA > scoreB ? 1 : scoreA < scoreB ? 0 : 0.5;
        double margin = 1 + 0.5 * Math.Min(Math.Abs(scoreA - scoreB), 10) / 10.0;

        double Adr(MatchPlayer p) => (double)p.Damage / Math.Max(1, p.Rounds);
        double Kpr(MatchPlayer p) => (double)p.Kills / Math.Max(1, p.Rounds);
        double avgAdr = players.Average(Adr);
        double avgKpr = players.Average(Kpr);
        double Perf(MatchPlayer p) =>
            0.7 * (avgAdr > 0 ? Adr(p) / avgAdr : 1) + 0.3 * (avgKpr > 0 ? Kpr(p) / avgKpr : 1);

        double weight = Math.Clamp(EloPerfWeight.Value, 0, 1);
        int kNormal = Math.Max(1, EloK.Value);
        int kProvisional = Math.Max(1, EloKProvisional.Value);

        var deltas = new Dictionary<ulong, int>();
        var teamTotals = new double[2];
        foreach (var (team, result, expected, index) in new[]
                 {
                     (teamA, resultA, expectedA, 0),
                     (teamB, 1 - resultA, 1 - expectedA, 1),
                 })
        {
            double K(MatchPlayer p) =>
                GetOrCreateEloPlayer(p.SteamId, p.Name).Matches < ProvisionalMatches ? kProvisional : kNormal;

            double total = team.Sum(p => K(p) * margin * (result - expected));
            teamTotals[index] = total;

            // Gaining team: strong players get a bigger share. Losing team: strong players lose less.
            double Factor(MatchPlayer p)
            {
                double perf = Perf(p) - 1;
                return Math.Clamp(total >= 0 ? 1 + weight * perf : 1 - weight * perf, 0.5, 1.5);
            }

            double sumWeights = team.Sum(p => K(p) * Factor(p));
            foreach (var p in team)
            {
                double delta = sumWeights > 0 ? total * K(p) * Factor(p) / sumWeights : 0;
                deltas[p.SteamId] = (int)Math.Round(delta);
            }
        }

        // Apply, announce and log.
        var historyPlayers = new List<object>();
        PrintAll($"Rating changes ({Hl($"{scoreA}-{scoreB}")}):");
        foreach (var p in teamA.Concat(teamB))
        {
            var stored = GetOrCreateEloPlayer(p.SteamId, p.Name);
            int old = stored.Rating;
            int result = p.Team == 0 ? Math.Sign(scoreA - scoreB) : Math.Sign(scoreB - scoreA);

            stored.Rating = Math.Max(0, old + deltas[p.SteamId]);
            stored.Matches++;
            if (result > 0) stored.Wins++;
            else if (result < 0) stored.Losses++;
            else stored.Draws++;
            stored.LastPlayed = DateTime.UtcNow;

            int change = stored.Rating - old;
            string color = change > 0 ? $"{ChatColors.Lime}+{change}" : change < 0 ? $"{ChatColors.Red}{change}" : "±0";
            PrintAll($"{p.Name}: {old} → {stored.Rating} ({color}{ChatColors.Default})");

            historyPlayers.Add(new
            {
                steamId = p.SteamId.ToString(),
                p.Name,
                team = p.Team == 0 ? "A" : "B",
                p.Rounds,
                adr = Math.Round(Adr(p), 1),
                kpr = Math.Round(Kpr(p), 2),
                perf = Math.Round(Perf(p), 2),
                old,
                @new = stored.Rating,
                delta = change,
            });
        }

        eloData.LastTeamA = teamA.Select(p => p.SteamId.ToString()).ToList();
        eloData.LastTeamB = teamB.Select(p => p.SteamId.ToString()).ToList();
        SaveElo();

        AppendEloHistory(new
        {
            type = "match",
            date = DateTime.UtcNow,
            matchId,
            map = Server.MapName,
            scoreA,
            scoreB,
            ratingA = Math.Round(ratingA),
            ratingB = Math.Round(ratingB),
            expectedA = Math.Round(expectedA, 3),
            teamTotalA = Math.Round(teamTotals[0], 1),
            teamTotalB = Math.Round(teamTotals[1], 1),
            players = historyPlayers,
        });
    }

    // ---- Commands ----

    private void CmdElo(CCSPlayerController player, string[] args)
    {
        if (args.Length > 0 && args[0].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            CmdEloSet(player, args.Skip(1).ToArray());
            return;
        }

        CCSPlayerController? target = player;
        if (args.Length > 0)
        {
            target = FindOnlinePlayer(player, string.Join(' ', args));
            if (target == null) return;
        }

        string key = target.SteamID.ToString();
        if (!eloData.Players.TryGetValue(key, out var stored) || stored.Matches == 0)
        {
            int rating = stored?.Rating ?? StartRating;
            Reply(player, $"{Hl(target.PlayerName)}: {FormatRating(rating, 0)}, no rated matches yet.");
            return;
        }

        var ranked = eloData.Players.Where(p => p.Value.Matches > 0).OrderByDescending(p => p.Value.Rating).ToList();
        int rank = ranked.FindIndex(p => p.Key == key) + 1;
        Reply(player, $"{Hl(target.PlayerName)}: {Hl(FormatRating(stored.Rating, stored.Matches))}, " +
                      $"{stored.Wins}W {stored.Losses}L {stored.Draws}D in {stored.Matches} matches, rank #{rank}/{ranked.Count}.");
    }

    private void CmdEloSet(CCSPlayerController player, string[] args)
    {
        if (!RequireAdmin(player)) return;
        if (args.Length < 2 || !int.TryParse(args[^1], out int rating) || rating < 0 || rating > 5000)
        {
            Reply(player, "Usage: .elo set <name> <rating>");
            return;
        }

        var target = FindOnlinePlayer(player, string.Join(' ', args[..^1]));
        if (target == null) return;

        var stored = GetOrCreateEloPlayer(target.SteamID, target.PlayerName);
        int old = stored.Rating;
        stored.Rating = rating;
        SaveElo();
        AppendEloHistory(new
        {
            type = "set",
            date = DateTime.UtcNow,
            admin = player.PlayerName,
            steamId = target.SteamID.ToString(),
            name = target.PlayerName,
            old,
            @new = rating,
        });
        Reply(player, $"Set {Hl(target.PlayerName)}'s rating {old} → {Hl(rating.ToString())}.");
    }

    private void CmdTop(CCSPlayerController player)
    {
        var top = eloData.Players.Values.Where(p => p.Matches > 0).OrderByDescending(p => p.Rating).Take(10).ToList();
        if (top.Count == 0)
        {
            Reply(player, "No rated matches yet.");
            return;
        }
        Reply(player, "Top players:");
        for (int i = 0; i < top.Count; i++)
        {
            var p = top[i];
            Reply(player, $"{Hl($"{i + 1}.")} {p.Name} {FormatRating(p.Rating, p.Matches)} ({p.Wins}W {p.Losses}L {p.Draws}D)");
        }
    }

    /// <summary>Finds one online human by partial name; replies to <paramref name="caller"/> if none or several match.</summary>
    private CCSPlayerController? FindOnlinePlayer(CCSPlayerController? caller, string query)
    {
        query = query.Trim();
        var humans = HumanPlayers().ToList();
        var exact = humans.Where(p => p.PlayerName.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = exact.Count > 0
            ? exact
            : humans.Where(p => p.PlayerName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 1) return matches[0];
        Reply(caller, matches.Count == 0
            ? $"No player matching '{query}'."
            : $"Several players match '{query}': {string.Join(", ", matches.Select(p => p.PlayerName))}");
        return null;
    }
}
