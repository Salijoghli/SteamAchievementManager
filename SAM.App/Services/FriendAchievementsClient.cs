using System.Collections.Concurrent;
using System.Globalization;

namespace SAM.App.Services;

/// <summary>
/// Reads friends' achievements from their public Steam Community stats pages (see
/// <see cref="SteamCommunityXml.ParseAchievements"/>) and caches each (friend, game) answer for
/// 30 minutes in memory and on disk (<c>&lt;directory&gt;\&lt;friend&gt;_&lt;appid&gt;.json</c>), so
/// game windows (separate processes) share it. Failures aren't cached; when a fetch fails an
/// older cached answer is returned if there is one. Concurrent requests for the same pair share
/// one download. Thread-safe; throws only OperationCanceledException.
/// </summary>
internal sealed class FriendAchievementsClient(SteamCommunityFetcher fetcher, string? directory, Func<DateTimeOffset>? clock = null)
{
    /// <summary>How long an answer is served without asking Steam again.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<(ulong, uint), Cached> _memory = new();
    private readonly ConcurrentDictionary<(ulong, uint), Lazy<Task<FriendAchievements>>> _inFlight = new();

    public async Task<FriendAchievements> GetAsync(ulong friendSteamId, uint appId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = (friendSteamId, appId);
        var cached = this.GetCached(key);
        if (cached != null && this.IsFresh(cached))
        {
            return cached.ToResult(friendSteamId, appId);
        }

        // The download isn't tied to one caller's token: if that caller gives up, others (and the
        // cache) still get the answer.
        var lazy = this._inFlight.GetOrAdd(key, k => new Lazy<Task<FriendAchievements>>(() => this.FetchAsync(k)));
        return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Forgets the in-memory answers (the disk copies stay until they're 30 minutes old).</summary>
    public void ClearMemory() => this._memory.Clear();

    private async Task<FriendAchievements> FetchAsync((ulong Friend, uint App) key)
    {
        try
        {
            var (status, unlocked) = await this.DownloadAsync(key.Friend, key.App).ConfigureAwait(false);
            if (status == FriendAchievementsStatus.Failed)
            {
                var stale = this.GetCached(key);
                return stale != null
                    ? stale.ToResult(key.Friend, key.App)
                    : new FriendAchievements(key.Friend, key.App, FriendAchievementsStatus.Failed, unlocked);
            }

            var entry = new Cached
            {
                FetchedAt = this._clock(),
                Status = status,
                Unlocked = unlocked.ToDictionary(p => p.Key, p => p.Value?.ToUnixTimeSeconds(), StringComparer.OrdinalIgnoreCase),
            };
            this._memory[key] = entry;
            var path = this.GetPath(key);
            if (path != null)
            {
                JsonFile.Write(path, entry);
            }

            return new FriendAchievements(key.Friend, key.App, status, unlocked);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Diagnostics.Log($"Couldn't read a friend's achievements for {key.App}", e);
            return new FriendAchievements(key.Friend, key.App, FriendAchievementsStatus.Failed, new Dictionary<string, DateTimeOffset?>());
        }
        finally
        {
            this._inFlight.TryRemove(key, out _);
        }
    }

    private async Task<(FriendAchievementsStatus, Dictionary<string, DateTimeOffset?>)> DownloadAsync(ulong friend, uint app)
    {
        // Games with a community name (730 → CSGO) redirect to /stats/<Name>?tab=achievements; the
        // fetcher follows that keeping xml=1.
        var response = await fetcher.GetAsync(SteamCommunityXml.AchievementsUri(friend, app), CancellationToken.None).ConfigureAwait(false);
        if (response.Outcome != CommunityFetchOutcome.Ok)
        {
            return (FriendAchievementsStatus.Failed, new Dictionary<string, DateTimeOffset?>());
        }

        if (response.FinalUri != null && SteamCommunityXml.IsStatsPage(response.FinalUri) == false)
        {
            // Steam sends stats URLs for games without stats (or unknown games) to the profile.
            return (FriendAchievementsStatus.NotOwned, new Dictionary<string, DateTimeOffset?>());
        }

        return SteamCommunityXml.LooksLikeXml(response.Body!)
            ? SteamCommunityXml.ParseAchievements(response.Body!)
            : (FriendAchievementsStatus.Failed, new Dictionary<string, DateTimeOffset?>());
    }

    private bool IsFresh(Cached cached)
    {
        var age = this._clock() - cached.FetchedAt;
        return age >= TimeSpan.Zero && age < MaxAge;
    }

    private Cached? GetCached((ulong, uint) key)
    {
        if (this._memory.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var path = this.GetPath(key);
        if (path == null || File.Exists(path) == false)
        {
            return null;
        }

        cached = JsonFile.Read<Cached>(path);
        if (cached?.Unlocked == null || Enum.IsDefined(cached.Status) == false || cached.Status == FriendAchievementsStatus.Failed)
        {
            return null;
        }

        cached.Unlocked = new Dictionary<string, long?>(cached.Unlocked, StringComparer.OrdinalIgnoreCase);
        return this._memory.GetOrAdd(key, cached);
    }

    private string? GetPath((ulong Friend, uint App) key) => directory == null
        ? null
        : Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{key.Friend}_{key.App}.json"));

    /// <summary>One cached answer; also the disk format.</summary>
    internal sealed class Cached
    {
        public DateTimeOffset FetchedAt { get; set; }

        public FriendAchievementsStatus Status { get; set; }

        /// <summary>Achievement API name → unlock time (Unix seconds) when known.</summary>
        public Dictionary<string, long?> Unlocked { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public FriendAchievements ToResult(ulong friend, uint app) => new(
            friend,
            app,
            this.Status,
            this.Unlocked.ToDictionary(
                p => p.Key,
                p => p.Value is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null,
                StringComparer.OrdinalIgnoreCase));
    }
}
