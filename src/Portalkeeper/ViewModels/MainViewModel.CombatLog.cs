using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

// Evermore fork: sharing combat logs with the realm (CombatLogUploadService). Off until the player
// turns it on in Settings, and only offered when the portal says the realm collects them. Nothing
// here holds up a launch or reports a failure: what didn't go out goes with the next run.
public sealed partial class MainViewModel
{
    private readonly CombatLogUploadService _combatLogs = new();
    private readonly SemaphoreSlim _combatLogLock = new(1, 1);
    private readonly CancellationTokenSource _combatLogShutdown = new();
    private CancellationTokenSource? _combatLogRun;
    private Task _combatLogWork = Task.CompletedTask;
    private CombatLogOffer? _combatLogOffer;
    private bool _combatLogOfferKnown;
    private bool _combatLogStartupDone;
    private string _combatLogStatus = "";

    // The portal is the built-in realm's: another realm's logs are none of its business.
    private bool CombatLogsApply => IsLoggedIn && RealmBranding.IsBuiltIn(_realmInfo);

    /// <summary>The realm collects combat logs and the player is logged in: show the option.</summary>
    public bool CombatLogSharingOffered => CombatLogsApply && _combatLogOffer is not null;

    public bool ShareCombatLogs
    {
        get => _savedSettings.ShareCombatLogs;
        set
        {
            if (_savedSettings.ShareCombatLogs == value) return;
            _savedSettings.ShareCombatLogs = value;
            // What the player logged before opting in stays theirs: it's renamed, never uploaded.
            _savedSettings.CombatLogSetAsidePending = value;
            OnPropertyChanged();
            SaveSettings();
            if (!value) _combatLogRun?.Cancel();
            // Under a running game the log and the addon are left as they are; the next launch or
            // the game's exit puts them right.
            if (IsGameRunning || IsLaunching) return;
            if (value) SetAsideEarlierCombatLog();
            SyncCombatLogAddon();
        }
    }

    /// <summary>
    /// Renames a combat log from before the player turned sharing on (see
    /// <see cref="CombatLogUploadService.SetAsideLog"/>). False while that is still owed, because the
    /// game has the log open or the client isn't ready: nothing is collected for upload until it's done.
    /// </summary>
    private bool SetAsideEarlierCombatLog()
    {
        if (!_savedSettings.CombatLogSetAsidePending) return true;
        try
        {
            if (!ClientValid || !_combatLogs.SetAsideLog(EffectiveClientPath)) return false;
        }
        catch (Exception)
        {
            return false;
        }
        _savedSettings.CombatLogSetAsidePending = false;
        SaveSettings();
        return true;
    }

    public string CombatLogStatus => _combatLogStatus;

    private void SetCombatLogStatus(string status)
    {
        if (_combatLogStatus == status) return;
        _combatLogStatus = status;
        OnPropertyChanged(nameof(CombatLogStatus));
        OnPropertyChanged(nameof(FooterText));
    }

    /// <summary>
    /// Once per login, as soon as the login, the realm and the client are all known: asks the portal
    /// whether the realm collects combat logs and sends what an earlier run left behind. Called after
    /// every sync, so a portal that couldn't be reached is asked again by the next one.
    /// </summary>
    private async Task StartCombatLogSharingAsync()
    {
        if (_combatLogStartupDone || !CombatLogsApply || !ClientValid || IsGameRunning || IsLaunching) return;
        _combatLogStartupDone = true;
        await RefreshCombatLogOfferAsync();
        if (!_combatLogOfferKnown)
        {
            _combatLogStartupDone = false;
            return;
        }
        await UploadCombatLogsAsync(afterGame: false);
    }

    /// <summary>
    /// Asks the portal whether the realm collects combat logs. When it can't be asked (offline, portal
    /// down, login refused) what was known stays, and so does the addon.
    /// </summary>
    private async Task RefreshCombatLogOfferAsync()
    {
        var session = _session;
        if (session is null || !CombatLogsApply) return;
        try
        {
            var offer = await _combatLogs.GetOfferAsync(session, _combatLogShutdown.Token);
            if (!ReferenceEquals(session, _session)) return;
            _combatLogOffer = offer;
            _combatLogOfferKnown = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException
            or InvalidDataException or LauncherApiException)
        {
            return;
        }
        OnPropertyChanged(nameof(CombatLogSharingOffered));
        if (!IsGameRunning && !IsLaunching) SyncCombatLogAddon();
    }

