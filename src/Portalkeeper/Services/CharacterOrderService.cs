using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Portalkeeper.Services;

/// <param name="LastPlayed">When it last logged out, or null when it never played.</param>
/// <param name="Order">Its place in the saved order, or null when the player never placed it (the portal lists those last).</param>
public sealed record OrderedCharacter(uint Guid, string Name, int Race, int Class, int Gender, int Level,
    DateTimeOffset? LastPlayed, bool Online, int? Order);

public enum CharacterOrderOutcome
{
    /// <summary>Saved; <see cref="CharacterOrderResult.Characters"/> is the list in its new order.</summary>
    Saved,
    /// <summary>The portal said no (409: a character that isn't the account's, a character twice). Nothing changed.</summary>
    Refused,
    /// <summary>Turned away before anything changed (400, 429, 503, no connection).</summary>
    NotTried,
    /// <summary>The login is no longer good (401).</summary>
    LoggedOut,
    /// <summary>The realm has no character order (404).</summary>
    Unavailable,
    /// <summary>No answer (502, a lost connection): it may or may not have been saved.</summary>
    Unknown,
    /// <summary>An order from the game that doesn't fit the realm's list any more (another count of characters): dropped.</summary>
    Stale,
}

/// <summary>
/// An order the character select screen left: <paramref name="Positions"/> are 1-based places in the
/// realm's list of <paramref name="Count"/> characters, top first; characters it leaves out go after them.
/// </summary>
public sealed record PendingCharacterPositions(int Count, IReadOnlyList<int> Positions);

/// <param name="Code">The portal's reason for a refusal (409: not_yours, duplicate), or null.</param>
/// <param name="Order">The guids an order from the game came to, once they're known (to keep for another try).</param>
public sealed record CharacterOrderResult(CharacterOrderOutcome Outcome, string Message,
    IReadOnlyList<OrderedCharacter>? Characters = null, string? Code = null, IReadOnlyList<uint>? Order = null);

/// <summary>
/// Evermore fork: the order of the account's characters on the realm's character select screen (the
/// portal's api/launcher/characters.php, stored in characters.order). The list comes in display order;
/// a save sends the account's characters by guid in the order wanted, and characters it leaves out
/// go after them. The game's own character select screen can reorder too: it can't send that to the
/// realm, so it leaves the order in Config.wtf's accountList (<see cref="OrderPrefix"/>) for the
/// launcher to send.
/// </summary>
public sealed class CharacterOrderService
{
    public const string Endpoint = "characters.php";
    /// <summary>
    /// The Config.wtf CVar the character select screen leaves an order in. It's one the glue screens
    /// can write that the client saves on exit; the launcher's login handoff uses it too (a
    /// "portalkeeper:" value), and the realm's login screen neither shows nor clears an order.
    /// </summary>
    public const string ConfigSetting = "accountList";
    /// <summary>An order in accountList: "evermoreorder:ACCOUNT:COUNT:p1,p2,...".</summary>
    public const string OrderPrefix = "evermoreorder:";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _http;
    private readonly Uri _api;

    public CharacterOrderService(Uri? api = null, HttpClient? http = null)
    {
        _api = api ?? RealmBranding.LauncherApi;
        _http = http ?? Http;
    }

    /// <summary>
    /// The account's characters in display order. Null when the realm has no character order (404).
    /// Throws <see cref="LauncherApiException"/> with the portal's message for anything else it refused
    /// (401: the login expired; 503: not set up), and HttpRequestException / TaskCanceledException /
    /// JsonException when it couldn't be asked.
    /// </summary>
    public async Task<IReadOnlyList<OrderedCharacter>?> GetCharactersAsync(LauncherSession session, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Get, session.Token);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadAsync(response, cancellationToken);
            var message = Message(error, "");
            throw new LauncherApiException(response.StatusCode, message.Length > 0
                ? message
                : $"The character list answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        var body = await response.Content.ReadFromJsonAsync<ListBody>(cancellationToken)
            ?? throw new InvalidDataException("The character list was empty.");
        return Parse(body);
    }

