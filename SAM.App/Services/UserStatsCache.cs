using System.Globalization;
using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>
/// Reads achievement state from Steam's local stats cache
/// (appcache\stats\UserGameStats_&lt;accountid&gt;_&lt;appid&gt;.bin). The layout is
/// cache → &lt;stat block id&gt; → data (int32 bitfield) and AchievementTimes (bit index → unix time).
/// An achievement is unlocked only if its bit is set: re-locked achievements keep their old time.
/// </summary>
internal sealed class UserStatsCache
{
    private readonly KeyValue _cache;

    private UserStatsCache(KeyValue cache) => this._cache = cache;

    /// <summary>A cache with no data, for users who never played the game.</summary>
    public static UserStatsCache Empty { get; } = new(new KeyValue());

    /// <summary>Reads <paramref name="statsPath"/>. Null if it's missing or malformed. Never throws.</summary>
    public static UserStatsCache? Load(string statsPath)
    {
        var root = KeyValue.LoadAsBinary(statsPath);
        return root == null ? null : new UserStatsCache(root["cache"]);
    }

    /// <summary>Whether <paramref name="achievement"/>'s bit is set, and its unlock time if it is and one is recorded.</summary>
    public (bool Unlocked, DateTimeOffset? Time) GetState(AchievementDefinition achievement)
    {
        if (achievement.BitIndex is < 0 or > 31 || achievement.StatBlockId < 0)
        {
            return (false, null);
        }

        var block = this._cache[achievement.StatBlockId.ToString(CultureInfo.InvariantCulture)];
        var data = block["data"].AsInteger(0);
        if ((data & (1 << achievement.BitIndex)) == 0)
        {
            return (false, null);
        }

        var seconds = block["AchievementTimes"][achievement.BitIndex.ToString(CultureInfo.InvariantCulture)].AsInteger(0);
        return (true, seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds((uint)seconds) : null);
    }
}
