using System.Diagnostics;

namespace SAM.App.Services;

/// <summary>A release's downloadable SAM.exe.</summary>
public sealed record UpdateAsset(string Name, Uri Url, long Size);

/// <summary>The result of checking a downloaded SAM.exe before it replaces the running one.</summary>
public enum UpdateVerification
{
    Ok,

    /// <summary>The file is missing, smaller than any real build, or not the size GitHub announced.</summary>
    Incomplete,

    /// <summary>The file isn't a Windows program (no MZ/PE header).</summary>
    NotExecutable,

    /// <summary>The program's FileVersion isn't the release's version.</summary>
    WrongVersion,
}

/// <summary>
/// Replaces the running single-file SAM.exe with a newer release. A running exe can't be
/// overwritten but can be renamed, so the update is downloaded next to it
/// (<c>SAM.exe.download</c>), verified, then the running exe becomes <c>SAM.exe.old</c> and the
/// download takes its name. The next start deletes the leftovers (<see cref="CleanUp"/>).
/// </summary>
public static class SelfUpdater
{
    /// <summary>A real SAM.exe (self-contained, single file) is far larger than this.</summary>
    internal const long MinimumSize = 10L * 1024 * 1024;

    internal const string DownloadSuffix = ".download";
    internal const string OldSuffix = ".old";

    private static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        // Downloads take a while; the caller's token bounds them instead of HttpClient's timeout.
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SteamAchievementManager/{UpdateService.CurrentVersion.ToString(3)}");
        return http;
    });

    /// <summary>The HTTP client for update downloads.</summary>
    public static HttpClient Http => SharedHttp.Value;

    /// <summary>
    /// The running SAM.exe if SAM can replace it: it's a published single-file build and its
    /// folder is writable (not e.g. Program Files without admin rights). Null otherwise; then the
    /// release page is the way to update.
    /// </summary>
    public static string? GetReplaceableExecutable()
    {
        var path = Environment.ProcessPath;
        if (path == null || IsDevelopmentLayout(path, typeof(SelfUpdater).Assembly.Location))
        {
            return null;
        }

        return Path.GetDirectoryName(path) is { } folder && CanWriteTo(folder) ? path : null;
    }

    /// <summary>
    /// True for a development build: the app's code (SAM.dll) sits next to the exe, so swapping only
    /// the exe would break it. A published single-file build has its code inside the exe; Windows
    /// App SDK apps extract it to a temp folder (%TEMP%\.net\SAM\…) at start, so the assembly has a
    /// location there, but never in the exe's own folder.
    /// </summary>
    internal static bool IsDevelopmentLayout(string exePath, string? assemblyLocation) =>
        string.IsNullOrEmpty(assemblyLocation) == false &&
        string.Equals(
            Path.GetFullPath(Path.GetDirectoryName(assemblyLocation) ?? ""),
            Path.GetFullPath(Path.GetDirectoryName(exePath) ?? ""),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>True if a file can be created (and deleted) in <paramref name="folder"/>.</summary>
    internal static bool CanWriteTo(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".sam-write-test-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads <paramref name="asset"/> to <paramref name="destination"/> (replacing it),
    /// reporting progress from 0 to 1. A partial file is deleted on failure. Throws on HTTP or I/O
    /// errors and on cancellation.
    /// </summary>
    public static async Task DownloadAsync(
        HttpClient http,
        UpdateAsset asset,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? asset.Size;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long received = 0;
            var lastReported = -1.0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                if (total > 0)
                {
                    var fraction = Math.Min(1.0, (double)received / total);
                    if (fraction - lastReported >= 0.01 || fraction >= 1)
                    {
                        lastReported = fraction;
                        progress?.Report(fraction);
                    }
                }
            }
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    /// <summary>
    /// Checks a downloaded SAM.exe: at least <paramref name="minimumSize"/> bytes (and exactly
    /// <paramref name="expectedSize"/> when known), a Windows PE file, and its FileVersion equals
    /// <paramref name="releaseVersion"/>. Never throws.
    /// </summary>
    public static UpdateVerification Verify(string path, string releaseVersion, long? expectedSize, long minimumSize = MinimumSize)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists == false || file.Length < minimumSize || (expectedSize is { } size && file.Length != size))
            {
                return UpdateVerification.Incomplete;
            }

            if (IsPortableExecutable(path) == false)
            {
                return UpdateVerification.NotExecutable;
            }

            var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return UpdateService.IsSameVersion(version, releaseVersion) ? UpdateVerification.Ok : UpdateVerification.WrongVersion;
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't verify {path}", e);
            return UpdateVerification.NotExecutable;
        }
    }

    /// <summary>True if the file starts with an MZ header whose e_lfanew points at a "PE\0\0" signature.</summary>
    internal static bool IsPortableExecutable(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
        {
            return false;
        }

        stream.Position = 0x3C;
        var peOffset = reader.ReadInt32();
        if (peOffset <= 0 || peOffset > stream.Length - 4)
        {
            return false;
        }

        stream.Position = peOffset;
        return reader.ReadUInt32() == 0x00004550;
    }

    /// <summary>
    /// Puts <paramref name="newPath"/> in place of <paramref name="exePath"/>: the current exe is
    /// renamed to <c>&lt;exe&gt;.old</c> (allowed while it runs), then the new file is moved to
    /// its name. If that fails the old exe is put back and the error is thrown.
    /// </summary>
    public static void Swap(string exePath, string newPath)
    {
        var old = exePath + OldSuffix;
        TryDelete(old);
        if (File.Exists(old))
        {
            // Still in use (e.g. a game window from before the last update runs from it).
            old = $"{exePath}.{DateTime.UtcNow.Ticks}{OldSuffix}";
        }

        File.Move(exePath, old);
        try
        {
            File.Move(newPath, exePath);
        }
        catch
        {
            File.Move(old, exePath);
            throw;
        }
    }

    /// <summary>
    /// Deletes what an update left next to <paramref name="exePath"/>: the previous exe(s)
    /// (<c>SAM.exe*.old</c>) and an unfinished download. Files still in use stay until next time.
    /// Never throws.
    /// </summary>
    public static void CleanUp(string exePath)
    {
        try
        {
            var folder = Path.GetDirectoryName(exePath);
            if (folder == null || Directory.Exists(folder) == false)
            {
                return;
            }

            foreach (var old in Directory.EnumerateFiles(folder, Path.GetFileName(exePath) + "*" + OldSuffix))
            {
                TryDelete(old);
            }

            TryDelete(exePath + DownloadSuffix);
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't clean up after an update", e);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
