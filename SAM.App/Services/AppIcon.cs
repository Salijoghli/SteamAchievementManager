namespace SAM.App.Services;

/// <summary>
/// Path of the app's .ico for window, tray and notification icons: the icon embedded in the exe,
/// written to %LocalAppData%\SteamAchievementManager\SAM.ico. That path stays the same across
/// builds — a single-file SAM.exe runs its content from a per-build temp folder, and Windows keeps
/// the notification icon's path in the registry. Falls back to Assets\SAM.ico next to the app.
/// </summary>
public static class AppIcon
{
    private static readonly Lazy<string> Lazy = new(Resolve);

    public static string Path => Lazy.Value;

    private static string Resolve()
    {
        var local = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "SAM.ico");
        var extracted = System.IO.Path.Combine(AppCache.RootDirectory, "SAM.ico");
        try
        {
            using var resource = typeof(AppIcon).Assembly.GetManifestResourceStream("SAM.App.Assets.SAM.ico");
            if (resource != null)
            {
                Directory.CreateDirectory(AppCache.RootDirectory);
                using var memory = new MemoryStream();
                resource.CopyTo(memory);
                var bytes = memory.ToArray();

                // Rewrite only when missing or changed (e.g. after updating to a new icon).
                if (File.Exists(extracted) == false || File.ReadAllBytes(extracted).AsSpan().SequenceEqual(bytes) == false)
                {
                    File.WriteAllBytes(extracted, bytes);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log("Couldn't write the app icon", e);
        }

        return File.Exists(extracted) ? extracted : local;
    }
}
