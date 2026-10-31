using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using Microsoft.UI.Dispatching;
using SAM.App.Models;

namespace SAM.App.Services.Autopilot;

/// <summary>
/// The headless Autopilot worker process (<c>SAM.exe &lt;appid&gt; --autopilot-worker &lt;parent pid&gt;</c>).
/// Keeps a Steam client connection open for one App ID, which is what makes Steam show the game as
/// running and count its playtime, and serves the engine's requests (see <see cref="AutopilotProtocol"/>)
/// on a current-user-only named pipe. Unlocks mirror the command-line commit: backup, raise linked
/// stats, set achievements, store once, undo on failure, history (Source = Farm).
/// </summary>
/// <remarks>
/// Exits when told to, when the parent (library) process exits, or when no engine has been
/// connected for <see cref="DisconnectedTimeout"/>. In dry-run mode (<c>--autopilot-dry-run</c>) it logs
/// intended changes instead of making them (and writes no backups or history).
/// </remarks>
public sealed class AutopilotWorker
{
    /// <summary>How long the worker keeps running without a connected engine.</summary>
    internal static readonly TimeSpan DisconnectedTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Extra time allowed for the first connection (the engine connects right after start).</summary>
    private static readonly TimeSpan FirstConnectTimeout = TimeSpan.FromSeconds(120);

    private readonly uint _appId;
    private readonly int _parentProcessId;
    private readonly AppSettings _settings;
    private readonly bool _dryRun;
    private readonly CancellationTokenSource _stop = new();

    private AppServices? _services;
    private GameSchema? _schema;
    private string _gameName = "";
    private DateTimeOffset _lastContact = DateTimeOffset.Now;
    private bool _connected;

    /// <summary>Set when the game's own window opened: the worker stops and exits with ExitWindowOpen.</summary>
    private bool _windowOpened;
    private bool _everConnected;

    /// <summary>Dry run: achievements "unlocked" this session, reported as unlocked so plans move on.</summary>
    private readonly HashSet<string> _dryRunUnlocked = new(StringComparer.Ordinal);

    private AutopilotWorker(uint appId, int parentProcessId, AppSettings settings, bool dryRun)
    {
        this._appId = appId;
        this._dryRun = dryRun;
        this._parentProcessId = parentProcessId;
        this._settings = settings;
    }

    /// <summary>
    /// Runs the worker until it's stopped and returns the process exit code (see
    /// <see cref="AutopilotProtocol"/>). Call on the UI thread; it never shows a window. Startup
    /// errors are also written to stderr (the engine redirects it) as one line.
    /// </summary>
    /// <param name="dryRun">Log intended changes instead of making them (the engine passes <c>--autopilot-dry-run</c>).</param>
    public static Task<int> RunAsync(uint appId, int parentProcessId, AppSettings settings, bool dryRun) =>
        new AutopilotWorker(appId, parentProcessId, settings, dryRun).RunAsync();

