using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>
/// Checks GitHub releases (Salijoghli/SteamAchievementManager) for a version newer than the
/// running one. Returns null when up to date, offline, or skipped by the user.
/// </summary>
public sealed class UpdateService(HttpClient http, AppSettings settings)
{
    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);

    public Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(infra)");
}
