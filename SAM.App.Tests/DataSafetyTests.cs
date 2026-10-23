using System.Runtime.CompilerServices;
using System.Text.Json;
using SAM.App.Infrastructure;
using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.Tests;

/// <summary>Keeps every test's log lines out of the user's real sam.log.</summary>
internal static class TestLogRedirect
{
    [ModuleInitializer]
    internal static void Initialize() =>
        Diagnostics.LogDirectory = Path.Combine(Path.GetTempPath(), "SAM.App.Tests", "logs");
}

public sealed class JsonFileTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => this._dir.Dispose();

    [Fact]
    public void TryReadTellsMissingFromReadable()
    {
        var path = Path.Combine(this._dir.Path, "data.json");
        Assert.Equal(JsonReadStatus.Missing, JsonFile.TryRead<List<int>>(path, out var missing));
        Assert.Null(missing);

        JsonFile.WriteOrThrow(path, new List<int> { 1, 2 });
        Assert.Equal(JsonReadStatus.Ok, JsonFile.TryRead<List<int>>(path, out var value));
        Assert.Equal([1, 2], value!);
    }

    [Fact]
    public void TryReadSetsCorruptFileAside()
    {
        var path = this._dir.Write("data.json", "{ not json");

        Assert.Equal(JsonReadStatus.Corrupt, JsonFile.TryRead<List<int>>(path, out var value));
        Assert.Null(value);
        Assert.False(File.Exists(path));
        var kept = Assert.Single(Directory.GetFiles(this._dir.Path, "data.json.corrupt-*"));
        Assert.Equal("{ not json", File.ReadAllText(kept));
    }

    [Fact]
    public void TryReadReportsLockedFileAsUnavailable()
    {
        var path = this._dir.Write("data.json", "[1]");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(JsonReadStatus.Unavailable, JsonFile.TryRead<List<int>>(path, out _));
        }

        Assert.Equal("[1]", File.ReadAllText(path));
    }

    [Fact]
    public void WriteOrThrowThrowsWhenTheFileCantBeWritten()
    {
        // A file where the directory should be.
        var blocker = this._dir.Write("blocked", "");
        Assert.ThrowsAny<IOException>(() => JsonFile.WriteOrThrow(Path.Combine(blocker, "data.json"), 1));
    }

    [Fact]
    public void ConcurrentWritesUseTheirOwnTempFiles()
    {
        var path = Path.Combine(this._dir.Path, "data.json");
        Parallel.For(0, 40, i => JsonFile.WriteOrThrow(path, new List<int> { i, i }));

        var value = JsonFile.Read<List<int>>(path);
        Assert.NotNull(value);
        Assert.Equal(value[0], value[1]);
        Assert.Empty(Directory.GetFiles(this._dir.Path, "*.tmp"));
    }

    [Fact]
    public void LockNameDependsOnlyOnTheFullPath()
    {
        var path = Path.Combine(this._dir.Path, "data.json");
        Assert.Equal(JsonFile.LockName(path), JsonFile.LockName(path.ToLowerInvariant()));
        Assert.NotEqual(JsonFile.LockName(path), JsonFile.LockName(path + "2"));
        Assert.StartsWith("SteamAchievementManager.File.", JsonFile.LockName(path), StringComparison.Ordinal);
    }

    [Fact]
    public void SharedFileBecomesReadOnlyWhenItCantBeRead()
    {
        var path = this._dir.Write("data.json", "[1,2,3]");
        var file = new SharedJsonFile<List<int>>(path);
        bool updated;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            updated = file.Update(_ => [9]);
        }

        Assert.False(updated);
        Assert.True(file.IsReadOnly);

        // Still read-only once the file is free again: never overwritten this session.
        Assert.False(file.Update(_ => [9]));
        Assert.Equal("[1,2,3]", File.ReadAllText(path));
    }
}

