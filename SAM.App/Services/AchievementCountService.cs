using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace SAM.App.Services;

/// <summary>What the Steam store says about a game's achievements.</summary>
public enum StoreAchievementStatus
{
    /// <summary>The game has achievements; <see cref="StoreAchievementCount.Total"/> is how many.</summary>
    Count,

    /// <summary>The store page lists no achievements.</summary>
    None,

    /// <summary>The lookup failed (offline, rate limited, no store data): unknown.</summary>
    Failed,
}

/// <summary>A store lookup's result: its status and, for <see cref="StoreAchievementStatus.Count"/>, the number of achievements.</summary>
public readonly record struct StoreAchievementCount(StoreAchievementStatus Status, int Total)
{
    public static StoreAchievementCount None { get; } = new(StoreAchievementStatus.None, 0);

    public static StoreAchievementCount Failed { get; } = new(StoreAchievementStatus.Failed, 0);

    public static StoreAchievementCount Of(int total) =>
        total > 0 ? new(StoreAchievementStatus.Count, total) : None;
}

/// <summary>
/// Tells whether a game has achievements without its schema (which Steam only downloads once the
/// game's stats are requested), from the public store API
/// (<c>store.steampowered.com/api/appdetails?appids=&lt;id&gt;&amp;filters=achievements</c>).
/// </summary>
/// <remarks>
/// One app per request, at most two at a time. Results are cached in
/// cache\shared\achievement-counts.json: counts for 7 days, "no achievements" for 1 day. Failures
/// aren't written to disk; they're remembered in memory for a few minutes so a refresh doesn't ask
/// again right away. On HTTP 429/403 every request waits (backing off up to 5 minutes) and then
/// retries. Thread-safe; never throws (cancellation gives <see cref="StoreAchievementCount.Failed"/>).
/// </remarks>
public sealed class AchievementCountService
{
    private static readonly TimeSpan CountMaxAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan NoneMaxAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan FailureMemory = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private const int MaxAttempts = 3;

    private readonly HttpClient _http;
    private readonly string _path;
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly ConcurrentDictionary<uint, Task<StoreAchievementCount>> _inFlight = new();
    private readonly ConcurrentDictionary<uint, DateTimeOffset> _failures = new();
    private readonly object _lock = new();
    private Dictionary<uint, CachedCount>? _entries;
    private DateTimeOffset _blockedUntil;
    private TimeSpan _backoff = FirstBackoff;
    private Timer? _saveTimer;

    public AchievementCountService(AppCache cache, HttpClient http)
    {
        this._http = http;
        this._path = Path.Combine(cache.SharedDirectory, "achievement-counts.json");
    }

    /// <summary>The cached result for <paramref name="appId"/> if it's still fresh, else null. Reads the cache file on first use.</summary>
    public StoreAchievementCount? TryGetCached(uint appId)
    {
        lock (this._lock)
        {
            var entries = this.LoadEntries();
            if (entries.TryGetValue(appId, out var entry) && IsFresh(entry, DateTimeOffset.UtcNow))
            {
                return StoreAchievementCount.Of(entry.Total);
            }
        }

        return this._failures.TryGetValue(appId, out var failedAt) && DateTimeOffset.UtcNow - failedAt < FailureMemory
            ? StoreAchievementCount.Failed
            : null;
    }

    /// <summary>The cached result, or asks the store. Concurrent calls for the same game share one request.</summary>
    public Task<StoreAchievementCount> GetAsync(uint appId, CancellationToken cancellationToken)
    {
        if (this.TryGetCached(appId) is { } cached)
        {
            return Task.FromResult(cached);
        }

        return this._inFlight.GetOrAdd(appId, id => this.FetchAndForgetAsync(id, cancellationToken));
    }

