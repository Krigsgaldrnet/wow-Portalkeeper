using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Portalkeeper.Services;

/// <param name="Generated">When the portal put this together (it keeps one answer for every launcher for about 30 seconds).</param>
public sealed record RealmLive(
    [property: JsonPropertyName("generated")] long Generated,
    [property: JsonPropertyName("players")] RealmLivePlayers? Players,
    [property: JsonPropertyName("server")] RealmLiveServer? Server,
    [property: JsonPropertyName("events")] IReadOnlyList<RealmLiveEvent>? Events,
    [property: JsonPropertyName("rares")] RealmLiveRares? Rares)
{
    /// <summary>When the world server goes down, if it is counting down to a shutdown or restart.</summary>
    [JsonIgnore]
    public DateTimeOffset? RestartAt => Server?.RestartIn is { } seconds
        ? DateTimeOffset.FromUnixTimeSeconds(Generated).AddSeconds(seconds)
        : null;
}

public sealed record RealmLivePlayers(
    [property: JsonPropertyName("online")] int Online,
    [property: JsonPropertyName("bots")] int Bots);

/// <param name="RestartIn">Seconds left until a shutdown or restart (from <see cref="RealmLive.Generated"/>), or null.</param>
public sealed record RealmLiveServer(
    [property: JsonPropertyName("uptime")] long Uptime,
    [property: JsonPropertyName("restart_in")] long? RestartIn);

public sealed record RealmLiveEvent(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record RealmLiveRares(
    [property: JsonPropertyName("up")] int Up,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("world_bosses")] IReadOnlyList<RealmLiveBoss> WorldBosses);

public sealed record RealmLiveBoss(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("zone")] string Zone,
    [property: JsonPropertyName("up")] bool Up,
    [property: JsonPropertyName("respawn_at")] long? RespawnAt);

/// <param name="Kind">"feature" (something new), "change" or "fix".</param>
/// <param name="Title">A new feature's name; null for changes and fixes.</param>
/// <param name="Text">The note's lines, as plain text.</param>
public sealed record PatchNote(
    [property: JsonPropertyName("date")] DateTimeOffset Date,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("section")] string Section,
    [property: JsonPropertyName("sub")] string? Sub,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("text")] IReadOnlyList<string> Text,
    [property: JsonPropertyName("url")] string Url);

/// <param name="Url">The patch notes page, for everything that didn't fit.</param>
/// <param name="More">How many more notes there are past the ones asked for.</param>
public sealed record WhatsNew(
    [property: JsonPropertyName("since")] DateTimeOffset Since,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("notes")] IReadOnlyList<PatchNote> Notes,
    [property: JsonPropertyName("more")] int More);

/// <summary>
/// The realm as it is now and what changed lately, for the home screen: the portal's
/// api/launcher/live.php (players online, a pending restart, events on, rares up) and
/// api/launcher/whatsnew.php (patch notes since a time). Neither needs the login.
/// </summary>
public sealed class RealmLiveService
{
    public const string LiveEndpoint = "live.php";
    public const string WhatsNewEndpoint = "whatsnew.php";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly HttpClient _http;
    private readonly Uri _api;

    public RealmLiveService(Uri? api = null, HttpClient? http = null)
    {
        _api = api ?? RealmBranding.LauncherApi;
        _http = http ?? Http;
    }

    /// <summary>
    /// What is going on now. Null when the portal doesn't offer it (404, an older portal); throws
    /// HttpRequestException, TaskCanceledException, JsonException or InvalidDataException when it
    /// couldn't be asked.
    /// </summary>
    public Task<RealmLive?> GetLiveAsync(CancellationToken cancellationToken = default) =>
        GetAsync<RealmLive>(LiveEndpoint, cancellationToken);

    /// <summary>
    /// The patch notes merged after <paramref name="since"/> (the portal's default, the last two weeks,
    /// when it's null), newest first and at most <paramref name="limit"/>. Null when the realm has no
    /// patch notes or doesn't offer them, and while the portal hasn't built them yet (503).
    /// </summary>
    public async Task<WhatsNew?> GetWhatsNewAsync(DateTimeOffset? since, int limit, CancellationToken cancellationToken = default)
    {
        var query = "?limit=" + limit.ToString(CultureInfo.InvariantCulture);
        if (since is { } time) query += "&since=" + time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return await GetAsync<WhatsNew>(WhatsNewEndpoint + query, cancellationToken);
    }

    private async Task<T?> GetAsync<T>(string relative, CancellationToken cancellationToken) where T : class
    {
        using var response = await _http.GetAsync(new Uri(_api, relative), cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable) return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The portal answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidDataException("The portal sent an empty answer.");
    }
}
