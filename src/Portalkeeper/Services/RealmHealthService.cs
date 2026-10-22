using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

public sealed class RealmHealthService
{
    private static readonly TimeSpan ConnectTimeout =
        TimeSpan.FromSeconds(2);

    // Evermore fork: the server rewrites a "booting" status every few seconds. One older than this
    // was left behind by a server that stopped mid-boot, so the ports decide instead.
    private static readonly TimeSpan BootStatusMaxAge =
        TimeSpan.FromMinutes(3);

    private readonly HttpClient _http;

    public RealmHealthService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <param name="bootStatusUrl">
    /// Evermore fork: the realm's realm-status.json, or null. While the login server is unreachable
    /// and that file says the realm is booting, the state is <see cref="RealmHealthState.Booting"/>.
    /// </param>
    public async Task<RealmHealthInfo> CheckAsync(
        RealmInfo realm,
        string? bootStatusUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (!realm.IsConfigured)
        {
            return new RealmHealthInfo
            {
                State = RealmHealthState.Unknown
            };
        }

        var authTask = CanConnectAsync(
            realm.Address,
            realm.AuthPort,
            cancellationToken);

        var worldTask = CanConnectAsync(
            realm.Address,
            realm.WorldPort,
            cancellationToken);

        var bootTask = bootStatusUrl is null
            ? Task.FromResult<RealmBootStatus?>(null)
            : ReadBootStatusAsync(bootStatusUrl, cancellationToken);

        await Task.WhenAll(authTask, worldTask, bootTask);

        var authReachable = await authTask;
        var worldReachable = await worldTask;
        var boot = await bootTask;

        if (!authReachable && boot is not null)
        {
            return new RealmHealthInfo
            {
                State = RealmHealthState.Booting,
                WorldReachable = worldReachable,
                Boot = boot
            };
        }

        var state = (authReachable, worldReachable) switch
        {
            (true, true) => RealmHealthState.Online,
            (true, false) => RealmHealthState.AuthOnly,
            (false, true) => RealmHealthState.WorldOnly,
            _ => RealmHealthState.Offline
        };

        return new RealmHealthInfo
        {
            State = state,
            AuthReachable = authReachable,
            WorldReachable = worldReachable
        };
    }

    /// <summary>The realm's boot status when it says it is booting and is current; otherwise null.</summary>
    private async Task<RealmBootStatus?> ReadBootStatusAsync(
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null; // no file: the realm doesn't have the gate

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            // The server's clock, when the answer says it, so a PC clock that is off doesn't matter.
            var now = response.Headers.Date ?? DateTimeOffset.UtcNow;
            return ParseBootStatus(json, now);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads realm-status.json. Returns the status only when it says "booting" and was written
    /// within the last few minutes of <paramref name="now"/>.
    /// </summary>
    public static RealmBootStatus? ParseBootStatus(string json, DateTimeOffset now)
    {
        RealmBootStatus? status;
        try { status = JsonSerializer.Deserialize<RealmBootStatus>(json); }
        catch (JsonException) { return null; }

        if (status is null || !string.Equals(status.State, "booting", StringComparison.Ordinal))
            return null;

        var age = now - DateTimeOffset.FromUnixTimeSeconds(status.Updated);
        return age <= BootStatusMaxAge && age >= -BootStatusMaxAge ? status : null;
    }

    private static async Task<bool> CanConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            timeout.CancelAfter(ConnectTimeout);

            using var client = new TcpClient();

            await client.ConnectAsync(
                host,
                port,
                timeout.Token);

            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
