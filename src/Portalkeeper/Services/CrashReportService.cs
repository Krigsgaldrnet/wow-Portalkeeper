using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Portalkeeper.Services;

/// <param name="Name">The error file's name in the client's Errors folder.</param>
/// <param name="Text">Its text, with the player's Windows and computer names taken out.</param>
public sealed record CrashReportFile(string Name, string Text);

/// <param name="HappenedAt">When the game wrote the newest error file.</param>
/// <param name="Summary">What went wrong in one line ("ERROR #132 · ACCESS_VIOLATION at 0081D559"), or empty.</param>
public sealed record CrashReport(DateTimeOffset HappenedAt, IReadOnlyList<CrashReportFile> Files, string Summary);

/// <summary>
/// Crash reports: when World of Warcraft crashes it writes an error report into the client's Errors
/// folder. After the game exits, <see cref="Collect"/> picks up the ones written while it ran, and once
/// the player agrees they go to the realm (the portal's api/launcher/crashreport.php). The report's
/// "User:" and "Computer:" lines, the user folder in its paths and the Windows user and computer
/// names anywhere else are taken out before anything leaves the PC.
/// </summary>
public sealed class CrashReportService
{
    public const string Endpoint = "crashreport.php";

    // The portal's limits: a few error files, each cut to this many bytes (the error is at the top).
    public const int MaxFiles = 4;
    public const int MaxFileBytes = 262144;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _http;
    private readonly Uri _api;

    public CrashReportService(Uri? api = null, HttpClient? http = null)
    {
        _api = api ?? RealmBranding.LauncherApi;
        _http = http ?? Http;
    }

    // ---------------------------------------------------------
    // The game's error reports
    // ---------------------------------------------------------

    /// <summary>
    /// The error reports the game wrote since <paramref name="since"/> (the launch), newest first, or
    /// null when there are none: the game didn't crash, or it crashed without writing one.
    /// </summary>
    public static CrashReport? Collect(string clientDirectory, DateTimeOffset since)
    {
        var errors = FindErrorsFolder(clientDirectory);
        if (errors is null) return null;
        // A little slack for file systems that store modification times coarsely.
        var from = since.UtcDateTime.AddSeconds(-5);
        var written = new DirectoryInfo(errors).EnumerateFiles("*.txt")
            .Where(f => f.LastWriteTimeUtc >= from && f.Length > 0)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(MaxFiles)
            .ToArray();
        if (written.Length == 0) return null;

        var files = new List<CrashReportFile>();
        foreach (var file in written)
        {
            // One file the game's error reporter still holds doesn't hide the others.
            try { files.Add(new CrashReportFile(SafeName(file.Name), Cut(Redact(Read(file.FullName))))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (files.Count == 0) return null;
        return new CrashReport(new DateTimeOffset(written[0].LastWriteTimeUtc, TimeSpan.Zero), files,
            Summarize(string.Join("\n", files.Select(f => f.Text))));
    }

    /// <summary>The client's Errors folder, whatever its case on a case-sensitive file system.</summary>
    private static string? FindErrorsFolder(string clientDirectory)
    {
        if (!Directory.Exists(clientDirectory)) return null;
        return Directory.EnumerateDirectories(clientDirectory)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Errors", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>UTF-8 when the file is valid UTF-8, else the Windows code page the game wrote it in.</summary>
    private static string Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return AnsiCodePage().GetString(bytes); }
    }

    /// <summary>The PC's ANSI code page (Cyrillic, Central European, ...), so names in it can be found and taken out.</summary>
    private static Encoding AnsiCodePage()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.Latin1; }
    }

    /// <summary>The name as the portal takes it: letters, digits, spaces and ._-() only, at most 100 characters.</summary>
    private static string SafeName(string name)
    {
        var safe = Regex.Replace(name, @"[^A-Za-z0-9_ .()\-]", "_");
        return safe.Length > 100 ? safe[^100..] : safe;
    }

