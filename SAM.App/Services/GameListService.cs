using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.UI.Dispatching;
using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>
/// The user's owned games. Uses SAM's community game list (https://gib.me/sam/games.xml,
/// cached in cache\shared\games.xml, refreshed when older than 24 hours, stale copy used when
/// offline) filtered by Steam ownership. The resulting owned list is cached per account in
/// cache\users\&lt;steamid64&gt;\games.json so the library can render instantly on startup.
/// </summary>
/// <remarks>
/// Games added with <see cref="TryAddOwnedApp"/> are also kept in
/// cache\users\&lt;steamid64&gt;\extra-apps.json so a refresh doesn't drop them.
/// </remarks>
public sealed class GameListService(AppCache cache, SteamSession steam, HttpClient http)
{
    internal const string GameListUrl = "https://gib.me/sam/games.xml";

    private static readonly TimeSpan MaxListAge = TimeSpan.FromHours(24);

    /// <summary>How many App IDs to check per UI-thread turn during a refresh.</summary>
    private const int OwnershipChunkSize = 500;

    private readonly object _fileLock = new();

    // AppServices creates this service on the UI thread; Steam calls are marshalled back to it
    // because the UI thread uses the same Steam pipe (callback pump, art lookups).
    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();

    private string OwnedListPath => Path.Combine(cache.UserCacheDirectory, "games.json");

    private string ExtraAppsPath => Path.Combine(cache.UserCacheDirectory, "extra-apps.json");

    /// <summary>The owned list saved by the last refresh, or null if there isn't one yet.</summary>
    public IReadOnlyList<GameEntry>? LoadCached()
    {
        lock (this._fileLock)
        {
            return JsonFile.Read<List<GameEntry>>(this.OwnedListPath);
        }
    }

