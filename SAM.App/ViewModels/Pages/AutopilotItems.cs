// Partial-property [ObservableProperty] needs C# 13; this project uses C# 12 and isn't AOT-compiled.
#pragma warning disable MVVMTK0045

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.ViewModels.Pages;

/// <summary>A choice in a ComboBox: a stored key and its display name.</summary>
public sealed record AutopilotChoice(string Key, string Name)
{
    public override string ToString() => this.Name;
}

/// <summary>
/// Game card images for the Autopilot page and dialogs, loaded through the shared image cache
/// and memoized per game for the page's lifetime. Never throws.
/// </summary>
public sealed class AutopilotArtLoader(AppServices services)
{
    private readonly Dictionary<uint, Task<string?>> _tasks = [];

    /// <summary>The local path of a game's cached card image, or null when there is none.</summary>
    public Task<string?> GetAsync(uint appId)
    {
        if (this._tasks.TryGetValue(appId, out var task) == false)
        {
            task = this.LoadAsync(appId);
            this._tasks[appId] = task;
        }

        return task;
    }

    private async Task<string?> LoadAsync(uint appId)
    {
        try
        {
            var urls = services.Art.GetCardImageUrls(appId);
            return await services.Images.GetAsync(urls, CancellationToken.None);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Autopilot: couldn't load the image of {appId}", e);
            return null;
        }
    }
}

/// <summary>A game in one of the Autopilot lists (manual queue, excluded games, overrides).</summary>
public sealed partial class AutopilotGameItem : ObservableObject
{
    private readonly Action<AutopilotGameItem>? _moveUp;
    private readonly Action<AutopilotGameItem>? _moveDown;
    private readonly Action<AutopilotGameItem>? _remove;
    private readonly Action<AutopilotGameItem>? _editOverrides;
    private readonly Action<AutopilotGameItem, bool>? _includedChanged;

    public AutopilotGameItem(
        uint appId,
        string name,
        string detail,
        AutopilotArtLoader art,
        Action<AutopilotGameItem>? moveUp = null,
        Action<AutopilotGameItem>? moveDown = null,
        Action<AutopilotGameItem>? remove = null,
        Action<AutopilotGameItem>? editOverrides = null,
        bool isIncluded = true,
        Action<AutopilotGameItem, bool>? includedChanged = null,
        int unlocked = 0,
        int total = 0)
    {
        this.Unlocked = unlocked;
        this.Total = total;
        this._isIncluded = isIncluded;
        this._includedChanged = includedChanged;
        this.AppId = appId;
        this.Name = name;
        this.Detail = detail;
        this._moveUp = moveUp;
        this._moveDown = moveDown;
        this._remove = remove;
        this._editOverrides = editOverrides;
        _ = this.LoadImageAsync(art);
    }

    public uint AppId { get; }

    public string Name { get; }

    /// <summary>Secondary text, e.g. achievement progress.</summary>
    public string Detail { get; }

    public bool HasDetail => this.Detail.Length > 0;

    /// <summary>Achievements unlocked / in total (Total 0: unknown, no progress bar).</summary>
    public int Unlocked { get; }

    public int Total { get; }

    public bool HasProgress => this.Total > 0;

    public string ProgressCountText => Strings.Format("Autopilot_GameProgressShort", this.Unlocked, this.Total);

    /// <summary>1-based place in the run order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private int _position;

    public string PositionText => this.Position.ToString(System.Globalization.CultureInfo.CurrentCulture);

    [ObservableProperty]
    private ImageSource? _image;

    /// <summary>Ticked: Autopilot runs this game. Unticked games stay in the list but are skipped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    private bool _isIncluded;

    /// <summary>Skipped games are dimmed.</summary>
    public double RowOpacity => this.IsIncluded ? 1 : 0.5;

    public string IncludeLabel => Strings.Format("Autopilot_IncludeNamed", this.Name);

    partial void OnIsIncludedChanged(bool value) => this._includedChanged?.Invoke(this, value);

