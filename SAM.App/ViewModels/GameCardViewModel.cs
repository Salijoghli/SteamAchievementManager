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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string _name;

    /// <summary>"normal", "demo", "mod" or "junk".</summary>
    [ObservableProperty]
    private string _type;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteGlyph), nameof(FavoriteLabel))]
    private bool _isFavorite;

    /// <summary>Null when unknown (no schema).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ProgressPercent), nameof(HasAchievements), nameof(IsPerfect), nameof(CompletionSortKey))]
    private GameProgress? _progress;

    /// <summary>False until progress has been computed at least once (shows a skeleton).</summary>
    [ObservableProperty]
    private bool _isProgressLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaytimeText), nameof(LastPlayed))]
    private PlaytimeInfo? _playtime;

    /// <summary>False until the playtime map has been read at least once (shows a skeleton).</summary>
    [ObservableProperty]
    private bool _isPlaytimeLoaded;

    /// <summary>Local path of the cached card image, once downloaded.</summary>
    [ObservableProperty]
    private string? _imagePath;

    /// <summary>True when no image could be loaded; the card shows a fallback.</summary>
    [ObservableProperty]
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
        null or { Total: 0 } => "No achievements",
        { Unlocked: 0 } p => $"0 / {p.Total} · Not started",
        { } p => $"{p.Unlocked} / {p.Total} · {Math.Floor(p.Fraction * 100).ToString(CultureInfo.CurrentCulture)}%",
    };

    public string PlaytimeText => LibraryFormat.PlaytimeSummary(this.Playtime);

    public string FavoriteGlyph => this.IsFavorite ? "" : "";

    public string FavoriteLabel => this.IsFavorite ? "Remove from favorites" : "Add to favorites";

    public string ToolTipText => $"{this.Name} (App ID {this.AppIdText})";

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
    private void ToggleFavorite() => this.IsFavorite = !this.IsFavorite;

    [RelayCommand]
    private void CopyAppId() => this._owner.CopyAppId(this);

    [RelayCommand]
    private void OpenStorePage() => this._owner.OpenUrl($"https://store.steampowered.com/app/{this.AppIdText}");

    [RelayCommand]
    private void OpenAchievementsPage() => this._owner.OpenUrl($"https://steamcommunity.com/stats/{this.AppIdText}/achievements");
}
