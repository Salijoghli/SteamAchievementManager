using System.Globalization;
using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.Infrastructure;

/// <summary>
/// Runs <c>SAM.exe &lt;appid&gt; … --commit</c> without a window: validates the requested
/// achievement IDs against the game's schema, backs the game up, stages every change and stores
/// them in one go. All or nothing: if Steam refuses any change, the staged ones are undone.
/// </summary>
public static class CliRunner
{
    /// <summary>
    /// Prints <paramref name="text"/> to the console, or shows it in a notification when the
    /// app wasn't started from one (unless <paramref name="quiet"/>).
    /// </summary>
    public static void Report(AppSettings settings, string title, string text, bool isError, bool quiet = false)
    {
        if (ConsoleOutput.IsAvailable)
        {
            if (isError)
            {
                ConsoleOutput.WriteError(text);
            }
            else
            {
                ConsoleOutput.WriteLine(text);
            }

            return;
        }

        if (quiet == false && NotificationService.Register(null))
        {
            new NotificationService(settings).Show(title, text);
        }
    }

    /// <summary>
    /// Commits the changes in <paramref name="command"/> (which must be in
    /// <see cref="LaunchMode.Commit"/> mode) and reports the result. Must be called on the UI
    /// thread (Steam callbacks are pumped there). Returns the process exit code.
    /// </summary>
    public static async Task<int> RunAsync(CommandLine command, AppSettings settings)
    {
        if (command.Mode != LaunchMode.Commit)
        {
            throw new ArgumentException("Not a --commit command line.", nameof(command));
        }

        var appId = command.AppId;
        var title = $"SAM: App {appId.ToString(CultureInfo.InvariantCulture)}";

        // If the game's window is open in SAM, committing behind its back would leave the window
        // showing stale state. Holding the claim for the run also stops a window from opening
        // for this game meanwhile (the launch says a command-line commit is running instead).
        using var instance = SingleInstance.TryClaimForCommandLine(appId, out var commitRunning);
        if (instance == null)
        {
            Report(settings, title,
                commitRunning
                    ? $"Another command-line commit for App ID {appId} is running. Wait for it to finish, then run the command again. Nothing was changed."
                    : $"SAM's window for App ID {appId} is open. Close the game's window first, then run the command again. Nothing was changed.",
                isError: true, command.Quiet);
            return CommandLine.ExitBusy;
        }

        SteamSession steam;
        try
        {
            steam = SteamSession.Initialize(appId);
        }
        catch (SteamUnavailableException e)
        {
            Report(settings, title, $"Can't connect to Steam: {e.Message}", isError: true, command.Quiet);
            return CommandLine.ExitFailed;
        }

        try
        {
            var services = new AppServices(steam, settings);
            var gameName = steam.GetAppData(appId, "name") ?? $"App {appId.ToString(CultureInfo.InvariantCulture)}";
            title = $"SAM: {gameName}";
            var (code, message) = await CommitAsync(command, services, gameName);
            Report(settings, title, message, isError: code != CommandLine.ExitSuccess, command.Quiet);
            return code;
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Command-line commit for {appId} failed", e);
            Report(settings, title, $"Something went wrong: {e.Message}", isError: true, command.Quiet);
            return CommandLine.ExitFailed;
        }
        finally
        {
            steam.Dispose();
        }
    }

    /// <summary>A change staged in Steam's local state, and how to put it back.</summary>
    private sealed record StagedChange(string Id, Func<bool> Undo);

