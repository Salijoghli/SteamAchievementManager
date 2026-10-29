using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SAM.App.Services;
using SAM.App.ViewModels;

namespace SAM.App.Views.Game;

/// <summary>
/// Picks a Steam friend to compare achievements with: a search box over the friends list
/// (online first, avatars when Steam has them). On opening it checks which friends own the game
/// and lists only them unless "Show all friends" is ticked. Choosing one (click, or select and
/// Compare) sets <see cref="SelectedFriend"/> and closes the dialog.
/// </summary>
public sealed partial class FriendPickerDialog : ContentDialog
{
    private readonly IReadOnlyList<FriendItemViewModel> _all;
    private readonly string? _loadError;
    private readonly Func<ulong, CancellationToken, Task<FriendAchievements>>? _check;
    private readonly CancellationTokenSource _checks = new();
    private readonly Dictionary<ulong, FriendAchievementsStatus> _statuses = [];
    private bool _checking;

    /// <param name="friends">The user's friends, or null when they couldn't be read.</param>
    /// <param name="loadError">Shown instead of the list when <paramref name="friends"/> is null.</param>
    /// <param name="check">Reads a friend's achievements in the game; null lists everyone without checking.</param>
    public FriendPickerDialog(
        IReadOnlyList<SteamFriend>? friends,
        string? loadError,
        ImageCache images,
        Func<ulong, CancellationToken, Task<FriendAchievements>>? check = null)
    {
        this._check = check;
        this._all = (friends ?? [])
            .DistinctBy(f => f.SteamId)
            .Select(f => new FriendItemViewModel(f, images))
            .ToList();
        this._loadError = friends == null ? loadError ?? Strings.Get("Game_FriendsLoadFailed") : null;
        this.InitializeComponent();
        this._checking = check != null && this._all.Count > 0;
        this.ApplySearch();
        this.PrimaryButtonClick += (_, _) => this.SelectedFriend = (this.FriendList.SelectedItem as FriendItemViewModel)?.Friend;
        this.Opened += (_, _) =>
        {
            this.SearchBox.Focus(FocusState.Programmatic);
            this.CheckOwnership();
        };
        this.Closed += (_, _) => this._checks.Cancel();
    }

    /// <summary>The friends shown (after the search).</summary>
    public ObservableCollection<FriendItemViewModel> Items { get; } = [];

    /// <summary>The friend chosen, or null when cancelled.</summary>
    public SteamFriend? SelectedFriend { get; private set; }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => this.ApplySearch();

    private void OnShowAllChanged(object sender, RoutedEventArgs e) => this.ApplySearch();

    /// <summary>Asks for every friend's achievements in the game (three at a time; answers are cached).</summary>
    private async void CheckOwnership()
    {
        if (this._checking == false)
        {
            return;
        }

        var token = this._checks.Token;
        await Task.WhenAll(this._all.Select(async item =>
        {
            FriendAchievements? result = null;
            try
            {
                result = await this._check!(item.Friend.SteamId, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Diagnostics.Log("Couldn't check whether a friend owns the game", e);
            }

            // Back on the UI thread (the dialog's context).
            item.Ownership = result;
            this._statuses[item.Friend.SteamId] = result?.Status ?? FriendAchievementsStatus.Failed;
            this.ApplySearch();
        }));

        if (token.IsCancellationRequested == false)
        {
            this._checking = false;
            this.ApplySearch();
        }
    }

    private void ApplySearch()
    {
        var byId = this._all.ToDictionary(f => f.Friend.SteamId);
        var showAll = this._check == null || this.ShowAllBox.IsChecked == true;
        var candidates = FriendComparison.Owners(this._all.Select(f => f.Friend), this._statuses, showAll);
        var shown = FriendComparison.FilterFriends(candidates, this.SearchBox.Text).Select(f => byId[f.SteamId]).ToList();

        // Rebuilt only when it changed, so checks finishing don't drop the selection or flicker.
        if (shown.SequenceEqual(this.Items) == false)
        {
            var selected = this.FriendList.SelectedItem;
            this.Items.Clear();
            foreach (var item in shown)
            {
                this.Items.Add(item);
            }

            if (selected != null && this.Items.Contains(selected))
            {
                this.FriendList.SelectedItem = selected;
            }
        }

        var owners = this._statuses.Values.Count(s => s == FriendAchievementsStatus.Ok);
        this.CheckingRing.IsActive = this._checking;
        this.CheckingRing.Visibility = this._checking ? Visibility.Visible : Visibility.Collapsed;
        this.OwnershipText.Text = this._check == null || this._all.Count == 0 || this._loadError != null ? ""
            : this._checking ? Strings.Format("Game_FriendsChecking", this._statuses.Count, this._all.Count)
            : Strings.Format("Game_FriendsOwnersCount", owners, this._all.Count);
        this.ShowAllBox.Visibility = this._check == null || this._all.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var searching = string.IsNullOrWhiteSpace(this.SearchBox.Text) == false;
        string? empty = this._loadError
            ?? (this._all.Count == 0 ? Strings.Get("Game_FriendsNone")
            : this.Items.Count > 0 ? null
            : searching ? Strings.Get("Game_FriendsNoMatch")
            : this._checking ? ""
            : Strings.Get("Game_FriendsNoOwners"));
        this.EmptyText.Text = empty ?? "";
        this.EmptyState.Visibility = empty == null ? Visibility.Collapsed : Visibility.Visible;
        this.FriendList.Visibility = empty == null ? Visibility.Visible : Visibility.Collapsed;
        this.SearchBox.IsEnabled = this._all.Count > 0;
        this.IsPrimaryButtonEnabled = this.FriendList.SelectedItem != null;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        this.IsPrimaryButtonEnabled = this.FriendList.SelectedItem != null;

    private void OnFriendClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FriendItemViewModel item)
        {
            this.SelectedFriend = item.Friend;
            this.Hide();
        }
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue == false && args.Item is FriendItemViewModel item)
        {
            item.EnsureAvatar();
        }
    }
}
