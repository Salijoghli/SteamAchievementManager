using System.Globalization;
using System.Net;
using System.Text.Json;

namespace SAM.App.Services;

/// <summary>
/// Global unlock percentages from the public Steam Web API
/// (ISteamUserStats/GetGlobalAchievementPercentagesForApp, no key needed), cached for 24 hours
/// in cache\shared\percentages\&lt;appid&gt;.json. Falls back to a stale cache when offline.
/// </summary>
public sealed class GlobalPercentService(AppCache cache, HttpClient http)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>Achievement ID → percent of players (0-100). Null if unavailable.</summary>
    /// <remarks>
    /// Games without achievements (the API answers 403) are cached as "no data" too. Returns
    /// null rather than throwing when <paramref name="cancellationToken"/> is cancelled.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, double>?> GetAsync(uint appId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(cache.SharedDirectory, "percentages", $"{appId.ToString(CultureInfo.InvariantCulture)}.json");
        var cached = JsonFile.Read<CachedPercentages>(path);
        var age = cached == null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - cached.FetchedAt;
        if (cached != null && age >= TimeSpan.Zero && age < MaxAge)
        {
            return ToResult(cached.Percentages);
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
            return ToResult(percentages);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            // Offline, timed out, or a malformed response: serve the stale copy if there is one.
            Diagnostics.Log($"Couldn't fetch global achievement percentages for {appId}: {e.Message}");
            return ToResult(cached?.Percentages);
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

    private static IReadOnlyDictionary<string, double>? ToResult(Dictionary<string, double>? percentages) =>
        percentages == null || percentages.Count == 0 ? null : percentages;

    private sealed class CachedPercentages
    {
        public DateTimeOffset FetchedAt { get; set; }

        /// <summary>Null when the game has no global stats.</summary>
        public Dictionary<string, double>? Percentages { get; set; }
    }
}
