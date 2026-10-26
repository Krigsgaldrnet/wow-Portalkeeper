using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Portalkeeper.Services;

/// <summary>
/// Evermore fork: sends the orders the game's character select screen leaves in Config.wtf's
/// accountList ("evermoreorder:ACCOUNT:COUNT:HASH:p1,p2,...") to the realm, before a launch and after
/// the game exits.
///
/// Positions only mean something against the list they were made from, and sending them twice
/// would scramble an order the realm already applied. So a value is kept in the settings the moment
/// it's read (under its own account: the login handoff overwrites accountList), turned into guids as
/// soon as the realm's list is read (only when COUNT and HASH still match it, else it's stale and
/// dropped), and then taken out of accountList whatever happens to the send. From then on only the
/// guids are sent, which is harmless to repeat, until the realm saves them (or answers that a
/// character isn't the account's). A value in accountList equal to the kept one is that same order.
///
/// One sync at a time, and the page's SAVE (<see cref="SavePageOrderAsync"/>) takes the same turn: it
/// waits for a send already on its way, so the realm gets the page's order last, and the sync changes
/// nothing after it. A waiting order the realm refuses outright (400, 409) is dropped, not sent forever.
/// </summary>
public sealed class CharacterOrderSync
{
    private readonly CharacterOrderService _service;
    private readonly Func<PortalkeeperSettings> _settings;
    private readonly Action _save;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _generation;

    /// <param name="settings">The launcher's settings, where waiting orders are kept.</param>
    /// <param name="save">Writes them to disk.</param>
    public CharacterOrderSync(CharacterOrderService service, Func<PortalkeeperSettings> settings, Action save)
    {
        _service = service;
        _settings = settings;
        _save = save;
    }

    private Dictionary<string, PendingCharacterOrder> Orders => _settings().PendingCharacterOrders ??= new();

    private static string Key(string account) => account.Trim().ToUpperInvariant();

    /// <summary>The order waiting for <paramref name="account"/>, or null.</summary>
    public PendingCharacterOrder? Waiting(string account) => Orders.GetValueOrDefault(Key(account));

    /// <summary>
    /// Keeps what accountList in <paramref name="clientDirectory"/> holds and sends what's waiting for
    /// the session's account. Config.wtf is only written while <paramref name="mayWriteConfig"/> says
    /// so. Null when there was nothing to send, the turn didn't come within
    /// <paramref name="cancellationToken"/>, it was cancelled, or a page save overtook it; otherwise
    /// the realm's answer (Stale: dropped; LoggedOut: the login expired).
    /// </summary>
    public async Task<CharacterOrderResult?> SyncAsync(LauncherSession session, string clientDirectory, Func<bool> mayWriteConfig,
        CancellationToken cancellationToken = default)
    {
        try { await _lock.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return null; }
        try { return await SyncLockedAsync(session, clientDirectory, mayWriteConfig, cancellationToken); }
        catch (OperationCanceledException) { return null; }
        finally { _lock.Release(); }
    }