    private void ForgetCombatLogOffer()
    {
        _combatLogRun?.Cancel();
        _combatLogOffer = null;
        _combatLogOfferKnown = false;
        _combatLogStartupDone = false;
        OnPropertyChanged(nameof(CombatLogSharingOffered));
    }

    /// <summary>
    /// Writes the launcher's combat log addon into the client the game runs from while the option is
    /// on and the realm offers it, and deletes it otherwise. While the portal hasn't answered yet an
    /// addon the player opted in to is left alone.
    /// </summary>
    private void SyncCombatLogAddon()
    {
        try
        {
            if (!ClientValid) return;
            var wanted = ShareCombatLogs && CombatLogsApply;
            if (wanted && !_combatLogOfferKnown) return;
            CombatLogUploadService.SyncAddon(EffectiveClientPath, wanted && _combatLogOffer is not null);
        }
        catch (Exception)
        {
            // The addon is a convenience; it never stands between the player and the game.
        }
    }

    /// <summary>Right before the game starts: the addon is put right, and an upload gives the line to the game.</summary>
    private void PrepareCombatLogsForLaunch()
    {
        _combatLogRun?.Cancel();
        if (ShareCombatLogs) SetAsideEarlierCombatLog();
        SyncCombatLogAddon();
    }

    /// <summary>
    /// Moves the game's combat log aside and uploads every pending one. Runs after the game exits
    /// (asking the portal again first, so a launcher left open notices a realm that stopped or started
    /// collecting) and once after start-up. With the option off only logs queued more than 14 days
    /// ago are cleared away.
    /// </summary>
    private async Task UploadCombatLogsAsync(bool afterGame)
    {
        try { await _combatLogLock.WaitAsync(_combatLogShutdown.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (afterGame) await RefreshCombatLogOfferAsync();
            var session = _session;
            if (session is null || !CombatLogsApply || !ClientValid || IsGameRunning || IsLaunching) return;
            var client = EffectiveClientPath;
            if (!ShareCombatLogs || _combatLogOffer is not { } offer)
            {
                if (_combatLogOfferKnown || !ShareCombatLogs) await Task.Run(() => _combatLogs.DeleteExpired(client));
                return;
            }

            // On Windows a log the game still has open can't be moved. Under Wine it can, and what the
            // game wrote afterwards would be lost: leave it to the next run while a game is running.
            var collect = !OperatingSystem.IsLinux()
                || ClientService.FindWowExecutable(client, _realmInfo!.Client.Executable) is not { } executable
                || !RealmLaunchService.IsLinuxGameProcessRunning(executable);
            // A log from before the player opted in is renamed first; until that works nothing is collected.
            if (collect && !SetAsideEarlierCombatLog()) collect = false;
            using var run = CancellationTokenSource.CreateLinkedTokenSource(_combatLogShutdown.Token);
            _combatLogRun = run;
            var progress = new Progress<string>(SetCombatLogStatus);
            // Compressing and hashing a few hundred MB: not on the UI thread.
            _combatLogWork = Task.Run(() => _combatLogs.UploadPendingAsync(client, session, offer, progress, run.Token, collect));
            await _combatLogWork;
        }
        catch (Exception)
        {
            // Quiet: whatever is left is sent by the next run.
        }
        finally
        {
            _combatLogRun = null;
            SetCombatLogStatus("");
            _combatLogLock.Release();
        }
    }

    /// <summary>Stops an upload as Portalkeeper closes; the next start carries on where the portal says it stopped.</summary>
    private async Task StopCombatLogUploadAsync()
    {
        try
        {
            _combatLogShutdown.Cancel();
            await _combatLogWork;
        }
        catch (Exception) { }
    }
}
