// Partial-property [ObservableProperty] needs C# 13; this project uses C# 12 and isn't AOT-compiled.
#pragma warning disable MVVMTK0045

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.ViewModels.Pages;

/// <summary>A titled list of lines in a history entry's details, e.g. "Unlocked" and its achievements.</summary>
public sealed record HistoryDetailGroup(string Title, IReadOnlyList<string> Lines);

/// <summary>One commit in the History page.</summary>
public sealed partial class HistoryItemViewModel : ObservableObject
{
    private readonly SchemaService _schemas;
    private bool _detailsRequested;

    public HistoryItemViewModel(CommitRecord record, SchemaService schemas)
    {
        this.Record = record;
        this._schemas = schemas;
        this.Details = this.BuildDetails(null);
    }

    public CommitRecord Record { get; }

    public uint AppId => this.Record.AppId;

    public string GameName => string.IsNullOrWhiteSpace(this.Record.GameName)
        ? Strings.Format("Pages_AppFallbackName", this.Record.AppId.ToString(CultureInfo.InvariantCulture))
        : this.Record.GameName;

    public string OpenGameLabel => Strings.Format("Pages_OpenGame", this.GameName);

    public string TimeText => this.Record.Time.LocalDateTime.ToString("t", CultureInfo.CurrentCulture);

    public string DateText => PageActions.DayTitle(this.Record.Time.LocalDateTime, shortDate: true);

    public string FullTimeText => this.Record.Time.LocalDateTime.ToString("F", CultureInfo.CurrentCulture);

    /// <summary>The stored <see cref="CommitSource"/> value (used for filtering).</summary>
    public string Source => this.Record.Source ?? "";

    /// <summary>The source as shown: known <see cref="CommitSource"/> values are localized.</summary>
    public string SourceText => HistoryViewModel.SourceName(this.Record.Source);

    public bool Succeeded => this.Record.Succeeded;

    public bool Failed => this.Record.Succeeded == false;

    public string StatusText => Strings.Get(this.Record.Succeeded ? "History_StatusSucceeded" : "History_StatusFailed");

    public string? Message => this.Record.Message;

    public bool HasFailureMessage => this.Record.Succeeded == false && string.IsNullOrWhiteSpace(this.Record.Message) == false;

    public bool HasChanges => this.Record.Unlocked.Count + this.Record.Locked.Count + this.Record.Stats.Count > 0;

    /// <summary>"Unlocked 3, locked 1, changed 2 stats".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>(3);
            if (this.Record.Unlocked.Count > 0)
            {
                parts.Add(Strings.Plural("History_SummaryUnlocked", this.Record.Unlocked.Count));
            }

            if (this.Record.Locked.Count > 0)
            {
                parts.Add(Strings.Plural("History_SummaryLocked", this.Record.Locked.Count));
            }

            if (this.Record.Stats.Count > 0)
            {
                parts.Add(Strings.Plural("History_SummaryStats", this.Record.Stats.Count));
            }

            if (parts.Count == 0)
            {
                return Strings.Get("History_NoChanges");
            }

            var text = string.Join(", ", parts);

