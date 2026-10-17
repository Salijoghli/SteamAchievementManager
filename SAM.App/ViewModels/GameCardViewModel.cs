// Partial-property [ObservableProperty] needs C# 13; this project uses C# 12 and isn't AOT-compiled.
#pragma warning disable MVVMTK0045

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SAM.App.Models;

namespace SAM.App.ViewModels;

/// <summary>
/// One game in the library. Instances are kept in <see cref="LibraryViewModel"/>'s master list and
/// reused across filtering, sorting and refreshes, so per-game state (progress, image) survives.
/// </summary>
public sealed partial class GameCardViewModel : ObservableObject
{
    private readonly LibraryViewModel _owner;
    private bool _suppressFavoriteCallback;

    public GameCardViewModel(LibraryViewModel owner, GameEntry entry, bool isFavorite)
    {
        this._owner = owner;
        this.AppId = entry.AppId;
        this.AppIdText = entry.AppId.ToString(CultureInfo.InvariantCulture);
        this._name = entry.Name;
        this._type = entry.Type;
        this._suppressFavoriteCallback = true;
        this.IsFavorite = isFavorite;
        this._suppressFavoriteCallback = false;
    }

    public uint AppId { get; }

    public string AppIdText { get; }

    /// <summary>The library this card belongs to (used by context menus for collection actions).</summary>
    internal LibraryViewModel Owner => this._owner;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string _name;

    /// <summary>Install info from Steam's app manifests, or null if the game isn't installed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(InstalledText), nameof(PlayLabel), nameof(PlayGlyph), nameof(ToolTipText))]
    private InstalledGame? _installed;

    /// <summary>True when the user has hidden this game; hidden cards are shown dimmed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HiddenLabel), nameof(HiddenGlyph), nameof(CardOpacity))]
    private bool _isHidden;

    /// <summary>Extra label for the active smart list, e.g. "3 left" or "Rarest 0.4%". Null when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSmartBadge))]
    private string? _smartBadge;

    /// <summary>True while the smart list's per-game data (e.g. rarity) is still loading.</summary>
    [ObservableProperty]
    private bool _isSmartBadgeLoading;