    /// <summary>Whether this game has per-game override settings (shows a badge).</summary>
    [ObservableProperty]
    private bool _hasOverride;

    /// <summary>Whether this game is first / last in a reorderable list (disables Up / Down).</summary>
    [ObservableProperty]
    private bool _canMoveUp;

    [ObservableProperty]
    private bool _canMoveDown;

    public bool IsReorderable => this._moveUp != null;

    public bool CanEditOverrides => this._editOverrides != null;

    public string RemoveLabel => Strings.Format("Autopilot_RemoveNamed", this.Name);

    public string MoveUpLabel => Strings.Format("Autopilot_MoveUpNamed", this.Name);

    public string MoveDownLabel => Strings.Format("Autopilot_MoveDownNamed", this.Name);

    public string OverridesLabel => Strings.Format("Autopilot_OverridesNamed", this.Name);

    [RelayCommand]
    private void MoveUp() => this._moveUp?.Invoke(this);

    [RelayCommand]
    private void MoveDown() => this._moveDown?.Invoke(this);

    [RelayCommand]
    private void Remove() => this._remove?.Invoke(this);

    [RelayCommand]
    private void EditOverrides() => this._editOverrides?.Invoke(this);

    private async Task LoadImageAsync(AutopilotArtLoader art)
    {
        var path = await art.GetAsync(this.AppId);
        this.Image = LibraryFormat.Thumbnail(path);
    }
}

/// <summary>One weekday chip on the schedule.</summary>
public sealed partial class AutopilotDayToggle(DayOfWeek day, string name, string fullName, Action changed) : ObservableObject
{
    public DayOfWeek Day { get; } = day;

    public string Name { get; } = name;

    public string FullName { get; } = fullName;

    [ObservableProperty]
    private bool _isChecked = true;

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>
/// One Autopilot game's live status card. Updated in place from the engine's status so the
/// cards don't flicker on every change.
/// </summary>
public sealed partial class AutopilotStatusItem : ObservableObject
{
    private readonly AutopilotViewModel _owner;

    public AutopilotStatusItem(AutopilotViewModel owner, AutopilotGameStatus status, AutopilotArtLoader art)
    {
        this._owner = owner;
        this.AppId = status.AppId;
        this._status = status;
        _ = this.LoadImageAsync(art);
    }

