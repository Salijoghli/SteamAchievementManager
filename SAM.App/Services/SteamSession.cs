using Microsoft.UI.Dispatching;
using SAM.API;

namespace SAM.App.Services;

/// <summary>Thrown when the app can't talk to the Steam client. The message is user-facing.</summary>
public sealed class SteamUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

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
    private TaskCompletionSource<int>? _pendingStatsRequest;

    private SteamSession(Client client, uint appId)
    {
        this._client = client;
        this.AppId = appId;
        this.SteamId = client.SteamUser.GetSteamId();
        this.SteamInstallPath = API.Steam.GetInstallPath();
        this.Language = client.SteamApps008.GetCurrentGameLanguage() ?? "english";

        this._userStatsReceived = client.CreateAndRegisterCallback<API.Callbacks.UserStatsReceived>();
        this._userStatsReceived.OnRun += p => this._pendingStatsRequest?.TrySetResult(p.Result);

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
            throw new SteamUnavailableException("Steam doesn't appear to be installed.");
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
                ClientInitializeFailure.ConnectToGlobalUser =>
                    "Steam isn't running or you're not logged in. Start Steam, log in, and try again.\n\n" +
                    "If you have this game through Family Sharing, it may be locked while the owner is playing.",
                ClientInitializeFailure.AppIdMismatch =>
                    $"Steam didn't accept App ID {appId}. Make sure you own the game.",
                _ => "Steam isn't running. Start Steam and try again.",
            };
            throw new SteamUnavailableException($"{message}\n\n({e.Message})", e);
        }
        catch (DllNotFoundException e)
        {
            client.Dispose();
            throw new SteamUnavailableException("Couldn't load Steam's client library (steamclient64.dll).", e);
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

        if (this._client.SteamUserStats.RequestUserStats(this.SteamId) == CallHandle.Invalid)
        {
            throw new SteamUnavailableException("Steam refused the request for your stats.");
        }

        var finished = await Task.WhenAny(request.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(15)));
        if (finished != request.Task)
        {
            throw new SteamUnavailableException("Steam didn't respond with your stats in time.");
        }

        var result = request.Task.Result;
        if (result != 1)
        {
            throw new SteamUnavailableException(result == 2
                ? "Steam returned a generic error. This usually means you don't own the game."
                : $"Steam returned error {result} while fetching your stats.");
        }
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

    /// <summary>Uploads pending achievement and stat changes to Steam.</summary>
    public bool StoreStats() => this._client.SteamUserStats.StoreStats();

    public bool ResetAllStats(bool achievementsToo) => this._client.SteamUserStats.ResetAllStats(achievementsToo);
    #endregion

    public void Dispose()
    {
        this._callbackTimer.Stop();
        this._client.Dispose();
    }
}
