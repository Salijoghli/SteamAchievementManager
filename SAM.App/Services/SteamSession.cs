using Microsoft.UI.Dispatching;
using SAM.API;

namespace SAM.App.Services;

/// <summary>Thrown when the app can't talk to the Steam client. The message is user-facing.</summary>
public class SteamUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>How a <see cref="SteamSession.StoreStatsAsync"/> call failed.</summary>
public enum SteamStoreFailure
{
    /// <summary>StoreStats returned false: nothing was sent, the changes are still staged locally.</summary>
    Refused,

    /// <summary>Steam's servers answered with an error; the changes weren't stored.</summary>
    Rejected,

    /// <summary>No answer in time; the upload may still be in progress or retried later.</summary>
    TimedOut,
}

/// <summary>Thrown by <see cref="SteamSession.StoreStatsAsync"/>. The message is user-facing.</summary>
public sealed class SteamStoreException(SteamStoreFailure failure, string message) : SteamUnavailableException(message)
{
    public SteamStoreFailure Failure { get; } = failure;
}

/// <summary>
/// One connection to the running Steam client, bound to a single App ID for the
/// life of the process (0 for the library). Wraps <see cref="SAM.API.Client"/>.
/// Steam callbacks are pumped on the UI thread that called <see cref="Initialize"/>.
/// </summary>
public sealed class SteamSession : IDisposable
{
    private readonly Client _client;
    private readonly DispatcherQueueTimer _callbackTimer;
    private readonly API.Callbacks.UserStatsReceived _userStatsReceived;
    private readonly API.Callbacks.UserStatsStored _userStatsStored;
    private TaskCompletionSource<int>? _pendingStatsRequest;
    private TaskCompletionSource<int>? _pendingStore;

    private SteamSession(Client client, uint appId)
    {
        this._client = client;
        this.AppId = appId;
        this.SteamId = client.SteamUser.GetSteamId();
        this.SteamInstallPath = API.Steam.GetInstallPath();
        this.Language = client.SteamApps008.GetCurrentGameLanguage() ?? "english";

        this._userStatsReceived = client.CreateAndRegisterCallback<API.Callbacks.UserStatsReceived>();
        this._userStatsReceived.OnRun += this.OnUserStatsReceived;
        this._userStatsStored = client.CreateAndRegisterCallback<API.Callbacks.UserStatsStored>();
        this._userStatsStored.OnRun += this.OnUserStatsStored;

        this._callbackTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        this._callbackTimer.Interval = TimeSpan.FromMilliseconds(100);
        this._callbackTimer.Tick += (_, _) => this._client.RunCallbacks(false);
        this._callbackTimer.Start();
    }

    /// <summary>The App ID this process is bound to; 0 for the library.</summary>
    public uint AppId { get; }

    /// <summary>The logged-in user's SteamID64.</summary>
    public ulong SteamId { get; }

    /// <summary>The 32-bit account ID used in Steam's file names (userdata\&lt;id&gt;, UserGameStats_&lt;id&gt;_*).</summary>
    public uint AccountId => (uint)(this.SteamId & 0xFFFFFFFF);

    public string SteamInstallPath { get; }

    /// <summary>Steam UI language, e.g. "english"; used to pick localized schema strings.</summary>
    public string Language { get; }

    public static SteamSession Initialize(uint appId)
    {
        if (string.IsNullOrEmpty(API.Steam.GetInstallPath()))
        {
            throw new SteamUnavailableException(Strings.Get("Steam_NotInstalled"));
        }

        var client = new Client();
        try
        {
            client.Initialize(appId);
        }
        catch (ClientInitializeException e)
        {
            client.Dispose();
            var message = e.Failure switch
            {
                ClientInitializeFailure.ConnectToGlobalUser => Strings.Get("Steam_NotLoggedIn"),
                ClientInitializeFailure.AppIdMismatch => Strings.Format("Steam_AppIdRejected", appId),
                _ => Strings.Get("Steam_NotRunning"),
            };
            throw new SteamUnavailableException($"{message}\n\n({e.Message})", e);
        }
        catch (DllNotFoundException e)
        {
            client.Dispose();
            throw new SteamUnavailableException(Strings.Get("Steam_ClientLibraryMissing"), e);
        }

        return new SteamSession(client, appId);
    }

    public bool OwnsApp(uint appId) => this._client.SteamApps008.IsSubscribedApp(appId);