    public uint AppId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(Name), nameof(KindText), nameof(KindGlyph), nameof(KindBrush), nameof(IsPaused), nameof(PauseLabel), nameof(PauseGlyph),
        nameof(CountsText), nameof(HasNext), nameof(Message), nameof(HasMessage), nameof(IsError), nameof(AutomationName),
        nameof(ProgressValue), nameof(ProgressMaximum), nameof(HasTarget), nameof(ProgressText),
        nameof(CanUnlockNow), nameof(IsUnlockingNow), nameof(IsNotUnlockingNow), nameof(UnlockNowLabel))]
    private AutopilotGameStatus _status;

    /// <summary>The time target this game's progress bar measures against (from its resolved profile).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressValue), nameof(ProgressMaximum), nameof(HasTarget), nameof(ProgressText))]
    private AutopilotTargetMode _targetMode = AutopilotTargetMode.AddHours;

    [ObservableProperty]
    private ImageSource? _image;

    /// <summary>Refreshed by the page timer so relative times stay current.</summary>
    [ObservableProperty]
    private string _nextText = "";

    public string Name => this.Status.Name;

    public string KindText => AutopilotFormat.StatusText(this.Status.Kind);

    public string KindGlyph => AutopilotFormat.StatusGlyph(this.Status.Kind);

    public Brush KindBrush => AutopilotFormat.StatusBrush(this.Status.Kind);

    public bool IsPaused => this.Status.Kind == AutopilotGameStatusKind.PausedByUser;

    public string PauseLabel => Strings.Get(this.IsPaused ? "Autopilot_Resume" : "Autopilot_Pause");

    public string PauseGlyph => this.IsPaused ? "" : "";

    public bool IsError => this.Status.Kind == AutopilotGameStatusKind.Error;

    public bool HasTarget => this.Status.TargetHours is > 0 && this.TargetMode != AutopilotTargetMode.Unlimited;

    /// <summary>Hours counted toward the target: Autopilot time for "add hours", total playtime otherwise.</summary>
    private double CountedHours => this.TargetMode == AutopilotTargetMode.AddHours ? this.Status.AutopilotHours : this.Status.PlaytimeHours;

    public double ProgressMaximum => this.HasTarget ? this.Status.TargetHours!.Value : 1;

    public double ProgressValue => this.HasTarget ? Math.Min(this.CountedHours, this.ProgressMaximum) : 0;

    public string ProgressText => this.HasTarget
        ? Strings.Format(
            this.TargetMode == AutopilotTargetMode.AddHours ? "Autopilot_ProgressAdded" : "Autopilot_ProgressTotal",
            AutopilotFormat.Hours(this.CountedHours),
            AutopilotFormat.Hours(this.Status.TargetHours!.Value))
        : Strings.Format("Autopilot_ProgressNoTarget", AutopilotFormat.Hours(this.Status.PlaytimeHours), AutopilotFormat.Hours(this.Status.AutopilotHours));

    public string CountsText => Strings.Format(
        "Autopilot_Counts",
        Strings.Plural("Autopilot_UnlockedByAutopilot", this.Status.UnlockedByAutopilot),
        Strings.Plural("Autopilot_Remaining", this.Status.RemainingAllowed));

    public bool HasNext => string.IsNullOrEmpty(this.Status.NextUnlockName) == false;

    /// <summary>"Unlock next now" is offered for running games that have a next unlock.</summary>
    public bool CanUnlockNow => this.Status.Kind == AutopilotGameStatusKind.Running && (this.HasNext || this.IsUnlockingNow);

    public bool IsUnlockingNow => this.Status.UnlockingNow;

    public bool IsNotUnlockingNow => this.IsUnlockingNow == false;

    public string UnlockNowLabel => Strings.Format(
        this.IsUnlockingNow ? "Autopilot_UnlockingNow" : "Autopilot_UnlockNow",
        this.Status.NextUnlockName ?? "");

    public string? Message => this.Status.Message;

    public bool HasMessage => string.IsNullOrWhiteSpace(this.Status.Message) == false;

    public string AutomationName => $"{this.Name}, {this.KindText}";

    public string OpenGameLabel => Strings.Format("Pages_OpenGame", this.Name);

    /// <summary>Recomputes the "next unlock" line (relative to now).</summary>
    public void RefreshTimes(DateTimeOffset now)
    {
        if (this.HasNext == false)
        {
            this.NextText = "";
            return;
        }

        var eta = AutopilotFormat.RelativeEta(this.Status.NextUnlockEstimate, now);
        this.NextText = eta.Length == 0
            ? Strings.Format("Autopilot_NextUnlock", this.Status.NextUnlockName)
            : Strings.Format("Autopilot_NextUnlockEta", this.Status.NextUnlockName, eta);
    }

    [RelayCommand]
    private void TogglePause() => this._owner.TogglePause(this);

    [RelayCommand]
    private Task PreviewAsync() => this._owner.PreviewGameAsync(this.AppId, this.Name);

    [RelayCommand]
    private void UnlockNow() => this._owner.UnlockNextNow(this);

    [RelayCommand]
    private Task ResetAsync() => this._owner.ResetGameAsync(this);

    [RelayCommand]
    private void RemoveFromAutopilot() => this._owner.RemoveFromAutopilot(this.AppId);

    [RelayCommand]
    private Task EditOverridesAsync() => this._owner.EditOverridesAsync(this.AppId, this.Name);

    [RelayCommand]
    private void OpenGame() => PageActions.OpenGame(this.AppId);

    private async Task LoadImageAsync(AutopilotArtLoader art)
    {
        var path = await art.GetAsync(this.AppId);
        if (path != null)
        {
            this.Image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
            {
                DecodePixelWidth = 184,
                DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical,
                UriSource = new Uri(path),
            };
        }
    }
}