    private static async Task<(int Code, string Message)> CommitAsync(CommandLine command, AppServices services, string gameName)
    {
        var steam = services.Steam;
        var appId = command.AppId;

        try
        {
            await steam.RequestUserStatsAsync();
        }
        catch (SteamUnavailableException e)
        {
            return (CommandLine.ExitFailed, $"Couldn't load your stats for {gameName}: {e.Message}");
        }

        var schema = await Task.Run(() => services.Schemas.Load(appId));
        if (schema == null)
        {
            return (CommandLine.ExitFailed,
                $"{gameName} has no achievement schema on this PC. Open the game in SAM (or run it once) so Steam downloads it.");
        }

        // Resolve and validate every requested ID before touching anything.
        var byId = new Dictionary<string, AchievementDefinition>(StringComparer.Ordinal);
        foreach (var achievement in schema.Achievements)
        {
            byId.TryAdd(achievement.Id, achievement);
        }

        var unknown = new List<string>();
        var protectedIds = new List<string>();
        var conflicting = new List<string>();
        var targets = new List<(AchievementDefinition Definition, bool Unlock)>();

        void Resolve(IEnumerable<string> ids, bool unlock)
        {
            foreach (var id in ids)
            {
                var definition = Find(byId, schema.Achievements, id);
                if (definition == null)
                {
                    unknown.Add(id);
                }
                else if (definition.IsProtected)
                {
                    protectedIds.Add(definition.Id);
                }
                else if (targets.FindIndex(t => t.Definition.Id == definition.Id) is var index and >= 0)
                {
                    // E.g. "--unlock ach_win --lock ACH_WIN": both resolve to the same achievement.
                    if (targets[index].Unlock != unlock && conflicting.Contains(definition.Id) == false)
                    {
                        conflicting.Add(definition.Id);
                    }
                }
                else
                {
                    targets.Add((definition, unlock));
                }
            }
        }

        Resolve(command.Unlock, unlock: true);
        Resolve(command.Lock, unlock: false);

        var skippedProtected = 0;
        if (command.UnlockAll)
        {
            foreach (var definition in schema.Achievements)
            {
                if (definition.IsProtected)
                {
                    skippedProtected++;
                }
                else if (targets.Any(t => t.Definition.Id == definition.Id) == false)
                {
                    targets.Add((definition, true));
                }
            }
        }

        if (unknown.Count > 0 || protectedIds.Count > 0 || conflicting.Count > 0)
        {
            var errors = new List<string>();
            if (conflicting.Count > 0)
            {
                errors.Add($"Can't both unlock and lock {string.Join(", ", conflicting)}.");
            }

            if (unknown.Count > 0)
            {
                errors.Add($"{gameName} has no achievement {string.Join(", ", unknown)}.");
            }

            if (protectedIds.Count > 0)
            {
                errors.Add($"{string.Join(", ", protectedIds)} {(protectedIds.Count == 1 ? "is" : "are")} protected and can't be changed.");
            }

            return (CommandLine.ExitInvalidArguments, $"{string.Join(" ", errors)} Nothing was changed.");
        }

        // Only what actually differs from Steam's current state.
        var changes = new List<(AchievementDefinition Definition, bool Unlock, bool Original)>();
        foreach (var (definition, unlock) in targets)
        {
            if (steam.TryGetAchievement(definition.Id, out var achieved, out _) == false)
            {
                return (CommandLine.ExitFailed, $"Steam didn't return the state of {definition.Id}. Nothing was changed.");
            }

            if (achieved != unlock)
            {
                changes.Add((definition, unlock, achieved));
            }
        }

        if (changes.Count == 0)
        {
            return (CommandLine.ExitSuccess, $"{gameName}: nothing to change; the achievements are already in that state.");
        }

        if (command.NoBackup == false)
        {
            try
            {
                services.Backups.Save(CreateSnapshot(services, schema, gameName));
            }
            catch (Exception e)
            {
                Diagnostics.Log($"Command-line backup for {appId} failed", e);
                return (CommandLine.ExitFailed,
                    $"Couldn't back up {gameName} before committing ({e.Message}). Nothing was changed. " +
                    "Use --no-backup to commit without a backup.");
            }
        }

        var unlocked = changes.Where(c => c.Unlock).Select(c => c.Definition.Id).ToList();
        var locked = changes.Where(c => c.Unlock == false).Select(c => c.Definition.Id).ToList();

        var staged = new List<StagedChange>();
        string? failure = null;
        try
        {
            foreach (var (definition, unlock, original) in changes)
            {
                if (steam.SetAchievement(definition.Id, unlock) == false)
                {
                    failure = $"Steam refused to {(unlock ? "unlock" : "lock")} \"{definition.Name}\" ({definition.Id}).";
                    break;
                }

                var id = definition.Id;
                staged.Add(new StagedChange(id, () => steam.SetAchievement(id, original)));
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Setting achievements for app {appId} from the command line failed", e);
            failure = $"Something went wrong while preparing the changes: {e.Message}";
        }

        if (failure == null)
        {
            try
            {
                await steam.StoreStatsAsync();
            }
            catch (SteamStoreException e) when (e.Failure is SteamStoreFailure.Refused or SteamStoreFailure.Rejected)
            {
                failure = e.Message;
            }
            catch (SteamStoreException e)
            {
                // Timed out: the upload may still go through, so don't undo anything.
                AppendHistory(services, appId, gameName, unlocked, locked, false, e.Message);
                return (CommandLine.ExitFailed, e.Message);
            }
        }

        if (failure != null)
        {
            var message = FailAndUndo(appId, staged, failure);
            AppendHistory(services, appId, gameName, unlocked, locked, false, message);
            return (CommandLine.ExitFailed, message);
        }

        var parts = new List<string>();
        if (unlocked.Count > 0)
        {
            parts.Add($"Unlocked {Plural(unlocked.Count, "achievement")}: {string.Join(", ", unlocked)}.");
        }

        if (locked.Count > 0)
        {
            parts.Add($"Locked {Plural(locked.Count, "achievement")}: {string.Join(", ", locked)}.");
        }

        if (skippedProtected > 0)
        {
            parts.Add($"Skipped {Plural(skippedProtected, "protected achievement")}.");
        }

        var summary = $"{gameName}: {string.Join(" ", parts)}";
        AppendHistory(services, appId, gameName, unlocked, locked, true, null);
        return (CommandLine.ExitSuccess, summary);
    }

    /// <summary>Exact ID first; otherwise a case-insensitive match if there's exactly one.</summary>
    private static AchievementDefinition? Find(
        Dictionary<string, AchievementDefinition> byId, IReadOnlyList<AchievementDefinition> all, string id)
    {
        if (byId.TryGetValue(id, out var exact))
        {
            return exact;
        }

        var matches = all.Where(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static GameSnapshot CreateSnapshot(AppServices services, GameSchema schema, string gameName)
    {
        var steam = services.Steam;
        var snapshot = new GameSnapshot
        {
            AppId = schema.AppId,
            GameName = gameName,
            Reason = "Before command-line commit",
        };

        foreach (var achievement in schema.Achievements)
        {
            if (steam.TryGetAchievement(achievement.Id, out var achieved, out var unlockTime))
            {
                snapshot.Achievements.Add(new AchievementSnapshotEntry(achievement.Id, achieved, unlockTime));
            }
        }

        foreach (var stat in schema.Stats)
        {
            if (stat.Kind == StatKind.Integer)
            {
                if (steam.TryGetStat(stat.Id, out int value))
                {
                    snapshot.Stats.Add(new StatSnapshotEntry(stat.Id, stat.Kind, value));
                }
            }
            else if (steam.TryGetStat(stat.Id, out float value))
            {
                snapshot.Stats.Add(new StatSnapshotEntry(stat.Id, stat.Kind, value));
            }
        }

        return snapshot;
    }

    /// <summary>Undoes the staged changes (newest first) and builds the failure message.</summary>
    private static string FailAndUndo(uint appId, List<StagedChange> staged, string failure)
    {
        var notUndone = new List<string>();
        for (var i = staged.Count - 1; i >= 0; i--)
        {
            bool ok;
            try
            {
                ok = staged[i].Undo();
            }
            catch (Exception e)
            {
                Diagnostics.Log($"Undoing the staged change to {staged[i].Id} for app {appId} failed", e);
                ok = false;
            }

            if (ok == false)
            {
                notUndone.Add(staged[i].Id);
            }
        }

        var message = $"Nothing was stored. {failure}";
        if (notUndone.Count > 0)
        {
            notUndone.Reverse();
            message += $" SAM couldn't undo the change already made to {string.Join(", ", notUndone)}; " +
                "Steam may upload it the next time this game's stats are stored.";
        }

        return message;
    }

    private static void AppendHistory(
        AppServices services, uint appId, string gameName, List<string> unlocked, List<string> locked, bool succeeded, string? message)
    {
        try
        {
            services.History.Append(new CommitRecord(
                DateTimeOffset.Now, appId, gameName, unlocked, locked, [], CommitSource.CommandLine, succeeded, message));
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't record the command-line commit for {appId} in the history", e);
        }
    }

    private static string Plural(int count, string noun) =>
        count.ToString(CultureInfo.CurrentCulture) + " " + noun + (count == 1 ? "" : "s");
}
