namespace SAM.App.Services;

/// <summary>Appends errors to %LocalAppData%\SteamAchievementManager\sam.log. Never throws.</summary>
public static class Diagnostics
{
    private static readonly object Lock = new();

    public static void Log(string message, Exception? exception = null)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:O} {message}{(exception == null ? "" : Environment.NewLine + exception)}{Environment.NewLine}";
            lock (Lock)
            {
                Directory.CreateDirectory(AppCache.RootDirectory);
                File.AppendAllText(Path.Combine(AppCache.RootDirectory, "sam.log"), line);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
