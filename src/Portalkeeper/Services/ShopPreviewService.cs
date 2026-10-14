using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Portalkeeper.Services;

/// <summary>
/// Evermore fork: the coin shop's turntable pictures (<see cref="ShopPreview"/>). Each is fetched
/// once and kept on disk by its URL, which the portal changes when the picture does, so a kept
/// file never goes stale. The newest <see cref="KeepFiles"/> are kept.
/// </summary>
public sealed class ShopPreviewService
{
    /// <summary>A picture bigger than this isn't fetched (a 36-frame strip is well under 1 MB).</summary>
    public const long MaxBytes = 16 * 1024 * 1024;
    public const int KeepFiles = 64;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(3);
    // One download per file at a time: a tile and the stage often ask for the same picture at once.
    private readonly Dictionary<string, Task<string?>> _fetching = [];

    public ShopPreviewService(string? directory = null, HttpClient? http = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Portalkeeper", "shop-previews");
        _http = http ?? Http;
    }

    /// <summary>
    /// The picture, decoded at most <paramref name="frameWidth"/> pixels across per frame (less
    /// memory for a small view), or null when it couldn't be fetched or read.
    /// </summary>
    public async Task<Bitmap?> LoadAsync(ShopPreview preview, int? frameWidth = null, CancellationToken cancellationToken = default)
    {
        string? path = null;
        try
        {
            path = await FetchAsync(preview.Url, cancellationToken);
            if (path is null || cancellationToken.IsCancellationRequested) return null;
            return await Task.Run(() => Decode(path, preview.Frames, frameWidth), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Downloaded but not a picture (a proxy's error page with a 200, a broken file): the URL
            // only changes with the picture, so a kept copy would never be fetched again.
            if (path is not null)
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return null;
        }
    }

    private static Bitmap? Decode(string path, int frames, int? frameWidth)
    {
        // Shared for delete: on Windows a picture being read must not stop the cache from replacing it.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return frameWidth is int width
            ? Bitmap.DecodeToWidth(stream, width * frames, BitmapInterpolationMode.HighQuality)
            : new Bitmap(stream);
    }

    /// <summary>The local copy of <paramref name="url"/>, fetched when there isn't one yet.</summary>
    private Task<string?> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        var path = CachePath(url);
        if (File.Exists(path))
        {
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return Task.FromResult<string?>(path);
        }
        lock (_fetching)
        {
            if (!_fetching.TryGetValue(path, out var fetch))
            {
                _fetching[path] = fetch = DownloadAsync(url, path);
                _ = fetch.ContinueWith(_ => { lock (_fetching) _fetching.Remove(path); }, TaskScheduler.Default);
            }
            // The download itself isn't cancelled with one caller (the HTTP timeout bounds it): others may wait for it too.
            return fetch.WaitAsync(cancellationToken);
        }
    }

    private string CachePath(Uri url)
    {
        Directory.CreateDirectory(_directory);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri)))[..32].ToLowerInvariant()
                   + Path.GetExtension(url.AbsolutePath).ToLowerInvariant() switch
                   {
                       ".png" => ".png", ".jpg" or ".jpeg" => ".jpg", _ => ".webp",
                   };
        return Path.Combine(_directory, name);
    }

    private async Task<string?> DownloadAsync(Uri url, string path)
    {
        var cancellationToken = CancellationToken.None;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path)) return path;
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
            var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var target = File.Create(temp))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        total += read;
                        if (total > MaxBytes) return null;
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }
                try { File.Move(temp, path, overwrite: true); }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(path))
                {
                    // Someone else's copy got there first (another Portalkeeper): use that one.
                }
            }
            finally
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            Prune();
            return path;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Prune()
    {
        try
        {
            foreach (var old in new DirectoryInfo(_directory).GetFiles()
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepFiles))
                old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