            // Only Latin text is capitalized: upper-casing Georgian would give Mtavruli capitals.
            return char.IsAsciiLetterLower(text[0]) ? char.ToUpperInvariant(text[0]) + text[1..] : text;
        }
    }

    public string AutomationName => $"{this.GameName}, {this.Summary}, {this.StatusText}, {this.SourceText}, {this.FullTimeText}";

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>The changed achievements and stats; names fill in once the game's schema has been read.</summary>
    [ObservableProperty]
    private IReadOnlyList<HistoryDetailGroup> _details = [];

    public bool Matches(string query, string? source) =>
        (source == null || string.Equals(this.Source, source, StringComparison.OrdinalIgnoreCase)) &&
        (query.Length == 0 ||
         this.GameName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
         this.Record.AppId.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.Ordinal));

    [RelayCommand]
    private void OpenGame() => PageActions.OpenGame(this.AppId);

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            this.LoadNames();
        }
    }

    /// <summary>Resolves achievement and stat names from the game's schema, off the UI thread.</summary>
    private async void LoadNames()
    {
        if (this._detailsRequested)
        {
            return;
        }

        this._detailsRequested = true;
        GameSchema? schema = null;
        try
        {
            schema = await Task.Run(() => this._schemas.Load(this.AppId));
        }
        catch (Exception e)
        {
            Diagnostics.Log($"History: couldn't load the schema for {this.AppId}", e);
        }

        if (schema != null)
        {
            this.Details = this.BuildDetails(schema);
        }
    }

    private IReadOnlyList<HistoryDetailGroup> BuildDetails(GameSchema? schema)
    {
        Dictionary<string, string>? achievementNames = null, statNames = null;
        if (schema != null)
        {
            achievementNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var achievement in schema.Achievements)
            {
                achievementNames.TryAdd(achievement.Id, achievement.Name);
            }

            statNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var stat in schema.Stats)
            {
                statNames.TryAdd(stat.Id, stat.DisplayName);
            }
        }

        string Achievement(string id) =>
            achievementNames != null && achievementNames.TryGetValue(id, out var name) && string.IsNullOrWhiteSpace(name) == false && name != id
                ? $"{name} ({id})"
                : id;

        string Stat(StatChange change)
        {
            var label = statNames != null && statNames.TryGetValue(change.Id, out var name) && string.IsNullOrWhiteSpace(name) == false && name != change.Id
                ? $"{name} ({change.Id})"
                : change.Id;
            return $"{label}: {change.From.ToString("G", CultureInfo.CurrentCulture)} → {change.To.ToString("G", CultureInfo.CurrentCulture)}";
        }

        var groups = new List<HistoryDetailGroup>(3);
        if (this.Record.Unlocked.Count > 0)
        {
            groups.Add(new HistoryDetailGroup(Strings.Get("History_DetailUnlocked"), this.Record.Unlocked.Select(Achievement).ToList()));
        }

        if (this.Record.Locked.Count > 0)
        {
            groups.Add(new HistoryDetailGroup(Strings.Get("History_DetailLocked"), this.Record.Locked.Select(Achievement).ToList()));
        }

        if (this.Record.Stats.Count > 0)
        {
            groups.Add(new HistoryDetailGroup(Strings.Get("History_DetailStats"), this.Record.Stats.Select(Stat).ToList()));
        }

        return groups;
    }
}

