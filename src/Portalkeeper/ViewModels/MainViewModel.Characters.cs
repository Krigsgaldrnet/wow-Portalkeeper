using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

// Evermore fork: the character order (CharacterOrderService). The CHARACTERS page (MainViewModel.Pages.cs)
// is shown once the portal has answered a list for this login, like LEGACY: a realm without it
// answers 404 and the link stays hidden. The game's character select screen reorders too, but it
// can't reach the realm: it leaves the order in Config.wtf's accountList, and the launcher sends it
// before the next launch and after the game exits, then empties it; until the realm has it the
// launcher keeps its own copy in its settings (the login handoff overwrites accountList). Sending it never holds up a launch for long.
public sealed partial class MainViewModel
{
    /// <summary>How long ENTER REALM waits for an order the game left to be sent; after that it goes next time.</summary>
    private static readonly TimeSpan PendingOrderLaunchWait = TimeSpan.FromSeconds(5);

    private readonly CharacterOrderService _characterOrderService = new();
    private readonly SemaphoreSlim _pendingOrderLock = new(1, 1);
    private bool _charactersOffered;
    private bool _charactersKnown;
    private LauncherSession? _charactersProbeSession;
    private DateTimeOffset _charactersRetryAfter;
    private CharactersViewModel? _characters;

    private bool CharactersApply => IsLoggedIn && RealmBranding.IsBuiltIn(_realmInfo);

    public bool CharactersAvailable => CharactersApply && _charactersOffered;

    public CharactersViewModel? Characters => _characters;

    /// <summary>Once per login, after a sync: does this realm keep a character order? Like <see cref="ProbeLegacyAsync"/>.</summary>
    private async Task ProbeCharactersAsync(bool retry = false)
    {
        var session = _session;
        if (session is null || _charactersKnown || ReferenceEquals(_charactersProbeSession, session) || !CharactersApply) return;
        if (retry && DateTimeOffset.UtcNow < _charactersRetryAfter) return;
        _charactersProbeSession = session;
        _charactersRetryAfter = DateTimeOffset.UtcNow + ShopRetryInterval;
        try
        {
            var characters = await _characterOrderService.GetCharactersAsync(session);
            if (!ReferenceEquals(session, _session)) return;
            _charactersOffered = characters is not null;
            _charactersKnown = true;
        }
        catch (LauncherApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            if (ReferenceEquals(session, _session)) _charactersOffered = false;
        }
        catch (Exception ex) when (ex is LauncherApiException or HttpRequestException or OperationCanceledException
            or JsonException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            // Not set up or unreachable right now: what was known stays, and the next sync asks again.
        }
        finally
        {
            if (ReferenceEquals(_charactersProbeSession, session)) _charactersProbeSession = null;
            OnPropertyChanged(nameof(CharactersAvailable));
        }
    }

    /// <summary>Logged out: the page and its draft go.</summary>
    private void ForgetCharacters()
    {
        _charactersOffered = false;
        _charactersKnown = false;
        _charactersRetryAfter = default;
        OnPropertyChanged(nameof(CharactersAvailable));
        CloseCharacters();
        LeaveUnavailablePage();
    }

    /// <summary>The page's view model for this login, made on the first visit. False without a list to show.</summary>
    private bool EnsureCharacters()
    {
        if (_characters is not null) return true;
        if (_session is not { } session || !CharactersAvailable) return false;
        var characters = new CharactersViewModel(_characterOrderService, session, IsGameRunning);
        characters.LoginExpired += message => _ = ShopLoginExpiredAsync(message);
        characters.Unavailable += CharactersTurnedOff;
        characters.OrderSaved += ForgetPendingCharacterOrder;
        _characters = characters;
        OnPropertyChanged(nameof(Characters));
        return true;
    }

    /// <summary>A visit got a 404: the realm has no character order, so the link goes until the next login.</summary>
    private void CharactersTurnedOff()
    {
        _charactersOffered = false;
        _charactersKnown = true;
        OnPropertyChanged(nameof(CharactersAvailable));
        LeaveUnavailablePage();
    }

    private void CloseCharacters()
    {
        if (_characters is not { } characters) return;
        characters.Unavailable -= CharactersTurnedOff;
        characters.OrderSaved -= ForgetPendingCharacterOrder;
        _characters = null;
        characters.Dispose();
        OnPropertyChanged(nameof(Characters));
    }