    /// <summary>
    /// Saves <paramref name="guids"/> as the account's order. Never throws for an answer or a lost
    /// connection: the result says what happened.
    /// </summary>
    public async Task<CharacterOrderResult> SaveAsync(LauncherSession session, IReadOnlyList<uint> guids,
        CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            using var request = Authorized(HttpMethod.Post, session.Token);
            request.Content = JsonContent.Create(new SaveRequest(guids.Distinct().ToArray()));
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError)
        {
            return new CharacterOrderResult(CharacterOrderOutcome.NotTried, "Couldn't reach the realm. The order wasn't saved; try again in a moment.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return new CharacterOrderResult(CharacterOrderOutcome.Unknown,
                "The realm didn't answer. The order may or may not have been saved; the page shows what the realm has now.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.OK)
            {
                ListBody? list = null;
                try { list = await response.Content.ReadFromJsonAsync<ListBody>(cancellationToken); }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException) { }
                return list?.Characters is not null
                    ? new CharacterOrderResult(CharacterOrderOutcome.Saved, "Character order saved.", Parse(list))
                    : new CharacterOrderResult(CharacterOrderOutcome.Unknown,
                        "The realm sent an answer Portalkeeper doesn't understand. The page shows what the realm has now.");
            }

            var body = await ReadAsync(response, cancellationToken);
            var code = (body?.Error ?? "").Trim();
            return response.StatusCode switch
            {
                HttpStatusCode.Conflict => new CharacterOrderResult(CharacterOrderOutcome.Refused, RefusalMessage(code, body?.Message),
                    null, code.Length > 0 ? code : null),
                HttpStatusCode.Unauthorized => new CharacterOrderResult(CharacterOrderOutcome.LoggedOut,
                    Message(body, "Your login has expired. Log in again.")),
                HttpStatusCode.NotFound => new CharacterOrderResult(CharacterOrderOutcome.Unavailable,
                    "This realm doesn't keep a character order."),
                HttpStatusCode.TooManyRequests => new CharacterOrderResult(CharacterOrderOutcome.NotTried,
                    Message(body, "The last order is still being saved. Wait a moment, then save again.")),
                HttpStatusCode.ServiceUnavailable => new CharacterOrderResult(CharacterOrderOutcome.NotTried,
                    Message(body, "The realm can't save a character order right now. Try again later.")),
                HttpStatusCode.BadRequest => new CharacterOrderResult(CharacterOrderOutcome.NotTried,
                    Message(body, "The realm didn't understand that order. Nothing was saved.")),
                >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError => new CharacterOrderResult(CharacterOrderOutcome.NotTried,
                    Message(body, $"The realm answered {(int)response.StatusCode} {response.ReasonPhrase}. Nothing was saved.")),
                // A 502 or a proxy's 504 could have come after the order was written.
                _ => new CharacterOrderResult(CharacterOrderOutcome.Unknown,
                    Message(body, $"The realm answered {(int)response.StatusCode} {response.ReasonPhrase}. The order may or may not have been saved.")),
            };
        }
    }

    /// <summary>
    /// Sends an order left by the character select screen: <paramref name="order"/>'s positions mapped
    /// to the guids in the realm's list (the same list, in the same order, as the game's). Stale when
    /// the list doesn't fit it any more; Unavailable on a realm without character order. Once the
    /// guids are known the result names them (<see cref="CharacterOrderResult.Order"/>).
    /// </summary>
    public async Task<CharacterOrderResult> SendPendingAsync(LauncherSession session, PendingCharacterPositions order,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OrderedCharacter>? characters;
        try { characters = await GetCharactersAsync(session, cancellationToken); }
        catch (LauncherApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            return new CharacterOrderResult(CharacterOrderOutcome.LoggedOut, ex.Message);
        }
        catch (Exception ex) when (ex is LauncherApiException or HttpRequestException or JsonException or InvalidDataException
            or NotSupportedException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new CharacterOrderResult(CharacterOrderOutcome.NotTried, UserErrorService.Format(ex, "Couldn't ask for the character list"));
        }
        if (characters is null) return new CharacterOrderResult(CharacterOrderOutcome.Unavailable, "This realm doesn't keep a character order.");
        var guids = GuidsFor(order, characters);
        if (guids is null)
            return new CharacterOrderResult(CharacterOrderOutcome.Stale,
                "The order from the game doesn't fit your characters any more (one was made or deleted since).", characters);
        return await SaveAsync(session, guids, cancellationToken) with { Order = guids };
    }

    /// <summary>
    /// The order in an accountList value the character select screen left for <paramref name="account"/>
    /// ("evermoreorder:ACCOUNT:COUNT:p1,p2,..."; the account is compared without case). Null when
    /// there's nothing for this account: empty, a login handoff, another account's, no positions, or
    /// positions that can't be (twice, or outside 1..COUNT).
    /// </summary>
    public static PendingCharacterPositions? ParsePending(string? value, string? account)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrWhiteSpace(account) || !value.StartsWith(OrderPrefix, StringComparison.Ordinal)) return null;
        var parts = value[OrderPrefix.Length..].Split(':');
        if (parts.Length != 3 || !string.Equals(parts[0].Trim(), account.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        if (!int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
            || count < 1 || count > 255)
            return null;
        var positions = new List<int>();
        foreach (var text in parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var position)
                || position < 1 || position > count || positions.Contains(position))
                return null;
            positions.Add(position);
        }
        return positions.Count == 0 ? null : new PendingCharacterPositions(count, positions);
    }

    /// <summary>
    /// The guids at <paramref name="order"/>'s positions in the realm's list, in that order. Null when
    /// the list isn't the one the order was made from (another length) or a position doesn't fit.
    /// </summary>
    public static IReadOnlyList<uint>? GuidsFor(PendingCharacterPositions order, IReadOnlyList<OrderedCharacter> characters)
    {
        if (characters.Count != order.Count || order.Positions.Count == 0) return null;
        if (order.Positions.Any(p => p < 1 || p > characters.Count) || order.Positions.Distinct().Count() != order.Positions.Count) return null;
        return order.Positions.Select(p => characters[p - 1].Guid).ToArray();
    }

    /// <summary>
    /// What to send for <paramref name="account"/>: an order in Config.wtf (<paramref name="fromConfig"/>),
    /// being newer, wins over the launcher's unsent copy (<paramref name="kept"/>), which only counts
    /// when it's this account's. Null when there's neither.
    /// </summary>
    public static PendingCharacterOrder? PendingOrder(PendingCharacterPositions? fromConfig, PendingCharacterOrder? kept, string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return null;
        if (fromConfig is not null)
            return new PendingCharacterOrder { Account = account.Trim(), Count = fromConfig.Count, Positions = fromConfig.Positions.ToList() };
        if (kept is null || !string.Equals((kept.Account ?? "").Trim(), account.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        var guids = (kept.Guids ?? []).Where(g => g > 0).Distinct().ToList();
        if (guids.Count > 0) return new PendingCharacterOrder { Account = kept.Account!.Trim(), Guids = guids };
        var positions = kept.Positions ?? [];
        return kept.Count > 0 && positions.Count > 0
            ? new PendingCharacterOrder { Account = kept.Account!.Trim(), Count = kept.Count, Positions = positions.ToList() }
            : null;
    }

    /// <summary>The portal's message for a refusal, or one for its code when it sent none.</summary>
    public static string RefusalMessage(string? code, string? message)
    {
        if (!string.IsNullOrWhiteSpace(message)) return message.Trim();
        return (code ?? "").Trim() switch
        {
            "not_yours" => "A character in that order isn't on your account any more. The page shows what the realm has now.",
            "duplicate" => "That order lists a character twice.",
            _ => "The realm refused that order.",
        };
    }

    private static IReadOnlyList<OrderedCharacter> Parse(ListBody body)
    {
        var seen = new HashSet<uint>();
        return (body.Characters ?? []).OfType<CharacterBody>().Where(c => c.Guid > 0 && seen.Add(c.Guid))
            .Select(c => new OrderedCharacter(c.Guid, (c.Name ?? "").Trim(), c.Race, c.Class, c.Gender, c.Level,
                c.LastPlayed is > 0 and < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(c.LastPlayed.Value) : null,
                c.Online, c.Order))
            .ToArray();
    }

    /// <summary>The portal's message, else its error when that's a sentence rather than a code.</summary>
    private static string Message(ErrorBody? body, string fallback)
    {
        var message = (body?.Message ?? "").Trim();
        if (message.Length > 0) return message;
        var error = (body?.Error ?? "").Trim();
        return error.Length > 0 && !error.All(c => c is >= 'a' and <= 'z' or '_') ? error : fallback;
    }

    private static async Task<ErrorBody?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try { return await response.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException) { return null; }
    }

    private HttpRequestMessage Authorized(HttpMethod method, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(_api, Endpoint));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private sealed record CharacterBody(
        [property: JsonPropertyName("guid")] uint Guid,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("race")] int Race,
        [property: JsonPropertyName("class")] int Class,
        [property: JsonPropertyName("gender")] int Gender,
        [property: JsonPropertyName("level")] int Level,
        [property: JsonPropertyName("lastPlayed")] long? LastPlayed,
        [property: JsonPropertyName("online")] bool Online,
        [property: JsonPropertyName("order")] int? Order);
    private sealed record ListBody(
        [property: JsonPropertyName("characters")] CharacterBody?[]? Characters);
    private sealed record SaveRequest(
        [property: JsonPropertyName("order")] uint[] Order);
    private sealed record ErrorBody(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("message")] string? Message);
}
