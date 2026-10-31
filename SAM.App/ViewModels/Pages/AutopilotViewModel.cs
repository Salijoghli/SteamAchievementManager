// Partial-property [ObservableProperty] needs C# 13; this project uses C# 12 and isn't AOT-compiled.
#pragma warning disable MVVMTK0045

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using SAM.App.Models;
using SAM.App.Services;
using SAM.App.Services.Autopilot;

namespace SAM.App.ViewModels.Pages;

/// <summary>Dialogs the Autopilot page shows for its view model (implemented by the page).</summary>
public interface IAutopilotDialogs
{
    /// <summary>A searchable multi-select list of owned games with achievements. Null when cancelled.</summary>
    Task<IReadOnlyList<uint>?> PickGamesAsync(string title, string primaryText, IReadOnlyCollection<uint> alreadyListed);

    /// <summary>Edits <paramref name="value"/> in place. True when the user saved.</summary>
    Task<bool> EditOverridesAsync(uint appId, string name, AutopilotGameOverride value, AutopilotConfig config);

    /// <summary>The dry-run plan for the given games under <paramref name="config"/> (the edited, unsaved copy).</summary>
    Task ShowPreviewAsync(IReadOnlyList<(uint AppId, string Name)> games, AutopilotConfig config);

    Task<bool> ConfirmAsync(string title, string message, string primaryText);

    /// <summary>Asks for a name. Null when cancelled.</summary>
    Task<string?> PromptNameAsync(string title, string initial, string primaryText, Func<string, string?> validate);
}

/// <summary>
/// The Autopilot tab: live status from <see cref="AutopilotEngine"/>, and an editor for every
/// <see cref="AutopilotConfig"/> option. Edits go into a copy of the engine's configuration; Save
/// applies it (<see cref="AutopilotEngine.SaveConfig"/>), Discard reloads it. Every call into the
/// engine is guarded, so the page shows a friendly message instead of failing when it isn't available.
/// </summary>
public sealed partial class AutopilotViewModel : ObservableObject
{
    /// <summary>Source choices, in <see cref="SourceIndex"/> order.</summary>
    private static readonly AutopilotGameSource[] Sources =
    [
        AutopilotGameSource.Manual,
        AutopilotGameSource.AllWithLocked,
        AutopilotGameSource.Collection,
        AutopilotGameSource.SmartList,
        AutopilotGameSource.Installed,
    ];

    private static readonly AutopilotNotifyMode[] NotifyModes = [AutopilotNotifyMode.None, AutopilotNotifyMode.EachUnlock, AutopilotNotifyMode.DailySummary];

    private static readonly TimeSpan DefaultActiveFrom = TimeSpan.FromHours(9);
    private static readonly TimeSpan DefaultActiveTo = TimeSpan.FromHours(23);
    private static readonly TimeSpan DefaultQuietFrom = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultQuietTo = TimeSpan.FromHours(8);

    private readonly AppServices _services;
    private readonly LibraryViewModel _library;
    private readonly DispatcherQueue? _dispatcher;
    private readonly AutopilotArtLoader _art;
    private readonly DispatcherQueueTimer? _clock;
    private AutopilotEngine? _engine;
    private AutopilotConfig _edit = new();
    private string _savedSnapshot = "";
    private bool _wasRunningSaved;
    private bool _syncingLists;

    // Remembered while "All day" / quiet hours are off, so turning them back on restores the times.
    private TimeSpan _activeFrom = DefaultActiveFrom;
    private TimeSpan _activeTo = DefaultActiveTo;
    private TimeSpan _quietFrom = DefaultQuietFrom;
    private TimeSpan _quietTo = DefaultQuietTo;

