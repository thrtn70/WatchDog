using System.Text.Json;

namespace WatchDog.Core.Highlights.Cs2;

internal static class Cs2GsiPayloadParser
{
    public static Cs2GameState? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Verify this is the local player (provider.steamid == player.steamid)
            var providerSteamId = GetString(root, "provider", "steamid");
            var playerSteamId = GetString(root, "player", "steamid");

            if (providerSteamId is null || playerSteamId is null || providerSteamId != playerSteamId)
                return null; // Spectating another player, ignore

            var playerTeam = GetString(root, "player", "team");

            // Player state
            var health = GetInt(root, "player", "state", "health");
            var roundKills = GetInt(root, "player", "state", "round_kills");

            // Match stats
            var kills = GetInt(root, "player", "match_stats", "kills");
            var deaths = GetInt(root, "player", "match_stats", "deaths");
            var assists = GetInt(root, "player", "match_stats", "assists");

            // Round and map phase
            var roundPhase = GetString(root, "round", "phase");
            var mapPhase = GetString(root, "map", "phase");

            // Scores — relative to player's team
            var ctScore = GetInt(root, "map", "team_ct", "score");
            var tScore = GetInt(root, "map", "team_t", "score");

            int teamScore, enemyScore;
            if (playerTeam == "CT")
            {
                teamScore = ctScore;
                enemyScore = tScore;
            }
            else
            {
                teamScore = tScore;
                enemyScore = ctScore;
            }

            return new Cs2GameState
            {
                Kills = kills,
                Deaths = deaths,
                Assists = assists,
                Health = health,
                RoundKills = roundKills,
                RoundPhase = roundPhase,
                MapPhase = mapPhase,
                TeamScore = teamScore,
                EnemyScore = enemyScore,
                PlayerTeam = playerTeam,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var key in path)
        {
            if (!current.TryGetProperty(key, out current))
                return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static int GetInt(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var key in path)
        {
            if (!current.TryGetProperty(key, out current))
                return 0;
        }
        return current.ValueKind == JsonValueKind.Number ? current.GetInt32() : 0;
    }
}
