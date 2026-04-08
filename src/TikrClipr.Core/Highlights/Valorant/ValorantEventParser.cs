using System.Text.Json;

namespace TikrClipr.Core.Highlights.Valorant;

/// <summary>
/// Parses Valorant local API JSON responses into game state snapshots.
/// The Riot Client local API returns session and match data that we diff
/// to detect highlight-worthy events.
/// </summary>
internal static class ValorantEventParser
{
    public static ValorantGameState? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new ValorantGameState
            {
                Kills = GetInt(root, "kills"),
                Deaths = GetInt(root, "deaths"),
                Assists = GetInt(root, "assists"),
                Health = GetInt(root, "health", 100),
                RoundKills = GetInt(root, "roundKills"),
                RoundPhase = GetString(root, "roundPhase"),
                MatchPhase = GetString(root, "matchPhase"),
                TeamScore = GetInt(root, "teamScore"),
                EnemyScore = GetInt(root, "enemyScore"),
                AgentName = GetString(root, "agentName"),
                SpikeState = GetBool(root, "spikePlanted"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the Riot Client's pre-game/core-game session payload into game state.
    /// This handles the nested structure returned by the /chat/v4/presences or
    /// /liveclientdata endpoints.
    /// </summary>
    public static ValorantGameState? ParseSessionPayload(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Try to navigate nested structures common in Riot's local API
            var player = TryGetProperty(root, "player") ?? root;
            var match = TryGetProperty(root, "match") ?? root;
            var round = TryGetProperty(root, "round") ?? TryGetProperty(match, "round") ?? root;

            return new ValorantGameState
            {
                Kills = GetInt(player, "kills"),
                Deaths = GetInt(player, "deaths"),
                Assists = GetInt(player, "assists"),
                Health = GetInt(player, "health", 100),
                RoundKills = GetInt(player, "roundKills"),
                RoundPhase = GetString(round, "phase") ?? GetString(root, "roundPhase"),
                MatchPhase = GetString(match, "phase") ?? GetString(root, "matchPhase"),
                TeamScore = GetInt(match, "teamScore") != 0 ? GetInt(match, "teamScore") : GetInt(root, "teamScore"),
                EnemyScore = GetInt(match, "enemyScore") != 0 ? GetInt(match, "enemyScore") : GetInt(root, "enemyScore"),
                AgentName = GetString(player, "agentName") ?? GetString(root, "agentName"),
                SpikeState = GetBool(round, "spikePlanted") || GetBool(root, "spikePlanted"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? TryGetProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(name, out var prop) ? prop : null;
    }

    private static int GetInt(JsonElement element, string name, int fallback = 0)
    {
        if (element.ValueKind != JsonValueKind.Object) return fallback;
        if (!element.TryGetProperty(name, out var prop)) return fallback;
        return prop.ValueKind == JsonValueKind.Number ? prop.GetInt32() : fallback;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static bool GetBool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty(name, out var prop)) return false;
        return prop.ValueKind is JsonValueKind.True or JsonValueKind.False && prop.GetBoolean();
    }
}
