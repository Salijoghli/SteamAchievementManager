using System.Collections.Concurrent;
using System.Globalization;
using SAM.App.Models;
using SAM.App.Services.Autopilot;
using SAM.App.ViewModels;

namespace SAM.App.Services;

/// <summary>
/// The achievement bits set in a stats cache file (UserGameStats_&lt;accountid&gt;_&lt;appid&gt;.bin),
/// as keys <c>(stat block id &lt;&lt; 5) | bit</c>. Read without a schema, so every game can be
/// snapshotted cheaply at startup; the schema only names the bits that changed.
/// </summary>
internal static class UnlockSnapshot
{
    /// <summary>The key of <paramref name="achievement"/>'s bit, or null if it has none.</summary>
    public static long? KeyOf(AchievementDefinition achievement) =>
        achievement.BitIndex is < 0 or > 31 || achievement.StatBlockId < 0
            ? null
            : ((long)achievement.StatBlockId << 5) | (uint)achievement.BitIndex;

    /// <summary>
    /// The set bits in <paramref name="statsPath"/>: empty when the file doesn't exist (never
    /// played), null when it can't be read (e.g. Steam is writing it). Never throws.
    /// </summary>
    public static HashSet<long>? Read(string statsPath)
    {
        try
        {
            if (File.Exists(statsPath) == false)
            {
                return [];
            }

            var root = KeyValue.LoadAsBinary(statsPath);
            if (root == null)
            {
                return null;
            }

            var bits = new HashSet<long>();
            foreach (var block in root["cache"].Children ?? [])
            {
                // Bookkeeping values (crc, PendingChanges) aren't numbered blocks with data.
                if (int.TryParse(block.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var blockId) == false)
                {
                    continue;
                }

                var data = block["data"].AsInteger(0);
                for (var bit = 0; bit < 32; bit++)
                {
                    if ((data & (1 << bit)) != 0)
                    {
                        bits.Add(((long)blockId << 5) | (uint)bit);
                    }
                }
            }

            return bits;
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't read {statsPath}", e);
            return null;
        }
    }

    /// <summary>
    /// The schema achievements set in <paramref name="current"/> but not in <paramref name="previous"/>,
    /// in schema order. Nothing when there's no previous snapshot (the first look is only a baseline).
    /// </summary>
    public static IReadOnlyList<AchievementDefinition> NewlyUnlocked(
        IReadOnlySet<long>? previous,
        IReadOnlySet<long> current,
        GameSchema schema)
    {
        if (previous == null)
        {
            return [];
        }

        return schema.Achievements
            .Where(a => KeyOf(a) is { } key && current.Contains(key) && previous.Contains(key) == false)
            .ToList();
    }

    /// <summary>
    /// Drops the achievements SAM itself unlocked for <paramref name="appId"/>: those in a commit
    /// record (game window, command line, Autopilot, restore) from the last <paramref name="window"/>.
    /// </summary>
    public static IReadOnlyList<AchievementDefinition> ExceptSamUnlocks(
        IReadOnlyList<AchievementDefinition> unlocked,
        IEnumerable<CommitRecord> history,
        uint appId,
        DateTimeOffset now,
        TimeSpan window)
    {
        if (unlocked.Count == 0)
        {
            return unlocked;
        }

        var bySam = history
            .Where(r => r.AppId == appId && r.Time >= now - window && r.Time <= now + window)
            .SelectMany(r => r.Unlocked ?? [])
            .ToHashSet(StringComparer.Ordinal);
        return bySam.Count == 0 ? unlocked : unlocked.Where(a => bySam.Contains(a.Id) == false).ToList();
    }
}

/// <summary>A Windows notification to show for unlocks.</summary>
internal readonly record struct UnlockNotice(string Title, string Message);

/// <summary>Turns a game's new unlocks into notifications (one each, or one summary for a burst).</summary>
internal static class UnlockNotices
{
    /// <summary>More unlocks than this at once become one summary notification.</summary>
    public const int MaxIndividual = 3;