    private async Task<int> RunAsync()
    {
        // The game's own window is open: running it here too would let two processes change its
        // achievements behind each other's back.
        if (IsWindowOpen(this._appId))
        {
            return Fail(AutopilotProtocol.ExitWindowOpen, Strings.Get("AutopilotEngine_WindowOpen"));
        }

        using var mutex = new Mutex(true, AutopilotProtocol.MutexName(this._appId), out var createdNew);
        if (createdNew == false)
        {
            return Fail(AutopilotProtocol.ExitAlreadyRunning, Strings.Get("AutopilotEngine_AlreadyRunning"));
        }

        Process? parent = null;
        try
        {
            try
            {
                parent = Process.GetProcessById(this._parentProcessId);
                using var self = Process.GetCurrentProcess();
                if (parent.StartTime > self.StartTime)
                {
                    // The library exited and its process ID was reused by something newer.
                    return AutopilotProtocol.ExitSuccess;
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The library is already gone: nobody to work for.
                return AutopilotProtocol.ExitSuccess;
            }

            SteamSession steam;
            try
            {
                steam = SteamSession.Initialize(this._appId);
            }
            catch (SteamUnavailableException e)
            {
                return Fail(AutopilotProtocol.ExitSteamFailed, e.Message);
            }

            try
            {
                this._services = new AppServices(steam, this._settings);
                this._gameName = steam.GetAppData(this._appId, "name") ?? $"App {this._appId.ToString(CultureInfo.InvariantCulture)}";

                try
                {
                    await steam.RequestUserStatsAsync();
                }
                catch (SteamUnavailableException e)
                {
                    return Fail(AutopilotProtocol.ExitSteamFailed, e.Message);
                }

                this._schema = await Task.Run(() => this._services.Schemas.Load(this._appId));
                if (this._schema == null)
                {
                    return Fail(AutopilotProtocol.ExitNoSchema, Strings.Get("AutopilotEngine_NoSchema"));
                }

                if (this._dryRun)
                {
                    Diagnostics.Log($"Autopilot dry run: worker for {this._appId} ({this._gameName}) started; unlocks will only be logged");
                }

                var watchdog = DispatcherQueue.GetForCurrentThread().CreateTimer();
                watchdog.Interval = TimeSpan.FromSeconds(2);
                watchdog.Tick += (_, _) => this.CheckWatchdog(parent);
                watchdog.Start();
                try
                {
                    await this.ServeAsync(steam.SteamId);
                }
                finally
                {
                    watchdog.Stop();
                }

                return this._windowOpened ? AutopilotProtocol.ExitWindowOpen : AutopilotProtocol.ExitSuccess;
            }
            finally
            {
                steam.Dispose();
            }
        }
        finally
        {
            parent?.Dispose();
            mutex.ReleaseMutex();
        }
    }

    private static int Fail(int code, string message)
    {
        Diagnostics.Log($"Autopilot worker exiting with {code}: {message}");
        try
        {
            // The engine reads this line to show why the game couldn't run.
            Console.Error.WriteLine(message.ReplaceLineEndings(" "));
            Console.Error.Flush();
        }
        catch (Exception)
        {
            // No stderr (started by hand): the log has it.
        }

        return code;
    }

    /// <summary>Whether the game's own SAM window (its single-instance event) exists.</summary>
    internal static bool IsWindowOpen(uint appId)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(AutopilotProtocol.WindowEventName(appId), out var handle))
            {
                handle.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private void CheckWatchdog(Process parent)
    {
        bool parentExited;
        try
        {
            parentExited = parent.HasExited;
        }
        catch (Exception)
        {
            parentExited = true;
        }

        var limit = this._everConnected ? DisconnectedTimeout : FirstConnectTimeout;
        if (parentExited)
        {
            Diagnostics.Log($"Autopilot worker for {this._appId}: the library exited; stopping");
            this._stop.Cancel();
        }
        else if (this._connected == false && DateTimeOffset.Now - this._lastContact > limit)
        {
            Diagnostics.Log($"Autopilot worker for {this._appId}: no engine connected for {limit.TotalSeconds:0} s; stopping");
            this._stop.Cancel();
        }
        else if (this._windowOpened == false && IsWindowOpen(this._appId))
        {
            // The game's window waits for this worker to let go before it commits anything. An
            // unlock in progress still finishes and answers: only the wait for the next request
            // is cancelled.
            Diagnostics.Log($"Autopilot worker for {this._appId}: the game's window opened; handing the game over");
            this._windowOpened = true;
            this._stop.Cancel();
        }
    }

    /// <summary>Accepts one engine connection at a time until stopped.</summary>
    private async Task ServeAsync(ulong steamId)
    {
        var token = this._stop.Token;
        while (token.IsCancellationRequested == false)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    AutopilotProtocol.PipeName(steamId, this._appId),
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    64 * 1024,
                    64 * 1024);
            }
            catch (IOException e)
            {
                Diagnostics.Log($"Autopilot worker for {this._appId} couldn't create its pipe", e);
                return;
            }

