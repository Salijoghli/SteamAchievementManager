using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>
/// Checks GitHub releases (Salijoghli/SteamAchievementManager) for a version newer than the
/// running one. Returns null when up to date, offline, or skipped by the user.
/// </summary>
/// <remarks>
/// The latest release is fetched once per process and reused; the "skipped version" setting is
/// applied on every call, so skipping a version takes effect without another request. Callers
/// decide whether to check at all (<see cref="AppSettings.CheckForUpdates"/>).
/// </remarks>
public sealed class UpdateService(HttpClient http, AppSettings settings)
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/Salijoghli/SteamAchievementManager/releases/latest";

    private readonly object _lock = new();
    private Task<UpdateInfo?>? _latest;

    /// <summary>The running app's version (from the assembly, e.g. 8.1.0.0).</summary>
    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>
    /// The latest release if it's newer than <see cref="CurrentVersion"/> and not the version in
    /// <see cref="AppSettings.SkippedUpdateVersion"/>; otherwise null. Also null when there are no
    /// releases yet, GitHub can't be reached or its answer can't be read (the error is logged).
    /// Never throws, except <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        Task<UpdateInfo?> latest;
        lock (this._lock)
        {
            // The request itself isn't tied to the caller's token, so a cancelled caller
            // doesn't poison the cached result for everyone else.
            latest = this._latest ??= this.FetchLatestAsync();
        }

        var info = await latest.WaitAsync(cancellationToken);
        if (info == null)
        {
            return null;
        }

        var version = ParseVersion(info.Version);
        if (version == null || version <= Normalize(CurrentVersion))
        {
            return null;
        }

        var skipped = ParseVersion(settings.SkippedUpdateVersion);
        return skipped != null && skipped == version ? null : info;
    }

    /// <summary>
    /// Parses a release tag such as "v8.1.0", "8.1" or "v8.1.0-beta" into a version with all four
    /// components set (missing ones are 0). Null if it isn't a version.
    /// </summary>
    internal static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        // Drop pre-release or build suffixes ("-beta.1", "+abc").
        var suffix = text.IndexOfAny(['-', '+', ' ']);
        if (suffix >= 0)
        {
            text = text[..suffix];
        }

        if (text.Contains('.') == false &&
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            return new Version(major, 0, 0, 0);
        }

        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    /// <summary>Fills undefined components with 0, so 8.1.0 and 8.1.0.0 compare equal.</summary>
    private static Version Normalize(Version version) => new(
        version.Major,
        Math.Max(version.Minor, 0),
        Math.Max(version.Build, 0),
        Math.Max(version.Revision, 0));

    private async Task<UpdateInfo?> FetchLatestAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // No releases published yet.
                return null;
            }

            if (response.IsSuccessStatusCode == false)
            {
                Diagnostics.Log($"Update check: GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseRelease(json);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException or UriFormatException or FormatException)
        {
            Diagnostics.Log("Update check failed", e);
            return null;
        }
    }

    /// <summary>
    /// Reads a GitHub "release" object. Null (logged) when the tag isn't a version or the
    /// release page is missing.
    /// </summary>
    internal static UpdateInfo? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var tag = GetString(root, "tag_name");
        if (tag == null || ParseVersion(tag) == null)
        {
            Diagnostics.Log($"Update check: the latest release's tag \"{tag}\" isn't a version");
            return null;
        }

        var page = GetString(root, "html_url");
        if (page == null || Uri.TryCreate(page, UriKind.Absolute, out var releasePage) == false)
        {
            Diagnostics.Log("Update check: the latest release has no page URL");
            return null;
        }

        Uri? download = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = GetString(asset, "name");
                var url = GetString(asset, "browser_download_url");
                if (name != null && url != null &&
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreate(url, UriKind.Absolute, out download))
                {
                    break;
                }

                download = null;
            }
        }

        var version = tag.TrimStart('v', 'V');
        var title = GetString(root, "name");
        return new UpdateInfo(
            version,
            string.IsNullOrWhiteSpace(title) ? $"Steam Achievement Manager {version}" : title,
            GetString(root, "body") ?? "",
            releasePage,
            download);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