    /// <param name="game">The game's name.</param>
    /// <param name="unlocks">The new unlocks with their global percentages (null when unknown).</param>
    public static IReadOnlyList<UnlockNotice> Build(string game, IReadOnlyList<(AchievementDefinition Achievement, double? Percent)> unlocks)
    {
        if (unlocks.Count == 0)
        {
            return [];
        }

        if (unlocks.Count > MaxIndividual)
        {
            var names = string.Join(", ", unlocks.Take(MaxIndividual).Select(u => u.Achievement.Name));
            var message = Strings.Format("Notify_UnlockBatchMore", names, (unlocks.Count - MaxIndividual).ToString("N0", CultureInfo.CurrentCulture));
            return [new UnlockNotice(Strings.Plural("Notify_UnlockBatch", unlocks.Count, game), message)];
        }

        return unlocks
            .Select(u => new UnlockNotice(Strings.Format("Notify_UnlockTitle", u.Achievement.Name, game), Describe(u.Achievement, u.Percent)))
            .ToList();
    }

    private static string Describe(AchievementDefinition achievement, double? percent)
    {
        if (percent is { } p)
        {
            return Strings.Format("Notify_UnlockRarity", AchievementViewModel.RarityName(AchievementViewModel.GetRarity(p)), p);
        }

        return string.IsNullOrWhiteSpace(achievement.Description) ? Strings.Get("Notify_UnlockNoRarity") : achievement.Description;
    }
}

/// <summary>
/// Notifies when the user unlocks achievements by playing, while the library (or its tray icon)
/// runs. Watches Steam's stats cache folder, keeps a snapshot of each game's unlocked bits (all
/// games are read in the background at startup), and diffs a game's file whenever Steam rewrites
/// it. Unlocks SAM made itself (they're in the commit history) and games Autopilot runs are skipped.
/// </summary>
public sealed class UnlockNotifier : IDisposable
{
    /// <summary>Commits by SAM within this window of a change count as the change's cause.</summary>
    internal static readonly TimeSpan SamCommitWindow = TimeSpan.FromMinutes(5);

    /// <summary>Lets the SAM process that committed record its history before the change is judged.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan PercentTimeout = TimeSpan.FromSeconds(8);

    private readonly AppServices _services;
    private readonly ConcurrentDictionary<uint, HashSet<long>> _snapshots = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _statsDirectory;
    private readonly Task _initialScan;
    private Dictionary<uint, string>? _names;
    private SteamStatsWatcher? _watcher;

    public UnlockNotifier(AppServices services)
    {
        this._services = services;
        this._statsDirectory = Path.Combine(services.Steam.SteamInstallPath, "appcache", "stats");
        this._initialScan = Task.Run(this.SnapshotAll);
        try
        {
            this._watcher = new SteamStatsWatcher(services.Steam.SteamInstallPath, services.Steam.AccountId);
            this._watcher.Changed += this.OnStatsChanged;
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't watch Steam's stats folder for unlock notifications", e);
        }
    }

    public void Dispose()
    {
        if (this._lifetime.IsCancellationRequested)
        {
            return;
        }

        this._lifetime.Cancel();
        this._watcher?.Dispose();
        this._watcher = null;
    }

    private bool IsEnabled => this._services.Settings.NotifyOnUnlock && this._services.Settings.ShowNotifications;