    private static string Cut(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= MaxFileBytes) return text;
        const string marker = "\n[... the rest was cut by Portalkeeper]";
        var length = Math.Min(text.Length, MaxFileBytes);
        while (length > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, length)) + marker.Length > MaxFileBytes)
            length -= Math.Max(1, length / 20);
        // Never between the two halves of a character outside the BMP: the portal refuses broken UTF-8.
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + marker;
    }

    /// <summary>
    /// Takes the player out of an error report: the "User:" and "Computer:" lines, the folder under
    /// Users\ (or /home/ under Wine) in any path, and the user and computer names wherever else they appear.
    /// </summary>
    public static string Redact(string text, string? userName = null, string? machineName = null)
    {
        userName ??= Environment.UserName;
        machineName ??= Environment.MachineName;
        text = Regex.Replace(text, @"^([ \t]*(?:User|Computer)[ \t]*:).*$", "$1 (removed)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"([\\/](?:Users|home)[\\/])[^\\/\r\n""]+", "$1(user)", RegexOptions.IgnoreCase);
        // Short names ("pc", "me") would also match ordinary words: only names of 3 characters or more.
        // Windows (and Wine) report a computer by its first 15 characters, upper case.
        var names = new[] { userName, machineName, machineName.Length > 15 ? machineName[..15] : null };
        foreach (var name in names.OrderByDescending(n => n?.Length ?? 0))
            if (!string.IsNullOrWhiteSpace(name) && name.Trim().Length >= 3)
                text = Regex.Replace(text, @"\b" + Regex.Escape(name.Trim()) + @"\b", "(removed)", RegexOptions.IgnoreCase);
        return text;
    }

    /// <summary>
    /// "ERROR #132 · ACCESS_VIOLATION at 0081D559" from the report's ERROR and Exception lines, else the
    /// first line that mentions an error, else empty.
    /// </summary>
    public static string Summarize(string text)
    {
        var parts = new List<string>();
        if (Regex.Match(text, @"ERROR\s+#(\d+)") is { Success: true } error)
            parts.Add("ERROR #" + error.Groups[1].Value);
        if (Regex.Match(text, @"Exception:\s*0x[0-9A-Fa-f]+\s*\(([A-Z_]+)\)\s*at\s*[0-9A-Fa-f]+:([0-9A-Fa-f]+)") is { Success: true } exception)
            parts.Add(exception.Groups[1].Value + " at " + exception.Groups[2].Value.ToUpperInvariant());
        if (parts.Count > 0) return string.Join(" · ", parts);
        var line = Regex.Match(text, @"^.*\berror\b.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Value.Trim();
        return line.Length > 160 ? line[..160] : line;
    }

    // ---------------------------------------------------------
    // The portal
    // ---------------------------------------------------------

    /// <summary>
    /// Whether the realm takes crash reports. False when it doesn't (404); throws
    /// <see cref="LauncherApiException"/>, HttpRequestException or TaskCanceledException when it
    /// couldn't be asked.
    /// </summary>
    public async Task<bool> IsOfferedAsync(LauncherSession session, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Get, session.Token);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, cancellationToken);
        return true;
    }

    /// <summary>
    /// Sends a report and returns the summary the portal made of it. Sending the same report again is
    /// harmless: the portal keeps it once. Throws <see cref="LauncherApiException"/> with the portal's
    /// message when it refused (429: enough for today), and HttpRequestException / TaskCanceledException
    /// when it couldn't be reached.
    /// </summary>
    public async Task<string> SendAsync(LauncherSession session, CrashReport report, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Post, session.Token);
        request.Content = JsonContent.Create(new SendRequest(report.HappenedAt.ToUnixTimeSeconds(),
            "Portalkeeper/" + RealmConfigurationService.CurrentVersion,
            report.Files.Select(f => new SendFile(f.Name, f.Text)).ToArray()));
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<SendBody>(cancellationToken)
            ?? throw new InvalidDataException("The realm sent an empty answer.");
        return body.Summary ?? report.Summary;
    }

    private HttpRequestMessage Authorized(HttpMethod method, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(_api, Endpoint));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<LauncherApiException> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? message = null;
        try { message = (await response.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken))?.Error; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        return new LauncherApiException(response.StatusCode, string.IsNullOrWhiteSpace(message)
            ? $"The portal answered {(int)response.StatusCode} {response.ReasonPhrase}."
            : message);
    }

    private sealed record SendFile(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("text")] string Text);
    private sealed record SendRequest(
        [property: JsonPropertyName("happened_at")] long HappenedAt,
        [property: JsonPropertyName("client")] string Client,
        [property: JsonPropertyName("files")] IReadOnlyList<SendFile> Files);
    private sealed record SendBody(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("summary")] string? Summary);
    private sealed record ErrorBody([property: JsonPropertyName("error")] string? Error);
}
