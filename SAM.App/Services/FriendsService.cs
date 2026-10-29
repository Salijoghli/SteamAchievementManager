using System.Collections.Concurrent;
using System.Globalization;

namespace SAM.App.Services;

/// <summary>A Steam friend of the logged-in user.</summary>
public sealed record SteamFriend(ulong SteamId, string Name, string? AvatarUrl, bool IsOnline);

/// <summary>Why a friend's achievements couldn't be read.</summary>
public enum FriendAchievementsStatus
{
    Ok,

    /// <summary>The friend's profile or game details are private.</summary>
    Private,

    /// <summary>The friend doesn't own the game (or has never played it).</summary>
    NotOwned,

    /// <summary>Network or parse failure; try again later.</summary>
    Failed,
}

/// <summary>A friend's achievement state for one game.</summary>
public sealed record FriendAchievements(
    ulong SteamId,
    uint AppId,
    FriendAchievementsStatus Status,
    IReadOnlyDictionary<string, DateTimeOffset?> Unlocked);

/// <summary>
/// The user's Steam friends (from the running Steam client) and their achievements for a game
/// (from their public Steam Community profile; no API key needed).
/// </summary>
/// <remarks>
/// Community pages are read anonymously, so a friend whose profile is "friends only" looks
/// <see cref="FriendAchievementsStatus.Private"/>. Steam's stats page shows a game the friend doesn't
/// own exactly like one with nothing unlocked, so both are <see cref="FriendAchievementsStatus.NotOwned"/>.
/// Avatar URLs come from each friend's profile XML, fetched in the background after
/// <see cref="GetFriends"/> and cached for a week in <c>cache\users\&lt;id&gt;\friends\avatars.json</c>;
/// <see cref="AvatarsUpdated"/> says when new ones arrived.
/// </remarks>
public sealed class FriendsService(SteamSession steam, AppCache cache, HttpClient http)
{
    private static readonly TimeSpan AvatarMaxAge = TimeSpan.FromDays(7);

    private readonly SteamCommunityFetcher _fetcher = new(SteamCommunityFetcher.CreateHttpClient(http));
    private readonly string _directory = Path.Combine(cache.UserCacheDirectory, "friends");
    private readonly object _avatarLock = new();
    private FriendAchievementsClient? _achievements;
    private ConcurrentDictionary<ulong, CachedAvatar>? _avatars;
    private Task? _avatarRefresh;

    /// <summary>
    /// Raised (on a thread-pool thread) when avatar URLs for friends returned by
    /// <see cref="GetFriends"/> were fetched; call <see cref="GetFriends"/> again to get them.
    /// </summary>
    public event EventHandler? AvatarsUpdated;

    private FriendAchievementsClient Achievements =>
        this._achievements ??= new FriendAchievementsClient(this._fetcher, this._directory);

    /// <summary>
    /// The user's friends (regular friends only), sorted online-first then by name. Empty if Steam
    /// can't be asked. Call on the UI thread (Steam client calls).
    /// </summary>
    public IReadOnlyList<SteamFriend> GetFriends()
    {
        IReadOnlyList<(ulong Id, string Name, bool Online)> raw;
        try
        {
            raw = steam.GetFriends();
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't read the friends list from Steam", e);
            return [];
        }

        var avatars = this.LoadAvatars();
        var now = DateTimeOffset.UtcNow;
        var missing = new List<ulong>();
        var friends = new List<SteamFriend>(raw.Count);
        foreach (var (id, name, online) in raw)
        {
            avatars.TryGetValue(id, out var avatar);
            if (avatar == null || IsFresh(avatar, now) == false)
            {
                missing.Add(id);
            }

            friends.Add(new SteamFriend(id, name, avatar?.Url, online));
        }

        if (missing.Count > 0)
        {
            this.StartAvatarRefresh(missing);
        }

        return Sort(friends);
    }

    /// <summary>
    /// A friend's unlocked achievements for <paramref name="appId"/> (achievement API name → unlock
    /// time when known). Cached for 30 minutes. Never throws except OperationCanceledException.
    /// </summary>
    /// <remarks>Achievement names are compared case-insensitively (the community page may differ in case from the schema).</remarks>
    public Task<FriendAchievements> GetAchievementsAsync(ulong friendSteamId, uint appId, CancellationToken cancellationToken) =>
        this.Achievements.GetAsync(friendSteamId, appId, cancellationToken);

