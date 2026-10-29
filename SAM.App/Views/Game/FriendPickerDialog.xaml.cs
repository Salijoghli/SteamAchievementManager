using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SAM.App.Services;
using SAM.App.ViewModels;

namespace SAM.App.Views.Game;

/// <summary>
/// Picks a Steam friend to compare achievements with: a search box over the friends list
/// (online first, avatars when Steam has them). Choosing one (click, or select and Compare)
/// sets <see cref="SelectedFriend"/> and closes the dialog.
/// </summary>
public sealed partial class FriendPickerDialog : ContentDialog
{
    private readonly IReadOnlyList<FriendItemViewModel> _all;
    private readonly string? _loadError;

    /// <param name="friends">The user's friends, or null when they couldn't be read.</param>
    /// <param name="loadError">Shown instead of the list when <paramref name="friends"/> is null.</param>
    public FriendPickerDialog(IReadOnlyList<SteamFriend>? friends, string? loadError, ImageCache images)
    {
        this._all = (friends ?? [])
            .DistinctBy(f => f.SteamId)
            .Select(f => new FriendItemViewModel(f, images))
            .ToList();
        this._loadError = friends == null ? loadError ?? Strings.Get("Game_FriendsLoadFailed") : null;
        this.InitializeComponent();
        this.ApplySearch();
        this.PrimaryButtonClick += (_, _) => this.SelectedFriend = (this.FriendList.SelectedItem as FriendItemViewModel)?.Friend;
        this.Opened += (_, _) => this.SearchBox.Focus(FocusState.Programmatic);
    }

    /// <summary>The friends shown (after the search).</summary>
    public ObservableCollection<FriendItemViewModel> Items { get; } = [];

    /// <summary>The friend chosen, or null when cancelled.</summary>
    public SteamFriend? SelectedFriend { get; private set; }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => this.ApplySearch();

    private void ApplySearch()
    {
        var byId = this._all.ToDictionary(f => f.Friend.SteamId);
        var shown = FriendComparison.FilterFriends(this._all.Select(f => f.Friend), this.SearchBox.Text);
        this.Items.Clear();
        foreach (var friend in shown)
        {
            this.Items.Add(byId[friend.SteamId]);
        }

        string? empty = this._loadError
            ?? (this._all.Count == 0 ? Strings.Get("Game_FriendsNone")
            : this.Items.Count == 0 ? Strings.Get("Game_FriendsNoMatch")
            : null);
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