    /// <summary>
    /// Re-checks ownership against the (possibly re-downloaded) game list and saves the result.
    /// The download and parsing run off the UI thread; the Steam ownership checks run on the UI
    /// thread (the one that created this service) in chunks of <see cref="OwnershipChunkSize"/>
    /// at low priority, so input and rendering keep running between chunks. If the list can't
    /// be downloaded and there's no cached copy, throws <see cref="HttpRequestException"/>.
    /// </summary>
    /// <remarks>Throws <see cref="OperationCanceledException"/> if cancelled.</remarks>
    public async Task<IReadOnlyList<GameEntry>> RefreshAsync(CancellationToken cancellationToken)
    {
        var xmlPath = Path.Combine(cache.SharedDirectory, "games.xml");
        var candidates = await GetGameListAsync(xmlPath, http, cancellationToken).ConfigureAwait(false);
        var listed = candidates.Select(c => c.AppId).ToHashSet();

        List<GameEntry> extras;
        lock (this._fileLock)
        {
            extras = JsonFile.Read<List<GameEntry>>(this.ExtraAppsPath) ?? [];
        }

        // Manually added games that the community list doesn't know (yet).
        var unlistedExtras = extras.Where(e => listed.Contains(e.AppId) == false).ToList();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var owned = new Dictionary<uint, GameEntry>();
        for (var offset = 0; offset < candidates.Count; offset += OwnershipChunkSize)
        {
            var start = offset;
            await this.RunOnUiThreadAsync(
                () =>
                {
                    var end = Math.Min(start + OwnershipChunkSize, candidates.Count);
                    for (var i = start; i < end; i++)
                    {
                        var (appId, type) = candidates[i];
                        if (owned.ContainsKey(appId) || steam.OwnsApp(appId) == false)
                        {
                            continue;
                        }

                        var name = steam.GetAppData(appId, "name");
                        if (string.IsNullOrEmpty(name) == false)
                        {
                            owned[appId] = new GameEntry(appId, name, type);
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        await this.RunOnUiThreadAsync(
            () =>
            {
                foreach (var extra in unlistedExtras)
                {
                    if (owned.ContainsKey(extra.AppId) == false && steam.OwnsApp(extra.AppId))
                    {
                        owned[extra.AppId] = extra with { Name = steam.GetAppData(extra.AppId, "name") ?? extra.Name };
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);

        Diagnostics.Log(string.Create(
            CultureInfo.InvariantCulture,
            $"Checked ownership of {candidates.Count} apps in {stopwatch.ElapsedMilliseconds} ms ({owned.Count} owned)"));

        lock (this._fileLock)
        {
            if (unlistedExtras.Count != extras.Count)
            {
                // Drop extras that are now in the community list (handled above). Re-read so an
                // app added by TryAddOwnedApp while the checks ran isn't lost.
                var current = JsonFile.Read<List<GameEntry>>(this.ExtraAppsPath) ?? [];
                JsonFile.Write(this.ExtraAppsPath, current.Where(e => listed.Contains(e.AppId) == false).ToList());
            }

            var result = owned.Values
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(g => g.AppId)
                .ToList();
            JsonFile.Write(this.OwnedListPath, result);
            return result;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread at low priority and waits for it, or runs
    /// it inline when already on the UI thread or there's no dispatcher.
    /// </summary>
    private Task RunOnUiThreadAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (this._dispatcher == null || this._dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = this._dispatcher.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception e)
                {
                    completion.TrySetException(e);
                }
            });
        if (queued == false)
        {
            // The UI thread is shutting down.
            completion.TrySetCanceled(cancellationToken);
        }

        return completion.Task;
    }

    /// <summary>
    /// For App IDs not in the community list: returns an entry if the user owns it (name from
    /// Steam's app data, type "normal") and adds it to the cached owned list, else null.
    /// </summary>
    /// <remarks>If Steam has no name for the app, the entry is named "App &lt;id&gt;".</remarks>
    public GameEntry? TryAddOwnedApp(uint appId)
    {
        try
        {
            if (appId == 0 || steam.OwnsApp(appId) == false)
            {
                return null;
            }

            var entry = new GameEntry(appId, steam.GetAppData(appId, "name") ?? $"App {appId.ToString(CultureInfo.InvariantCulture)}", "normal");

            lock (this._fileLock)
            {
                var owned = JsonFile.Read<List<GameEntry>>(this.OwnedListPath) ?? [];
                var existing = owned.Find(g => g.AppId == appId);
                if (existing != null)
                {
                    return existing;
                }

                owned.Add(entry);
                JsonFile.Write(this.OwnedListPath, owned);

                var extras = JsonFile.Read<List<GameEntry>>(this.ExtraAppsPath) ?? [];
                extras.RemoveAll(g => g.AppId == appId);
                extras.Add(entry);
                JsonFile.Write(this.ExtraAppsPath, extras);
            }

            return entry;
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't add app {appId}", e);
            return null;
        }
    }

    /// <summary>
    /// Returns the community game list from <paramref name="xmlPath"/> if it's under 24 hours
    /// old, else downloads it (saving it to <paramref name="xmlPath"/>), falling back to the stale
    /// file. Throws <see cref="HttpRequestException"/> if there's neither.
    /// </summary>
    internal static async Task<List<(uint AppId, string Type)>> GetGameListAsync(
        string xmlPath, HttpClient http, CancellationToken cancellationToken)
    {
        var file = new FileInfo(xmlPath);
        if (file.Exists && DateTime.UtcNow - file.LastWriteTimeUtc is var age && age >= TimeSpan.Zero && age < MaxListAge)
        {
            var fresh = TryParseFile(xmlPath);
            if (fresh != null)
            {
                return fresh;
            }
        }

        try
        {
            var bytes = await http.GetByteArrayAsync(GameListUrl, cancellationToken).ConfigureAwait(false);
            List<(uint, string)> list;
            using (var stream = new MemoryStream(bytes, false))
            {
                list = ParseGameList(stream);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(xmlPath)!);
                var temp = xmlPath + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes, CancellationToken.None).ConfigureAwait(false);
                File.Move(temp, xmlPath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Diagnostics.Log($"Couldn't save {xmlPath}", e);
            }

            return list;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or XmlException or FormatException)
        {
            Diagnostics.Log($"Couldn't download the game list: {e.Message}");
            var stale = file.Exists ? TryParseFile(xmlPath) : null;
            return stale ?? throw new HttpRequestException($"Couldn't download the game list: {e.Message}", e);
        }
    }

    /// <summary>Parses /games/game nodes: value = App ID, optional type attribute (default "normal").</summary>
    internal static List<(uint AppId, string Type)> ParseGameList(Stream stream)
    {
        var document = XDocument.Load(stream);
        var result = new List<(uint, string)>();
        if (document.Root is not { Name.LocalName: "games" } root)
        {
            throw new FormatException("The game list has no <games> root.");
        }

        foreach (var game in root.Elements("game"))
        {
            if (uint.TryParse(game.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var appId) == false || appId == 0)
            {
                continue;
            }

            var type = (string?)game.Attribute("type");
            result.Add((appId, string.IsNullOrEmpty(type) ? "normal" : type));
        }

        return result;
    }

    private static List<(uint AppId, string Type)>? TryParseFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ParseGameList(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException or FormatException)
        {
            Diagnostics.Log($"Couldn't read {path}", e);
            return null;
        }
    }
}