public sealed class SharedStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => this._dir.Dispose();

    [Fact]
    public void FavoritesFromTwoProcessesAreBothKept()
    {
        var path = Path.Combine(this._dir.Path, "favorites.json");
        var first = new FavoritesStore(path);
        var second = new FavoritesStore(path);

        first.Set(10, true);
        second.Set(20, true);
        first.Set(30, true);

        Assert.Equal([10u, 20u, 30u], JsonFile.Read<List<uint>>(path)!);
        Assert.True(first.Contains(20));
    }

    [Fact]
    public void FavoritesNeverOverwriteAnUnreadableFile()
    {
        var path = this._dir.Write("favorites.json", "[1,2,3]");
        FavoritesStore store;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            store = new FavoritesStore(path);
        }

        store.Set(4, true);

        Assert.True(store.Contains(4));
        Assert.Equal("[1,2,3]", File.ReadAllText(path));
    }

    [Fact]
    public void CorruptFavoritesAreKeptAsideAndReplaced()
    {
        var path = this._dir.Write("favorites.json", "[1,2,");
        var store = new FavoritesStore(path);
        store.Set(5, true);

        Assert.Equal([5u], JsonFile.Read<List<uint>>(path)!);
        Assert.Single(Directory.GetFiles(this._dir.Path, "favorites.json.corrupt-*"));
    }

    [Fact]
    public void NotesFromTwoProcessesAreBothKept()
    {
        var path = Path.Combine(this._dir.Path, "notes.json");
        var first = new NotesStore(path);
        var second = new NotesStore(path);
        Assert.Null(first.Get(1, "A"));
        Assert.Null(second.Get(1, "B"));

        first.Set(1, "A", "first");
        second.Set(1, "B", "second");
        first.Set(2, "C", "third");

        var reloaded = new NotesStore(path);
        Assert.Equal("first", reloaded.Get(1, "A"));
        Assert.Equal("second", reloaded.Get(1, "B"));
        Assert.Equal("third", reloaded.Get(2, "C"));
        Assert.Equal("second", first.Get(1, "B"));
    }

    [Fact]
    public void CollectionChangesFromTwoProcessesAreBothKept()
    {
        var path = Path.Combine(this._dir.Path, "collections.json");
        var first = new CollectionsService(() => null, path);
        var second = new CollectionsService(() => null, path);
        Assert.Empty(first.GetCustomCollections());
        Assert.Empty(second.HiddenAppIds);

        var created = first.CreateCustomCollection("Mine");
        second.SetHidden(7, true);
        second.SetInCustomCollection(created.Id, 42, true);
        first.SetInCustomCollection(created.Id, 43, true);

        var reloaded = new CollectionsService(() => null, path);
        Assert.Equal([7u], reloaded.HiddenAppIds);
        var collection = Assert.Single(reloaded.GetCustomCollections());
        Assert.Equal([42u, 43u], collection.AppIds.Order());
    }

    [Fact]
    public void SettingsUpdatesOnlyWriteWhatChanged()
    {
        var path = Path.Combine(this._dir.Path, "settings.json");
        var library = AppSettings.Load(path);
        var game = AppSettings.Load(path);

        // A game window turns spoiler protection off...
        game.Update(s => s.SpoilerProtection = false);

        // ...and the library, still holding the old value in memory, saves its own settings.
        library.UpdateChanged(s =>
        {
            s.Theme = "Dark";
            s.SpoilerProtection = library.SpoilerProtection;
        });

        var saved = AppSettings.Load(path);
        Assert.False(saved.SpoilerProtection);
        Assert.Equal("Dark", saved.Theme);
        Assert.Equal("Dark", library.Theme);
        Assert.False(library.ReadLatest()!.SpoilerProtection);
    }

    [Fact]
    public void SettingsNeverOverwriteAnUnreadableFile()
    {
        var path = this._dir.Write("settings.json", """{ "Theme": "Light" }""");
        AppSettings settings;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            settings = AppSettings.Load(path);
        }

        settings.Update(s => s.Theme = "Dark");

        Assert.Equal("Dark", settings.Theme);
        Assert.Equal("""{ "Theme": "Light" }""", File.ReadAllText(path));
    }

    [Fact]
    public void ExtraAppsMigrateOnceWithoutOverwriting()
    {
        var oldPath = this._dir.Write("cache\\extra-apps.json", "[1]");
        var newPath = Path.Combine(this._dir.Path, "data", "extra-apps.json");

        GameListService.MigrateExtraApps(oldPath, newPath);
        Assert.Equal("[1]", File.ReadAllText(newPath));

        File.WriteAllText(newPath, "[2]");
        GameListService.MigrateExtraApps(oldPath, newPath);
        Assert.Equal("[2]", File.ReadAllText(newPath));
    }
}

