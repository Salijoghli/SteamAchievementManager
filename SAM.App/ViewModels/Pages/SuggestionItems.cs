// Partial-property [ObservableProperty] needs C# 13; this project uses C# 12 and isn't AOT-compiled.
#pragma warning disable MVVMTK0045

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SAM.App.Services;

namespace SAM.App.ViewModels.Pages;

/// <summary>A locked achievement in the Stats page's "Easiest achievements you're missing" list.</summary>
public sealed partial class SuggestedAchievementItem : ObservableObject
{
    private readonly ImageCache _images;
    private bool _iconRequested;

    public SuggestedAchievementItem(MissingAchievement achievement, double percent, bool spoilerProtection, ImageCache images)
    {
        this.Achievement = achievement;
        this.Percent = percent;
        this._images = images;
        this._isSpoilerProtected = spoilerProtection;
    }

    public MissingAchievement Achievement { get; }

    /// <summary>Identifies the row across re-rankings (so its icon isn't reloaded).</summary>
    public (uint AppId, string Id) Key => (this.Achievement.AppId, this.Achievement.Id);

    public uint AppId => this.Achievement.AppId;

    public string GameName => this.Achievement.GameName;

    public string Name => this.Achievement.Name;

    public double Percent { get; }

    /// <summary>Mirrors <see cref="AppSettings.SpoilerProtection"/>: hidden achievements' descriptions are masked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayDescription), nameof(IsDescriptionMasked))]
    private bool _isSpoilerProtected;

    public bool IsDescriptionMasked => this.Achievement.IsHidden && this.IsSpoilerProtected;

    public string DisplayDescription => this.IsDescriptionMasked
        ? Strings.Get("Suggest_HiddenDescription")
        : string.IsNullOrWhiteSpace(this.Achievement.Description)
            ? (this.Achievement.IsHidden ? Strings.Get("Achievement_Hidden") : "")
            : this.Achievement.Description;

    public AchievementRarity Rarity => AchievementViewModel.GetRarity(this.Percent);

    public string RarityTierText => UnlockItemViewModel.RarityName(this.Rarity);

    public string PercentText => UnlockItemViewModel.FormatPercent(this.Percent);

    public string RarityToolTip => Strings.Format("Suggest_PercentToolTip", this.PercentText);

    public string OpenGameLabel => Strings.Format("Pages_OpenGame", this.GameName);

    public string AutomationName => Strings.Format("Suggest_AchievementAutomation", this.Name, this.GameName, this.PercentText);

    /// <summary>Screen readers announce list rows by their item's ToString.</summary>
    public override string ToString() => this.AutomationName;

    [ObservableProperty]
    private ImageSource? _iconSource;

    [ObservableProperty]
    private bool _isIconMissing;

    [RelayCommand]
    private void OpenGame() => PageActions.OpenGame(this.AppId);

    /// <summary>Starts loading the icon (the locked one for a masked hidden achievement). Call on the UI thread.</summary>
    public async void EnsureIcon()
    {
        if (this._iconRequested)
        {
            return;
        }

        this._iconRequested = true;
        var file = this.IsDescriptionMasked ? this.Achievement.LockedIconFile : this.Achievement.IconFile;
        if (string.IsNullOrEmpty(file))
        {
            this.IsIconMissing = true;
            return;
        }

        string? path = null;
        try
        {
            path = await this._images.GetAsync([GameArt.GetAchievementIconUrl(this.AppId, file)], CancellationToken.None);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't load icon for {this.AppId}/{this.Achievement.Id}", e);
        }

        if (path == null)
        {
            this.IsIconMissing = true;
            return;
        }

        try
        {
            this.IconSource = new BitmapImage(new Uri(path)) { DecodePixelWidth = 64, DecodePixelType = DecodePixelType.Logical };
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't show icon {path}", e);
            this.IsIconMissing = true;
        }
    }
}

/// <summary>A game in the Stats page's "Perfect games within reach" list.</summary>
public sealed partial class ReachableGameItem(ReachableGame game)
{
    public uint AppId => game.Game.AppId;

    public string Name => game.Game.Name;

    /// <summary>"1 left".</summary>
    public string RemainingText => Strings.Plural("Library_SmartBadgeLeft", game.Remaining);

    /// <summary>"27.9% of players have it", "hardest: 4.2% of players", or "" when rarity isn't known.</summary>
    public string DetailText => game.HardestPercent is not { } percent
        ? Strings.Plural("Suggest_ReachUnlockedOf", game.Game.Total, LibraryFormat.Count(game.Game.Unlocked))
        : game.Remaining == 1
            ? Strings.Format("Suggest_ReachLastOne", UnlockItemViewModel.FormatPercent(percent))
            : Strings.Format("Suggest_ReachHardest", UnlockItemViewModel.FormatPercent(percent));

    /// <summary>0-100, for the inline bar.</summary>
    public double CompletionPercent => game.Game.Total == 0 ? 0 : (double)game.Game.Unlocked / game.Game.Total * 100;

    public string CompletionText => Strings.Format(
        "Suggest_ReachCompletion",
        LibraryFormat.Count(game.Game.Unlocked),
        LibraryFormat.Count(game.Game.Total),
        Math.Floor(this.CompletionPercent).ToString("0", CultureInfo.CurrentCulture) + "%");

    public AchievementRarity Rarity => AchievementViewModel.GetRarity(game.HardestPercent);

    public bool HasRarity => game.HardestPercent.HasValue;

    public string AutomationName => $"{this.Name}, {this.RemainingText}, {this.DetailText}";

    /// <summary>Screen readers announce list rows by their item's ToString.</summary>
    public override string ToString() => this.AutomationName;

    [RelayCommand]
    private void Open() => PageActions.OpenGame(this.AppId);
}
