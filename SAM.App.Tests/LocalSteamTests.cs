using System.Globalization;
using SAM.App.Models;
using SAM.App.Services;
using Xunit.Abstractions;

namespace SAM.App.Tests;

/// <summary>
/// Read-only checks against the real Steam install. Skipped unless SAM_LOCAL_TESTS=1.
/// SAM_STEAM_PATH (default D:\steam) and SAM_ACCOUNT_ID pick the install and account.
/// </summary>
[Trait("Category", "Local")]
public sealed class LocalSteamTests(ITestOutputHelper output)
{
    private static string SteamPath => Environment.GetEnvironmentVariable("SAM_STEAM_PATH") ?? @"D:\steam";

    [LocalFact]
    public void PrintsInstalledGames()
    {
        var games = InstalledGamesService.Scan(SteamPath);
        foreach (var game in games.Values.OrderBy(g => g.AppId))
        {
            output.WriteLine($"{game.AppId,10}  {game.SizeOnDisk / 1e9,8:F2} GB  {game.InstallDirectory}");
        }

        Assert.NotEmpty(games);
    }

    [LocalFact]
    public void PrintsRecentUnlocks()
    {
        var accountId = uint.Parse(Environment.GetEnvironmentVariable("SAM_ACCOUNT_ID") ?? "0", CultureInfo.InvariantCulture);
        var prefix = $"UserGameStats_{accountId}_";
        var statsDir = Path.Combine(SteamPath, "appcache", "stats");

        var events = new List<UnlockEvent>();
        foreach (var file in Directory.EnumerateFiles(statsDir, prefix + "*.bin"))
        {
            if (uint.TryParse(Path.GetFileNameWithoutExtension(file)[prefix.Length..], out var appId) == false)
            {
                continue;
            }

            var schema = SchemaService.Parse(SchemaService.GetSchemaPath(SteamPath, appId), appId, "english");
            var stats = UserStatsCache.Load(file);
            if (schema != null && stats != null)
            {
                events.AddRange(TimelineService.GetUnlocks(new GameEntry(appId, appId.ToString(CultureInfo.InvariantCulture), "normal"), schema, stats));
            }
        }

        var sorted = TimelineService.SortNewestFirst(events);
        output.WriteLine($"{sorted.Count} unlocks total");
        foreach (var e in sorted.Take(10))
        {
            output.WriteLine($"{e.Time.ToLocalTime():yyyy-MM-dd HH:mm}  {e.AppId,8}  {e.AchievementName}  ({e.AchievementId}, icon {e.IconFile})");
        }

        Assert.NotEmpty(sorted);
    }
}