    /// <summary>The baseline: every stats file of this account, read once.</summary>
    private void SnapshotAll()
    {
        try
        {
            foreach (var (appId, path) in this.EnumerateStatsFiles())
            {
                if (this._lifetime.IsCancellationRequested)
                {
                    return;
                }

                if (UnlockSnapshot.Read(path) is { } bits)
                {
                    // A change handled meanwhile has the newer snapshot.
                    this._snapshots.TryAdd(appId, bits);
                }
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't snapshot unlocked achievements", e);
        }
    }

    private IEnumerable<(uint AppId, string Path)> EnumerateStatsFiles()
    {
        if (Directory.Exists(this._statsDirectory) == false)
        {
            yield break;
        }

        var accountId = this._services.Steam.AccountId;
        var pattern = $"UserGameStats_{accountId.ToString(CultureInfo.InvariantCulture)}_*.bin";
        foreach (var path in Directory.EnumerateFiles(this._statsDirectory, pattern))
        {
            if (SteamStatsWatcher.ParseFileName(Path.GetFileName(path), accountId) is { } appId)
            {
                yield return (appId, path);
            }
        }
    }

    private async void OnStatsChanged(object? sender, IReadOnlyList<uint> appIds)
    {
        var token = this._lifetime.Token;
        try
        {
            await this._initialScan.WaitAsync(token);
            await Task.Delay(SettleDelay, token);

            // Empty: changes may have been missed, so look at every game.
            var games = appIds.Count > 0
                ? appIds.Select(id => (AppId: id, Path: ProgressService.GetStatsPath(this._services.Steam.SteamInstallPath, this._services.Steam.AccountId, id))).ToList()
                : this.EnumerateStatsFiles().ToList();

            await this._gate.WaitAsync(token);
            try
            {
                foreach (var (appId, path) in games)
                {
                    token.ThrowIfCancellationRequested();
                    await this.CheckGameAsync(appId, path, token);
                }
            }
            finally
            {
                this._gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Diagnostics.Log("Checking for new unlocks failed", e);
        }
    }

    private async Task CheckGameAsync(uint appId, string path, CancellationToken token)
    {
        if (UnlockSnapshot.Read(path) is not { } current)
        {
            // Mid-write: the next change event re-reads it.
            return;
        }

        this._snapshots.TryGetValue(appId, out var previous);
        this._snapshots[appId] = current;
        if (previous == null || this.IsEnabled == false || current.IsSubsetOf(previous))
        {
            return;
        }

        // Autopilot's own notifications cover the games it runs.
        if (AutopilotEngine.IsGameRunningUnderAutopilot(appId))
        {
            return;
        }

        var schema = this._services.Schemas.Load(appId);
        if (schema == null)
        {
            return;
        }

        var unlocked = UnlockSnapshot.NewlyUnlocked(previous, current, schema);
        unlocked = UnlockSnapshot.ExceptSamUnlocks(unlocked, this._services.History.Load(200), appId, DateTimeOffset.Now, SamCommitWindow);
        if (unlocked.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, double>? percents = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(PercentTimeout);
            percents = await this._services.GlobalPercents.GetAsync(appId, timeout.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Diagnostics.Log($"Couldn't get global percentages for {appId}", e);
        }

        token.ThrowIfCancellationRequested();
        var notices = UnlockNotices.Build(
            this.NameOf(appId),
            unlocked.Select(a => (a, percents != null && percents.TryGetValue(a.Id, out var p) ? (double?)p : null)).ToList());
        Diagnostics.Log($"Noticed {unlocked.Count} new unlock(s) in {appId}");
        foreach (var notice in notices)
        {
            this._services.Notifications.Show(notice.Title, notice.Message);
        }
    }

    private string NameOf(uint appId)
    {
        if (this._names == null || this._names.ContainsKey(appId) == false)
        {
            try
            {
                this._names = (this._services.Games.LoadCached() ?? [])
                    .GroupBy(g => g.AppId)
                    .ToDictionary(g => g.Key, g => g.First().Name);
            }
            catch (Exception e)
            {
                Diagnostics.Log("Couldn't read the cached game list", e);
                this._names ??= [];
            }
        }

        return this._names.TryGetValue(appId, out var name) && string.IsNullOrWhiteSpace(name) == false
            ? name
            : Strings.Format("Notify_UnknownGame", appId);
    }
}
