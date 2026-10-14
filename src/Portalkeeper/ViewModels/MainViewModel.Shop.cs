using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

// Evermore fork: the coin shop (CoinShopService, ShopWindow). The SHOP link shows once the portal
// has answered a listing for this login; a realm without a shop answers 404 and the link stays
// hidden. A portal that can't be asked, or a closed shop (503), is asked again after the next sync,
// and by the realm status check while the world server is up, at most every 5 minutes: a closed
// shop's listing isn't cached by the portal and costs the world server a look every time.
public sealed partial class MainViewModel
{
    private readonly CoinShopService _coinShop = new();
    private static readonly TimeSpan ShopRetryInterval = TimeSpan.FromMinutes(5);
    private bool _shopOffered;
    private bool _shopKnown;
    private LauncherSession? _shopProbeSession;
    private DateTimeOffset _shopRetryAfter;

    // The portal is the built-in realm's: another realm's players have no wallet there.
    private bool ShopApplies => IsLoggedIn && RealmBranding.IsBuiltIn(_realmInfo);

    public bool ShopAvailable => ShopApplies && _shopOffered;

    /// <summary>
    /// Once per login, after a sync: does this realm have a shop? <paramref name="retry"/> (the realm
    /// status check) asks again only when the last try is <see cref="ShopRetryInterval"/> old.
    /// </summary>
    private async Task ProbeShopAsync(bool retry = false)
    {
        var session = _session;
        if (session is null || _shopKnown || ReferenceEquals(_shopProbeSession, session) || !ShopApplies) return;
        if (retry && DateTimeOffset.UtcNow < _shopRetryAfter) return;
        _shopProbeSession = session;
        _shopRetryAfter = DateTimeOffset.UtcNow + ShopRetryInterval;
        try
        {
            var listing = await _coinShop.GetListingAsync(session);
            if (!ReferenceEquals(session, _session)) return;
            _shopOffered = listing is not null;
            _shopKnown = true;
        }
        catch (LauncherApiException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Login trouble is the login's business (session.php at start-up); no link meanwhile.
            if (ReferenceEquals(session, _session)) _shopOffered = false;
        }
        catch (Exception ex) when (ex is LauncherApiException or HttpRequestException or OperationCanceledException
            or JsonException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            // Closed or unreachable right now: what was known stays, and the next sync asks again.
        }
        finally
        {
            if (ReferenceEquals(_shopProbeSession, session)) _shopProbeSession = null;
            OnPropertyChanged(nameof(ShopAvailable));
        }
    }

    private void ForgetShop()
    {
        _shopOffered = false;
        _shopKnown = false;
        _shopRetryAfter = default;
        OnPropertyChanged(nameof(ShopAvailable));
    }

    /// <summary>A Shop window's view model for the current login, or null when there's no shop to open.</summary>
    public ShopViewModel? CreateShop() =>
        _session is { } session && ShopAvailable ? new ShopViewModel(_coinShop, session) : null;

    /// <summary>The shop said the login is no longer good: back to the login screen, with its message.</summary>
    public async Task ShopLoginExpiredAsync(string message)
    {
        if (_session is null) return;
        // Before logging out: the login screen shows with it, and a quick new login clears it.
        _loginError = string.IsNullOrWhiteSpace(message) ? "Your login has expired. Log in again." : message;
        await LogOutAsync();
    }
}
