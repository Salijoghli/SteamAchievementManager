using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.Tests;

public sealed class InstalledGamesTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => this._dir.Dispose();

    private static string Manifest(uint appId, string installDir, int flags, long size) => $$"""
        "AppState"
        {
            "appid"     "{{appId}}"
            "name"      "Game {{appId}}"
            "StateFlags"        "{{flags}}"
            "installdir"        "{{installDir}}"
            "SizeOnDisk"        "{{size}}"
            "InstalledDepots" { "1" { "size" "1" } }
        }
        """;

    [Fact]
    public void ScansEveryLibraryAndFiltersByStateFlags()
    {
        var steam = Path.Combine(this._dir.Path, "Steam");
        var library = Path.Combine(this._dir.Path, "Library Two");
        var unreachable = Path.Combine(this._dir.Path, "Unplugged");

        // Paths in VDF are backslash-escaped.
        string Escape(string path) => path.Replace("\\", "\\\\");
        this._dir.Write("Steam\\steamapps\\libraryfolders.vdf", $$"""
            "libraryfolders"
            {
                "0" { "path" "{{Escape(steam)}}" "apps" { "10" "100" } }
                "1" { "path" "{{Escape(library)}}" "apps" { "20" "200" } }
                "2" { "path" "{{Escape(unreachable)}}" }
                "contentstatsid" "123"
            }
            """);

        this._dir.Write("Steam\\steamapps\\appmanifest_10.acf", Manifest(10, "Half-Life", 4, 100));
        this._dir.Write("Steam\\steamapps\\appmanifest_11.acf", Manifest(11, "Uninstalled", 1, 0));
        this._dir.Write("Steam\\steamapps\\appmanifest_12.acf", Manifest(12, "NeedsUpdate", 6, 300));
        this._dir.Write("Steam\\steamapps\\appmanifest_13.acf", "garbage { not vdf");
        this._dir.Write("Steam\\steamapps\\appmanifest_14.acf", "");
        this._dir.Write("Library Two\\steamapps\\appmanifest_20.acf", Manifest(20, "Other Game", 1030, 200));
        this._dir.Write("Library Two\\steamapps\\appmanifest_21.acf", Manifest(21, "Downloading", 1026, 50));

        var result = InstalledGamesService.Scan(steam);

        Assert.Equal([10u, 12u, 20u], result.Keys.Order());
        Assert.Equal(new InstalledGame(10, Path.Combine(steam, "steamapps", "common", "Half-Life"), 100), result[10]);
        Assert.Equal(Path.Combine(library, "steamapps", "common", "Other Game"), result[20].InstallDirectory);
        Assert.Equal(200, result[20].SizeOnDisk);
    }

    [Fact]
    public void ReadsOldLibraryFolderFormatAndAlwaysIncludesSteamFolder()
    {
        var steam = Path.Combine(this._dir.Path, "Steam");
        var library = Path.Combine(this._dir.Path, "Old");
        this._dir.Write("Steam\\steamapps\\libraryfolders.vdf", $$"""
            "LibraryFolders"
            {
                "TimeNextStatsReport" "123"
                "ContentStatsID" "456"
                "1" "{{library.Replace("\\", "\\\\")}}"
            }
            """);

        var folders = InstalledGamesService.GetLibraryFolders(steam);

        Assert.Equal([steam, library], folders);
    }

    [Fact]
    public void MissingLibraryFoldersFileStillScansSteamFolder()
    {
        var steam = Path.Combine(this._dir.Path, "Steam");
        this._dir.Write("Steam\\steamapps\\appmanifest_10.acf", Manifest(10, "Half-Life", 4, 1));

        Assert.Equal([10u], InstalledGamesService.Scan(steam).Keys);
        Assert.Empty(InstalledGamesService.Scan(Path.Combine(this._dir.Path, "Nothing")));
    }
}

public sealed class TimelineTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => this._dir.Dispose();

    private (GameSchema Schema, UserStatsCache Stats) Load()
    {
        var schema = SchemaService.Parse(this._dir.Write("schema.bin", Fixtures.Schema()), Fixtures.AppId, "english")!;
        var stats = UserStatsCache.Load(this._dir.Write("stats.bin", Fixtures.Stats()))!;
        return (schema, stats);
    }

    [Fact]
    public void EmitsOnlySetBitsWithTimes()
    {
        var (schema, stats) = this.Load();
        var game = new GameEntry(Fixtures.AppId, "Test Game", "normal");

        var events = TimelineService.SortNewestFirst(TimelineService.GetUnlocks(game, schema, stats));

        // ACH_B is re-locked (time left over), ACH_C is unlocked with no time: neither appears.
        Assert.Equal(["ACH_D", "ACH_A"], events.Select(e => e.AchievementId));
        var d = events[0];
        Assert.Equal(Fixtures.AppId, d.AppId);
        Assert.Equal("Test Game", d.GameName);
        Assert.Equal("Delta", d.AchievementName);
        Assert.Equal("Do Delta", d.Description);
        Assert.Equal("d.jpg", d.IconFile);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2000), d.Time);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1000), events[1].Time);
    }

    [Fact]
    public void ExportRowsCoverEverySchemaAchievement()
    {
        var (schema, stats) = this.Load();
        var game = new GameEntry(Fixtures.AppId, "Test Game", "normal");

        var rows = TimelineService.GetExportRows(game, schema, stats);

        Assert.Equal(["ACH_A", "ACH_B", "ACH_C", "ACH_D"], rows.Select(r => r.AchievementId));
        Assert.Equal([true, false, true, true], rows.Select(r => r.Unlocked));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1000), rows[0].UnlockTime);
        Assert.Null(rows[1].UnlockTime);
        Assert.Null(rows[2].UnlockTime);
        Assert.All(rows, r => Assert.Null(r.GlobalPercent));

        var empty = TimelineService.GetExportRows(game, schema, UserStatsCache.Empty);
        Assert.All(empty, r => Assert.False(r.Unlocked));
    }

    [Fact]
    public void SortsNewestFirstWithStableTies()
    {
        var t = DateTimeOffset.FromUnixTimeSeconds(5);
        UnlockEvent E(uint app, string id, DateTimeOffset time) => new(app, "", id, "", "", null, time);

        var sorted = TimelineService.SortNewestFirst([E(2, "b", t), E(1, "z", t.AddSeconds(-1)), E(1, "c", t), E(2, "a", t)]);

        Assert.Equal(["c", "a", "b", "z"], sorted.Select(e => e.AchievementId));
    }

    [Fact]
    public void MissingOrCorruptStatsCacheLoadsAsNull()
    {
        Assert.Null(UserStatsCache.Load(Path.Combine(this._dir.Path, "missing.bin")));
        Assert.Null(UserStatsCache.Load(this._dir.Write("bad.bin", [0, 1, 2])));
    }
}