    /// <summary>Reads Steam's app metadata, e.g. "name", "logo", "small_capsule/english". Null if missing.</summary>
    public string? GetAppData(uint appId, string key)
    {
        var value = this._client.SteamApps001.GetAppData(appId, key);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    #region Stats and achievements (only valid when AppId != 0)
    /// <summary>
    /// Asks Steam for the current user's stats for <see cref="AppId"/> and waits for the
    /// UserStatsReceived callback. Must be called on the UI thread. Throws
    /// <see cref="SteamUnavailableException"/> with a readable message on failure.
    /// </summary>
    public async Task RequestUserStatsAsync(TimeSpan? timeout = null)
    {
        var request = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        this._pendingStatsRequest = request;
        try
        {
            if (this._client.SteamUserStats.RequestUserStats(this.SteamId) == CallHandle.Invalid)
            {
                throw new SteamUnavailableException(Strings.Get("Steam_StatsRequestRefused"));
            }

            var finished = await Task.WhenAny(request.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(15)));
            if (finished != request.Task)
            {
                throw new SteamUnavailableException(Strings.Get("Steam_StatsTimedOut"));
            }

            var result = request.Task.Result;
            if (result != 1)
            {
                throw new SteamUnavailableException(result == 2
                    ? Strings.Get("Steam_StatsGenericError")
                    : Strings.Format("Steam_StatsError", result));
            }
        }
        finally
        {
            // Never leave a stale request behind for a late callback to complete.
            if (ReferenceEquals(this._pendingStatsRequest, request))
            {
                this._pendingStatsRequest = null;
            }
        }
    }

    private void OnUserStatsReceived(API.Types.UserStatsReceived p)
    {
        // The callback is broadcast: ignore other games and other users' stats.
        if (p.GameId != this.AppId || p.SteamIdUser != this.SteamId)
        {
            return;
        }

        this._pendingStatsRequest?.TrySetResult(p.Result);
    }

    public bool TryGetAchievement(string id, out bool achieved, out DateTimeOffset? unlockTime)
    {
        if (this._client.SteamUserStats.GetAchievementAndUnlockTime(id, out achieved, out var time) == false)
        {
            unlockTime = null;
            return false;
        }

        unlockTime = achieved && time > 0 ? DateTimeOffset.FromUnixTimeSeconds(time) : null;
        return true;
    }

    public bool SetAchievement(string id, bool achieved) => this._client.SteamUserStats.SetAchievement(id, achieved);

    public bool TryGetStat(string id, out int value) => this._client.SteamUserStats.GetStatValue(id, out value);

    public bool TryGetStat(string id, out float value) => this._client.SteamUserStats.GetStatValue(id, out value);

    public bool SetStat(string id, int value) => this._client.SteamUserStats.SetStatValue(id, value);

    public bool SetStat(string id, float value) => this._client.SteamUserStats.SetStatValue(id, value);

    /// <summary>
    /// Uploads the staged achievement and stat changes and waits for Steam's servers to
    /// confirm them (the UserStatsStored callback). StoreStats returning true only means the
    /// upload was queued. Must be called on the UI thread. Throws <see cref="SteamStoreException"/>
    /// with a readable message when Steam refuses, rejects or doesn't answer.
    /// </summary>
    public async Task StoreStatsAsync(TimeSpan? timeout = null)
    {
        var request = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        this._pendingStore = request;
        try
        {
            if (this._client.SteamUserStats.StoreStats() == false)
            {
                throw new SteamStoreException(SteamStoreFailure.Refused, Strings.Get("Steam_StoreRefused"));
            }

            var finished = await Task.WhenAny(request.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(30)));
            if (finished != request.Task)
            {
                throw new SteamStoreException(SteamStoreFailure.TimedOut, Strings.Get("Steam_StoreTimedOut"));
            }

            var result = request.Task.Result;
            if (result != 1)
            {
                throw new SteamStoreException(SteamStoreFailure.Rejected, DescribeStoreResult(result));
            }
        }
        finally
        {
            if (ReferenceEquals(this._pendingStore, request))
            {
                this._pendingStore = null;
            }
        }
    }

    private void OnUserStatsStored(API.Types.UserStatsStored p)
    {
        if (p.GameId != this.AppId)
        {
            return;
        }

        this._pendingStore?.TrySetResult(p.Result);
    }

    /// <summary>A readable message for a UserStatsStored EResult other than k_EResultOK.</summary>
    private static string DescribeStoreResult(int result) => result switch
    {
        8 => Strings.Get("Steam_StoreRejectedRule"),
        2 => Strings.Get("Steam_StoreRejectedGeneric"),
        3 => Strings.Get("Steam_StoreNoConnection"),
        15 => Strings.Get("Steam_StoreAccessDenied"),
        16 => Strings.Get("Steam_StoreServerTimedOut"),
        _ => Strings.Format("Steam_StoreRejectedError", result),
    };

    public bool ResetAllStats(bool achievementsToo) => this._client.SteamUserStats.ResetAllStats(achievementsToo);
    #endregion

    public void Dispose()
    {
        this._callbackTimer.Stop();
        this._client.Dispose();
    }
}
