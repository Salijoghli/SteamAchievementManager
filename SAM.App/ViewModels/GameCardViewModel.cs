// Partial-property [ObservableProperty] needs C# 13; this project uses C# 12 and isn't AOT-compiled.
#pragma warning disable MVVMTK0045

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SAM.App.Models;
using SAM.App.Services;

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
    [NotifyPropertyChangedFor(nameof(AchievementState), nameof(IsAchievementDataMissing), nameof(CanLoadAchievements))]
    private GameProgress? _progress;

    /// <summary>
    /// What the store says about the game's achievements, for games without a schema on this PC;
    /// null until the store answered (see <see cref="AchievementState"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(CompletionSortKey), nameof(ProgressCountText))]
    [NotifyPropertyChangedFor(nameof(AchievementState), nameof(IsAchievementDataMissing), nameof(CanLoadAchievements))]
    private StoreAchievementCount? _storeCount;

    /// <summary>True while SAM asks Steam for this game's achievement data ("Load achievements").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(CanLoadAchievements))]
    private bool _isFetchingAchievements;

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

    /// <summary>Loaded, not loaded, none, unknown or checking (see <see cref="Models.AchievementState"/>).</summary>
    public AchievementState AchievementState => AchievementStates.Classify(this.Progress, this.StoreCount);

    /// <summary>True when Steam hasn't downloaded the game's achievement data (not loaded, or unknown).</summary>
    public bool IsAchievementDataMissing => AchievementStates.IsMissing(this.AchievementState);

    /// <summary>Shows the "Load achievements" button.</summary>
    public bool CanLoadAchievements => this.IsFetchingAchievements == false && this.IsAchievementDataMissing;

    public string LoadAchievementsLabel => Strings.Get("Card_LoadAchievements");

    public bool IsPerfect => this.Progress is { IsPerfect: true };

    public double ProgressPercent => this.Progress is { Total: > 0 } p ? p.Fraction * 100 : 0;

    /// <summary>For sorting: fraction, -1 for games without achievements, -0.5 when progress isn't known.</summary>
    public double CompletionSortKey => AchievementStates.CompletionSortKey(this.AchievementState, this.Progress);

    public DateTimeOffset? LastPlayed => this.Playtime?.LastPlayed;

    public TimeSpan PlaytimeSpan => this.Playtime?.Playtime ?? TimeSpan.Zero;

    public string ProgressText => this.AchievementState switch
    {
        _ when this.IsFetchingAchievements && this.Progress == null => Strings.Get("Card_AchievementsLoading"),
        AchievementState.NotLoaded => Strings.Plural("Card_NotLoaded", this.StoreCount?.Total ?? 0),
        AchievementState.Unknown => Strings.Get("Card_AchievementsUnknown"),
        AchievementState.Checking => Strings.Get("Card_AchievementsChecking"),
        _ => this.Progress switch
        {
            null or { Total: 0 } => Strings.Get("Card_NoAchievements"),
            { Unlocked: 0 } p => Strings.Format("Card_ProgressNotStarted", LibraryFormat.Count(p.Total)),
            { } p => Strings.Format(
                "Card_Progress",
                LibraryFormat.Count(p.Unlocked),
                LibraryFormat.Count(p.Total),
                Math.Floor(p.Fraction * 100).ToString(CultureInfo.CurrentCulture)),
        },
    };

    public string PlaytimeText => LibraryFormat.PlaytimeSummary(this.Playtime);

    /// <summary>"12/30" for the list view, "?/30" when not loaded, "?" when unknown, or "–" without achievements.</summary>
    public string ProgressCountText => this.AchievementState switch
    {
        AchievementState.Loaded when this.Progress is { } p => $"{LibraryFormat.Count(p.Unlocked)}/{LibraryFormat.Count(p.Total)}",
        AchievementState.NotLoaded => $"?/{LibraryFormat.Count(this.StoreCount?.Total ?? 0)}",
        AchievementState.Unknown => "?",
        _ => "–",
    };

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

    /// <summary>Asks Steam for the game's achievement data (a short headless SAM run).</summary>
    [RelayCommand]
    private void LoadAchievements() => this._owner.LoadAchievements(this);

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
