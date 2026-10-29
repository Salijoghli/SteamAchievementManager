using System.Globalization;
using SAM.App.Services;

namespace SAM.App.ViewModels;

/// <summary>Index order of the comparison filter ComboBox.</summary>
public enum FriendCompareFilter
{
    All,
    OnlyFriend,
    OnlyMe,
    Both,
    Neither,
}

/// <summary>Pure helpers for comparing the user's achievements with a friend's (read-only).</summary>
public static class FriendComparison
{
    /// <summary>Whether a row with the given states passes <paramref name="filter"/>.</summary>
    public static bool Matches(FriendCompareFilter filter, bool iHaveIt, bool friendHasIt) => filter switch
    {
        FriendCompareFilter.OnlyFriend => friendHasIt && iHaveIt == false,
        FriendCompareFilter.OnlyMe => iHaveIt && friendHasIt == false,
        FriendCompareFilter.Both => iHaveIt && friendHasIt,
        FriendCompareFilter.Neither => iHaveIt == false && friendHasIt == false,
        _ => true,
    };

    /// <summary>
    /// The friend's unlocks keyed case-insensitively (the community page and the schema may
    /// disagree on the case of API names). Later duplicates keep the first unlock time known.
    /// </summary>
    public static Dictionary<string, DateTimeOffset?> Normalize(IReadOnlyDictionary<string, DateTimeOffset?> unlocked)
    {
        var result = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, time) in unlocked)
        {
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            if (result.TryGetValue(id, out var existing) == false || existing == null)
            {
                result[id] = time;
            }
        }

        return result;
    }

    /// <summary>"You 19/70 · Alex 45/70".</summary>
    public static string Summary(string friendName, int mine, int theirs, int total) =>
        Strings.Format(
            "Game_CompareSummary",
            mine.ToString("N0", CultureInfo.CurrentCulture),
            total.ToString("N0", CultureInfo.CurrentCulture),
            friendName,
            theirs.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>The row badge: "Alex has it · 3 Jan 2024", or without the date when unknown.</summary>
    public static string Badge(string friendName, DateTimeOffset? unlockTime) => unlockTime is { } time
        ? Strings.Format("Game_CompareFriendHasItOn", friendName, time.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture))
        : Strings.Format("Game_CompareFriendHasIt", friendName);

    /// <summary>The message for a comparison that couldn't be made, or null for <see cref="FriendAchievementsStatus.Ok"/>.</summary>
    public static string? StatusMessage(FriendAchievementsStatus status, string friendName) => status switch
    {
        FriendAchievementsStatus.Ok => null,
        FriendAchievementsStatus.Private => Strings.Format("Game_ComparePrivate", friendName),
        FriendAchievementsStatus.NotOwned => Strings.Format("Game_CompareNotOwned", friendName),
        _ => Strings.Format("Game_CompareFailed", friendName),
    };

    /// <summary>
    /// Friends whose name contains <paramref name="query"/> (any case), online first, then by
    /// name. An empty query keeps everyone.
    /// </summary>
    public static IReadOnlyList<SteamFriend> FilterFriends(IEnumerable<SteamFriend> friends, string? query)
    {
        var q = query?.Trim() ?? "";
        return friends
            .Where(f => q.Length == 0 || (f.Name ?? "").Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(f => f.IsOnline)
            .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
