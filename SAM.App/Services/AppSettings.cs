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

    public LibrarySort LibrarySort { get; set; } = LibrarySort.Name;

    public bool SortDescending { get; set; }

    /// <summary>Show favorites above everything else regardless of sort.</summary>
    public bool PinFavorites { get; set; } = true;

    public bool ShowGames { get; set; } = true;

    public bool ShowDemos { get; set; }

    public bool ShowMods { get; set; }

    public bool ShowJunk { get; set; }

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    public static AppSettings Load() => JsonFile.Read<AppSettings>(FilePath) ?? new AppSettings();

    public void Save() => JsonFile.Write(FilePath, this);
}
