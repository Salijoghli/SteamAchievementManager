using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace SAM.App.Services;

/// <summary>
/// Disk cache for images under cache\images. File name = SHA-256 of the URL. Images are kept
/// until the cache is cleared. Downloads are limited to a few at a time; concurrent requests
/// for the same URL share one download.
/// </summary>
public sealed class ImageCache(AppCache cache, HttpClient http)
{
    private const int MaxConcurrentDownloads = 6;

    private readonly SemaphoreSlim _downloadSlots = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private readonly Dictionary<string, Download> _downloads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _failedUrls = new(StringComparer.Ordinal);

    /// <summary>
    /// Tries each URL in order and returns the local file path of the first that's cached or
    /// downloads successfully, or null if none work. Failed URLs are remembered for the session.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing when <paramref name="cancellationToken"/> is cancelled.
    /// A shared download is only abandoned once every caller waiting for it has cancelled.
    /// </remarks>
    public async Task<string?> GetAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        foreach (var url in urls)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (string.IsNullOrEmpty(url) || this._failedUrls.ContainsKey(url))
            {
                continue;
            }

            var path = this.GetCachePath(url);
            if (File.Exists(path))
            {
                return path;
            }

            try
            {
                var result = await this.DownloadSharedAsync(url, path, cancellationToken).ConfigureAwait(false);
                if (result != null)
                {
                    return result;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        return null;
    }

    internal string GetCachePath(string url) => Path.Combine(cache.ImagesDirectory, GetCacheFileName(url));

    /// <summary>Lowercase hex SHA-256 of the URL plus the URL's extension (default .jpg).</summary>
    internal static string GetCacheFileName(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();

        var extension = ".jpg";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var candidate = Path.GetExtension(uri.AbsolutePath);
            if (candidate.Length is > 1 and <= 6 && candidate.Skip(1).All(char.IsAsciiLetterOrDigit))
            {
                extension = candidate.ToLowerInvariant();
            }
        }

        return hash + extension;
    }

    private async Task<string?> DownloadSharedAsync(string url, string path, CancellationToken cancellationToken)
    {
        Download download;
        lock (this._downloads)
        {
            if (this._downloads.TryGetValue(url, out var existing) == false || existing.Cancellation.IsCancellationRequested)
            {
                existing = new Download();
                var started = existing;
                existing.Task = Task.Run(() => this.DownloadAndForgetAsync(url, path, started));
                this._downloads[url] = existing;
            }

            download = existing;
            download.Waiters++;
        }

        try
        {
            return await download.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (this._downloads)
            {
                download.Waiters--;
                if (download.Waiters == 0 && download.Task.IsCompleted == false)
                {
                    // Nobody is waiting for this download any more (e.g. the card scrolled away).
                    download.Cancellation.Cancel();
                }
            }
        }
    }

    private async Task<string?> DownloadAndForgetAsync(string url, string path, Download download)
    {
        try
        {
            return await this.DownloadAsync(url, path, download.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (download.Cancellation.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            lock (this._downloads)
            {
                if (this._downloads.TryGetValue(url, out var current) && ReferenceEquals(current, download))
                {
                    this._downloads.Remove(url);
                }
            }
        }
    }

    private async Task<string?> DownloadAsync(string url, string path, CancellationToken cancellationToken)
    {
        await this._downloadSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temp = null;
        try
        {
            if (File.Exists(path))
            {
                return path;
            }

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode == false)
            {
                // 404 and friends: don't try this URL again this session.
                this._failedUrls.TryAdd(url, 0);
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType != null && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == false)
            {
                this._failedUrls.TryAdd(url, 0);
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temp = $"{path}.{Guid.NewGuid():N}.tmp";
            long length;
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await response.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                length = output.Length;
            }

            if (length == 0)
            {
                this._failedUrls.TryAdd(url, 0);
                return null;
            }

            File.Move(temp, path, overwrite: true);
            temp = null;
            return path;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            // Network errors and timeouts may be transient, so the URL isn't marked as failed.
            Diagnostics.Log($"Couldn't download {url}: {e.Message}");
            return null;
        }
        finally
        {
            this._downloadSlots.Release();
            if (temp != null)
            {
                TryDelete(temp);
            }
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

    private sealed class Download
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public Task<string?> Task { get; set; } = System.Threading.Tasks.Task.FromResult<string?>(null);

        public int Waiters { get; set; }
    }
}
