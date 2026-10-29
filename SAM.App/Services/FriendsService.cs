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
/// (from their public Steam Community profile; no API key needed). TODO(friends-data): stubs.
/// </summary>
public sealed class FriendsService(SteamSession steam, AppCache cache, HttpClient http)
{
    /// <summary>
    /// The user's friends (regular friends only), sorted online-first then by name. Empty if Steam
    /// can't be asked. Call on the UI thread (Steam client calls).
    /// </summary>
    public IReadOnlyList<SteamFriend> GetFriends() => throw new NotImplementedException("TODO(friends-data)");

    /// <summary>
    /// A friend's unlocked achievements for <paramref name="appId"/> (achievement API name → unlock
    /// time when known). Cached for 30 minutes. Never throws except OperationCanceledException.
    /// </summary>
    public Task<FriendAchievements> GetAchievementsAsync(ulong friendSteamId, uint appId, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(friends-data)");
}
