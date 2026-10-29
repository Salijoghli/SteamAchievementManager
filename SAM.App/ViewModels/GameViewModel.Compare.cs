// [ObservableProperty] on fields: partial properties need C# 13 and the project uses C# 12;
// the app isn't AOT-compiled, so this WinRT AOT warning doesn't apply.
#pragma warning disable MVVMTK0045

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using SAM.App.Services;

namespace SAM.App.ViewModels;

/// <summary>"Compare with friend": shows a friend's unlocks next to the user's. Read-only.</summary>
public sealed partial class GameViewModel
{
    private CancellationTokenSource? _compareCancellation;

    /// <summary>The friend's unlocks from the last successful read (case-insensitive IDs), reapplied after reloads.</summary>
    private Dictionary<string, DateTimeOffset?>? _friendUnlocks;

    /// <summary>The friend being compared with, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComparing))]
    [NotifyPropertyChangedFor(nameof(ComparedFriendName))]
    [NotifyPropertyChangedFor(nameof(ComparisonTitle))]
    [NotifyPropertyChangedFor(nameof(ComparisonStatusText))]
    private SteamFriend? _comparedFriend;

    [ObservableProperty]
    private ImageSource? _comparedFriendAvatar;

    /// <summary>Waiting for the friend's achievements.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComparisonReady))]
    [NotifyPropertyChangedFor(nameof(ComparisonStatusText))]
    private bool _isComparisonLoading;

    /// <summary>Why the comparison failed (private, not owned, failed), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComparisonError))]
    [NotifyPropertyChangedFor(nameof(IsComparisonReady))]
    [NotifyPropertyChangedFor(nameof(ComparisonStatusText))]
    private string? _comparisonError;

    /// <summary>The failure can be retried (network or parse failure, not a privacy setting).</summary>
    [ObservableProperty]
    private bool _canRetryComparison;

    /// <summary>"You 19/70 · Alex 45/70".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComparisonStatusText))]
    private string _comparisonSummary = "";

    /// <summary>Index into <see cref="FriendCompareFilter"/>.</summary>
    [ObservableProperty]
    private int _compareFilterIndex;

    public bool IsComparing => this.ComparedFriend != null;

    public string ComparedFriendName => this.ComparedFriend?.Name ?? "";

    public bool HasComparisonError => this.ComparisonError != null;

    /// <summary>"Comparing with Alex".</summary>
    public string ComparisonTitle => this.ComparedFriend is { } friend ? Strings.Format("Game_CompareWith", friend.Name) : "";

    /// <summary>The comparison bar's second line: loading, the failure, or the summary.</summary>
    public string ComparisonStatusText =>
        this.ComparedFriend == null ? ""
        : this.IsComparisonLoading ? Strings.Format("Game_CompareLoading", this.ComparedFriend.Name)
        : this.ComparisonError ?? this.ComparisonSummary;

    /// <summary>The friend's achievements are shown (enables the comparison filter).</summary>
    public bool IsComparisonReady => this.IsComparing && this.IsComparisonLoading == false && this.ComparisonError == null;

    partial void OnCompareFilterIndexChanged(int value) => this.ApplyFilter();

    /// <summary>The user's Steam friends for the picker. Call on the UI thread; throws if Steam can't be asked.</summary>
    public IReadOnlyList<SteamFriend> GetFriends() => this._services.Friends.GetFriends();

    public ImageCache Images => this._services.Images;

    /// <summary>A friend's achievements in this game (cached for 30 minutes, so comparing afterwards is instant).</summary>
    public Task<FriendAchievements> GetFriendAchievementsAsync(ulong friendSteamId, CancellationToken cancellationToken) =>
        this._services.Friends.GetAchievementsAsync(friendSteamId, this.AppId, cancellationToken);