    /// <summary>"normal", "demo", "mod" or "junk".</summary>
    [ObservableProperty]
    private string _type;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteGlyph), nameof(FavoriteLabel))]
    private bool _isFavorite;

    /// <summary>Null when unknown (no schema).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ProgressPercent), nameof(HasAchievements), nameof(IsPerfect), nameof(CompletionSortKey), nameof(ProgressCountText), nameof(PercentText))]
    private GameProgress? _progress;

    /// <summary>False until progress has been computed at least once (shows a skeleton).</summary>
    [ObservableProperty]
    private bool _isProgressLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaytimeText), nameof(LastPlayed), nameof(PlaytimeShortText), nameof(LastPlayedText))]
    private PlaytimeInfo? _playtime;

    /// <summary>False until the playtime map has been read at least once (shows a skeleton).</summary>
    [ObservableProperty]
    private bool _isPlaytimeLoaded;

    /// <summary>Local path of the cached card image, once downloaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImagePending))]
    private string? _imagePath;

    /// <summary>True when no image could be loaded; the card shows a fallback.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImagePending))]
    private bool _imageFailed;

    internal CancellationTokenSource? ImageLoad { get; set; }

    internal IReadOnlyList<string>? ImageUrls { get; set; }

    public bool HasAchievements => this.Progress is { Total: > 0 };

    public bool IsPerfect => this.Progress is { IsPerfect: true };

    public double ProgressPercent => this.Progress is { Total: > 0 } p ? p.Fraction * 100 : 0;

    /// <summary>For sorting: fraction, or -1 for games without achievements.</summary>
    public double CompletionSortKey => this.Progress is { Total: > 0 } p ? p.Fraction : -1;

    public DateTimeOffset? LastPlayed => this.Playtime?.LastPlayed;

    public TimeSpan PlaytimeSpan => this.Playtime?.Playtime ?? TimeSpan.Zero;

    public string ProgressText => this.Progress switch
    {
        null or { Total: 0 } => Strings.Get("Card_NoAchievements"),
        { Unlocked: 0 } p => Strings.Format("Card_ProgressNotStarted", LibraryFormat.Count(p.Total)),
        { } p => Strings.Format(
            "Card_Progress",
            LibraryFormat.Count(p.Unlocked),
            LibraryFormat.Count(p.Total),
            Math.Floor(p.Fraction * 100).ToString(CultureInfo.CurrentCulture)),
    };

    public string PlaytimeText => LibraryFormat.PlaytimeSummary(this.Playtime);

    /// <summary>"12/30" for the list view, or "–" without achievements.</summary>
    public string ProgressCountText => this.Progress is { Total: > 0 } p
        ? $"{LibraryFormat.Count(p.Unlocked)}/{LibraryFormat.Count(p.Total)}"
        : "–";

    /// <summary>"40%" for the list view, or "–" without achievements.</summary>
    public string PercentText => this.Progress is { Total: > 0 } p
        ? Strings.Format("Card_Percent", Math.Floor(p.Fraction * 100).ToString(CultureInfo.CurrentCulture))
        : "–";

    public string PlaytimeShortText => this.PlaytimeSpan > TimeSpan.Zero ? LibraryFormat.Playtime(this.PlaytimeSpan) : "–";

    public string LastPlayedText => LibraryFormat.RelativeDate(this.LastPlayed);

    public bool IsInstalled => this.Installed != null;

    public string InstalledText => this.Installed is { } i ? LibraryFormat.Bytes(i.SizeOnDisk) : "";

    /// <summary>"Play" for installed games, "Install" otherwise.</summary>
    public string PlayLabel => Strings.Get(this.IsInstalled ? "Card_Play" : "Card_Install");

    public string PlayGlyph => this.IsInstalled ? "" : "";

    public string HiddenLabel => Strings.Get(this.IsHidden ? "Card_UnhideGame" : "Card_HideGame");

    public string HiddenGlyph => this.IsHidden ? "" : "";

    public double CardOpacity => this.IsHidden ? 0.5 : 1;

    public bool HasSmartBadge => this.SmartBadge != null;

    /// <summary>True until the image has loaded or failed (the list view shows a skeleton).</summary>
    public bool IsImagePending => this.ImagePath == null && this.ImageFailed == false;

    public string FavoriteGlyph => this.IsFavorite ? "" : "";

    public string FavoriteLabel => Strings.Get(this.IsFavorite ? "Card_RemoveFromFavorites" : "Card_AddToFavorites");

    public string ToolTipText => this.Installed is { } installed
        ? Strings.Format("Card_ToolTipInstalled", this.Name, this.AppIdText, LibraryFormat.Bytes(installed.SizeOnDisk))
        : Strings.Format("Card_ToolTip", this.Name, this.AppIdText);

    internal void Update(GameEntry entry)
    {
        this.Name = entry.Name;
        this.Type = entry.Type;
    }

    /// <summary>Sets the favorite state without writing it back to the store.</summary>
    internal void SyncFavorite(bool value)
    {
        this._suppressFavoriteCallback = true;
        try
        {
            this.IsFavorite = value;
        }
        finally
        {
            this._suppressFavoriteCallback = false;
        }
    }

    partial void OnIsFavoriteChanged(bool value)
    {
        if (this._suppressFavoriteCallback == false)
        {
            this._owner.OnFavoriteToggled(this, value);
        }
    }

    [RelayCommand]
    private void Open() => this._owner.OpenGame(this);

    [RelayCommand]
    private void UnlockAll() => this._owner.LaunchGame(this, "--unlock-all");

    [RelayCommand]
    private void LockAll() => this._owner.LaunchGame(this, "--lock-all");

    [RelayCommand]
    private void Play() => this._owner.PlayOrInstall(this);

    [RelayCommand]
    private void ToggleHidden() => this._owner.SetHidden(this, !this.IsHidden);

    [RelayCommand]
    private void ToggleFavorite() => this.IsFavorite = !this.IsFavorite;

    [RelayCommand]
    private void CopyAppId() => this._owner.CopyAppId(this);

    [RelayCommand]
    private void OpenStorePage() => this._owner.OpenUrl($"https://store.steampowered.com/app/{this.AppIdText}");

    [RelayCommand]
    private void OpenAchievementsPage() => this._owner.OpenUrl($"https://steamcommunity.com/stats/{this.AppIdText}/achievements");
}
