// Partial-property [ObservableProperty] needs C# 13; this project uses C# 12 and isn't AOT-compiled.
#pragma warning disable MVVMTK0045

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.ViewModels.Pages;

/// <summary>One unlocked achievement in the Activity feed or the Stats page's rarest list.</summary>
public sealed partial class UnlockItemViewModel : ObservableObject
{
    private readonly ImageCache _images;
    private readonly GlobalPercentLoader? _percents;
    private bool _iconRequested;
    private bool _rarityRequested;

    /// <param name="percents">Loads the rarity on demand; null when the caller sets <see cref="GlobalPercent"/> itself.</param>
    public UnlockItemViewModel(UnlockEvent unlock, ImageCache images, GlobalPercentLoader? percents)
    {
        this.Event = unlock;
        this._images = images;
        this._percents = percents;
        this.LocalTime = unlock.Time.LocalDateTime;
    }

    public UnlockEvent Event { get; }

    public uint AppId => this.Event.AppId;

    public string GameName => this.Event.GameName;

    public string Name => this.Event.AchievementName;

    /// <summary>Screen readers announce list rows by their item's ToString.</summary>
    public override string ToString() => $"{this.Name}, {this.Event.GameName}";

    public string Description => this.Event.Description;

    public bool HasDescription => string.IsNullOrWhiteSpace(this.Event.Description) == false;

    public DateTime LocalTime { get; }

    public string TimeText => this.LocalTime.ToString("t", CultureInfo.CurrentCulture);

    public string DateTimeText => this.LocalTime.ToString("g", CultureInfo.CurrentCulture);

    public string OpenGameLabel => $"Open {this.GameName}";

    [ObservableProperty]
    private ImageSource? _iconSource;

    [ObservableProperty]
    private bool _isIconMissing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRarity), nameof(Rarity), nameof(RarityText), nameof(RarityTierText), nameof(RarityToolTip))]
    private double? _globalPercent;

    public bool HasRarity => this.GlobalPercent.HasValue;

    public AchievementRarity Rarity => AchievementViewModel.GetRarity(this.GlobalPercent);

    public string RarityText => this.GlobalPercent is { } p ? FormatPercent(p) : "";

    public string RarityTierText => this.Rarity == AchievementRarity.Unknown ? "" : this.Rarity.ToString();

    public string RarityToolTip => this.GlobalPercent is { } p
        ? $"{this.RarityTierText}: {FormatPercent(p)} of players have this achievement"
        : "";

    /// <summary>Percent with enough precision for rare achievements ("0.04%", "3.2%", "48%").</summary>
    public static string FormatPercent(double percent) => percent switch
    {
        <= 0 => "0%",
        < 0.01 => "<" + 0.01.ToString("0.00", CultureInfo.CurrentCulture) + "%",
        < 0.1 => percent.ToString("0.0#", CultureInfo.CurrentCulture) + "%",
        < 10 => percent.ToString("0.#", CultureInfo.CurrentCulture) + "%",
        _ => percent.ToString("0", CultureInfo.CurrentCulture) + "%",
    };

    [RelayCommand]
    private void OpenGame() => PageActions.OpenGame(this.AppId);

    /// <summary>Starts loading the icon and (if a loader was given) the rarity. Call on the UI thread.</summary>
    public void EnsureLoaded()
    {
        this.EnsureIcon();
        this.EnsureRarity();
    }

    private async void EnsureIcon()
    {
        if (this._iconRequested)
        {
            return;
        }

        this._iconRequested = true;
        if (string.IsNullOrEmpty(this.Event.IconFile))
        {
            this.IsIconMissing = true;
            return;
        }

        string? path = null;
        try
        {
            path = await this._images.GetAsync([GameArt.GetAchievementIconUrl(this.AppId, this.Event.IconFile)], CancellationToken.None);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't load icon for {this.AppId}/{this.Event.AchievementId}", e);
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

    private async void EnsureRarity()
    {
        if (this._rarityRequested || this._percents == null || this.GlobalPercent.HasValue)
        {
            return;
        }

        this._rarityRequested = true;
        var percents = await this._percents.GetAsync(this.AppId);
        if (percents != null && percents.TryGetValue(this.Event.AchievementId, out var percent))
        {
            this.GlobalPercent = percent;
        }
    }
}
