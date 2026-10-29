using System.Globalization;
using System.Net;
using System.Xml;
using System.Xml.Linq;

namespace SAM.App.Services;

/// <summary>What happened to a <see cref="SteamCommunityFetcher"/> request.</summary>
internal enum CommunityFetchOutcome
{
    /// <summary>Got a 2xx answer; <see cref="CommunityResponse.Body"/> holds it.</summary>
    Ok,

    /// <summary>steamcommunity.com said 429 (now or recently); nothing was sent while backing off.</summary>
    RateLimited,

    /// <summary>Offline, timed out, or a non-2xx status other than 429.</summary>
    Failed,
}

/// <summary>A community page: the outcome, its body, and the URL after redirects.</summary>
internal sealed record CommunityResponse(CommunityFetchOutcome Outcome, string? Body, Uri? FinalUri);

/// <summary>
/// Polite GETs to steamcommunity.com: at most three requests at a time, and after an HTTP 429
/// nothing is sent until the back-off (Retry-After, else 30 s doubling up to 10 minutes) ends.
/// <c>http</c> must not follow redirects itself (see <see cref="CreateHttpClient"/>): they're
/// followed here, keeping <c>xml=1</c> (see <see cref="WithXmlQuery"/>).
/// Thread-safe. Throws only OperationCanceledException for <c>cancellationToken</c>.
/// </summary>
internal sealed class SteamCommunityFetcher(HttpClient http, Func<DateTimeOffset>? clock = null)
{
    private const int MaxRedirects = 5;
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);

    /// <summary>
    /// An HttpClient for this fetcher: the same User-Agent and timeout as <paramref name="shared"/>,
    /// but it doesn't follow redirects on its own.
    /// </summary>
    public static HttpClient CreateHttpClient(HttpClient shared)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = shared.Timeout,
        };
        foreach (var product in shared.DefaultRequestHeaders.UserAgent)
        {
            client.DefaultRequestHeaders.UserAgent.Add(product);
        }

        return client;
    }

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim _slots = new(3, 3);
    private readonly object _lock = new();
    private DateTimeOffset _blockedUntil;
    private TimeSpan _backoff = FirstBackoff;

    /// <summary>True while backing off after a 429.</summary>
    public bool IsBackingOff
    {
        get
        {
            lock (this._lock)
            {
                return this._clock() < this._blockedUntil;
            }
        }
    }

    public async Task<CommunityResponse> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (this.IsBackingOff)
        {
            return new(CommunityFetchOutcome.RateLimited, null, null);
        }

        await this._slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.IsBackingOff)
            {
                return new(CommunityFetchOutcome.RateLimited, null, null);
            }

            var current = uri;
            for (var hop = 0; ; hop++)
            {
                using var response = await http.GetAsync(current, cancellationToken).ConfigureAwait(false);
                if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location && hop < MaxRedirects)
                {
                    // Followed here, not by the HttpClient: Steam's redirects drop xml=1, so put it back.
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (HasXmlQuery(uri))
                    {
                        current = WithXmlQuery(current);
                    }

                    continue;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    this.BackOff(response.Headers.RetryAfter);
                    Diagnostics.Log($"steamcommunity.com rate limited SAM; waiting before asking again ({uri.AbsolutePath})");
                    return new(CommunityFetchOutcome.RateLimited, null, current);
                }

                if (response.IsSuccessStatusCode == false)
                {
                    Diagnostics.Log($"steamcommunity.com answered {(int)response.StatusCode} for {uri.AbsolutePath}");
                    return new(CommunityFetchOutcome.Failed, null, current);
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                lock (this._lock)
                {
                    this._backoff = FirstBackoff;
                }

                return new(CommunityFetchOutcome.Ok, body, current);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Diagnostics.Log($"Couldn't fetch {uri.AbsolutePath} from steamcommunity.com: {e.Message}");
            return new(CommunityFetchOutcome.Failed, null, null);
        }
        finally
        {
            this._slots.Release();
        }
    }

    /// <summary>
    /// <paramref name="uri"/> with <c>xml=1</c> in its query (unchanged if it's already there).
    /// Steam redirects e.g. <c>/profiles/&lt;id&gt;/stats/730/achievements/?xml=1</c> to
    /// <c>/id/&lt;name&gt;/stats/CSGO?tab=achievements</c>, which without it is an HTML page that
    /// redirects again, to the new achievements page.
    /// </summary>
    internal static Uri WithXmlQuery(Uri uri)
    {
        if (HasXmlQuery(uri))
        {
            return uri;
        }

        var query = uri.Query.TrimStart('?');
        return new UriBuilder(uri) { Query = query.Length == 0 ? "xml=1" : query + "&xml=1" }.Uri;
    }

    private static bool HasXmlQuery(Uri uri) =>
        uri.Query.TrimStart('?').Split('&').Contains("xml=1", StringComparer.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private void BackOff(System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter)
    {
        lock (this._lock)
        {
            var now = this._clock();
            var wait = this._backoff;
            if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            {
                wait = delta;
            }
            else if (retryAfter?.Date is { } date && date > now)
            {
                wait = date - now;
            }

            wait = wait > MaxBackoff ? MaxBackoff : wait;
            this._blockedUntil = now + wait;
            this._backoff = TimeSpan.FromTicks(Math.Min(this._backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }
}

/// <summary>Parsers for the steamcommunity.com XML pages SAM reads.</summary>
internal static class SteamCommunityXml
{
    /// <summary>
    /// <c>/profiles/&lt;id&gt;/stats/&lt;appid&gt;/achievements/?xml=1</c> for a friend and game.
    /// </summary>
    public static Uri AchievementsUri(ulong steamId, uint appId) => new(string.Create(
        CultureInfo.InvariantCulture,
        $"https://steamcommunity.com/profiles/{steamId}/stats/{appId}/achievements/?xml=1"));

    /// <summary><c>/profiles/&lt;id&gt;/?xml=1</c>: the profile summary with avatar URLs.</summary>
    public static Uri ProfileUri(ulong steamId) => new(string.Create(
        CultureInfo.InvariantCulture,
        $"https://steamcommunity.com/profiles/{steamId}/?xml=1"));

    /// <summary>
    /// Whether <paramref name="uri"/> is a <c>/id|profiles/&lt;x&gt;/stats/...</c> page. Steam
    /// redirects stats URLs for games without stats (or unknown games) to the profile instead.
    /// </summary>
    public static bool IsStatsPage(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 4 &&
            (segments[0].Equals("profiles", StringComparison.OrdinalIgnoreCase) || segments[0].Equals("id", StringComparison.OrdinalIgnoreCase)) &&
            segments[2].Equals("stats", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether <paramref name="body"/> looks like an XML document rather than an HTML page.</summary>
    public static bool LooksLikeXml(string body)
    {
        var start = body.AsSpan().TrimStart();
        return start.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
            start.StartsWith("<playerstats", StringComparison.OrdinalIgnoreCase) ||
            start.StartsWith("<response", StringComparison.OrdinalIgnoreCase) ||
            start.StartsWith("<profile", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses a stats achievements XML page. Formats seen (Oct 2026):
    /// <list type="bullet">
    /// <item>Public: <c>&lt;playerstats&gt;&lt;privacyState&gt;public&lt;/privacyState&gt;…&lt;achievements&gt;
    /// &lt;achievement closed="1"&gt;…&lt;apiname&gt;X&lt;/apiname&gt;…&lt;unlockTimestamp&gt;unix&lt;/unlockTimestamp&gt;</c>;
    /// locked ones have <c>closed="0"</c> and no timestamp.</item>
    /// <item>Not public to an anonymous visitor (private or friends-only):
    /// <c>&lt;playerstats&gt;&lt;privacyState&gt;friendsonly&lt;/privacyState&gt;&lt;visibilityState&gt;1&lt;/visibilityState&gt;&lt;/playerstats&gt;</c>.</item>
    /// <item>Unknown game: <c>&lt;response&gt;&lt;error&gt;Invalid game specified.&lt;/error&gt;&lt;/response&gt;</c>.</item>
    /// </list>
    /// A game the user doesn't own comes back exactly like an owned one with nothing unlocked
    /// (every achievement <c>closed="0"</c>), so "nothing unlocked" is reported as
    /// <see cref="FriendAchievementsStatus.NotOwned"/>. Malformed input gives Failed.
    /// </summary>
    public static (FriendAchievementsStatus Status, Dictionary<string, DateTimeOffset?> Unlocked) ParseAchievements(string xml)
    {
        var unlocked = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        XElement? root;
        try
        {
            root = XDocument.Parse(xml).Root;
        }
        catch (XmlException)
        {
            return (FriendAchievementsStatus.Failed, unlocked);
        }

        if (root == null)
        {
            return (FriendAchievementsStatus.Failed, unlocked);
        }

        if (root.Name.LocalName == "response")
        {
            var error = root.Element("error")?.Value ?? string.Empty;
            return (ClassifyError(error), unlocked);
        }

        if (root.Name.LocalName != "playerstats")
        {
            return (FriendAchievementsStatus.Failed, unlocked);
        }

        var achievements = root.Element("achievements");
        if (achievements == null)
        {
            var privacy = root.Element("privacyState")?.Value.Trim();
            var error = root.Element("error")?.Value;
            if (error != null)
            {
                return (ClassifyError(error), unlocked);
            }

            return string.Equals(privacy, "public", StringComparison.OrdinalIgnoreCase)
                ? (FriendAchievementsStatus.NotOwned, unlocked)
                : (FriendAchievementsStatus.Private, unlocked);
        }

        foreach (var achievement in achievements.Elements("achievement"))
        {
            if ((string?)achievement.Attribute("closed") != "1")
            {
                continue;
            }

            var name = achievement.Element("apiname")?.Value.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            DateTimeOffset? time = null;
            if (long.TryParse(achievement.Element("unlockTimestamp")?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
                seconds > 0 && seconds < 253402300800)
            {
                time = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }

            unlocked[name] = time;
        }

        return (unlocked.Count == 0 ? FriendAchievementsStatus.NotOwned : FriendAchievementsStatus.Ok, unlocked);
    }

    /// <summary>
    /// The medium (64 px) avatar URL from a profile XML page
    /// (<c>&lt;profile&gt;…&lt;avatarMedium&gt;url&lt;/avatarMedium&gt;</c>; present even for private
    /// profiles). Null if missing, not an https URL, or the XML is malformed or an error.
    /// </summary>
    public static string? ParseAvatarUrl(string xml)
    {
        try
        {
            var root = XDocument.Parse(xml).Root;
            var url = root?.Name.LocalName == "profile" ? root.Element("avatarMedium")?.Value.Trim() : null;
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? url : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static FriendAchievementsStatus ClassifyError(string error)
    {
        if (error.Contains("private", StringComparison.OrdinalIgnoreCase))
        {
            return FriendAchievementsStatus.Private;
        }

        return error.Contains("invalid game", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("not own", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("stats", StringComparison.OrdinalIgnoreCase)
            ? FriendAchievementsStatus.NotOwned
            : FriendAchievementsStatus.Failed;
    }
}
