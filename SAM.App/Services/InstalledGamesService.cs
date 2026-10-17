using System.Globalization;
using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>
/// Installed games: parses steamapps\libraryfolders.vdf for every Steam library folder, then
/// each folder's appmanifest_&lt;appid&gt;.acf. Thread-safe; safe to call off the UI thread.
/// </summary>
public sealed class InstalledGamesService(SteamSession steam)
{
    /// <summary>appmanifest StateFlags bit for "fully installed" (StateFullyInstalled = 4).</summary>
    internal const int StateFullyInstalled = 4;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private IReadOnlyDictionary<uint, InstalledGame>? _cached;
    private DateTime _cachedAt;

    /// <summary>
    /// Every fully installed app across all library folders, keyed by App ID. Missing or corrupt
    /// files and unreachable library drives are skipped. Results are cached for 30 seconds.
    /// </summary>
    public IReadOnlyDictionary<uint, InstalledGame> Load()
    {
        lock (this._lock)
        {
            var now = DateTime.UtcNow;
            if (this._cached != null && now - this._cachedAt < CacheDuration && now >= this._cachedAt)
            {
                return this._cached;
            }

            IReadOnlyDictionary<uint, InstalledGame> result;
            try
            {
                result = Scan(steam.SteamInstallPath);
            }
            catch (Exception e)
            {
                Diagnostics.Log("Couldn't read installed games", e);
                result = new Dictionary<uint, InstalledGame>();
            }

            this._cached = result;
            this._cachedAt = now;
            return result;
        }
    }

    /// <summary>Scans every library folder of the Steam install at <paramref name="steamInstallPath"/>.</summary>
    internal static Dictionary<uint, InstalledGame> Scan(string steamInstallPath)
    {
        var result = new Dictionary<uint, InstalledGame>();
        foreach (var library in GetLibraryFolders(steamInstallPath))
        {
            foreach (var game in ScanLibrary(library))
            {
                // An app present in two libraries (stale manifest): keep the larger install.
                if (result.TryGetValue(game.AppId, out var existing) == false || game.SizeOnDisk > existing.SizeOnDisk)
                {
                    result[game.AppId] = game;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Library folders from steamapps\libraryfolders.vdf, always including the Steam install
    /// folder itself. Handles both the current format ("0" { "path" "..." }) and the old one
    /// ("1" "D:\\Games"). Duplicates (case-insensitive, ignoring trailing separators) are removed.
    /// </summary>
    internal static List<string> GetLibraryFolders(string steamInstallPath)
    {
        var folders = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string normalized;
            try
            {
                normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            if (seen.Add(normalized))
            {
                folders.Add(normalized);
            }
        }

        Add(steamInstallPath);

        var vdfPath = Path.Combine(steamInstallPath, "steamapps", "libraryfolders.vdf");
        TextVdfNode? root = null;
        try
        {
            if (File.Exists(vdfPath))
            {
                root = TextVdf.ParseFile(vdfPath);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log("Couldn't read libraryfolders.vdf", e);
        }

        if (root != null)
        {
            foreach (var container in root.Nodes("libraryfolders"))
            {
                foreach (var (key, value) in container.Entries)
                {
                    if (uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out _) == false)
                    {
                        continue;
                    }

                    Add(value switch
                    {
                        TextVdfNode node => node.Value("path"),
                        string path => path,
                        _ => null,
                    });
                }
            }
        }

        return folders;
    }

    /// <summary>Fully installed apps from one library folder's appmanifest_*.acf files.</summary>
    internal static List<InstalledGame> ScanLibrary(string libraryPath)
    {
        var result = new List<InstalledGame>();
        string[] manifests;
        try
        {
            var steamApps = Path.Combine(libraryPath, "steamapps");
            if (Directory.Exists(steamApps) == false)
            {
                return result;
            }

            manifests = Directory.GetFiles(steamApps, "appmanifest_*.acf");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unplugged or unreachable drive.
            return result;
        }

        foreach (var manifest in manifests)
        {
            var game = ParseManifest(manifest, libraryPath);
            if (game != null)
            {
                result.Add(game);
            }
        }

        return result;
    }

    /// <summary>
    /// Parses AppState → appid, installdir, SizeOnDisk, StateFlags. Null if the file is unreadable,
    /// malformed, or the app isn't fully installed.
    /// </summary>
    internal static InstalledGame? ParseManifest(string manifestPath, string libraryPath)
    {
        TextVdfNode root;
        try
        {
            root = TextVdf.ParseFile(manifestPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var state = root.Nodes("AppState").FirstOrDefault();
        if (state == null ||
            uint.TryParse(state.Value("appid"), NumberStyles.None, CultureInfo.InvariantCulture, out var appId) == false ||
            appId == 0 ||
            int.TryParse(state.Value("StateFlags"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags) == false ||
            (flags & StateFullyInstalled) == 0)
        {
            return null;
        }

        var installDir = state.Value("installdir");
        if (string.IsNullOrWhiteSpace(installDir) || installDir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }

        _ = long.TryParse(state.Value("SizeOnDisk"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
        return new InstalledGame(appId, Path.Combine(libraryPath, "steamapps", "common", installDir), Math.Max(0, size));
    }
}
