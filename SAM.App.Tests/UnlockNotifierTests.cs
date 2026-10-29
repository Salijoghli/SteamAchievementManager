using System.Globalization;
using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.Tests;

public sealed class UnlockNotifierTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public UnlockNotifierTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = this._culture;
        this._dir.Dispose();
    }

    private GameSchema Schema() =>
        SchemaService.Parse(this._dir.Write("schema.bin", Fixtures.Schema()), Fixtures.AppId, "english")!;

    private static byte[] Stats(int block1, int block2) => Kv.Serialize(
        Kv.Node(
            "cache",
            new Kv("crc", 12345),
            Kv.Node("1", new Kv("data", block1)),
            Kv.Node("2", new Kv("data", block2)),
            new Kv("PendingChanges", 0)));

    private static CommitRecord Commit(uint appId, DateTimeOffset time, params string[] unlocked) =>
        new(time, appId, "Game", unlocked, [], [], CommitSource.Manual, true, null);

    [Fact]
    public void ReadsSetBitsOfEveryBlock()
    {
        var bits = UnlockSnapshot.Read(this._dir.Write("stats.bin", Fixtures.Stats()));

        // Block 1: bits 0 and 2; block 2: bit 0. crc and PendingChanges aren't blocks.
        Assert.NotNull(bits);
        Assert.Equal([(1L << 5) | 0, (1L << 5) | 2, (2L << 5) | 0], bits!.Order());
    }

    [Fact]
    public void MissingFileIsEmptyAndUnreadableFileIsNull()
    {
        Assert.Empty(UnlockSnapshot.Read(Path.Combine(this._dir.Path, "missing.bin"))!);
        Assert.Null(UnlockSnapshot.Read(this._dir.Write("broken.bin", Fixtures.Stats()[..^3])));
    }

    [Fact]
    public void FirstSnapshotIsOnlyABaseline()
    {
        var current = UnlockSnapshot.Read(this._dir.Write("stats.bin", Fixtures.Stats()))!;

        Assert.Empty(UnlockSnapshot.NewlyUnlocked(null, current, this.Schema()));
    }

    [Fact]
    public void FindsAchievementsWhoseBitWasJustSet()
    {
        var schema = this.Schema();
        var before = UnlockSnapshot.Read(this._dir.Write("before.bin", Stats(0b001, 0)))!;
        var after = UnlockSnapshot.Read(this._dir.Write("after.bin", Stats(0b111, 1)))!;

        var unlocked = UnlockSnapshot.NewlyUnlocked(before, after, schema);

        Assert.Equal(["ACH_B", "ACH_C", "ACH_D"], unlocked.Select(a => a.Id));
    }

    [Fact]
    public void IgnoresRelocksAndUnchangedFiles()
    {
        var schema = this.Schema();
        var before = UnlockSnapshot.Read(this._dir.Write("before.bin", Stats(0b111, 1)))!;
        var after = UnlockSnapshot.Read(this._dir.Write("after.bin", Stats(0b001, 0)))!;

        Assert.Empty(UnlockSnapshot.NewlyUnlocked(before, after, schema));
        Assert.Empty(UnlockSnapshot.NewlyUnlocked(before, before, schema));
    }

    [Fact]
    public void LeavesOutUnlocksSamRecentlyCommittedForThatGame()
    {
        var schema = this.Schema();
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var unlocked = schema.Achievements.Where(a => a.Id is "ACH_A" or "ACH_B" or "ACH_C").ToList();
        var history = new[]
        {
            Commit(Fixtures.AppId, now.AddMinutes(-1), "ACH_A"),
            Commit(Fixtures.AppId, now.AddHours(-2), "ACH_B"), // Too long ago to be this change.
            Commit(Fixtures.AppId + 1, now, "ACH_C"),          // Another game.
        };

        var result = UnlockSnapshot.ExceptSamUnlocks(unlocked, history, Fixtures.AppId, now, UnlockNotifier.SamCommitWindow);

        Assert.Equal(["ACH_B", "ACH_C"], result.Select(a => a.Id));
    }

    [Fact]
    public void OneNotificationPerUnlockWithRarity()
    {
        var schema = this.Schema();
        var a = schema.Achievements.Single(x => x.Id == "ACH_A");
        var c = schema.Achievements.Single(x => x.Id == "ACH_C");

        var notices = UnlockNotices.Build("Test Game", [(a, 4.2), (c, null)]);

        Assert.Equal(2, notices.Count);
        Assert.Equal("Alpha — Test Game", notices[0].Title);
        Assert.Equal("Legendary · 4.2% of players", notices[0].Message);
        Assert.Equal("Gamma — Test Game", notices[1].Title);
        Assert.Equal("Do Gamma", notices[1].Message);
    }

    [Fact]
    public void BatchesMoreThanThreeUnlocks()
    {
        var schema = this.Schema();
        var unlocks = schema.Achievements.Select(x => (x, (double?)50)).ToList();
        unlocks.Add((schema.Achievements[0], 50));

        var notices = UnlockNotices.Build("Test Game", unlocks);

        var notice = Assert.Single(notices);
        Assert.Equal("5 achievements unlocked in Test Game", notice.Title);
        Assert.Equal("Alpha, Beta, Gamma and 2 more", notice.Message);
    }

    [Fact]
    public void ThreeUnlocksAreStillShownOneByOne()
    {
        var schema = this.Schema();
        var unlocks = schema.Achievements.Take(3).Select(x => (x, (double?)null)).ToList();

        Assert.Equal(3, UnlockNotices.Build("Test Game", unlocks).Count);
        Assert.Empty(UnlockNotices.Build("Test Game", []));
    }
}