public sealed class BackupAndHistoryTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => this._dir.Dispose();

    [Fact]
    public void BackupSaveThrowsWhenItCantWrite()
    {
        // A file where the game's backup folder should be.
        this._dir.Write("backups\\10", "");
        var backups = new BackupService(Path.Combine(this._dir.Path, "backups"));

        Assert.ThrowsAny<IOException>(() => backups.Save(new GameSnapshot { AppId = 10, GameName = "Game" }));
    }

    [Fact]
    public void HistoryAppendsFromManyProcessesAreAllKept()
    {
        var path = Path.Combine(this._dir.Path, "history.jsonl");

        // Separate instances behave like separate processes (no shared in-process lock).
        var services = Enumerable.Range(0, 4).Select(_ => new HistoryService(path)).ToList();
        Parallel.For(0, 100, i => services[i % services.Count].Append(Record((uint)i)));

        var loaded = new HistoryService(path).Load(max: 1000);
        Assert.Equal(Enumerable.Range(0, 100).Select(i => (uint)i), loaded.Select(r => r.AppId).Order());
    }

    [Fact]
    public void HistoryTrimKeepsRecordsAppendedByOtherProcesses()
    {
        var path = Path.Combine(this._dir.Path, "history.jsonl");
        var line = JsonSerializer.Serialize(Record(1));
        File.WriteAllText(path, string.Concat(Enumerable.Repeat(line + "\n", HistoryService.MaxLines)));

        var first = new HistoryService(path);
        var second = new HistoryService(path);
        first.Append(Record(2));   // Trims.
        second.Append(Record(3));  // Its line count is stale; must not lose anything.

        var loaded = new HistoryService(path).Load(max: int.MaxValue);
        Assert.Equal(HistoryService.TrimToLines + 1, loaded.Count);
        Assert.Equal(3u, loaded[0].AppId);
        Assert.Equal(2u, loaded[1].AppId);
    }

    private static CommitRecord Record(uint appId) =>
        new(DateTimeOffset.Now, appId, "Game", ["A"], [], [], CommitSource.Manual, true, null);
}

public sealed class CommandLineSafetyTests
{
    [Fact]
    public void RejectsUnlockingAndLockingTheSameIdInDifferentCase()
    {
        var command = CommandLine.Parse(["21690", "--unlock", "ach_win", "--lock", "ACH_WIN", "--commit"]);

        Assert.Equal(LaunchMode.Invalid, command.Mode);
        Assert.Contains("ach_win", command.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@x", "'@x")]
    [InlineData("\tTab", "'\tTab")]
    [InlineData("=a,b", "\"'=a,b\"")]
    [InlineData("Half-Life", "Half-Life")]
    public void CsvFieldsCantBecomeFormulas(string field, string expected) =>
        Assert.Equal(expected, CsvExport.Escape(field));

    [Fact]
    public void WindowLaunchSeesARunningCommandLineCommit()
    {
        // An App ID no real game uses, so this never meets a running SAM.
        var appId = (uint)Random.Shared.Next(1_900_000_000, int.MaxValue);
        Assert.False(SingleInstance.IsCommandLineCommitRunning(appId));

        using (var claim = SingleInstance.TryClaimForCommandLine(appId, out var running))
        {
            Assert.NotNull(claim);
            Assert.False(running);
            Assert.True(SingleInstance.IsCommandLineCommitRunning(appId));

            Assert.Null(SingleInstance.TryClaimForCommandLine(appId, out var secondRunning));
            Assert.True(secondRunning);

            Assert.Null(SingleInstance.TryClaim(appId, ["--unlock-all"], out var commitRunning));
            Assert.True(commitRunning);
        }

        Assert.False(SingleInstance.IsCommandLineCommitRunning(appId));
    }
}