    /// <summary>The friend's medium (64 px) avatar URL, from the cache or their profile. Null if unknown.</summary>
    public async Task<string?> GetAvatarUrlAsync(ulong friendSteamId, CancellationToken cancellationToken)
    {
        var avatars = this.LoadAvatars();
        if (avatars.TryGetValue(friendSteamId, out var cached) && IsFresh(cached, DateTimeOffset.UtcNow))
        {
            return cached.Url;
        }

        var url = await this.FetchAvatarAsync(friendSteamId, cancellationToken).ConfigureAwait(false);
        if (url != null)
        {
            this.SaveAvatars();
        }

        return url ?? cached?.Url;
    }

    /// <summary>Online first, then by name (current culture, ignoring case), then by ID.</summary>
    internal static IReadOnlyList<SteamFriend> Sort(IEnumerable<SteamFriend> friends) => friends
        .OrderByDescending(f => f.IsOnline)
        .ThenBy(f => f.Name, StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true))
        .ThenBy(f => f.SteamId)
        .ToList();

    private static bool IsFresh(CachedAvatar avatar, DateTimeOffset now)
    {
        var age = now - avatar.FetchedAt;
        return age >= TimeSpan.Zero && age < AvatarMaxAge;
    }

    private string AvatarsPath => Path.Combine(this._directory, "avatars.json");

    private ConcurrentDictionary<ulong, CachedAvatar> LoadAvatars()
    {
        if (this._avatars != null)
        {
            return this._avatars;
        }

        var loaded = JsonFile.Read<Dictionary<string, CachedAvatar>>(this.AvatarsPath);
        var avatars = new ConcurrentDictionary<ulong, CachedAvatar>();
        foreach (var (key, value) in loaded ?? new())
        {
            if (ulong.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && value?.Url != null)
            {
                avatars[id] = value;
            }
        }

        return Interlocked.CompareExchange(ref this._avatars, avatars, null) ?? avatars;
    }

    private void StartAvatarRefresh(IReadOnlyList<ulong> ids)
    {
        lock (this._avatarLock)
        {
            if (this._avatarRefresh is { IsCompleted: false } || this._fetcher.IsBackingOff)
            {
                return;
            }

            this._avatarRefresh = Task.Run(() => this.RefreshAvatarsAsync(ids));
        }
    }

    private async Task RefreshAvatarsAsync(IReadOnlyList<ulong> ids)
    {
        try
        {
            var results = await Task.WhenAll(ids.Select(id => this.FetchAvatarAsync(id, CancellationToken.None))).ConfigureAwait(false);
            if (results.Any(url => url != null))
            {
                this.SaveAvatars();
                this.AvatarsUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't refresh friends' avatars", e);
        }
    }

    /// <summary>Downloads the avatar URL into the in-memory cache. Null when it couldn't be read.</summary>
    private async Task<string?> FetchAvatarAsync(ulong id, CancellationToken cancellationToken)
    {
        var response = await this._fetcher.GetAsync(SteamCommunityXml.ProfileUri(id), cancellationToken).ConfigureAwait(false);
        var url = response.Outcome == CommunityFetchOutcome.Ok ? SteamCommunityXml.ParseAvatarUrl(response.Body!) : null;
        if (url == null)
        {
            return null;
        }

        this.LoadAvatars()[id] = new CachedAvatar { Url = url, FetchedAt = DateTimeOffset.UtcNow };
        return url;
    }

    private void SaveAvatars()
    {
        var avatars = this.LoadAvatars();
        lock (this._avatarLock)
        {
            JsonFile.Write(this.AvatarsPath, avatars.ToDictionary(p => p.Key.ToString(CultureInfo.InvariantCulture), p => p.Value));
        }
    }

    private sealed class CachedAvatar
    {
        public string? Url { get; set; }

        public DateTimeOffset FetchedAt { get; set; }
    }
}