    public AutopilotViewModel(AppServices services, LibraryViewModel library)
    {
        this._services = services;
        this._library = library;
        this._dispatcher = DispatcherQueue.GetForCurrentThread();
        this._art = new AutopilotArtLoader(services);

        var culture = CultureInfo.CurrentCulture.DateTimeFormat;
        for (var i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)culture.FirstDayOfWeek + i) % 7);
            this.Days.Add(new AutopilotDayToggle(day, culture.GetAbbreviatedDayName(day), culture.GetDayName(day), this.OnDaysChanged));
        }

        this.ManualGames.CollectionChanged += this.OnManualGamesCollectionChanged;
        library.CollectionsChanged += (_, _) => this.RefreshCollections();
        library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.IsSummaryReady))
            {
                this.UpdateSourceSummary();
            }
        };

        if (this._dispatcher != null)
        {
            this._clock = this._dispatcher.CreateTimer();
            this._clock.Interval = TimeSpan.FromSeconds(30);
            this._clock.Tick += (_, _) => this.RefreshTimes();
        }

        this.RefreshCollections();
        this.Connect();
    }

    /// <summary>Set by the page; dialogs aren't available until then.</summary>
    public IAutopilotDialogs? Dialogs { get; set; }

    #region Options
    public static IReadOnlyList<string> SourceOptions { get; } =
    [
        Strings.Get("Autopilot_SourceManual"),
        Strings.Get("Autopilot_SourceAllWithLocked"),
        Strings.Get("Autopilot_SourceCollection"),
        Strings.Get("Autopilot_SourceSmartList"),
        Strings.Get("Autopilot_SourceInstalled"),
    ];

    public static IReadOnlyList<AutopilotChoice> SmartListOptions { get; } =
    [
        new(LibraryViewModel.AlmostPerfectList, Strings.Get("Library_SmartAlmostPerfect")),
        new(LibraryViewModel.NeverStartedList, Strings.Get("Library_SmartNeverStarted")),
    ];

    public static IReadOnlyList<string> NotifyOptions { get; } =
    [
        Strings.Get("Autopilot_NotifyNone"),
        Strings.Get("Autopilot_NotifyEachUnlock"),
        Strings.Get("Autopilot_NotifyDailySummary"),
    ];

    /// <summary>Steam and SAM collections, by GameCollection.FilterKey.</summary>
    [ObservableProperty]
    private IReadOnlyList<AutopilotChoice> _collectionOptions = [];
    #endregion

    #region Engine state and header
    /// <summary>Set when the engine couldn't be reached; the page still shows (and edits) a default configuration.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEngineError), nameof(CanToggleRunning), nameof(HasHeaderError))]
    private string? _engineError;

    public bool HasEngineError => this.EngineError != null;

    /// <summary>Either InfoBar under the header is showing.</summary>
    public bool HasHeaderError => this.HasEngineError || this.HasActionError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopLabel), nameof(StartStopGlyph), nameof(IsStopped), nameof(CanToggleRunning))]
    private bool _isRunning;

    public bool IsStopped => this.IsRunning == false;

    /// <summary>The saved game source has games in it; Start is disabled without any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleRunning))]
    private bool _hasGamesToRun = true;

    /// <summary>Starting or stopping.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleRunning), nameof(IsNotToggling))]
    private bool _isToggling;

    /// <summary>Shows the Start/Stop icon; the spinner takes its place while toggling.</summary>
    public bool IsNotToggling => this.IsToggling == false;

    public bool CanToggleRunning => this.IsToggling == false && this.HasEngineError == false && (this.IsRunning || this.HasGamesToRun);

    public string StartStopLabel => Strings.Get(this.IsRunning ? "Autopilot_Stop" : "Autopilot_Start");

    public string StartStopGlyph => this.IsRunning ? "" : "";

    [ObservableProperty]
    private string _stateText = "";

    /// <summary>A failed action (start, pause, save…): shown in an InfoBar under the header.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionError), nameof(HasHeaderError))]
    private string? _actionError;

    public bool HasActionError
    {
        get => this.ActionError != null;
        set
        {
            if (value == false)
            {
                this.ActionError = null;
            }
        }
    }
    #endregion

    #region Status
    public ObservableCollection<AutopilotStatusItem> Status { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusEmpty), nameof(ShowGetStarted), nameof(ShowStatusArea))]
    private bool _hasStatus;

    public bool IsStatusEmpty => this.HasStatus == false;

    /// <summary>
    /// "Add games to get started": only while nothing is running and no games are picked yet (even
    /// unsaved), so the game settings below get the attention once there are some.
    /// </summary>
    public bool ShowGetStarted => this.HasStatus == false && this.IsManualSource && this.HasManualGames == false;

    /// <summary>The Status section: hidden entirely (no empty gap) when it has nothing to show.</summary>
    public bool ShowStatusArea => this.HasStatus || this.ShowGetStarted;
    #endregion

    #region Editing
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrors))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string? _errorsText;

    public bool HasErrors => this.ErrorsText != null;

    /// <summary>The configuration being edited (unsaved).</summary>
    public AutopilotConfig EditedConfig => this._edit;
    #endregion

    #region Games
    public int SourceIndex
    {
        get => Array.IndexOf(Sources, this._edit.Source);
        set
        {
            if (value >= 0 && value < Sources.Length && Sources[value] != this._edit.Source)
            {
                this._edit.Source = Sources[value];
                if (this._edit.Source == AutopilotGameSource.SmartList && this._edit.SmartList == null)
                {
                    this._edit.SmartList = LibraryViewModel.AlmostPerfectList;
                    this.OnPropertyChanged(nameof(this.SmartListIndex));
                }

                this.OnPropertyChanged();
                this.OnSourceChanged();
            }
        }
    }

    public bool IsManualSource => this._edit.Source == AutopilotGameSource.Manual;

    public bool IsCollectionSource => this._edit.Source == AutopilotGameSource.Collection;

    public bool IsSmartListSource => this._edit.Source == AutopilotGameSource.SmartList;

    public int CollectionIndex
    {
        get => this.CollectionOptions.ToList().FindIndex(c => c.Key == this._edit.CollectionKey);
        set
        {
            if (value >= 0 && value < this.CollectionOptions.Count && this.CollectionOptions[value].Key != this._edit.CollectionKey)
            {
                this._edit.CollectionKey = this.CollectionOptions[value].Key;
                this.OnPropertyChanged();
                this.OnSourceChanged();
            }
        }
    }

    public int SmartListIndex
    {
        get => SmartListOptions.ToList().FindIndex(c => c.Key == this._edit.SmartList);
        set
        {
            if (value >= 0 && value < SmartListOptions.Count && SmartListOptions[value].Key != this._edit.SmartList)
            {
                this._edit.SmartList = SmartListOptions[value].Key;
                this.OnPropertyChanged();
                this.OnSourceChanged();
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCollectionError))]
    private string? _collectionError;

    public bool HasCollectionError => this.CollectionError != null;

    /// <summary>"23 games match right now" for the automatic sources.</summary>
    [ObservableProperty]
    private string _sourceSummary = "";

    /// <summary>The manual queue, in run order.</summary>
    public ObservableCollection<AutopilotGameItem> ManualGames { get; } = [];

    public ObservableCollection<AutopilotGameItem> ExcludedGames { get; } = [];

    /// <summary>Games with per-game overrides.</summary>
    public ObservableCollection<AutopilotGameItem> OverrideGames { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGetStarted), nameof(ShowStatusArea))]
    private bool _hasManualGames;

    [ObservableProperty]
    private bool _hasExcludedGames;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverridesSection))]
    private bool _hasOverrideGames;

    /// <summary>
    /// The "Per-game overrides" group. With picked games each run-order row has its own edit button,
    /// so the group only appears there to list overrides for games no longer in the run order
    /// (which could otherwise not be removed). Other sources have no per-game rows, so it's always shown.
    /// </summary>
    public bool ShowOverridesSection => this.IsManualSource == false || this.HasOverrideGames;

    public double MaxConcurrentGames
    {
        get => this._edit.MaxConcurrentGames;
        set
        {
            if (double.IsFinite(value) == false)
            {
                AutopilotProfileEditor.Restore(() => this.OnPropertyChanged(nameof(this.MaxConcurrentGames)));
                return;
            }

            var count = (int)Math.Round(Math.Clamp(value, 1, 32));
            if (count != this._edit.MaxConcurrentGames)
            {
                this._edit.MaxConcurrentGames = count;
                this.OnPropertyChanged();
                this.OnEdited();
            }
        }
    }
    #endregion

    #region Rules
    /// <summary>Built-in profiles, then the edited copy's custom ones.</summary>
    [ObservableProperty]
    private IReadOnlyList<AutopilotChoice> _profileOptions = [];

    public int ProfileIndex
    {
        get => this.ProfileOptions.ToList().FindIndex(p => string.Equals(p.Key, this._edit.ActiveProfile, StringComparison.Ordinal));
        set
        {
            if (value >= 0 && value < this.ProfileOptions.Count && this.ProfileOptions[value].Key != this._edit.ActiveProfile)
            {
                this._edit.ActiveProfile = this.ProfileOptions[value].Key;
                this.OnPropertyChanged();
                this.LoadProfileEditor();
                this.OnEdited();
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomProfile), nameof(IsBuiltInProfile))]
    [NotifyCanExecuteChangedFor(nameof(RenameProfileCommand), nameof(DeleteProfileCommand))]
    private AutopilotProfileEditor? _profile;

    public bool IsCustomProfile => this.Profile is { IsBuiltIn: false };

    public bool IsBuiltInProfile => this.Profile is { IsBuiltIn: true };
    #endregion

    #region Schedule
    public bool IsAllDay
    {
        get => this._edit.ActiveFrom == null || this._edit.ActiveTo == null;
        set
        {
            if (value == this.IsAllDay)
            {
                return;
            }

            this._edit.ActiveFrom = value ? null : this._activeFrom;
            this._edit.ActiveTo = value ? null : this._activeTo;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.HasActiveWindow));
            this.OnEdited();
        }
    }

    public bool HasActiveWindow => this.IsAllDay == false;

    public TimeSpan ActiveFrom
    {
        get => this._edit.ActiveFrom ?? this._activeFrom;
        set => this.SetTime(ref this._activeFrom, value, v => this._edit.ActiveFrom = this.IsAllDay ? null : v);
    }

    public TimeSpan ActiveTo
    {
        get => this._edit.ActiveTo ?? this._activeTo;
        set => this.SetTime(ref this._activeTo, value, v => this._edit.ActiveTo = this.IsAllDay ? null : v);
    }

    public ObservableCollection<AutopilotDayToggle> Days { get; } = [];

    public bool HasQuietHours
    {
        get => this._edit.QuietFrom != null && this._edit.QuietTo != null;
        set
        {
            if (value == this.HasQuietHours)
            {
                return;
            }

            this._edit.QuietFrom = value ? this._quietFrom : null;
            this._edit.QuietTo = value ? this._quietTo : null;
            this.OnPropertyChanged();
            this.OnEdited();
        }
    }

    public TimeSpan QuietFrom
    {
        get => this._edit.QuietFrom ?? this._quietFrom;
        set => this.SetTime(ref this._quietFrom, value, v => this._edit.QuietFrom = this.HasQuietHours ? v : null);
    }

    public TimeSpan QuietTo
    {
        get => this._edit.QuietTo ?? this._quietTo;
        set => this.SetTime(ref this._quietTo, value, v => this._edit.QuietTo = this.HasQuietHours ? v : null);
    }

    public double MaxHoursPerGamePerDay
    {
        get => this._edit.MaxHoursPerGamePerDay;
        set
        {
            if (double.IsFinite(value) == false)
            {
                AutopilotProfileEditor.Restore(() => this.OnPropertyChanged(nameof(this.MaxHoursPerGamePerDay)));
                return;
            }

            var hours = Math.Clamp(Math.Round(value, 1), 0, 24);
            if (hours != this._edit.MaxHoursPerGamePerDay)
            {
                this._edit.MaxHoursPerGamePerDay = hours;
                this.OnPropertyChanged();
                this.OnEdited();
            }
        }
    }

    public bool PauseWhileGaming
    {
        get => this._edit.PauseWhileGaming;
        set => this.SetFlag(this._edit.PauseWhileGaming, value, v => this._edit.PauseWhileGaming = v);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScheduleError))]
    private string? _scheduleError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuietError))]
    private string? _quietError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDaysError))]
    private string? _daysError;

    public bool HasScheduleError => this.ScheduleError != null;

    public bool HasQuietError => this.QuietError != null;

    public bool HasDaysError => this.DaysError != null;

    /// <summary>"About 14 h of running time per game per day", from the planner.</summary>
    [ObservableProperty]
    private string _scheduleSummary = "";
    #endregion

    #region Behaviour
    public int NotifyIndex
    {
        get => Array.IndexOf(NotifyModes, this._edit.NotifyMode);
        set
        {
            if (value >= 0 && value < NotifyModes.Length && NotifyModes[value] != this._edit.NotifyMode)
            {
                this._edit.NotifyMode = NotifyModes[value];
                this.OnPropertyChanged();
                this.OnEdited();
            }
        }
    }

    public bool BackupBeforeUnlock
    {
        get => this._edit.BackupBeforeUnlock;
        set => this.SetFlag(this._edit.BackupBeforeUnlock, value, v => this._edit.BackupBeforeUnlock = v);
    }

    public bool StartWithWindows
    {
        get => this._edit.StartWithWindows;
        set => this.SetFlag(this._edit.StartWithWindows, value, v => this._edit.StartWithWindows = v);
    }

    public bool ResumeOnStartup
    {
        get => this._edit.ResumeOnStartup;
        set => this.SetFlag(this._edit.ResumeOnStartup, value, v => this._edit.ResumeOnStartup = v);
    }
    #endregion

    #region Lifetime
    /// <summary>The page is shown: refresh status and keep relative times current.</summary>
    public void OnShown()
    {
        this.RefreshStatus();
        this.UpdateSourceSummary();
        this._clock?.Start();
    }

    public void OnHidden() => this._clock?.Stop();

    /// <summary>Gets the engine and its configuration; on failure the page edits defaults and explains.</summary>
    private void Connect()
    {
        AutopilotConfig config;
        try
        {
            this._engine = this._services.Autopilot;
            config = this._engine.Config;
            this._engine.Changed += this.OnEngineChanged;
            this.EngineError = null;
        }
        catch (Exception e)
        {
            Diagnostics.Log("Autopilot: couldn't reach the engine", e);
            this.EngineError = AutopilotFormat.ErrorMessage(e);
            config = new AutopilotConfig();
        }

        this.LoadFrom(config);
        this.RefreshStatus();
    }

    private void OnEngineChanged(object? sender, EventArgs e)
    {
        if (this._dispatcher is { HasThreadAccess: false } dispatcher)
        {
            dispatcher.TryEnqueue(() => this.OnEngineChanged(sender, e));
            return;
        }

        // Pick up configuration changes made elsewhere unless the user is in the middle of editing.
        if (this.IsDirty == false && this._engine != null)
        {
            try
            {
                var config = this._engine.Config;
                if (AutopilotFormat.Snapshot(Normalize(AutopilotFormat.Clone(config))) != this._savedSnapshot)
                {
                    this.LoadFrom(config);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Autopilot: couldn't read the configuration", ex);
            }
        }

        this.RefreshStatus();
    }
    #endregion

    #region Header actions
    [RelayCommand]
    private async Task ToggleRunningAsync()
    {
        if (this._engine == null || this.IsToggling)
        {
            return;
        }

        this.IsToggling = true;
        this.ActionError = null;
        try
        {
            if (this._engine.IsRunning)
            {
                await this._engine.StopAsync();
            }
            else if (this._engine.HasGames())
            {
                await this._engine.StartAsync();
            }
            else
            {
                this.ActionError = Strings.Get("Autopilot_StartNoGames");
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log("Autopilot: couldn't start or stop", e);
            this.ActionError = AutopilotFormat.ErrorMessage(e);
        }
        finally
        {
            this.IsToggling = false;
            this.RefreshStatus();
        }
    }
    #endregion

    #region Status
    private void RefreshStatus()
    {
        IReadOnlyList<AutopilotGameStatus> status = [];
        var running = false;
        if (this._engine != null && this.EngineError == null)
        {
            try
            {
                running = this._engine.IsRunning;
                status = this._engine.Status;
            }
            catch (Exception e)
            {
                Diagnostics.Log("Autopilot: couldn't read the status", e);
                this.ActionError ??= AutopilotFormat.ErrorMessage(e);
            }
        }

        this.IsRunning = running;
        if (running == false && this._engine != null && this.EngineError == null)
        {
            try
            {
                this.HasGamesToRun = this._engine.HasGames();
            }
            catch (Exception e)
            {
                Diagnostics.Log("Autopilot: couldn't resolve the games", e);
                this.HasGamesToRun = true;
            }
        }

        // Update cards in place, in the engine's queue order, with finished games moved to the end
        // (OrderBy is stable, so the rest keep their order).
        status = status.OrderBy(s => s.Kind == AutopilotGameStatusKind.Completed ? 1 : 0).ToList();
        var existing = this.Status.ToDictionary(s => s.AppId);
        var savedConfig = this.SavedConfig();
        for (var i = 0; i < status.Count; i++)
        {
            var entry = status[i];
            if (existing.Remove(entry.AppId, out var item))
            {
                item.Status = entry;
                var from = this.Status.IndexOf(item);
                if (from != i)
                {
                    this.Status.Move(from, i);
                }
            }
            else
            {
                item = new AutopilotStatusItem(this, entry, this._art);
                this.Status.Insert(Math.Min(i, this.Status.Count), item);
            }

            item.TargetMode = ResolveTargetMode(savedConfig, entry.AppId);
        }

        foreach (var stale in existing.Values)
        {
            this.Status.Remove(stale);
        }

        this.HasStatus = this.Status.Count > 0;
        this.RefreshTimes();
    }

    private void RefreshTimes()
    {
        var now = DateTimeOffset.Now;
        foreach (var item in this.Status)
        {
            item.RefreshTimes(now);
        }

        if (this.HasEngineError)
        {
            this.StateText = Strings.Get("Autopilot_StateUnavailable");
            return;
        }

        if (this.IsRunning == false)
        {
            this.StateText = Strings.Get(this.HasGamesToRun ? "Autopilot_StateStopped" : "Autopilot_StateStoppedNoGames");
            return;
        }

        var runningCount = this.Status.Count(s => s.Status.Kind is AutopilotGameStatusKind.Running or AutopilotGameStatusKind.Starting);
        var next = this.Status.Select(s => s.Status.NextUnlockEstimate).Where(t => t != null).Min();
        var running = runningCount == 0 ? Strings.Get("Autopilot_StateIdle") : Strings.Plural("Autopilot_StateRunning", runningCount);
        this.StateText = next != null
            ? Strings.Format("Autopilot_StateRunningNext", running, AutopilotFormat.RelativeEta(next, now))
            : running;
    }

    private static AutopilotTargetMode ResolveTargetMode(AutopilotConfig config, uint appId)
    {
        try
        {
            return AutopilotPlanner.ResolveProfile(config, appId).TargetMode;
        }
        catch (Exception)
        {
            // Planner not available: fall back to the override or the default.
            return config.Overrides.TryGetValue(appId, out var o) && o.TargetMode is { } mode ? mode : AutopilotTargetMode.AddHours;
        }
    }

    internal void TogglePause(AutopilotStatusItem item)
    {
        this.RunEngineAction(engine =>
        {
            if (item.IsPaused)
            {
                engine.ResumeGame(item.AppId);
            }
            else
            {
                engine.PauseGame(item.AppId);
            }
        });
    }

    /// <summary>Unlocks a running game's next planned achievement right away (on the engine's next pass).</summary>
    internal void UnlockNextNow(AutopilotStatusItem item)
    {
        if (item.IsUnlockingNow == false)
        {
            this.RunEngineAction(engine => engine.UnlockNextNow(item.AppId));
        }
    }

    internal async Task ResetGameAsync(AutopilotStatusItem item)
    {
        if (this.Dialogs == null ||
            await this.Dialogs.ConfirmAsync(
                Strings.Get("Autopilot_ResetTitle"),
                Strings.Format("Autopilot_ResetMessage", item.Name),
                Strings.Get("Autopilot_ResetConfirm")) == false)
        {
            return;
        }

        this.RunEngineAction(engine => engine.ResetGame(item.AppId));
    }

    /// <summary>
    /// Takes a game out of Autopilot right away (manual list: removed; other sources: excluded).
    /// Applied to both the saved configuration and the edited copy, so other unsaved edits stay unsaved.
    /// </summary>
    internal void RemoveFromAutopilot(uint appId)
    {
        void Apply(AutopilotConfig config)
        {
            if (config.Source == AutopilotGameSource.Manual)
            {
                config.ManualGames.Remove(appId);
            }
            else if (config.ExcludedGames.Contains(appId) == false)
            {
                config.ExcludedGames.Add(appId);
            }
        }

        var wasDirty = this.IsDirty;
        var saved = this.SavedConfig();
        Apply(saved);
        if (this.TrySave(saved))
        {
            Apply(this._edit);
            this._savedSnapshot = AutopilotFormat.Snapshot(Normalize(AutopilotFormat.Clone(saved)));
            if (wasDirty == false)
            {
                this.LoadFrom(saved);
            }
            else
            {
                this.RebuildLists();
                this.OnEdited();
            }
        }

        this.RefreshStatus();
    }

    private void RunEngineAction(Action<AutopilotEngine> action)
    {
        if (this._engine == null)
        {
            return;
        }

        this.ActionError = null;
        try
        {
            action(this._engine);
        }
        catch (Exception e)
        {
            Diagnostics.Log("Autopilot: action failed", e);
            this.ActionError = AutopilotFormat.ErrorMessage(e);
        }

        this.RefreshStatus();
    }
    #endregion

    #region Save / discard / preview
    private bool CanSave() => this.IsDirty && this.HasErrors == false && this._engine != null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var copy = AutopilotFormat.Clone(this._edit);
        copy.WasRunning = this._wasRunningSaved;
        if (this.TrySave(copy))
        {
            this.LoadFrom(copy);
            this.RefreshStatus();
        }
    }

    [RelayCommand]
    private void Discard()
    {
        this.LoadFrom(this.SavedConfig());
        this.ActionError = null;
    }

    /// <summary>Previews every game the edited configuration would run.</summary>
    [RelayCommand]
    private async Task PreviewAllAsync()
    {
        if (this.Dialogs == null)
        {
            return;
        }

        var games = this.ResolveEditedGames().Select(g => (g.AppId, g.Name)).ToList();
        await this.Dialogs.ShowPreviewAsync(games, AutopilotFormat.Clone(this._edit));
    }

    internal async Task PreviewGameAsync(uint appId, string name)
    {
        if (this.Dialogs != null)
        {
            await this.Dialogs.ShowPreviewAsync([(appId, name)], AutopilotFormat.Clone(this._edit));
        }
    }

    private bool TrySave(AutopilotConfig config)
    {
        if (this._engine == null)
        {
            return false;
        }

        this.ActionError = null;
        try
        {
            this._engine.SaveConfig(config);
            return true;
        }
        catch (Exception e)
        {
            Diagnostics.Log("Autopilot: couldn't save the configuration", e);
            this.ActionError = AutopilotFormat.ErrorMessage(e);
            return false;
        }
    }

    /// <summary>A copy of the engine's saved configuration (defaults when the engine isn't available).</summary>
    private AutopilotConfig SavedConfig()
    {
        try
        {
            if (this._engine != null && this.EngineError == null)
            {
                return AutopilotFormat.Clone(this._engine.Config);
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log("Autopilot: couldn't read the configuration", e);
        }

        // No engine: the last loaded configuration (the snapshot), or defaults.
        try
        {
            return this._savedSnapshot.Length > 0
                ? System.Text.Json.JsonSerializer.Deserialize<AutopilotConfig>(this._savedSnapshot) ?? new AutopilotConfig()
                : new AutopilotConfig();
        }
        catch (System.Text.Json.JsonException)
        {
            return new AutopilotConfig();
        }
    }

    /// <summary>Starts editing a fresh copy of <paramref name="config"/>.</summary>
    private void LoadFrom(AutopilotConfig config)
    {
        this._edit = AutopilotFormat.Clone(config);
        this._wasRunningSaved = config.WasRunning;
        this._savedSnapshot = AutopilotFormat.Snapshot(Normalize(AutopilotFormat.Clone(config)));
        this._activeFrom = this._edit.ActiveFrom ?? DefaultActiveFrom;
        this._activeTo = this._edit.ActiveTo ?? DefaultActiveTo;
        this._quietFrom = this._edit.QuietFrom ?? DefaultQuietFrom;
        this._quietTo = this._edit.QuietTo ?? DefaultQuietTo;

        this._syncingLists = true;
        try
        {
            var all = this._edit.ActiveDays.Count == 0;
            foreach (var day in this.Days)
            {
                day.IsChecked = all || this._edit.ActiveDays.Contains(day.Day);
            }
        }
        finally
        {
            this._syncingLists = false;
        }

        this.RebuildProfiles();
        this.RebuildLists();

        // Every bound property reads from the new copy.
        this.OnPropertyChanged(string.Empty);
        this.OnEdited();
        this.UpdateSourceSummary();
    }

    /// <summary>The engine-owned flag isn't something the user edits; leave it out of comparisons.</summary>
    private static AutopilotConfig Normalize(AutopilotConfig config)
    {
        config.WasRunning = false;
        return config;
    }

    /// <summary>After any edit: validation, dirty state and derived text.</summary>
    private void OnEdited()
    {
        this.Validate();
        this.IsDirty = AutopilotFormat.Snapshot(Normalize(AutopilotFormat.Clone(this._edit))) != this._savedSnapshot;

        try
        {
            var hours = AutopilotPlanner.HoursPerDay(this._edit);
            this.ScheduleSummary = Strings.Format("Autopilot_ScheduleSummary", AutopilotFormat.Hours(hours));
        }
        catch (Exception)
        {
            this.ScheduleSummary = "";
        }
    }

    private void Validate()
    {
        this.ScheduleError = this.IsAllDay == false && this._edit.ActiveFrom == this._edit.ActiveTo
            ? Strings.Get("Autopilot_ErrorWindowSame")
            : null;
        this.QuietError = this.HasQuietHours && this._edit.QuietFrom == this._edit.QuietTo
            ? Strings.Get("Autopilot_ErrorQuietSame")
            : null;
        this.DaysError = this.Days.Any(d => d.IsChecked) ? null : Strings.Get("Autopilot_ErrorNoDays");
        this.CollectionError = this.IsCollectionSource && this.CollectionOptions.All(c => c.Key != this._edit.CollectionKey)
            ? Strings.Get("Autopilot_ErrorNoCollection")
            : null;

        var errors = new List<string>();
        errors.AddRange(new[] { this.CollectionError, this.ScheduleError, this.QuietError, this.DaysError }.OfType<string>());
        if (this.Profile != null)
        {
            errors.AddRange(this.Profile.Errors);
        }

        this.ErrorsText = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors.Distinct());
    }
    #endregion

    #region Games
    private void OnSourceChanged()
    {
        this.OnPropertyChanged(nameof(this.IsManualSource));
        this.OnPropertyChanged(nameof(this.IsCollectionSource));
        this.OnPropertyChanged(nameof(this.IsSmartListSource));
        this.OnPropertyChanged(nameof(this.ShowGetStarted));
        this.OnPropertyChanged(nameof(this.ShowStatusArea));
        this.OnPropertyChanged(nameof(this.ShowOverridesSection));
        this.RebuildLists();
        this.OnEdited();
        this.UpdateSourceSummary();
    }

    private void RefreshCollections()
    {
        var options = this._library.SteamCollections.Concat(this._library.CustomCollections)
            .Select(c => new AutopilotChoice(c.FilterKey, c.IsSteamCollection ? c.Name : Strings.Format("Autopilot_SamCollection", c.Name)))
            .ToList();

        // Keep a collection that disappeared selectable, so the stored choice stays visible.
        if (this._edit.CollectionKey is { } key && options.All(o => o.Key != key))
        {
            options.Add(new AutopilotChoice(key, Strings.Get("Autopilot_MissingCollection")));
        }

        this.CollectionOptions = options;
        this.OnPropertyChanged(nameof(this.CollectionIndex));
        this.Validate();
        this.UpdateSourceSummary();
    }

    [RelayCommand]
    private async Task AddGamesAsync()
    {
        if (this.Dialogs == null)
        {
            return;
        }

        var picked = await this.Dialogs.PickGamesAsync(Strings.Get("Autopilot_AddGamesTitle"), Strings.Get("Autopilot_AddGamesPrimary"), this._edit.ManualGames);
        if (picked == null || picked.Count == 0)
        {
            return;
        }

        if (this.IsManualSource == false)
        {
            this.SourceIndex = 0;
        }

        var before = this._edit.ManualGames.Count;
        foreach (var appId in picked.Where(id => this._edit.ManualGames.Contains(id) == false))
        {
            this._edit.ManualGames.Add(appId);
            this._edit.ExcludedGames.Remove(appId);
        }

        this.FollowGameCount(before);
        this.RebuildLists();
        this.OnEdited();
    }

    [RelayCommand]
    private async Task AddExcludedAsync()
    {
        if (this.Dialogs == null)
        {
            return;
        }

        var picked = await this.Dialogs.PickGamesAsync(Strings.Get("Autopilot_ExcludeGamesTitle"), Strings.Get("Autopilot_ExcludeGamesPrimary"), this._edit.ExcludedGames);
        if (picked == null || picked.Count == 0)
        {
            return;
        }

        foreach (var appId in picked.Where(id => this._edit.ExcludedGames.Contains(id) == false))
        {
            this._edit.ExcludedGames.Add(appId);
        }

        this.RebuildLists();
        this.OnEdited();
    }

    /// <summary>Picks a game (any source) and opens its overrides.</summary>
    [RelayCommand]
    private async Task AddOverrideAsync()
    {
        if (this.Dialogs == null)
        {
            return;
        }

        var picked = await this.Dialogs.PickGamesAsync(Strings.Get("Autopilot_OverrideGameTitle"), Strings.Get("Autopilot_OverrideGamePrimary"), this._edit.Overrides.Keys.ToList());
        if (picked is { Count: > 0 })
        {
            var appId = picked[0];
            await this.EditOverridesAsync(appId, AutopilotFormat.GameName(this._library, this._services, appId));
        }
    }

    internal async Task EditOverridesAsync(uint appId, string name)
    {
        if (this.Dialogs == null)
        {
            return;
        }

        var value = this._edit.Overrides.TryGetValue(appId, out var existing)
            ? AutopilotFormat.Clone(existing)
            : new AutopilotGameOverride { AppId = appId };
        value.AppId = appId;
        if (await this.Dialogs.EditOverridesAsync(appId, name, value, AutopilotFormat.Clone(this._edit)) == false)
        {
            return;
        }

        var isEmpty = value.ProfileName == null && value.TargetMode == null && value.TargetHours == null && value.IncludeRare == null &&
                      value.ExcludedAchievements.Count == 0 && value.OnlyAchievements.Count == 0 && value.OnlyMode == false;
        if (isEmpty)
        {
            this._edit.Overrides.Remove(appId);
        }
        else
        {
            this._edit.Overrides[appId] = value;
        }

        this.RebuildLists();
        this.OnEdited();
    }

    /// <summary>A run-order checkbox: unticked games stay listed but are skipped (stored as exclusions).</summary>
    private void SetIncluded(AutopilotGameItem item, bool included)
    {
        if (this._syncingLists)
        {
            return;
        }

        var changed = included ? this._edit.ExcludedGames.Remove(item.AppId) : this._edit.ExcludedGames.Contains(item.AppId) == false;
        if (included == false && changed)
        {
            this._edit.ExcludedGames.Add(item.AppId);
        }

        if (changed)
        {
            this.UpdateSourceSummary();
            this.OnEdited();
        }
    }

    private void RemoveManual(AutopilotGameItem item)
    {
        var before = this._edit.ManualGames.Count;
        this._edit.ManualGames.Remove(item.AppId);
        this._edit.ExcludedGames.Remove(item.AppId);
        this.FollowGameCount(before);
        this.RebuildLists();
        this.OnEdited();
    }

    /// <summary>
    /// "Games running at once" follows the number of picked games (all of them run together) until
    /// the user sets it to something else: it moves only while it still equals the old game count,
    /// or when the list was empty.
    /// </summary>
    private void FollowGameCount(int before)
    {
        var after = this._edit.ManualGames.Count;
        if (this.IsManualSource == false || after == 0 || after == before)
        {
            return;
        }

        if (before == 0 || this._edit.MaxConcurrentGames == Math.Clamp(before, 1, 32))
        {
            this._edit.MaxConcurrentGames = Math.Clamp(after, 1, 32);
            this.OnPropertyChanged(nameof(this.MaxConcurrentGames));
        }
    }

    private void RemoveExcluded(AutopilotGameItem item)
    {
        this._edit.ExcludedGames.Remove(item.AppId);
        this.RebuildLists();
        this.OnEdited();
    }

    private void RemoveOverride(AutopilotGameItem item)
    {
        this._edit.Overrides.Remove(item.AppId);
        this.RebuildLists();
        this.OnEdited();
    }

    private void MoveManual(AutopilotGameItem item, int delta)
    {
        var from = this.ManualGames.IndexOf(item);
        var to = from + delta;
        if (from >= 0 && to >= 0 && to < this.ManualGames.Count)
        {
            this.ManualGames.Move(from, to); // synced back by OnManualGamesCollectionChanged
        }
    }

    /// <summary>Drag-reordering (and Up/Down) changes the collection; mirror its order into the config.</summary>
    private void OnManualGamesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (this._syncingLists)
        {
            return;
        }

        // A drag reorder is a Remove followed by an Add; wait until the item is back.
        var order = this.ManualGames.Select(g => g.AppId).ToList();
        if (order.Count != this._edit.ManualGames.Count)
        {
            return;
        }

        this._edit.ManualGames = order;
        this.UpdateMoveFlags();
        this.OnEdited();
    }

    private void RebuildLists()
    {
        this._syncingLists = true;
        try
        {
            this.ManualGames.Clear();
            foreach (var appId in this._edit.ManualGames)
            {
                var progress = this._library.AllGames.FirstOrDefault(c => c.AppId == appId)?.Progress;
                this.ManualGames.Add(new AutopilotGameItem(
                    appId,
                    AutopilotFormat.GameName(this._library, this._services, appId),
                    this.ProgressDetail(appId),
                    this._art,
                    moveUp: i => this.MoveManual(i, -1),
                    moveDown: i => this.MoveManual(i, 1),
                    remove: this.RemoveManual,
                    editOverrides: i => _ = this.EditOverridesAsync(i.AppId, i.Name),
                    isIncluded: this._edit.ExcludedGames.Contains(appId) == false,
                    includedChanged: this.SetIncluded,
                    unlocked: progress?.Unlocked ?? 0,
                    total: progress?.Total ?? 0)
                {
                    HasOverride = this._edit.Overrides.ContainsKey(appId),
                });
            }

            this.ExcludedGames.Clear();
            foreach (var appId in this._edit.ExcludedGames)
            {
                this.ExcludedGames.Add(new AutopilotGameItem(
                    appId, AutopilotFormat.GameName(this._library, this._services, appId), "", this._art, remove: this.RemoveExcluded));
            }

            this.OverrideGames.Clear();
            var listed = this.IsManualSource ? this._edit.ManualGames.ToHashSet() : [];
            foreach (var (appId, value) in this._edit.Overrides.Where(o => listed.Contains(o.Key) == false))
            {
                this.OverrideGames.Add(new AutopilotGameItem(
                    appId,
                    AutopilotFormat.GameName(this._library, this._services, appId),
                    OverrideSummary(value),
                    this._art,
                    remove: this.RemoveOverride,
                    editOverrides: i => _ = this.EditOverridesAsync(i.AppId, i.Name)));
            }
        }
        finally
        {
            this._syncingLists = false;
        }

        this.UpdateMoveFlags();
        this.HasManualGames = this.ManualGames.Count > 0;
        this.HasExcludedGames = this.ExcludedGames.Count > 0;
        this.HasOverrideGames = this.OverrideGames.Count > 0;
        this.UpdateSourceSummary();
    }

    private void UpdateMoveFlags()
    {
        for (var i = 0; i < this.ManualGames.Count; i++)
        {
            this.ManualGames[i].Position = i + 1;
            this.ManualGames[i].CanMoveUp = i > 0;
            this.ManualGames[i].CanMoveDown = i < this.ManualGames.Count - 1;
        }
    }

    private static string OverrideSummary(AutopilotGameOverride value)
    {
        var parts = new List<string>();
        if (value.ProfileName != null)
        {
            parts.Add(Strings.Format("Autopilot_OverrideSummaryProfile", value.ProfileName));
        }

        if (value.TargetMode != null)
        {
            parts.Add(Strings.Get("Autopilot_OverrideSummaryTarget"));
        }

        if (value.IncludeRare != null)
        {
            parts.Add(Strings.Get(value.IncludeRare == true ? "Autopilot_OverrideSummaryRareOn" : "Autopilot_OverrideSummaryRareOff"));
        }

        if (AutopilotPlanner.IsOnlyMode(value))
        {
            parts.Add(Strings.Plural("Autopilot_OverrideSummaryOnly", value.OnlyAchievements.Count));
        }

        if (value.ExcludedAchievements.Count > 0)
        {
            parts.Add(Strings.Plural("Autopilot_OverrideSummaryExcluded", value.ExcludedAchievements.Count));
        }

        return string.Join(" · ", parts);
    }

    private string ProgressDetail(uint appId)
    {
        var card = this._library.AllGames.FirstOrDefault(c => c.AppId == appId);
        return card?.Progress is { Total: > 0 } p
            ? Strings.Format("Autopilot_GameProgress", p.Unlocked, p.Total)
            : "";
    }

    /// <summary>
    /// The games the edited configuration would run, from the library's data (the engine resolves the
    /// saved configuration; this mirrors it for unsaved edits): source, minus excluded games.
    /// </summary>
    private IReadOnlyList<GameEntry> ResolveEditedGames()
    {
        var excluded = this._edit.ExcludedGames.ToHashSet();
        IEnumerable<(uint AppId, string Name)> games;
        if (this._edit.Source == AutopilotGameSource.Manual)
        {
            games = this._edit.ManualGames.Select(id => (id, AutopilotFormat.GameName(this._library, this._services, id)));
        }
        else
        {
            var cards = this._library.AllGames.Where(c => c.Progress is { Total: > 0 } p && p.Unlocked < p.Total);
            cards = this._edit.Source switch
            {
                AutopilotGameSource.Installed => cards.Where(c => c.IsInstalled),
                AutopilotGameSource.Collection => this.CollectionMembers() is { } members ? cards.Where(c => members.Contains(c.AppId)) : [],
                AutopilotGameSource.SmartList => this._edit.SmartList switch
                {
                    LibraryViewModel.NeverStartedList => cards.Where(c => c.Progress is { Unlocked: 0 }),
                    _ => cards.Where(c => c.Progress is { } p && p.Fraction >= 0.8),
                },
                _ => cards,
            };
            games = cards.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Select(c => (c.AppId, c.Name));
        }

        return games.Where(g => excluded.Contains(g.AppId) == false).Select(g => new GameEntry(g.AppId, g.Name, "normal")).ToList();
    }

    private IReadOnlySet<uint>? CollectionMembers() =>
        this._library.SteamCollections.Concat(this._library.CustomCollections).FirstOrDefault(c => c.FilterKey == this._edit.CollectionKey)?.AppIds;

    private void UpdateSourceSummary()
    {
        if (this.IsManualSource || this._library.IsSummaryReady == false)
        {
            this.SourceSummary = "";
            return;
        }

        this.SourceSummary = Strings.Plural("Autopilot_SourceMatches", this.ResolveEditedGames().Count);
    }
    #endregion

    #region Profiles
    private IReadOnlyList<AutopilotProfile> BuiltInProfiles()
    {
        try
        {
            return AutopilotStore.BuiltInProfiles();
        }
        catch (Exception e)
        {
            Diagnostics.Log("Autopilot: couldn't get the built-in profiles", e);
            return [new AutopilotProfile { Name = "Natural", IsBuiltIn = true }];
        }
    }

    private void RebuildProfiles()
    {
        var builtIn = this.BuiltInProfiles();
        var options = builtIn.Select(p => new AutopilotChoice(p.Name, Strings.Format("Autopilot_BuiltInProfileName", p.Name)))
            .Concat(this._edit.CustomProfiles.Select(p => new AutopilotChoice(p.Name, p.Name)))
            .ToList();

        // An active profile that no longer exists falls back to the first one (as the planner does).
        if (options.All(o => o.Key != this._edit.ActiveProfile) && options.Count > 0)
        {
            this._edit.ActiveProfile = options[0].Key;
        }

        this.ProfileOptions = options;
        this.OnPropertyChanged(nameof(this.ProfileIndex));
        this.LoadProfileEditor();
    }

    private void LoadProfileEditor()
    {
        var name = this._edit.ActiveProfile;
        var profile = this._edit.CustomProfiles.FirstOrDefault(p => p.Name == name) ??
                      this.BuiltInProfiles().FirstOrDefault(p => p.Name == name) ??
                      this.BuiltInProfiles().First();
        this.Profile = new AutopilotProfileEditor(profile, this.OnEdited);
    }

    private string? ValidateProfileName(string name, string? except)
    {
        var taken = this.BuiltInProfiles().Select(p => p.Name).Concat(this._edit.CustomProfiles.Select(p => p.Name))
            .Any(n => string.Equals(n, name, StringComparison.CurrentCultureIgnoreCase) && string.Equals(n, except, StringComparison.Ordinal) == false);
        return taken ? Strings.Get("Autopilot_ProfileNameTaken") : null;
    }

    private string UniqueName(string baseName)
    {
        var name = baseName;
        for (var i = 2; this.ValidateProfileName(name, null) != null; i++)
        {
            name = Strings.Format("Autopilot_ProfileNumbered", baseName, i);
        }

        return name;
    }

    /// <summary>A new custom profile, starting from the current one's rules.</summary>
    [RelayCommand]
    private Task NewProfileAsync() => this.CopyProfileAsync(Strings.Get("Autopilot_NewProfileTitle"), Strings.Get("Autopilot_NewProfileName"));

    /// <summary>Duplicate (also "Duplicate to edit" on built-ins).</summary>
    [RelayCommand]
    private Task DuplicateProfileAsync() => this.CopyProfileAsync(
        Strings.Get("Autopilot_DuplicateProfileTitle"),
        Strings.Format("Autopilot_ProfileCopyName", this.Profile?.Name ?? ""));

    private async Task CopyProfileAsync(string title, string suggested)
    {
        if (this.Dialogs == null || this.Profile == null)
        {
            return;
        }

        var name = await this.Dialogs.PromptNameAsync(title, this.UniqueName(suggested), Strings.Get("Common_Create"), n => this.ValidateProfileName(n, null));
        if (name == null)
        {
            return;
        }

        this._edit.CustomProfiles.Add(this.Profile.Profile.Clone(name));
        this._edit.ActiveProfile = name;
        this.RebuildProfiles();
        this.OnEdited();
    }

    private bool CanEditCustomProfile() => this.Profile is { IsBuiltIn: false };

    [RelayCommand(CanExecute = nameof(CanEditCustomProfile))]
    private async Task RenameProfileAsync()
    {
        if (this.Dialogs == null || this.Profile is not { IsBuiltIn: false } editor)
        {
            return;
        }

        var old = editor.Name;
        var name = await this.Dialogs.PromptNameAsync(Strings.Get("Autopilot_RenameProfileTitle"), old, Strings.Get("Common_Rename"), n => this.ValidateProfileName(n, old));
        if (name == null || name == old)
        {
            return;
        }

        editor.Profile.Name = name;
        this._edit.ActiveProfile = name;
        foreach (var value in this._edit.Overrides.Values.Where(o => o.ProfileName == old))
        {
            value.ProfileName = name;
        }

        this.RebuildProfiles();
        this.RebuildLists();
        this.OnEdited();
    }

    [RelayCommand(CanExecute = nameof(CanEditCustomProfile))]
    private async Task DeleteProfileAsync()
    {
        if (this.Dialogs == null || this.Profile is not { IsBuiltIn: false } editor)
        {
            return;
        }

        var users = this._edit.Overrides.Values.Count(o => o.ProfileName == editor.Name);
        var message = users == 0
            ? Strings.Format("Autopilot_DeleteProfileMessage", editor.Name)
            : Strings.Plural("Autopilot_DeleteProfileMessageUsed", users, editor.Name);
        if (await this.Dialogs.ConfirmAsync(Strings.Get("Autopilot_DeleteProfileTitle"), message, Strings.Get("Common_Delete")) == false)
        {
            return;
        }

        this._edit.CustomProfiles.Remove(editor.Profile);
        foreach (var value in this._edit.Overrides.Values.Where(o => o.ProfileName == editor.Name))
        {
            value.ProfileName = null;
        }

        this._edit.ActiveProfile = this.BuiltInProfiles().First().Name;
        this.RebuildProfiles();
        this.RebuildLists();
        this.OnEdited();
    }
    #endregion

    #region Helpers
    private void OnDaysChanged()
    {
        if (this._syncingLists)
        {
            return;
        }

        var checkedDays = this.Days.Where(d => d.IsChecked).Select(d => d.Day).ToList();

        // Every day is stored as "no restriction". (No day checked is a validation error, so it's never saved.)
        this._edit.ActiveDays = checkedDays.Count is 7 or 0 ? [] : checkedDays.OrderBy(d => d).ToList();

        this.OnEdited();
    }

    private void SetTime(ref TimeSpan field, TimeSpan value, Action<TimeSpan> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        apply(value);
        this.OnPropertyChanged(name);
        this.OnEdited();
    }

    private void SetFlag(bool current, bool value, Action<bool> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (current == value)
        {
            return;
        }

        apply(value);
        this.OnPropertyChanged(name);
        this.OnEdited();
    }
    #endregion
}