    private async Task<StoreAchievementCount> FetchAndForgetAsync(uint appId, CancellationToken cancellationToken)
    {
        try
        {
            return await this.FetchAsync(appId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this._inFlight.TryRemove(appId, out _);
        }
    }

    private async Task<StoreAchievementCount> FetchAsync(uint appId, CancellationToken cancellationToken)
    {
        try
        {
            await this._slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return StoreAchievementCount.Failed;
        }

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var wait = this.GetBackoffWait();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }

                var url = $"https://store.steampowered.com/api/appdetails?appids={appId.ToString(CultureInfo.InvariantCulture)}&filters=achievements";
                using var response = await this._http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
                {
                    this.BackOff();
                    if (attempt >= MaxAttempts)
                    {
                        Diagnostics.Log($"Store achievement count for {appId}: still rate limited after {MaxAttempts} attempts");
                        return this.RememberFailure(appId);
                    }

                    continue;
                }

                response.EnsureSuccessStatusCode();
                this.ResetBackoff();
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var result = ParseResponse(json, appId);
                if (result.Status == StoreAchievementStatus.Failed)
                {
                    return this.RememberFailure(appId);
                }

                this.Store(appId, result);
                return result;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StoreAchievementCount.Failed;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Diagnostics.Log($"Couldn't get the store achievement count for {appId}: {e.Message}");
            return this.RememberFailure(appId);
        }
        finally
        {
            this._slots.Release();
        }
    }

    /// <summary>
    /// Parses <c>{"&lt;id&gt;":{"success":true,"data":{"achievements":{"total":78,...}}}}</c>. No
    /// "achievements" key (or a total of 0) means the game has none; success false, a missing app
    /// entry or malformed JSON is <see cref="StoreAchievementStatus.Failed"/>. Never throws.
    /// </summary>
    internal static StoreAchievementCount ParseResponse(string json, uint appId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out var app) == false ||
                app.ValueKind != JsonValueKind.Object ||
                app.TryGetProperty("success", out var success) == false ||
                success.ValueKind != JsonValueKind.True)
            {
                return StoreAchievementCount.Failed;
            }

            // With the filter, a game without achievements answers "data": [] (or no data at all).
            if (app.TryGetProperty("data", out var data) == false || data.ValueKind != JsonValueKind.Object ||
                data.TryGetProperty("achievements", out var achievements) == false ||
                achievements.ValueKind != JsonValueKind.Object)
            {
                return StoreAchievementCount.None;
            }

            if (achievements.TryGetProperty("total", out var total) == false)
            {
                return StoreAchievementCount.Failed;
            }

            var count = total.ValueKind switch
            {
                JsonValueKind.Number when total.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(total.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) => n,
                _ => -1,
            };
            return count < 0 ? StoreAchievementCount.Failed : StoreAchievementCount.Of(count);
        }
        catch (JsonException)
        {
            return StoreAchievementCount.Failed;
        }
    }

    /// <summary>Whether a cached entry is still usable at <paramref name="now"/>.</summary>
    internal static bool IsFresh(CachedCount entry, DateTimeOffset now)
    {
        var age = now - entry.FetchedAt;
        return age >= TimeSpan.Zero && age < (entry.Total > 0 ? CountMaxAge : NoneMaxAge);
    }

    private StoreAchievementCount RememberFailure(uint appId)
    {
        this._failures[appId] = DateTimeOffset.UtcNow;
        return StoreAchievementCount.Failed;
    }

    private TimeSpan GetBackoffWait()
    {
        lock (this._lock)
        {
            var wait = this._blockedUntil - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
    }

    private void BackOff()
    {
        lock (this._lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (this._blockedUntil > now)
            {
                // The other request hit the limit too; one backoff step is enough.
                return;
            }

            Diagnostics.Log($"Steam store rate limit: pausing achievement count lookups for {this._backoff.TotalSeconds:0} s");
            this._blockedUntil = now + this._backoff;
            this._backoff = TimeSpan.FromTicks(Math.Min(this._backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private void ResetBackoff()
    {
        lock (this._lock)
        {
            this._backoff = FirstBackoff;
        }
    }

    private Dictionary<uint, CachedCount> LoadEntries()
    {
        if (this._entries != null)
        {
            return this._entries;
        }

        var file = JsonFile.Read<CountsFile>(this._path);
        var entries = new Dictionary<uint, CachedCount>();
        foreach (var (key, value) in file?.Apps ?? [])
        {
            if (value != null && value.Total >= 0 &&
                uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var appId))
            {
                entries[appId] = value;
            }
        }

        return this._entries = entries;
    }

    private void Store(uint appId, StoreAchievementCount result)
    {
        lock (this._lock)
        {
            this.LoadEntries()[appId] = new CachedCount { Total = result.Total, FetchedAt = DateTimeOffset.UtcNow };
            this._failures.TryRemove(appId, out _);

            // Coalesce the writes of a burst of lookups into one.
            this._saveTimer ??= new Timer(_ => this.Save(), null, Timeout.Infinite, Timeout.Infinite);
            this._saveTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
    }

    private void Save()
    {
        CountsFile file;
        lock (this._lock)
        {
            var now = DateTimeOffset.UtcNow;
            file = new CountsFile
            {
                Apps = this.LoadEntries()
                    .Where(e => IsFresh(e.Value, now))
                    .ToDictionary(e => e.Key.ToString(CultureInfo.InvariantCulture), e => e.Value),
            };
        }

        JsonFile.Write(this._path, file);
    }

    /// <summary>One cached lookup: the number of achievements (0 = none) and when it was fetched.</summary>
    internal sealed class CachedCount
    {
        public int Total { get; set; }

        public DateTimeOffset FetchedAt { get; set; }
    }

    private sealed class CountsFile
    {
        public Dictionary<string, CachedCount>? Apps { get; set; }
    }
}