/// <summary>
/// The commit log, newest first, filtered by game and source. New commits made while the page
/// is shown are added at the top as they're logged.
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherQueue _dispatcher;
    private List<HistoryItemViewModel> _all = [];
    private bool _isListening;
    private int _loadVersion;

    public HistoryViewModel(AppServices services)
    {
        this._services = services;
        this._dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>Stored source values, in <see cref="SourceIndex"/> order (null for "All sources").</summary>
    private static readonly string?[] SourceKeys =
        [null, CommitSource.Manual, CommitSource.Restore, CommitSource.CommandLine, CommitSource.Farm];

    /// <summary>Source filter choices as shown, in <see cref="SourceIndex"/> order ("All sources" first).</summary>
    public IReadOnlyList<string> SourceOptions { get; } =
        SourceKeys.Select(key => key == null ? Strings.Get("History_AllSources") : SourceName(key)).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListVisible), nameof(IsEmpty), nameof(IsNoMatches))]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsListVisible), nameof(IsEmpty), nameof(IsNoMatches))]
    private string? _error;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private int _sourceIndex;

    [ObservableProperty]
    private ObservableCollection<HistoryItemViewModel> _items = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(IsNoMatches))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoMatches))]
    private int _shownCount;

    [ObservableProperty]
    private string _countText = "";

    public bool HasError => this.Error != null;

    public bool IsListVisible => this.IsLoading == false && this.Error == null;

    public bool IsEmpty => this.IsLoading == false && this.Error == null && this.TotalCount == 0;

    public bool IsNoMatches => this.IsLoading == false && this.Error == null && this.TotalCount > 0 && this.ShownCount == 0;

    private string? SelectedSource => this.SourceIndex > 0 && this.SourceIndex < SourceKeys.Length ? SourceKeys[this.SourceIndex] : null;

    /// <summary>
    /// A history entry's source as shown. The log stores <see cref="CommitSource"/>'s English
    /// constants; known ones are localized and anything else is shown as stored.
    /// </summary>
    public static string SourceName(string? source) => source switch
    {
        CommitSource.Manual => Strings.Get("History_SourceManual"),
        CommitSource.Restore => Strings.Get("History_SourceRestore"),
        CommitSource.CommandLine => Strings.Get("History_SourceCommandLine"),
        CommitSource.Farm => Strings.Get("History_SourceFarm"),
        _ => string.IsNullOrWhiteSpace(source) ? Strings.Get("History_SourceUnknown") : source,
    };

    partial void OnSearchTextChanged(string value) => this.ApplyFilter();

    partial void OnSourceIndexChanged(int value) => this.ApplyFilter();

    /// <summary>Starts listening for new commits and reloads the log. Call when the page is shown.</summary>
    public void OnShown()
    {
        if (this._isListening == false)
        {
            this._services.History.Appended += this.OnAppended;
            this._isListening = true;
        }

        _ = this.LoadAsync();
    }

    /// <summary>Stops listening for new commits. Call when the page is hidden.</summary>
    public void OnHidden()
    {
        if (this._isListening)
        {
            this._services.History.Appended -= this.OnAppended;
            this._isListening = false;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => this.LoadAsync();

    [RelayCommand]
    private void ClearFilters()
    {
        this.SearchText = "";
        this.SourceIndex = 0;
    }

    private async Task LoadAsync()
    {
        var version = ++this._loadVersion;
        this.IsRefreshing = true;

        IReadOnlyList<CommitRecord>? records = null;
        string? error = null;
        try
        {
            records = await Task.Run(() => this._services.History.Load());
        }
        catch (Exception e)
        {
            Diagnostics.Log("History: couldn't load the commit log", e);
            error = e is NotImplementedException
                ? Strings.Get("History_Unavailable")
                : Strings.Format("History_LoadError", e.Message);
        }

        if (version != this._loadVersion)
        {
            return;
        }

        this.IsRefreshing = false;
        this.Error = error;
        this._all = (records ?? [])
            .OrderByDescending(r => r.Time)
            .Select(r => new HistoryItemViewModel(r, this._services.Schemas))
            .ToList();
        this.TotalCount = this._all.Count;
        this.IsLoading = false;
        this.ApplyFilter();
    }

    private void OnAppended(object? sender, CommitRecord record)
    {
        // Raised on whichever thread logged the commit.
        this._dispatcher.TryEnqueue(() =>
        {
            if (this.IsLoading || this.Error != null)
            {
                return; // the pending load will include it
            }

            var item = new HistoryItemViewModel(record, this._services.Schemas);
            this._all.Insert(0, item);
            this.TotalCount = this._all.Count;
            if (item.Matches(this.SearchText.Trim(), this.SelectedSource))
            {
                this.Items.Insert(0, item);
                this.ShownCount = this.Items.Count;
            }

            this.UpdateCountText();
        });
    }

    private void ApplyFilter()
    {
        var query = this.SearchText.Trim();
        var source = this.SelectedSource;
        this.Items = new ObservableCollection<HistoryItemViewModel>(this._all.Where(item => item.Matches(query, source)));
        this.ShownCount = this.Items.Count;
        this.UpdateCountText();
    }

    private void UpdateCountText()
    {
        this.CountText = this.Error != null ? ""
            : this.ShownCount == this.TotalCount ? Strings.Plural("History_Commits", this.TotalCount)
            : Strings.Plural("History_ShownOfTotal", this.TotalCount, LibraryFormat.Count(this.ShownCount));
    }
}