    /// <summary>
    /// Compares this game's achievements with <paramref name="friend"/>'s. Replaces any earlier
    /// comparison. Never throws; failures show as <see cref="ComparisonError"/>.
    /// </summary>
    public async Task CompareWithAsync(SteamFriend friend)
    {
        this._compareCancellation?.Cancel();
        var cts = this._compareCancellation = new CancellationTokenSource();

        if (this.ComparedFriend?.SteamId != friend.SteamId)
        {
            this.ComparedFriendAvatar = null;
            this.LoadComparedFriendAvatar(friend, cts.Token);
        }

        this.ComparedFriend = friend;
        this.ComparisonError = null;
        this.CanRetryComparison = false;
        this.IsComparisonLoading = true;
        this._friendUnlocks = null;
        this.ApplyComparison();

        FriendAchievements? result = null;
        string? error = null;
        try
        {
            result = await this._services.Friends.GetAchievementsAsync(friend.SteamId, this.AppId, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't read friend {friend.SteamId}'s achievements for app {this.AppId}", e);
            error = e is NotImplementedException
                ? Strings.Get("Game_CompareUnavailable")
                : FriendComparison.StatusMessage(FriendAchievementsStatus.Failed, friend.Name);
        }

        if (cts.IsCancellationRequested || ReferenceEquals(this._compareCancellation, cts) == false)
        {
            return; // Cleared, or another friend was picked meanwhile.
        }

        if (result != null)
        {
            error = FriendComparison.StatusMessage(result.Status, friend.Name);
            if (result.Status == FriendAchievementsStatus.Ok)
            {
                this._friendUnlocks = FriendComparison.Normalize(result.Unlocked ?? new Dictionary<string, DateTimeOffset?>());
            }
        }
        else
        {
            error ??= FriendComparison.StatusMessage(FriendAchievementsStatus.Failed, friend.Name);
        }

        this.CanRetryComparison = error != null && result?.Status is null or FriendAchievementsStatus.Failed;
        this.ComparisonError = error;
        this.IsComparisonLoading = false;
        this.ApplyComparison();
    }

    [RelayCommand]
    private async Task RetryComparison()
    {
        if (this.ComparedFriend is { } friend)
        {
            await this.CompareWithAsync(friend);
        }
    }

    /// <summary>Stops comparing: the rows lose the friend's state and the comparison filter resets.</summary>
    [RelayCommand]
    private void ClearComparison()
    {
        this._compareCancellation?.Cancel();
        this._compareCancellation = null;
        this._friendUnlocks = null;
        this.ComparedFriend = null;
        this.ComparedFriendAvatar = null;
        this.ComparisonError = null;
        this.CanRetryComparison = false;
        this.IsComparisonLoading = false;
        this.ComparisonSummary = "";
        this.CompareFilterIndex = 0;
        this.ApplyComparison();
    }

    private async void LoadComparedFriendAvatar(SteamFriend friend, CancellationToken token)
    {
        var avatar = await FriendItemViewModel.LoadAvatarAsync(this._services.Images, friend.AvatarUrl);
        if (token.IsCancellationRequested == false && this.ComparedFriend?.SteamId == friend.SteamId)
        {
            this.ComparedFriendAvatar = avatar;
            if (this._friendUnlocks != null)
            {
                foreach (var achievement in this._achievements)
                {
                    achievement.FriendAvatar = avatar;
                }
            }
        }
    }

    /// <summary>Puts the friend's state on every row (or clears it) and refreshes the summary and list.</summary>
    private void ApplyComparison()
    {
        var unlocks = this._friendUnlocks;
        var friend = this.ComparedFriend;
        var theirs = 0;
        foreach (var achievement in this._achievements)
        {
            if (unlocks == null || friend == null)
            {
                achievement.SetFriendState(null, false, null, null);
                continue;
            }

            var hasIt = unlocks.TryGetValue(achievement.Id, out var time);
            if (hasIt)
            {
                theirs++;
            }

            achievement.SetFriendState(friend.Name, hasIt, time, this.ComparedFriendAvatar);
        }

        this.ComparisonSummary = unlocks != null && friend != null
            ? FriendComparison.Summary(friend.Name, this._achievements.Count(a => a.IsAchieved), theirs, this._achievements.Count)
            : "";

        if (this.IsLoaded)
        {
            this.ApplyFilter();
        }
    }
}
