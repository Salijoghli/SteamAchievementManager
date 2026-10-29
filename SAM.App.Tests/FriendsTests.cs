using System.Net;
using System.Runtime.InteropServices;
using SAM.API.Interfaces;
using SAM.App.Services;

namespace SAM.App.Tests;

/// <summary>Real response shapes from steamcommunity.com (Oct 2026), trimmed.</summary>
internal static class CommunityFixtures
{
    public const string Unlocked = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?><playerstats>
        	<privacyState>public</privacyState>
        	<visibilityState>3</visibilityState>
        	<game>
        		<gameFriendlyName><![CDATA[21690]]></gameFriendlyName>
        		<gameName><![CDATA[Resident Evil 5]]></gameName>
        	</game>
        	<player>
        		<steamID64>76561198000000001</steamID64>
        	</player>
        	<stats>
        		<hoursPlayed>0</hoursPlayed>
        	</stats>
        	<achievements>
        		<achievement closed="1">
        			<iconClosed><![CDATA[https://example.invalid/a.jpg]]></iconClosed>
        			<name><![CDATA[Completed Chapter 1 - 1]]></name>
        			<apiname><![CDATA[ach_chapter11_clear]]></apiname>
        			<description><![CDATA[Complete Chapter 1 - 1.]]></description>
        			<unlockTimestamp>1785357113</unlockTimestamp>
        		</achievement>
        		<achievement closed="1">
        			<name><![CDATA[No time]]></name>
        			<apiname><![CDATA[ACH_NoTime]]></apiname>
        		</achievement>
        		<achievement closed="0">
        			<name><![CDATA[Completed Chapter 2 - 3]]></name>
        			<apiname><![CDATA[ach_chapter23_clear]]></apiname>
        		</achievement>
        	</achievements>
        </playerstats>
        """;

    public const string AllLocked = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?><playerstats>
        	<privacyState>public</privacyState>
        	<visibilityState>3</visibilityState>
        	<achievements>
        		<achievement closed="0"><apiname><![CDATA[a]]></apiname></achievement>
        		<achievement closed="0"><apiname><![CDATA[b]]></apiname></achievement>
        	</achievements>
        </playerstats>
        """;

    public const string FriendsOnly = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?><playerstats>
        	<privacyState>friendsonly</privacyState>
        	<visibilityState>1</visibilityState>
        </playerstats>
        """;

    public const string InvalidGame =
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><response><error><![CDATA[Invalid game specified.]]></error></response>""";

    public const string Html = """
        <!DOCTYPE html>
        <html class=" responsive DesktopUI" lang="en"  >
        <head><meta http-equiv="Content-Type" content="text/html; charset=UTF-8"><title>Steam Community :: Error</title>
        """;

    public const string Profile = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?><profile>
        	<steamID64>76561198000000001</steamID64>
        	<privacyState>friendsonly</privacyState>
        	<avatarIcon><![CDATA[https://avatars.example.invalid/x.jpg]]></avatarIcon>
        	<avatarMedium><![CDATA[https://avatars.example.invalid/x_medium.jpg]]></avatarMedium>
        </profile>
        """;
}

public sealed class SteamCommunityXmlTests
{
    [Fact]
    public void ParsesUnlockedAchievementsWithTimes()
    {
        var (status, unlocked) = SteamCommunityXml.ParseAchievements(CommunityFixtures.Unlocked);

        Assert.Equal(FriendAchievementsStatus.Ok, status);
        Assert.Equal(2, unlocked.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785357113), unlocked["ach_chapter11_clear"]);
        Assert.Null(unlocked["ACH_NoTime"]);
        Assert.True(unlocked.ContainsKey("ach_notime"), "API names match case-insensitively");
        Assert.False(unlocked.ContainsKey("ach_chapter23_clear"));
    }

    [Fact]
    public void NothingUnlockedIsNotOwned()
    {
        // Steam's page for a game the friend doesn't own looks exactly like this.
        var (status, unlocked) = SteamCommunityXml.ParseAchievements(CommunityFixtures.AllLocked);

        Assert.Equal(FriendAchievementsStatus.NotOwned, status);
        Assert.Empty(unlocked);
    }

    [Fact]
    public void NonPublicProfileIsPrivate() =>
        Assert.Equal(FriendAchievementsStatus.Private, SteamCommunityXml.ParseAchievements(CommunityFixtures.FriendsOnly).Status);

    [Fact]
    public void InvalidGameErrorIsNotOwned() =>
        Assert.Equal(FriendAchievementsStatus.NotOwned, SteamCommunityXml.ParseAchievements(CommunityFixtures.InvalidGame).Status);

    [Theory]
    [InlineData("")]
    [InlineData("<playerstats><achievements><achievement closed=\"1\">")]
    [InlineData("<somethingelse/>")]
    [InlineData("<response><error>Something broke</error></response>")]
    [InlineData(CommunityFixtures.Html)]
    public void MalformedOrUnexpectedIsFailed(string xml) =>
        Assert.Equal(FriendAchievementsStatus.Failed, SteamCommunityXml.ParseAchievements(xml).Status);

    [Fact]
    public void ParsesMediumAvatar()
    {
        Assert.Equal("https://avatars.example.invalid/x_medium.jpg", SteamCommunityXml.ParseAvatarUrl(CommunityFixtures.Profile));
        Assert.Null(SteamCommunityXml.ParseAvatarUrl(CommunityFixtures.InvalidGame));
        Assert.Null(SteamCommunityXml.ParseAvatarUrl("<profile><avatarMedium>javascript:alert(1)</avatarMedium></profile>"));
        Assert.Null(SteamCommunityXml.ParseAvatarUrl("not xml"));
    }

    [Fact]
    public void KeepsXmlQueryAcrossRedirects()
    {
        Assert.Equal(
            "https://steamcommunity.com/id/someone/stats/CSGO?tab=achievements&xml=1",
            SteamCommunityFetcher.WithXmlQuery(new Uri("https://steamcommunity.com/id/someone/stats/CSGO?tab=achievements")).AbsoluteUri);
        Assert.Equal(
            "https://steamcommunity.com/profiles/1/stats/Portal2?xml=1",
            SteamCommunityFetcher.WithXmlQuery(new Uri("https://steamcommunity.com/profiles/1/stats/Portal2")).AbsoluteUri);
        Assert.Equal(
            "https://steamcommunity.com/id/someone/?xml=1",
            SteamCommunityFetcher.WithXmlQuery(new Uri("https://steamcommunity.com/id/someone/?xml=1")).AbsoluteUri);

        Assert.True(SteamCommunityXml.IsStatsPage(new Uri("https://steamcommunity.com/id/someone/stats/CSGO?tab=achievements")));
        Assert.True(SteamCommunityXml.IsStatsPage(SteamCommunityXml.AchievementsUri(76561198000000001, 21690)));
        Assert.False(SteamCommunityXml.IsStatsPage(new Uri("https://steamcommunity.com/id/someone?xml=1")));
        Assert.False(SteamCommunityXml.IsStatsPage(new Uri("https://steamcommunity.com/id/someone/achievements/730")));
    }

    [Fact]
    public void SortsOnlineFirstThenByName()
    {
        var sorted = FriendsService.Sort(
        [
            new SteamFriend(1, "bob", null, false),
            new SteamFriend(2, "Zed", null, true),
            new SteamFriend(3, "alice", null, false),
            new SteamFriend(4, "Amy", null, true),
        ]);

        Assert.Equal(new ulong[] { 4, 2, 3, 1 }, sorted.Select(f => f.SteamId));
    }

    [Fact]
    public void FriendsVtablesMatchSteamworksHeaders()
    {
        // SteamFriends017 (SDK 1.61): GetPersonaName, SetPersonaName, GetPersonaState, GetFriendCount,
        // GetFriendByIndex, GetFriendRelationship, GetFriendPersonaState, GetFriendPersonaName.
        Assert.Equal(3 * 8, Marshal.OffsetOf<ISteamFriends017>(nameof(ISteamFriends017.GetFriendCount)).ToInt32());
        Assert.Equal(4 * 8, Marshal.OffsetOf<ISteamFriends017>(nameof(ISteamFriends017.GetFriendByIndex)).ToInt32());
        Assert.Equal(6 * 8, Marshal.OffsetOf<ISteamFriends017>(nameof(ISteamFriends017.GetFriendPersonaState)).ToInt32());
        Assert.Equal(7 * 8, Marshal.OffsetOf<ISteamFriends017>(nameof(ISteamFriends017.GetFriendPersonaName)).ToInt32());

        // SteamFriends018 (SDK 1.62+) dropped SetPersonaName.
        Assert.Equal(2 * 8, Marshal.OffsetOf<ISteamFriends018>(nameof(ISteamFriends018.GetFriendCount)).ToInt32());
        Assert.Equal(3 * 8, Marshal.OffsetOf<ISteamFriends018>(nameof(ISteamFriends018.GetFriendByIndex)).ToInt32());
        Assert.Equal(5 * 8, Marshal.OffsetOf<ISteamFriends018>(nameof(ISteamFriends018.GetFriendPersonaState)).ToInt32());
        Assert.Equal(6 * 8, Marshal.OffsetOf<ISteamFriends018>(nameof(ISteamFriends018.GetFriendPersonaName)).ToInt32());

        // GetISteamFriends is entry 8 of ISteamClient018.
        Assert.Equal(8 * 8, Marshal.OffsetOf<ISteamClient018>(nameof(ISteamClient018.GetISteamFriends)).ToInt32());
    }
}

public sealed class FriendAchievementsClientTests : IDisposable
{
    private const ulong Friend = 76561198000000001;
    private readonly TempDir _dir = new();
    private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => this._dir.Dispose();

    private FriendAchievementsClient Create(FakeHandler handler) =>
        new(new SteamCommunityFetcher(new HttpClient(handler), () => this._now), Path.Combine(this._dir.Path, "friends"), () => this._now);

    [Fact]
    public async Task CachesForThirtyMinutesInMemoryAndOnDisk()
    {
        var handler = new FakeHandler(_ => FakeHandler.Xml(CommunityFixtures.Unlocked));
        var client = this.Create(handler);

        var first = await client.GetAsync(Friend, 21690, CancellationToken.None);
        Assert.Equal(FriendAchievementsStatus.Ok, first.Status);
        Assert.Equal("/profiles/76561198000000001/stats/21690/achievements/", handler.Requests.Single().AbsolutePath);
        Assert.Equal("?xml=1", handler.Requests.Single().Query);

        this._now += TimeSpan.FromMinutes(29);
        var second = await client.GetAsync(Friend, 21690, CancellationToken.None);
        Assert.Single(handler.Requests);
        Assert.Equal(first.Unlocked.Count, second.Unlocked.Count);

        // Another process (a new client) reads the disk copy, with times intact.
        var otherProcess = this.Create(handler);
        var fromDisk = await otherProcess.GetAsync(Friend, 21690, CancellationToken.None);
        Assert.Single(handler.Requests);
        Assert.Equal(FriendAchievementsStatus.Ok, fromDisk.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785357113), fromDisk.Unlocked["ACH_CHAPTER11_CLEAR"]);
        Assert.Null(fromDisk.Unlocked["ach_notime"]);

        this._now += TimeSpan.FromMinutes(2);
        await client.GetAsync(Friend, 21690, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);

        // A different game is a different entry.
        await client.GetAsync(Friend, 400, CancellationToken.None);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task CachesPrivateAndNotOwnedButNotFailures()
    {
        var bodies = new Queue<HttpResponseMessage>(
        [
            FakeHandler.Xml(CommunityFixtures.FriendsOnly),
            new HttpResponseMessage(HttpStatusCode.InternalServerError),
            FakeHandler.Xml(CommunityFixtures.AllLocked),
        ]);
        var handler = new FakeHandler(_ => bodies.Dequeue());
        var client = this.Create(handler);

        Assert.Equal(FriendAchievementsStatus.Private, (await client.GetAsync(Friend, 1, CancellationToken.None)).Status);
        Assert.Equal(FriendAchievementsStatus.Private, (await client.GetAsync(Friend, 1, CancellationToken.None)).Status);
        Assert.Single(handler.Requests);

        Assert.Equal(FriendAchievementsStatus.Failed, (await client.GetAsync(Friend, 2, CancellationToken.None)).Status);
        var retried = await client.GetAsync(Friend, 2, CancellationToken.None);
        Assert.Equal(FriendAchievementsStatus.NotOwned, retried.Status);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ServesStaleAnswerWhenRefreshFails()
    {
        var fail = false;
        var handler = new FakeHandler(_ => fail ? new HttpResponseMessage(HttpStatusCode.BadGateway) : FakeHandler.Xml(CommunityFixtures.Unlocked));
        var client = this.Create(handler);
        await client.GetAsync(Friend, 21690, CancellationToken.None);

        fail = true;
        this._now += TimeSpan.FromHours(2);
        var stale = await client.GetAsync(Friend, 21690, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(FriendAchievementsStatus.Ok, stale.Status);
        Assert.Equal(2, stale.Unlocked.Count);
    }

    [Fact]
    public async Task BacksOffAfterTooManyRequests()
    {
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return response;
        });
        var client = this.Create(handler);

        Assert.Equal(FriendAchievementsStatus.Failed, (await client.GetAsync(Friend, 1, CancellationToken.None)).Status);
        this._now += TimeSpan.FromSeconds(60);
        Assert.Equal(FriendAchievementsStatus.Failed, (await client.GetAsync(Friend, 2, CancellationToken.None)).Status);
        Assert.Single(handler.Requests);

        this._now += TimeSpan.FromSeconds(31);
        await client.GetAsync(Friend, 3, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FollowsFriendlyNameRedirectAndTreatsProfileRedirectAsNotOwned()
    {
        // The redirects Steam really sends (Oct 2026).
        var handler = new FakeHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.Contains("/stats/730/", StringComparison.Ordinal))
            {
                return FakeHandler.Redirect("https://steamcommunity.com/id/someone/stats/CSGO?tab=achievements");
            }

            if (uri.AbsolutePath.EndsWith("/stats/CSGO", StringComparison.Ordinal))
            {
                // Without xml=1 Steam would redirect again, to the new HTML achievements page.
                return uri.Query == "?tab=achievements&xml=1"
                    ? FakeHandler.Xml(CommunityFixtures.Unlocked)
                    : FakeHandler.Redirect("https://steamcommunity.com/id/someone/achievements/730");
            }

            if (uri.AbsolutePath.Contains("/stats/", StringComparison.Ordinal))
            {
                return FakeHandler.Redirect("https://steamcommunity.com/id/someone");
            }

            return FakeHandler.Xml(CommunityFixtures.Profile);
        });
        var client = this.Create(handler);

        var cs = await client.GetAsync(Friend, 730, CancellationToken.None);
        Assert.Equal(FriendAchievementsStatus.Ok, cs.Status);
        Assert.Equal(2, cs.Unlocked.Count);
        Assert.Equal(2, handler.Requests.Count);

        Assert.Equal(FriendAchievementsStatus.NotOwned, (await client.GetAsync(Friend, 570, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task SharesOneDownloadAndHonoursCancellation()
    {
        var release = new TaskCompletionSource();
        var handler = new FakeHandler(async _ =>
        {
            await release.Task;
            return FakeHandler.Xml(CommunityFixtures.Unlocked);
        });
        var client = this.Create(handler);

        using var cancel = new CancellationTokenSource();
        var cancelled = client.GetAsync(Friend, 21690, cancel.Token);
        var waiting = client.GetAsync(Friend, 21690, CancellationToken.None);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        release.SetResult();
        Assert.Equal(FriendAchievementsStatus.Ok, (await waiting).Status);
        Assert.Single(handler.Requests);
    }

    /// <summary>Answers requests with a callback and records the URLs asked for.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this(request => Task.FromResult(respond(request)))
        {
        }

        public List<Uri> Requests { get; } = [];

        public static HttpResponseMessage Xml(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "text/xml") };

        public static HttpResponseMessage Redirect(string location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location);
            return response;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (this.Requests)
            {
                this.Requests.Add(request.RequestUri!);
            }

            var response = await respond(request);
            response.RequestMessage ??= request;
            return response;
        }
    }
}

/// <summary>
/// Read-only checks against steamcommunity.com (anonymous GETs). Skipped unless SAM_LOCAL_TESTS=1.
/// SAM_STEAM_ID (a public profile's SteamID64) picks the profile.
/// </summary>
[Trait("Category", "Local")]
public sealed class LiveCommunityTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [LocalFact]
    public async Task ReadsAPublicProfile()
    {
        var steamId = ulong.Parse(Environment.GetEnvironmentVariable("SAM_STEAM_ID") ?? "76561198183548038", System.Globalization.CultureInfo.InvariantCulture);
        using var shared = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        shared.DefaultRequestHeaders.UserAgent.ParseAdd("SteamAchievementManager/tests");
        using var http = SteamCommunityFetcher.CreateHttpClient(shared);
        var client = new FriendAchievementsClient(new SteamCommunityFetcher(http), null);

        foreach (var appId in new uint[] { 21690, 730, 570, 0 })
        {
            var result = await client.GetAsync(steamId, appId, CancellationToken.None);
            output.WriteLine($"{appId,8}: {result.Status}, {result.Unlocked.Count} unlocked");
            Assert.NotEqual(FriendAchievementsStatus.Failed, result.Status);
        }

        var profile = await new SteamCommunityFetcher(http).GetAsync(SteamCommunityXml.ProfileUri(steamId), CancellationToken.None);
        output.WriteLine($"avatar: {SteamCommunityXml.ParseAvatarUrl(profile.Body ?? string.Empty)}");
    }
}
