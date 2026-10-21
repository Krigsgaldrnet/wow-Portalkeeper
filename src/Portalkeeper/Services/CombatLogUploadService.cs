using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Portalkeeper.Services;

/// <summary>What the portal takes: the most bytes in one part, and the largest (compressed) log.</summary>
public sealed record CombatLogOffer(int PartSize, long MaxSize);

/// <param name="Uploaded">Logs the portal confirmed (and that are deleted here).</param>
/// <param name="Discarded">Logs deleted without being sent: older than 14 days or too large.</param>
/// <param name="Pending">Logs left for the next run.</param>
public sealed record CombatLogUploadResult(int Uploaded, int Discarded, int Pending)
{
    /// <summary>
    /// The line the player sees once a run is over, or "" when there was nothing to send. A run the
    /// game or Portalkeeper's exit stopped (<paramref name="stopped"/>) says nothing about what's left.
    /// </summary>
    public string Describe(bool stopped = false)
    {
        var sent = Uploaded switch
        {
            0 => "",
            1 => "Combat log sent to the realm ✓",
            _ => $"{Uploaded} combat logs sent to the realm ✓"
        };
        if (stopped || Pending == 0) return sent;
        var left = Pending == 1 ? "1 combat log not sent yet" : $"{Pending} combat logs not sent yet";
        return (sent.Length > 0 ? sent + " · " : "") + left + ", trying again next time";
    }
}

/// <summary>
/// Sharing combat logs with the realm, for players who turn it on in Settings (off by default; the
/// realm tunes its dungeon and raid bots from them). Two halves:
///
/// The launcher writes its own small addon into the client (Interface/AddOns/PortalkeeperCombatLog)
/// while the option is on, and deletes it when it's off. The addon turns combat logging on inside
/// dungeons and raids.
///
/// After the game closes, Logs/WoWCombatLog.txt is moved to Logs/PortalkeeperUpload (so the game
/// starts a new file), gzipped next to it and sent to the portal's combatlog.php in parts. A log is
/// only deleted once the portal confirms it has all of it; anything that fails stays for the next
/// run, which picks up where the portal says it stopped.
/// </summary>
public sealed class CombatLogUploadService
{
    public const string AddonFolder = "PortalkeeperCombatLog";
    public const string LogFileName = "WoWCombatLog.txt";
    public const string PendingFolder = "PortalkeeperUpload";
    public static readonly TimeSpan MaxPendingAge = TimeSpan.FromDays(14);

    private const string PendingPrefix = "WoWCombatLog-";
    private const string SetAsidePrefix = "WoWCombatLog-before-sharing-";
    private const string StampFormat = "yyyyMMdd-HHmmss";
    // The portal names the part size; a part is held in memory, so it's never larger than this.
    private const int LargestPart = 16 << 20;
    // A portal that keeps answering "wrong offset" isn't followed forever.
    private const int MaxOffsetCorrections = 8;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    // A part can take minutes on a home uplink, and finishing makes the portal hash the whole file.
    private static readonly HttpClient UploadHttp = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly HttpClient _http;
    private readonly HttpClient _uploadHttp;
    private readonly Uri _api;
    private readonly Func<DateTimeOffset> _now;

    public CombatLogUploadService(Uri? api = null, HttpClient? http = null, Func<DateTimeOffset>? now = null)
    {
        _api = api ?? RealmBranding.LauncherApi;
        _http = http ?? Http;
        _uploadHttp = http ?? UploadHttp;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    // ---------------------------------------------------------
    // The addon
    // ---------------------------------------------------------

    private static readonly string AddonToc = """
        ## Interface: 30300
        ## Title: Portalkeeper Combat Log
        ## Notes: Turns combat logging on in dungeons and raids. Portalkeeper writes this addon while "Share combat logs with the realm" is on and deletes it when it's off.
        ## Author: Portalkeeper
        PortalkeeperCombatLog.lua

        """.ReplaceLineEndings("\r\n");

    private static readonly string AddonLua = """
        -- Written by Portalkeeper while "Share combat logs with the realm" is on in its Settings, and
        -- deleted by it when the option is off. Changes made here are overwritten.
        --
        -- Turns combat logging on when you enter a dungeon or raid and off again when you leave. A log
        -- you started yourself with /combatlog is left alone, and so is one you stop inside.

        local inside = false
        local startedHere = false

        local function Say(text)
            DEFAULT_CHAT_FRAME:AddMessage("|cffe8b64cPortalkeeper:|r " .. text)
        end

        local frame = CreateFrame("Frame")
        frame:RegisterEvent("PLAYER_ENTERING_WORLD")
        frame:RegisterEvent("ZONE_CHANGED_NEW_AREA")
        frame:SetScript("OnEvent", function()
            local _, instanceType = IsInInstance()
            local now = instanceType == "party" or instanceType == "raid"
            if now == inside then return end
            inside = now
            if now then
                if not LoggingCombat() then
                    LoggingCombat(1)
                    startedHere = true
                    Say("combat logging is on for this dungeon or raid. The log is shared with the realm after you close the game.")
                end
            elseif startedHere then
                startedHere = false
                if LoggingCombat() then
                    LoggingCombat(0)
                    Say("combat logging is off again.")
                end
            end
        end)

        """.ReplaceLineEndings("\r\n");

    /// <summary>
    /// Writes the addon into <paramref name="clientDirectory"/> (the folder the game runs from) when
    /// <paramref name="enabled"/>, and deletes its folder otherwise. The realm's addon list never names
    /// this folder, so nothing else in the launcher installs, checks or removes it.
    /// </summary>
    public static void SyncAddon(string clientDirectory, bool enabled)
    {
        var folder = ManagedPath.Resolve(clientDirectory, Path.Combine("Interface", "AddOns", AddonFolder));
        ManagedRuntimeWriteGuard.Check(clientDirectory, folder);
        if (!enabled)
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            return;
        }
        Directory.CreateDirectory(folder);
        WriteIfChanged(Path.Combine(folder, AddonFolder + ".toc"), AddonToc);
        WriteIfChanged(Path.Combine(folder, AddonFolder + ".lua"), AddonLua);
    }