    private async Task<CharacterOrderResult?> SyncLockedAsync(LauncherSession session, string clientDirectory, Func<bool> mayWriteConfig,
        CancellationToken cancellationToken)
    {
        var generation = _generation;
        var account = Key(session.AccountName);

        // What the game left, kept under its own account before anything can overwrite it; a newer
        // value replaces what was waiting.
        var value = await ReadAsync(clientDirectory);
        if (CharacterOrderService.ParseOrder(value) is { } left)
        {
            var owner = Key(left.Account);
            if (!Orders.TryGetValue(owner, out var known) || known.SourceValue != value)
            {
                Orders[owner] = new PendingCharacterOrder { SourceValue = value };
                Save();
            }
        }
        if (!Orders.TryGetValue(account, out var pending)) return null;

        // Positions to guids, as soon as the realm's list is read.
        if (pending.Guids is not { Count: > 0 })
        {
            if (CharacterOrderService.ParsePending(pending.SourceValue, session.AccountName) is not { } order)
            {
                Drop(account, pending);
                return null;
            }
            IReadOnlyList<OrderedCharacter>? list;
            try { list = await _service.GetCharactersAsync(session, cancellationToken); }
            catch (LauncherApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
            {
                return new CharacterOrderResult(CharacterOrderOutcome.LoggedOut, ex.Message);
            }
            catch (Exception ex) when (ex is LauncherApiException or HttpRequestException or JsonException or InvalidDataException
                or NotSupportedException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                return new CharacterOrderResult(CharacterOrderOutcome.NotTried, UserErrorService.Format(ex, "Couldn't ask for the character list"));
            }
            if (generation != _generation) return null;
            if (list is null) return new CharacterOrderResult(CharacterOrderOutcome.Unavailable, "This realm doesn't keep a character order.");
            var guids = CharacterOrderService.GuidsFor(order, list);
            if (guids is null)
            {
                Drop(account, pending);
                await ClearAsync(clientDirectory, pending.SourceValue, mayWriteConfig);
                return new CharacterOrderResult(CharacterOrderOutcome.Stale,
                    "The order from the game was made against another character list; it was dropped.", list);
            }
            pending.Guids = guids.ToList();
            Save();
        }

        // Translated and kept: out of accountList, whatever the send does.
        await ClearAsync(clientDirectory, pending.SourceValue, mayWriteConfig);
        if (generation != _generation) return null;

        var result = await _service.SaveAsync(session, pending.Guids, cancellationToken);
        if (generation != _generation) return null;
        if (result.Outcome == CharacterOrderOutcome.Refused || result.Code == CharacterOrderService.BadRequestCode)
            Trace.WriteLine($"Portalkeeper: the realm refused the character order from the game for {account} ({result.Code ?? "no code"}: {result.Message}); dropped.");
        if (result.Outcome is CharacterOrderOutcome.Saved or CharacterOrderOutcome.Refused || result.Code == CharacterOrderService.BadRequestCode)
            Drop(account, pending);
        return result;
    }

    /// <summary>
    /// The page's SAVE of <paramref name="guids"/>. It takes the sync's turn, so a send of the game's
    /// order already on its way finishes first and the realm ends with the page's; a sync waiting for
    /// its turn, or in flight, changes nothing after it. Once saved, the account's waiting order and its
    /// value in accountList (never a login handoff or another account's, and only while
    /// <paramref name="mayWriteConfig"/> says so) are dropped: they're older.
    /// </summary>
    public async Task<CharacterOrderResult> SavePageOrderAsync(LauncherSession session, IReadOnlyList<uint> guids, string clientDirectory,
        Func<bool> mayWriteConfig, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _generation);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var result = await _service.SaveAsync(session, guids, cancellationToken);
            if (result.Outcome != CharacterOrderOutcome.Saved) return result;
            var account = Key(session.AccountName);
            if (Orders.Remove(account)) Save();
            var value = await ReadAsync(clientDirectory);
            if (CharacterOrderService.ParsePending(value, session.AccountName) is not null) await ClearAsync(clientDirectory, value, mayWriteConfig);
            return result;
        }
        finally { _lock.Release(); }
    }

    private void Drop(string account, PendingCharacterOrder pending)
    {
        if (!Orders.TryGetValue(account, out var current) || !ReferenceEquals(current, pending)) return;
        Orders.Remove(account);
        Save();
    }

    private void Save()
    {
        try { _save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static async Task<string?> ReadAsync(string clientDirectory)
    {
        try { return await Task.Run(() => RealmLaunchService.ReadConfigSetting(clientDirectory, CharacterOrderService.ConfigSetting)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Empties accountList while it still holds <paramref name="expected"/>, when Config.wtf may be written.</summary>
    private static async Task ClearAsync(string clientDirectory, string? expected, Func<bool> mayWriteConfig)
    {
        if (string.IsNullOrEmpty(expected) || !mayWriteConfig()) return;
        try { await Task.Run(() => RealmLaunchService.ClearConfigSetting(clientDirectory, CharacterOrderService.ConfigSetting, expected)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
