using System.Text.Json.Serialization;

namespace Portalkeeper.Models;

public enum RealmHealthState
{
    Unknown = 0,
    Checking = 1,
    Online = 2,
    AuthOnly = 3,
    WorldOnly = 4,
    Offline = 5,
    // Evermore fork: login is still closed because the realm is starting up (realm-status.json).
    Booting = 6
}

public sealed class RealmHealthInfo
{
    public RealmHealthState State { get; init; }
    public bool AuthReachable { get; init; }
    public bool WorldReachable { get; init; }
    /// <summary>How far the boot has got, while <see cref="State"/> is <see cref="RealmHealthState.Booting"/>.</summary>
    public RealmBootStatus? Boot { get; init; }
}

/// <summary>
/// Evermore fork: the server's realm-status.json (wow-server's realm-gate.sh), which holds the
/// login server back until the world server is up and its bots have logged in.
/// </summary>
/// <param name="State">"booting", "online" or "offline".</param>
/// <param name="Stage">While booting: "world" (the world server is loading) or "bots" (bots are logging in).</param>
/// <param name="Since">When this boot started (Unix seconds, server clock).</param>
/// <param name="Updated">When the file was written; rewritten every few seconds while booting.</param>
public sealed record RealmBootStatus(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("stage")] string? Stage,
    [property: JsonPropertyName("bots_online")] int BotsOnline,
    [property: JsonPropertyName("bots_target")] int BotsTarget,
    [property: JsonPropertyName("since")] long Since,
    [property: JsonPropertyName("updated")] long Updated);
