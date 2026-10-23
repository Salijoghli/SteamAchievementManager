namespace SAM.App.Services;

/// <summary>
/// On-disk layout under %LocalAppData%\SteamAchievementManager:
/// <code>
/// settings.json                 app settings (never cleared)
/// sam.log                       error log
/// data\users\&lt;steamid64&gt;\      per-account user data, e.g. favorites (never cleared by "Clear cache")
/// cache\images\                 downloaded images, shared by all accounts (content doesn't depend on the user)
/// cache\shared\                 non-user downloads: game list, global achievement percentages
/// cache\users\&lt;steamid64&gt;\     per-account cache: owned games list
/// cache\last-user.txt           SteamID64 of the account that last used the cache
/// </code>
/// </summary>
public sealed class AppCache
{
    public static readonly string RootDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteamAchievementManager");

    public static readonly string CacheDirectory = Path.Combine(RootDirectory, "cache");

    public AppCache(ulong steamId)
    {
        this.SteamId = steamId;
        this.ImagesDirectory = Path.Combine(CacheDirectory, "images");
        this.SharedDirectory = Path.Combine(CacheDirectory, "shared");
        this.UserCacheDirectory = Path.Combine(CacheDirectory, "users", steamId.ToString());
        this.UserDataDirectory = Path.Combine(RootDirectory, "data", "users", steamId.ToString());

        foreach (var directory in new[] { this.ImagesDirectory, this.SharedDirectory, this.UserCacheDirectory, this.UserDataDirectory })
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Reads and writes there fail (and are handled) later; don't stop SAM from starting.
                Diagnostics.Log($"Couldn't create {directory}", e);
            }
        }
    }

    public ulong SteamId { get; }

    public string ImagesDirectory { get; }

    public string SharedDirectory { get; }

    public string UserCacheDirectory { get; }

    public string UserDataDirectory { get; }

    /// <summary>
    /// Call once at startup. If a different Steam account used the cache last time and
    /// <paramref name="keepOtherAccounts"/> is false, deletes the other accounts' cached
    /// data (not their favorites or settings). Returns true if a switch was detected.
    /// </summary>
    public bool HandleAccountSwitch(bool keepOtherAccounts)
    {
        var marker = Path.Combine(CacheDirectory, "last-user.txt");
        string? previous = null;
        try
        {
            previous = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log($"Couldn't read {marker}", e);
        }

        var current = this.SteamId.ToString();
        var switched = previous != null && previous != current;

        if (switched && keepOtherAccounts == false)
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(Path.Combine(CacheDirectory, "users")))
                {
                    if (Path.GetFileName(dir) != current)
                    {
                        TryDeleteDirectory(dir);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Diagnostics.Log("Couldn't clear other accounts' cache", e);
            }
        }

        try
        {
            File.WriteAllText(marker, current);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log($"Couldn't write {marker}", e);
        }

        return switched;
    }

    /// <summary>Total size of everything under cache\ in bytes.</summary>
    public static long GetCacheSize()
    {
        if (Directory.Exists(CacheDirectory) == false)
        {
            return 0;
        }

        return new DirectoryInfo(CacheDirectory)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(f => f.Length);
    }

    /// <summary>Deletes all cached downloads for every account. Favorites and settings are kept.</summary>
    public void ClearCache()
    {
        foreach (var dir in new[] { this.ImagesDirectory, this.SharedDirectory, Path.Combine(CacheDirectory, "users") })
        {
            TryDeleteDirectory(dir);
        }

        Directory.CreateDirectory(this.ImagesDirectory);
        Directory.CreateDirectory(this.SharedDirectory);
        Directory.CreateDirectory(this.UserCacheDirectory);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log($"Couldn't delete {path}", e);
        }
    }
}
