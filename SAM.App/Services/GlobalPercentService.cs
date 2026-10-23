using System.Globalization;
using System.Net;
using System.Text.Json;

namespace SAM.App.Services;

/// <summary>Outcome of a global-percentage lookup.</summary>
public enum GlobalPercentStatus
{
    /// <summary>Steam answered (now or earlier, from the cache) with percentages.</summary>
    Ok,

    /// <summary>Steam answered that the game has no global achievement data.</summary>
    NoData,

    /// <summary>The lookup failed (offline, server error, timeout, cancelled) and nothing is cached: unknown, try again later.</summary>
    Failed,
}

/// <summary>A global-percentage lookup: its status and, when Ok, achievement ID → percent of players (0-100).</summary>
public sealed record GlobalPercentResult(GlobalPercentStatus Status, IReadOnlyDictionary<string, double>? Percents)
{
    public static GlobalPercentResult Failed { get; } = new(GlobalPercentStatus.Failed, null);

    public static GlobalPercentResult NoData { get; } = new(GlobalPercentStatus.NoData, null);

    internal static GlobalPercentResult From(Dictionary<string, double>? percentages) =>
        percentages == null || percentages.Count == 0 ? NoData : new(GlobalPercentStatus.Ok, percentages);
}

/// <summary>
/// Global unlock percentages from the public Steam Web API
/// (ISteamUserStats/GetGlobalAchievementPercentagesForApp, no key needed), cached for 24 hours
/// in cache\shared\percentages\&lt;appid&gt;.json. Falls back to a stale cache when offline.
/// </summary>
public sealed class GlobalPercentService(AppCache cache, HttpClient http)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>Achievement ID → percent of players (0-100). Null if unavailable (no data, or the lookup failed).</summary>
    /// <remarks>
    /// Callers that must tell "the game has no data" from "couldn't ask Steam" use <see cref="FetchAsync"/>.
    /// Returns null rather than throwing when <paramref name="cancellationToken"/> is cancelled.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, double>?> GetAsync(uint appId, CancellationToken cancellationToken) =>
        (await this.FetchAsync(appId, cancellationToken).ConfigureAwait(false)).Percents;

    /// <summary>Global percentages with their status (see <see cref="GlobalPercentStatus"/>).</summary>
    /// <remarks>
    /// Games without achievements (the API answers 400/403/404) are cached as "no data". Offline,
    /// 5xx, timeouts and malformed answers aren't cached: they serve a stale cache if there is one,
    /// else <see cref="GlobalPercentStatus.Failed"/>. Never throws for cancellation (Failed).
    /// </remarks>
    public async Task<GlobalPercentResult> FetchAsync(uint appId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(cache.SharedDirectory, "percentages", $"{appId.ToString(CultureInfo.InvariantCulture)}.json");
        var cached = JsonFile.Read<CachedPercentages>(path);
        var age = cached == null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - cached.FetchedAt;
        if (cached != null && age >= TimeSpan.Zero && age < MaxAge)
        {
            return GlobalPercentResult.From(cached.Percentages);
        }

        try
        {
            var url = $"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={appId.ToString(CultureInfo.InvariantCulture)}";
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);

            Dictionary<string, double>? percentages;
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                // What the API returns for games with no achievements (or no stats).
                percentages = null;
            }
            else
            {
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                percentages = ParseResponse(json);
            }

            JsonFile.Write(path, new CachedPercentages { FetchedAt = DateTimeOffset.UtcNow, Percentages = percentages });
            return GlobalPercentResult.From(percentages);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return GlobalPercentResult.Failed;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            // Offline, timed out, or a malformed response: serve the stale copy if there is one.
            Diagnostics.Log($"Couldn't fetch global achievement percentages for {appId}: {e.Message}");
            return cached == null ? GlobalPercentResult.Failed : GlobalPercentResult.From(cached.Percentages);
        }
    }

    /// <summary>
    /// Parses {"achievementpercentages":{"achievements":[{"name":"...","percent":12.3}]}}, where
    /// percent may be a number or a string. Returns an empty dictionary if there's no list.
    /// </summary>
    internal static Dictionary<string, double> ParseResponse(string json)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.TryGetProperty("achievementpercentages", out var container) == false ||
            container.ValueKind != JsonValueKind.Object ||
            container.TryGetProperty("achievements", out var achievements) == false ||
            achievements.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in achievements.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                item.TryGetProperty("name", out var nameElement) == false ||
                nameElement.ValueKind != JsonValueKind.String ||
                item.TryGetProperty("percent", out var percentElement) == false)
            {
                continue;
            }

            var name = nameElement.GetString();
            double percent;
            if (percentElement.ValueKind == JsonValueKind.Number)
            {
                percent = percentElement.GetDouble();
            }
            else if (percentElement.ValueKind != JsonValueKind.String ||
                double.TryParse(percentElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out percent) == false)
            {
                continue;
            }

            if (string.IsNullOrEmpty(name) == false && double.IsFinite(percent))
            {
                result[name] = Math.Clamp(percent, 0, 100);
            }
        }

        return result;
    }

    private sealed class CachedPercentages
    {
        public DateTimeOffset FetchedAt { get; set; }

        /// <summary>Null when the game has no global stats.</summary>
        public Dictionary<string, double>? Percentages { get; set; }
    }
}