    /// <summary>
    /// Sends the order the game's character select screen left for this login's account: the one in
    /// Config.wtf's accountList, or else the launcher's own copy of one that wasn't sent yet (the login
    /// handoff overwrites accountList). Once the realm has it, or it can never fit (stale, refused),
    /// both are emptied; anything else (no answer, out of time) keeps it in the launcher's settings for
    /// the next try, as guids once the realm's list was read, a newer one from Config.wtf replacing it.
    /// Before a launch it's given <paramref name="limit"/>; a login that expired is only acted on after
    /// the game (<paramref name="afterGame"/>), never in the middle of ENTER REALM.
    /// </summary>
    private async Task SendPendingCharacterOrderAsync(bool afterGame, TimeSpan? limit = null)
    {
        var session = _session;
        var realm = _realmInfo;
        if (session is null || realm is null || !RealmBranding.IsBuiltIn(realm) || IsIsolatedRealm || !ClientValid) return;
        if (!await _pendingOrderLock.WaitAsync(0)) return;
        try
        {
            var root = EffectiveClientPath;
            string? value;
            try { value = await Task.Run(() => RealmLaunchService.ReadConfigSetting(root, CharacterOrderService.ConfigSetting)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { value = null; }
            var fromConfig = CharacterOrderService.ParsePending(value, session.AccountName);
            var pending = CharacterOrderService.PendingOrder(fromConfig, _savedSettings.PendingCharacterOrder, session.AccountName);
            if (pending is null) return;

            CharacterOrderResult? result = null;
            using (var cancel = limit is { } wait ? new CancellationTokenSource(wait) : new CancellationTokenSource())
            {
                try
                {
                    result = pending.Guids.Count > 0
                        ? await _characterOrderService.SaveAsync(session, pending.Guids, cancel.Token) with { Order = pending.Guids }
                        : await _characterOrderService.SendPendingAsync(session,
                            new PendingCharacterPositions(pending.Count, pending.Positions), cancel.Token);
                }
                catch (OperationCanceledException) { }
            }
            if (!ReferenceEquals(session, _session)) return;

            if (result?.Outcome is not (CharacterOrderOutcome.Saved or CharacterOrderOutcome.Stale or CharacterOrderOutcome.Refused))
            {
                KeepPendingCharacterOrder(session.AccountName, result?.Order is { Count: > 0 } guids
                    ? new PendingCharacterOrder { Account = pending.Account, Guids = guids.ToList() }
                    : pending);
                if (result?.Outcome == CharacterOrderOutcome.LoggedOut && afterGame) _ = ShopLoginExpiredAsync(result.Message);
                return;
            }
            // Saved, or it never will be (the list changed, a character isn't the account's): done with it.
            KeepPendingCharacterOrder(session.AccountName, null);
            // The game keeps its own copy while it runs and writes it back on exit: the next exit sends it again.
            if (fromConfig is not null && !IsGameRunning)
            {
                try { await Task.Run(() => RealmLaunchService.ClearConfigSetting(root, CharacterOrderService.ConfigSetting, value!)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (result.Outcome == CharacterOrderOutcome.Saved && result.Characters is { } characters) _characters?.ApplyFromRealm(characters);
        }
        finally { _pendingOrderLock.Release(); }
    }

    /// <summary>Keeps an unsent order for <paramref name="account"/> in the settings, or (null) forgets the one it has.</summary>
    private void KeepPendingCharacterOrder(string account, PendingCharacterOrder? order)
    {
        var kept = _savedSettings.PendingCharacterOrder;
        var keptHere = kept is not null && string.Equals(kept.Account, account, StringComparison.OrdinalIgnoreCase);
        if (order is null)
        {
            if (!keptHere) return;
            _savedSettings.PendingCharacterOrder = null;
        }
        else
        {
            if (keptHere && kept!.Count == order.Count && (kept.Guids ?? []).SequenceEqual(order.Guids)
                && (kept.Positions ?? []).SequenceEqual(order.Positions))
                return;
            _savedSettings.PendingCharacterOrder = order;
        }
        try { SaveSettings(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The page saved an order: one the game left behind is older, and sent later it would undo the
    /// page's. The launcher's copy goes; Config.wtf's only while the game isn't running (it would write
    /// its copy back anyway).
    /// </summary>
    private void ForgetPendingCharacterOrder()
    {
        var account = _session?.AccountName;
        if (account is not null) KeepPendingCharacterOrder(account, null);
        if (IsGameRunning || IsLaunching || !ClientValid || IsIsolatedRealm || !RealmBranding.IsBuiltIn(_realmInfo)) return;
        try
        {
            var root = EffectiveClientPath;
            var value = RealmLaunchService.ReadConfigSetting(root, CharacterOrderService.ConfigSetting);
            if (CharacterOrderService.ParsePending(value, account) is not null)
                RealmLaunchService.ClearConfigSetting(root, CharacterOrderService.ConfigSetting, value!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
