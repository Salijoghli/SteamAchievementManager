using System.Globalization;
using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>
/// Reads achievement progress from Steam's local stats cache
/// (appcache\stats\UserGameStats_&lt;accountid&gt;_&lt;appid&gt;.bin), with no network calls.
/// </summary>
public sealed class ProgressService(SteamSession steam)
{
    /// <summary>
    /// Unlocked/total for <paramref name="appId"/>. Total comes from the schema; unlocked from
    /// the cache file's achievement bitfields, counting only bits defined in the schema.
    /// Returns (0, total) when the schema exists but Steam has no cached stats for the user
    /// (never played), and null when there's no schema (unknown).
    /// </summary>
    /// <remarks>Also returns null if the cache file exists but can't be read (e.g. mid-write). Thread-safe.</remarks>
    public GameProgress? GetCachedProgress(uint appId, GameSchema? schema)
    {
        if (schema == null)
        {
            return null;
        }

        try
        {
            return Compute(GetStatsPath(steam.SteamInstallPath, steam.AccountId, appId), schema);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't read cached progress for {appId}", e);
            return null;
        }
    }

    internal static string GetStatsPath(string steamInstallPath, uint accountId, uint appId) =>
        Path.Combine(steamInstallPath, "appcache", "stats", $"UserGameStats_{accountId}_{appId}.bin");

    /// <summary>
    /// Counts the schema's achievements whose bit is set in the stats cache file. The file's
    /// layout is cache → &lt;stat block id&gt; → data (int32 bitfield), plus AchievementTimes
    /// (bit index → unix time) and bookkeeping values (crc, PendingChanges).
    /// </summary>
    internal static GameProgress? Compute(string statsPath, GameSchema schema)
    {
        var total = schema.Achievements.Count;
        if (File.Exists(statsPath) == false)
        {
            return new GameProgress(0, total);
        }

        var root = KeyValue.LoadAsBinary(statsPath);
        if (root == null)
        {
            return null;
        }

        var cache = root["cache"];
        var unlocked = 0;
        foreach (var achievement in schema.Achievements)
        {
            if (achievement.BitIndex is < 0 or > 31 || achievement.StatBlockId < 0)
            {
                continue;
            }

            var data = cache[achievement.StatBlockId.ToString(CultureInfo.InvariantCulture)]["data"].AsInteger(0);
            if ((data & (1 << achievement.BitIndex)) != 0)
            {
                unlocked++;
            }
        }

        return new GameProgress(unlocked, total);
    }
}
