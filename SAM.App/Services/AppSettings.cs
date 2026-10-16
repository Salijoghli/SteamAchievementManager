namespace SAM.App.Services;

public enum LibrarySort
{
    Name,
    AppId,
    RecentlyPlayed,
    Playtime,
    Progress,
}

/// <summary>User preferences, persisted to settings.json. Call <see cref="Save"/> after changing.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(AppCache.RootDirectory, "settings.json");

    /// <summary>Keep other Steam accounts' cached data when a different account logs in.</summary>
    public bool KeepOtherAccountsCache { get; set; }

    /// <summary>Defaults to most recently played first; never-played games sort last.</summary>
    public LibrarySort LibrarySort { get; set; } = LibrarySort.RecentlyPlayed;

    public bool SortDescending { get; set; } = true;

    /// <summary>Show favorites above everything else regardless of sort.</summary>
    public bool PinFavorites { get; set; } = true;

    public bool ShowGames { get; set; } = true;

    public bool ShowDemos { get; set; }

    public bool ShowMods { get; set; }

    public bool ShowJunk { get; set; }

    public bool FavoritesOnly { get; set; }

    /// <summary>Index into the library's completion filter (All, Has achievements, Not started, In progress, Perfect).</summary>
    public int CompletionFilter { get; set; }

    /// <summary>Index into the library's last-played filter (Any time … Custom range).</summary>
    public int LastPlayedFilter { get; set; }

    public DateTimeOffset? LastPlayedFrom { get; set; }

    public DateTimeOffset? LastPlayedTo { get; set; }

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>
    /// Culture for dates and numbers (and, later, UI text), e.g. "en-US". The UI is English-only
    /// for now, so this defaults to English rather than following the Windows region format.
    /// </summary>
    public string Language { get; set; } = "en-US";

    public static AppSettings Load() => JsonFile.Read<AppSettings>(FilePath) ?? new AppSettings();

    public void Save() => JsonFile.Write(FilePath, this);
}
