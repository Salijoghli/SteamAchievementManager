namespace SAM.App.Services;

/// <summary>Appends errors to %LocalAppData%\SteamAchievementManager\sam.log. Never throws.</summary>
public static class Diagnostics
{
    private static readonly object Lock = new();

    /// <summary>Where sam.log goes; tests point it at a temporary folder.</summary>
    internal static string LogDirectory { get; set; } = AppCache.RootDirectory;

    public static void Log(string message, Exception? exception = null)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:O} {message}{(exception == null ? "" : Environment.NewLine + exception)}{Environment.NewLine}";
            lock (Lock)
            {
                var directory = LogDirectory;
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "sam.log"), line);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