            await using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException e)
                {
                    Diagnostics.Log($"Autopilot worker for {this._appId}: pipe connection failed", e);
                    continue;
                }

                this._connected = true;
                this._everConnected = true;
                this._lastContact = DateTimeOffset.Now;
                try
                {
                    if (await this.ServeConnectionAsync(pipe, token))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // The engine went away; wait for it to reconnect.
                }
                finally
                {
                    this._connected = false;
                    this._lastContact = DateTimeOffset.Now;
                }
            }
        }
    }

    /// <summary>Handles requests until the engine disconnects (false) or asks the worker to stop (true).</summary>
    private async Task<bool> ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var encoding = new UTF8Encoding(false);
        using var reader = new StreamReader(pipe, encoding, false, 4096, leaveOpen: true);
        var writer = new StreamWriter(pipe, encoding, 4096, leaveOpen: true) { NewLine = "\n" };
        while (true)
        {
            var line = await reader.ReadLineAsync(token);
            if (line == null)
            {
                return false;
            }

            this._lastContact = DateTimeOffset.Now;
            var request = AutopilotProtocol.ParseRequest(line);
            AutopilotResponse response;
            try
            {
                response = await this.HandleAsync(request);
            }
            catch (Exception e)
            {
                Diagnostics.Log($"Autopilot worker for {this._appId}: request \"{request?.Type}\" failed", e);
                response = new AutopilotResponse { Ok = false, Message = e.Message };
            }

            response.AppId = this._appId;
            response.DryRun = this._dryRun;

            // Not cancellable: "stop" cancels the token, and its answer must still go out. But an
            // engine that's shutting down may not read it (the write then blocks), so after "stop"
            // the worker waits only briefly and exits anyway (the writer is then left undisposed:
            // disposing would flush, and the process is about to end).
            var write = WriteLineAsync(writer, AutopilotProtocol.Serialize(response));
            if (request?.Type == AutopilotProtocol.Stop)
            {
                await Task.WhenAny(write, Task.Delay(1000));
                return true;
            }

            await write;
        }
    }

    private static async Task WriteLineAsync(StreamWriter writer, string line)
    {
        await writer.WriteLineAsync(line);
        await writer.FlushAsync(CancellationToken.None);
    }

    private async Task<AutopilotResponse> HandleAsync(AutopilotRequest? request)
    {
        switch (request?.Type)
        {
            case AutopilotProtocol.Ping:
                return new AutopilotResponse { Ok = true };
            case AutopilotProtocol.Stop:
                this._stop.Cancel();
                return new AutopilotResponse { Ok = true };
            case AutopilotProtocol.State:
                return this.GetState();
            case AutopilotProtocol.Unlock:
                return await this.UnlockAsync(request);
            default:
                return new AutopilotResponse { Ok = false, Message = Strings.Get("AutopilotEngine_UnknownRequest") };
        }
    }

    private AutopilotResponse GetState()
    {
        var steam = this._services!.Steam;
        var schema = this._schema!;
        var achievements = new List<AutopilotAchievementState>(schema.Achievements.Count);
        foreach (var achievement in schema.Achievements)
        {
            if (steam.TryGetAchievement(achievement.Id, out var achieved, out var time))
            {
                var dryUnlocked = achieved == false && this._dryRunUnlocked.Contains(achievement.Id);
                achievements.Add(new AutopilotAchievementState(achievement.Id, achieved || dryUnlocked, dryUnlocked ? DateTimeOffset.Now : time));
            }
        }

        var stats = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var stat in schema.Stats)
        {
            if (TryGetStat(steam, stat, out var value))
            {
                stats[stat.Id] = value;
            }
        }

        return new AutopilotResponse { Ok = true, Achievements = achievements, Stats = stats };
    }

    private static bool TryGetStat(SteamSession steam, StatDefinition stat, out double value)
    {
        if (stat.Kind == StatKind.Integer)
        {
            var ok = steam.TryGetStat(stat.Id, out int intValue);
            value = intValue;
            return ok;
        }

        var result = steam.TryGetStat(stat.Id, out float floatValue);
        value = floatValue;
        return result;
    }

    /// <summary>A change staged in Steam's local state, and how to put it back.</summary>
    private sealed record StagedChange(string Id, Func<bool> Undo);

    private async Task<AutopilotResponse> UnlockAsync(AutopilotRequest request)
    {
        var services = this._services!;
        var steam = services.Steam;
        var schema = this._schema!;

        if (IsWindowOpen(this._appId))
        {
            return new AutopilotResponse { Ok = false, Message = Strings.Get("AutopilotEngine_WindowOpen") };
        }

        // Validate every requested ID before touching anything (mirrors CliRunner).
        var byId = schema.Achievements.GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var targets = new List<AchievementDefinition>();
        foreach (var id in request.Ids ?? [])
        {
            if (byId.TryGetValue(id, out var definition) == false)
            {
                return new AutopilotResponse { Ok = false, Message = Strings.Format("AutopilotEngine_UnknownAchievement", id) };
            }

            if (definition.IsProtected)
            {
                return new AutopilotResponse { Ok = false, Message = Strings.Format("AutopilotEngine_ProtectedAchievement", definition.Name) };
            }

            if (targets.Contains(definition) == false)
            {
                targets.Add(definition);
            }
        }

        var changes = new List<AchievementDefinition>();
        foreach (var definition in targets)
        {
            if (steam.TryGetAchievement(definition.Id, out var achieved, out _) == false)
            {
                return new AutopilotResponse { Ok = false, Message = Strings.Format("AutopilotEngine_NoAchievementState", definition.Name) };
            }

            if (achieved == false && this._dryRunUnlocked.Contains(definition.Id) == false)
            {
                changes.Add(definition);
            }
        }

        // Linked stats: only raise, never lower, and only what Steam lets a client set.
        var statsById = schema.Stats.GroupBy(s => s.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var statChanges = new List<(StatDefinition Definition, double From, double To)>();
        foreach (var (statId, target) in request.Stats ?? [])
        {
            if (statsById.TryGetValue(statId, out var stat) == false || TryGetStat(steam, stat, out var current) == false)
            {
                Diagnostics.Log($"Autopilot worker for {this._appId}: skipping unknown stat {statId}");
                continue;
            }

            var value = ValidateStatTarget(stat, current, target, out var reason);
            if (value == null)
            {
                if (reason != null)
                {
                    Diagnostics.Log($"Autopilot worker for {this._appId}: not setting stat {statId} to {target.ToString(CultureInfo.InvariantCulture)}: {reason}");
                }

                continue;
            }

            statChanges.Add((stat, current, value.Value));
        }

        if (changes.Count == 0 && statChanges.Count == 0)
        {
            return new AutopilotResponse { Ok = true, Unlocked = [], StatChanges = [], Message = Strings.Get("AutopilotEngine_NothingToUnlock") };
        }

        var unlockedIds = changes.Select(c => c.Id).ToList();
        var statRecords = statChanges.Select(s => new AutopilotStatChange(s.Definition.Id, s.From, s.To)).ToList();
        var names = string.Join(", ", changes.Select(c => c.Name));

        if (this._dryRun)
        {
            Diagnostics.Log(
                $"Autopilot dry run for {this._appId} ({this._gameName}): would " +
                (request.Backup ? "back up, then " : "") +
                $"set stats [{string.Join(", ", statRecords.Select(s => $"{s.Id}: {s.From.ToString(CultureInfo.InvariantCulture)} -> {s.To.ToString(CultureInfo.InvariantCulture)}"))}] " +
                $"and unlock [{string.Join(", ", unlockedIds)}]");
            this._dryRunUnlocked.UnionWith(unlockedIds);
            return new AutopilotResponse
            {
                Ok = true,
                Unlocked = unlockedIds,
                StatChanges = statRecords,
                Message = Strings.Plural("AutopilotEngine_UnlockedMessage", unlockedIds.Count, names) + Strings.Get("AutopilotEngine_DryRunSuffix"),
            };
        }

        if (request.Backup)
        {
            try
            {
                services.Backups.Save(this.CreateSnapshot());
            }
            catch (Exception e)
            {
                Diagnostics.Log($"Autopilot backup for {this._appId} failed", e);
                return new AutopilotResponse { Ok = false, Message = Strings.Format("AutopilotEngine_BackupFailed", e.Message) };
            }
        }

        var staged = new List<StagedChange>();
        string? failure = null;
        try
        {
            foreach (var (stat, from, to) in statChanges)
            {
                var id = stat.Id;
                bool set;
                Func<bool> undo;
                if (stat.Kind == StatKind.Integer)
                {
                    set = steam.SetStat(id, (int)to);
                    undo = () => steam.SetStat(id, (int)from);
                }
                else
                {
                    set = steam.SetStat(id, (float)to);
                    undo = () => steam.SetStat(id, (float)from);
                }

                if (set == false)
                {
                    failure = Strings.Format("AutopilotEngine_StatRefused", stat.DisplayName, id);
                    break;
                }

                staged.Add(new StagedChange(id, undo));
            }

            if (failure == null)
            {
                foreach (var definition in changes)
                {
                    var id = definition.Id;
                    if (steam.SetAchievement(id, true) == false)
                    {
                        failure = Strings.Format("AutopilotEngine_AchievementRefused", definition.Name, id);
                        break;
                    }

                    staged.Add(new StagedChange(id, () => steam.SetAchievement(id, false)));
                }
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Autopilot: staging changes for {this._appId} failed", e);
            failure = e.Message;
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
                this.AppendHistory(unlockedIds, statRecords, false, e.Message);
                return new AutopilotResponse { Ok = false, Uncertain = true, Message = e.Message };
            }
        }

        if (failure != null)
        {
            var message = this.FailAndUndo(staged, failure);
            this.AppendHistory(unlockedIds, statRecords, false, message);
            return new AutopilotResponse { Ok = false, Message = message };
        }

        this.AppendHistory(unlockedIds, statRecords, true, null);
        return new AutopilotResponse
        {
            Ok = true,
            Unlocked = unlockedIds,
            StatChanges = statRecords,
            Message = Strings.Plural("AutopilotEngine_UnlockedMessage", unlockedIds.Count, names),
        };
    }

    /// <summary>
    /// The value to set <paramref name="stat"/> to so it reaches <paramref name="target"/>, or null
    /// to leave it alone (with <paramref name="reason"/> when it's not simply already there). Never
    /// lowers a stat; integer stats need whole targets; protected, server-set and average-rate stats
    /// are never set; targets above the schema maximum are clamped to it.
    /// </summary>
    internal static double? ValidateStatTarget(StatDefinition stat, double current, double target, out string? reason)
    {
        reason = null;
        if (double.IsFinite(target) == false)
        {
            reason = "not a number";
            return null;
        }

        if (stat.IsProtected || stat.SetByTrustedGameServer)
        {
            reason = "protected";
            return null;
        }

        if (stat.Kind == StatKind.AverageRate)
        {
            reason = "average-rate stats can't be set directly";
            return null;
        }

        if (stat.Kind == StatKind.Integer)
        {
            if (Math.Abs(target - Math.Round(target)) > 1e-9)
            {
                reason = "integer stat with a fractional target";
                return null;
            }

            target = Math.Round(target);
            if (target > int.MaxValue)
            {
                target = int.MaxValue;
            }
        }

        if (stat.MaxValue > stat.MinValue && target > stat.MaxValue)
        {
            target = stat.MaxValue;
        }

        // Only ever raise: whether or not the stat is increment-only, lowering progress is never wanted.
        return target > current ? target : null;
    }

    private GameSnapshot CreateSnapshot()
    {
        var steam = this._services!.Steam;
        var schema = this._schema!;
        var snapshot = new GameSnapshot
        {
            AppId = this._appId,
            GameName = this._gameName,
            Reason = "Before Autopilot unlock",
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
            if (TryGetStat(steam, stat, out var value))
            {
                snapshot.Stats.Add(new StatSnapshotEntry(stat.Id, stat.Kind, value));
            }
        }

        return snapshot;
    }

    /// <summary>Undoes the staged changes (newest first) and builds the failure message.</summary>
    private string FailAndUndo(List<StagedChange> staged, string failure)
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
                Diagnostics.Log($"Undoing the staged Autopilot change to {staged[i].Id} for app {this._appId} failed", e);
                ok = false;
            }

            if (ok == false)
            {
                notUndone.Add(staged[i].Id);
            }
        }

        var message = Strings.Format("AutopilotEngine_NothingStored", failure);
        if (notUndone.Count > 0)
        {
            notUndone.Reverse();
            message += " " + Strings.Format("AutopilotEngine_NotUndone", string.Join(", ", notUndone));
        }

        return message;
    }

    private void AppendHistory(List<string> unlocked, List<AutopilotStatChange> stats, bool succeeded, string? message)
    {
        try
        {
            this._services!.History.Append(new CommitRecord(
                DateTimeOffset.Now,
                this._appId,
                this._gameName,
                unlocked,
                [],
                stats.Select(s => new StatChange(s.Id, s.From, s.To)).ToList(),
                CommitSource.Farm,
                succeeded,
                message));
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't record the Autopilot unlock for {this._appId} in the history", e);
        }
    }
}
