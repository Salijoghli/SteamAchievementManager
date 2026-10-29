// [ObservableProperty] on fields: partial properties need C# 13 and the project uses C# 12;
// the app isn't AOT-compiled, so this WinRT AOT warning doesn't apply.
#pragma warning disable MVVMTK0045

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SAM.App.Services;

namespace SAM.App.ViewModels;

/// <summary>One friend in the "Compare with friend" picker.</summary>
public sealed partial class FriendItemViewModel(SteamFriend friend, ImageCache images) : ObservableObject
{
    private bool _avatarRequested;

    public SteamFriend Friend { get; } = friend;

    public string Name => this.Friend.Name;

    public bool IsOnline => this.Friend.IsOnline;

    /// <summary>"Online", plus what the ownership check found once it's done: "Online · 12 achievements".</summary>
    public string StatusText
    {
        get
        {
            var presence = Strings.Get(this.Friend.IsOnline ? "Game_FriendOnline" : "Game_FriendOffline");
            return this.Ownership?.Status switch
            {
                FriendAchievementsStatus.Ok => Strings.Format("Game_FriendOwnsStatus", presence, this.Ownership.Unlocked.Count),
                FriendAchievementsStatus.Private => Strings.Format("Game_FriendPrivateStatus", presence),
                FriendAchievementsStatus.NotOwned => Strings.Format("Game_FriendNoAchievementsStatus", presence),
                _ => presence,
            };
        }
    }

    /// <summary>The friend's achievements in this game, once checked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private FriendAchievements? _ownership;

    /// <summary>The avatar once downloaded; until then (or without one) the picture shows initials.</summary>
    [ObservableProperty]
    private ImageSource? _avatar;

    /// <summary>Screen readers announce list rows by their item's ToString.</summary>
    public override string ToString() => Strings.Format("Game_FriendA11y", this.Name, this.StatusText);

    /// <summary>Starts downloading the avatar the first time the row is shown. Call on the UI thread.</summary>
    public async void EnsureAvatar()
    {
        if (this._avatarRequested || string.IsNullOrEmpty(this.Friend.AvatarUrl))
        {
            return;
        }

        this._avatarRequested = true;
        this.Avatar = await LoadAvatarAsync(images, this.Friend.AvatarUrl);
    }

    /// <summary>The avatar at <paramref name="url"/> from the image cache, or null. Never throws.</summary>
    public static async Task<ImageSource?> LoadAvatarAsync(ImageCache images, string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        try
        {
            var path = await images.GetAsync([url], CancellationToken.None);
            return path == null ? null : new BitmapImage(new Uri(path));
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't load a friend's avatar", e);
            return null;
        }
    }
}
