using System.Globalization;
using SAM.App.Services;
using SAM.App.ViewModels;

namespace SAM.App.Tests;

public sealed class FriendComparisonTests
{
    [Theory]
    [InlineData(FriendCompareFilter.All, true, true, true)]
    [InlineData(FriendCompareFilter.All, false, false, true)]
    [InlineData(FriendCompareFilter.OnlyFriend, false, true, true)]
    [InlineData(FriendCompareFilter.OnlyFriend, true, true, false)]
    [InlineData(FriendCompareFilter.OnlyFriend, false, false, false)]
    [InlineData(FriendCompareFilter.OnlyMe, true, false, true)]
    [InlineData(FriendCompareFilter.OnlyMe, true, true, false)]
    [InlineData(FriendCompareFilter.Both, true, true, true)]
    [InlineData(FriendCompareFilter.Both, true, false, false)]
    [InlineData(FriendCompareFilter.Neither, false, false, true)]
    [InlineData(FriendCompareFilter.Neither, false, true, false)]
    public void FiltersByWhoHasIt(FriendCompareFilter filter, bool mine, bool theirs, bool expected) =>
        Assert.Equal(expected, FriendComparison.Matches(filter, mine, theirs));

    [Fact]
    public void FriendUnlocksMatchIdsInAnyCase()
    {
        var time = new DateTimeOffset(2024, 1, 3, 10, 0, 0, TimeSpan.Zero);
        var normalized = FriendComparison.Normalize(new Dictionary<string, DateTimeOffset?>
        {
            ["ACH_WIN"] = null,
            ["ach_win"] = time,
            ["ACH_LOSE"] = time,
            [""] = time,
        });

        Assert.Equal(2, normalized.Count);
        Assert.Equal(time, normalized["Ach_Win"]); // The known time wins over an unknown one.
        Assert.True(normalized.ContainsKey("ach_lose"));
    }

    [Fact]
    public void SummaryAndBadgeText()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            Assert.Equal("You 19/70 · Alex 45/70", FriendComparison.Summary("Alex", 19, 45, 70));
            Assert.Equal("You 1,200/2,000 · Bo 0/2,000", FriendComparison.Summary("Bo", 1200, 0, 2000));
            Assert.Equal("Alex has it", FriendComparison.Badge("Alex", null));
            var when = new DateTimeOffset(2024, 1, 3, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2024, 1, 3)));
            Assert.Equal("Alex has it · 3 Jan 2024", FriendComparison.Badge("Alex", when));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void StatusMessages()
    {
        Assert.Null(FriendComparison.StatusMessage(FriendAchievementsStatus.Ok, "Alex"));
        Assert.Equal("Alex's game details are private", FriendComparison.StatusMessage(FriendAchievementsStatus.Private, "Alex"));
        Assert.Equal("Alex doesn't own this game", FriendComparison.StatusMessage(FriendAchievementsStatus.NotOwned, "Alex"));
        Assert.Contains("Alex", FriendComparison.StatusMessage(FriendAchievementsStatus.Failed, "Alex"));
    }

    [Fact]
    public void FriendSearchKeepsOnlineFirstThenByName()
    {
        SteamFriend[] friends =
        [
            new(1, "zed", null, false),
            new(2, "Bea", null, true),
            new(3, "alex", "https://example.invalid/a.jpg", false),
            new(4, "Ann", null, true),
        ];

        Assert.Equal(["Ann", "Bea", "alex", "zed"], FriendComparison.FilterFriends(friends, "").Select(f => f.Name));
        Assert.Equal(["Ann", "Bea", "alex"], FriendComparison.FilterFriends(friends, " A ").Select(f => f.Name));
        Assert.Equal(["Bea"], FriendComparison.FilterFriends(friends, "BE").Select(f => f.Name));
        Assert.Empty(FriendComparison.FilterFriends(friends, "nobody"));
    }
}