    private static void WriteIfChanged(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        File.WriteAllText(path, text);
    }

    // ---------------------------------------------------------
    // The portal
    // ---------------------------------------------------------

    /// <summary>
    /// Asks whether the realm collects combat logs. Null when it doesn't (404, or not enabled). Throws
    /// when the portal couldn't be asked or refused the login (401/403, as session.php does): the
    /// caller then keeps what it knew.
    /// </summary>
    public async Task<CombatLogOffer?> GetOfferAsync(LauncherSession session, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Get, "combatlog.php", session.Token);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<OfferBody>(cancellationToken);
        if (body is not { Enabled: true }) return null;
        if (body.PartSize < 1 || body.MaxSize < 1)
            throw new InvalidDataException("The portal's combat log limits make no sense.");
        return new CombatLogOffer((int)Math.Min(body.PartSize, LargestPart), body.MaxSize);
    }

    /// <summary>
    /// Sends every pending log, oldest first, after moving the game's current log in with them
    /// (<paramref name="collectLog"/>: not while a game may still be writing it). Failures are quiet:
    /// the result says what is left. <paramref name="status"/> gets a line such as
    /// "Uploading combat log... 40%", and nothing when there is nothing to send.
    /// </summary>
    public async Task<CombatLogUploadResult> UploadPendingAsync(string clientDirectory, LauncherSession session, CombatLogOffer offer,
        IProgress<string>? status = null, CancellationToken cancellationToken = default, bool collectLog = true)
    {
        int uploaded = 0, discarded = 0;
        string? pending = null;
        try
        {
            if (collectLog) CollectLog(clientDirectory);
            pending = Path.Combine(LogsDirectory(clientDirectory), PendingFolder);
            if (!Directory.Exists(pending)) return new(0, 0, 0);
            discarded += Tidy(pending);
            var logs = PendingLogs(pending);
            for (var index = 0; index < logs.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var label = logs.Length == 1 ? "combat log" : $"combat log {index + 1} of {logs.Length}";
                switch (await UploadAsync(logs[index], session, offer, label, status, cancellationToken))
                {
                    case Outcome.Uploaded: uploaded++; break;
                    case Outcome.Discarded: discarded++; break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The game is starting or Portalkeeper is closing: the next run carries on from here.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException
            or JsonException or InvalidDataException or LauncherApiException)
        {
            // Offline, the portal is down or refused: the logs stay where they are for the next run.
        }
        var left = 0;
        try { if (pending is not null && Directory.Exists(pending)) left = PendingLogs(pending).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new(uploaded, discarded, left);
    }

    /// <summary>
    /// Moves the game's combat log into the pending folder under a name with the current UTC time, so
    /// the game starts a new file. Returns the new path, or null when there's no log, it's empty, or it
    /// can't be moved (the game still has it open): then it's picked up next time.
    /// </summary>
    public string? CollectLog(string clientDirectory)
    {
        try
        {
            var logs = LogsDirectory(clientDirectory);
            return CurrentLog(logs) is { } log ? MoveLog(log, Path.Combine(logs, PendingFolder), PendingPrefix) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Renames a combat log that was there before the player turned sharing on (it stays in Logs as
    /// WoWCombatLog-before-sharing-...txt), so only what is logged from then on is uploaded and nothing
    /// they logged earlier is sent or deleted. True when no earlier log is left in the game's way (none,
    /// empty, or renamed); false when it couldn't be renamed yet (the game still has it open).
    /// </summary>
    public bool SetAsideLog(string clientDirectory)
    {
        try
        {
            var logs = LogsDirectory(clientDirectory);
            if (CurrentLog(logs) is { } log) MoveLog(log, logs, SetAsidePrefix);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The game's combat log in <paramref name="logs"/>, or null when there is none or it's empty.</summary>
    private static string? CurrentLog(string logs)
    {
        if (!Directory.Exists(logs)) return null;
        var log = Directory.EnumerateFiles(logs)
            .FirstOrDefault(f => Path.GetFileName(f).Equals(LogFileName, StringComparison.OrdinalIgnoreCase));
        return log is null || new FileInfo(log).Length == 0 ? null : log;
    }

    private string MoveLog(string log, string folder, string prefix)
    {
        Directory.CreateDirectory(folder);
        var name = prefix + _now().UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture);
        var target = Path.Combine(folder, name + ".txt");
        for (var copy = 2; File.Exists(target); copy++)
            target = Path.Combine(folder, $"{name}-{copy}.txt");
        File.Move(log, target);
        return target;
    }

    /// <summary>
    /// Deletes pending logs queued more than 14 days ago, and what an interrupted run left behind (a
    /// half-written gzip, a gzip whose log is gone). Returns how many logs were deleted. Only files this
    /// service named are touched.
    /// </summary>
    public int DeleteExpired(string clientDirectory)
    {
        try
        {
            var pending = Path.Combine(LogsDirectory(clientDirectory), PendingFolder);
            return Directory.Exists(pending) ? Tidy(pending) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private int Tidy(string pending)
    {
        foreach (var file in Directory.EnumerateFiles(pending, PendingPrefix + "*").ToArray())
        {
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || (file.EndsWith(".txt.gz", StringComparison.OrdinalIgnoreCase) && !File.Exists(file[..^3])))
                TryDelete(file);
        }
        var expired = 0;
        foreach (var log in PendingLogs(pending))
        {
            if (_now() - QueuedAt(log) <= MaxPendingAge) continue;
            TryDelete(log + ".gz");
            if (TryDelete(log)) expired++;
        }
        return expired;
    }

    /// <summary>The pending logs, oldest first (their names carry the UTC time they were queued).</summary>
    private static string[] PendingLogs(string pending) => Directory.EnumerateFiles(pending, PendingPrefix + "*")
        .Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
        .ToArray();

    private static DateTimeOffset QueuedAt(string log)
    {
        var name = Path.GetFileName(log);
        if (name.Length >= PendingPrefix.Length + StampFormat.Length
            && DateTime.TryParseExact(name.AsSpan(PendingPrefix.Length, StampFormat.Length), StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp))
            return new DateTimeOffset(stamp, TimeSpan.Zero);
        return new DateTimeOffset(File.GetLastWriteTimeUtc(log), TimeSpan.Zero);
    }

    /// <summary>The client's Logs folder. Under Wine it may exist in another case; the game uses the one that's there.</summary>
    private static string LogsDirectory(string clientDirectory)
    {
        var root = Path.GetFullPath(clientDirectory);
        var existing = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).FirstOrDefault(d => Path.GetFileName(d).Equals("Logs", StringComparison.OrdinalIgnoreCase))
            : null;
        return existing ?? Path.Combine(root, "Logs");
    }

    private enum Outcome { Uploaded, Discarded, Kept }

    private async Task<Outcome> UploadAsync(string log, LauncherSession session, CombatLogOffer offer, string label,
        IProgress<string>? status, CancellationToken cancellationToken)
    {
        var gzip = log + ".gz";
        var info = new FileInfo(log);
        if (info.Length == 0)
        {
            Delete(log, gzip);
            return Outcome.Discarded;
        }

        // A gzip is only ever there complete (it's written under another name first). Keeping it
        // between runs keeps its hash the same, which is how the portal knows an upload to continue.
        if (!File.Exists(gzip))
        {
            status?.Report($"Preparing {label}...");
            await CompressAsync(log, gzip, cancellationToken);
        }
        var size = new FileInfo(gzip).Length;
        if (size > offer.MaxSize)
        {
            Delete(log, gzip);
            return Outcome.Discarded;
        }
        string sha256;
        await using (var stream = new FileStream(gzip, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));

        // The log's lines carry local time without a year: the portal dates them from when the log
        // was last written and the PC's offset from UTC at that moment.
        var loggedUntil = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        BeginBody begin;
        using (var request = Authorized(HttpMethod.Post, "combatlog.php?action=begin", session.Token))
        {
            request.Content = JsonContent.Create(new BeginRequest(size, sha256, info.Length, loggedUntil.ToUnixTimeSeconds(),
                (int)TimeZoneInfo.Local.GetUtcOffset(loggedUntil).TotalMinutes, "Portalkeeper/" + RealmConfigurationService.CurrentVersion));
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                Delete(log, gzip);
                return Outcome.Discarded;
            }
            if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, cancellationToken);
            begin = await response.Content.ReadFromJsonAsync<BeginBody>(cancellationToken)
                ?? throw new InvalidDataException("The portal sent an empty answer.");
        }
        if (begin.Complete)
        {
            // This exact file is already there (the answer to an earlier finish never arrived).
            Delete(log, gzip);
            return Outcome.Uploaded;
        }
        if (begin.Upload is not { Length: 32 } upload || !upload.All(Uri.IsHexDigit))
            throw new InvalidDataException("The portal sent no upload to continue.");
        var partSize = (int)Math.Min(begin.PartSize > 0 ? begin.PartSize : offer.PartSize, LargestPart);
        var offset = begin.Received;
        if (offset < 0 || offset > size)
            throw new InvalidDataException("The portal has more of this log than there is.");

        var buffer = new byte[(int)Math.Min(partSize, size)];
        var corrections = 0;
        await using (var input = new FileStream(gzip, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
        {
            while (offset < size)
            {
                status?.Report($"Uploading {label}... {offset * 100 / size}%");
                input.Position = offset;
                var count = (int)Math.Min(buffer.Length, size - offset);
                await input.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
                using var request = Authorized(HttpMethod.Post, $"combatlog.php?action=part&upload={upload}&offset={offset}", session.Token);
                request.Content = new ByteArrayContent(buffer, 0, count);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var response = await _uploadHttp.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    // The portal is somewhere else in the file (a part arrived whose answer didn't).
                    var conflict = await response.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken);
                    if (conflict?.Received is not { } has || has < 0 || has > size || ++corrections > MaxOffsetCorrections)
                        throw new InvalidDataException("The portal and Portalkeeper disagree about this upload.");
                    offset = has;
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, cancellationToken);
                var part = await response.Content.ReadFromJsonAsync<PartBody>(cancellationToken);
                if (part is null || part.Received <= offset || part.Received > size)
                    throw new InvalidDataException("The portal didn't take the part.");
                offset = part.Received;
            }
        }
        status?.Report($"Uploading {label}... 100%");

        using (var request = Authorized(HttpMethod.Post, "combatlog.php?action=finish&upload=" + upload, session.Token))
        using (var response = await _uploadHttp.SendAsync(request, cancellationToken))
        {
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                // Size or hash didn't match and the portal threw it away. The gzip is made again from
                // the log, and the next run starts this one from the beginning.
                TryDelete(gzip);
                return Outcome.Kept;
            }
            if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, cancellationToken);
            var finish = await response.Content.ReadFromJsonAsync<FinishBody>(cancellationToken);
            if (finish is not { Complete: true })
                throw new InvalidDataException("The portal didn't confirm the upload.");
        }
        Delete(log, gzip);
        return Outcome.Uploaded;
    }

    /// <summary>Streams the log through gzip into a temp file next to it, renamed once it's whole.</summary>
    private static async Task CompressAsync(string log, string gzip, CancellationToken cancellationToken)
    {
        var temp = gzip + ".tmp";
        try
        {
            await using (var input = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await using (var compressed = new GZipStream(output, CompressionLevel.Optimal))
                await input.CopyToAsync(compressed, cancellationToken);
            File.Move(temp, gzip, true);
        }
        finally { TryDelete(temp); }
    }

    private static void Delete(string log, string gzip)
    {
        // The log first: if only the gzip went, the next run would compress and offer the log again.
        File.Delete(log);
        TryDelete(gzip);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private HttpRequestMessage Authorized(HttpMethod method, string relative, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(_api, relative));
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

    private sealed record OfferBody(
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("part_size")] long PartSize,
        [property: JsonPropertyName("max_size")] long MaxSize);
    private sealed record BeginRequest(
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("raw_size")] long RawSize,
        [property: JsonPropertyName("logged_until")] long LoggedUntil,
        [property: JsonPropertyName("utc_offset_minutes")] int UtcOffsetMinutes,
        [property: JsonPropertyName("client")] string Client);
    private sealed record BeginBody(
        [property: JsonPropertyName("upload")] string? Upload,
        [property: JsonPropertyName("received")] long Received,
        [property: JsonPropertyName("part_size")] long PartSize,
        [property: JsonPropertyName("complete")] bool Complete);
    private sealed record PartBody([property: JsonPropertyName("received")] long Received);
    private sealed record FinishBody([property: JsonPropertyName("complete")] bool Complete);
    private sealed record ErrorBody(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("received")] long? Received);
}
