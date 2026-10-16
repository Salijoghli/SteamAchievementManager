using System.Globalization;
using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>Reads last-played time and total playtime from userdata\&lt;accountid&gt;\config\localconfig.vdf.</summary>
public sealed class PlaytimeService(SteamSession steam)
{
    /// <summary>Per App ID. Empty if the file can't be read. Safe to call off the UI thread.</summary>
    public IReadOnlyDictionary<uint, PlaytimeInfo> Load()
    {
        try
        {
            return Parse(GetConfigPath(steam.SteamInstallPath, steam.AccountId));
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't read playtime from localconfig.vdf", e);
            return new Dictionary<uint, PlaytimeInfo>();
        }
    }

    internal static string GetConfigPath(string steamInstallPath, uint accountId) =>
        Path.Combine(steamInstallPath, "userdata", accountId.ToString(CultureInfo.InvariantCulture), "config", "localconfig.vdf");

    /// <summary>
    /// Reads UserLocalConfigStore → Software → Valve → Steam → apps → &lt;appid&gt; → LastPlayed
    /// (unix seconds) / Playtime (minutes), matching keys case-insensitively. Apps with neither
    /// value are left out. Empty if the file doesn't exist.
    /// </summary>
    internal static Dictionary<uint, PlaytimeInfo> Parse(string vdfPath)
    {
        var result = new Dictionary<uint, PlaytimeInfo>();
        if (File.Exists(vdfPath) == false)
        {
            return result;
        }

        var root = TextVdf.ParseFile(vdfPath);
        foreach (var apps in root.Path("UserLocalConfigStore", "Software", "Valve", "Steam", "apps"))
        {
            foreach (var (key, value) in apps.Entries)
            {
                if (value is not TextVdfNode app ||
                    uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) == false)
                {
                    continue;
                }

                DateTimeOffset? lastPlayed = null;
                if (long.TryParse(app.Value("LastPlayed"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
                    seconds > 0)
                {
                    lastPlayed = DateTimeOffset.FromUnixTimeSeconds(seconds);
                }

                var hasPlaytime = long.TryParse(app.Value("Playtime"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes);
                if (lastPlayed == null && hasPlaytime == false)
                {
                    continue;
                }

                var info = new PlaytimeInfo(lastPlayed, TimeSpan.FromMinutes(Math.Max(0, minutes)));

                // The same app can appear under differently-cased paths; keep the most informative values.
                if (result.TryGetValue(appId, out var existing))
                {
                    info = new PlaytimeInfo(
                        Max(existing.LastPlayed, info.LastPlayed),
                        existing.Playtime > info.Playtime ? existing.Playtime : info.Playtime);
                }

                result[appId] = info;
            }
        }

        return result;
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a == null ? b : b == null ? a : (a > b ? a : b);
}
