using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

// Evermore fork: REPAIR GAME (Settings, and the crash banner), for a game that crashes or looks wrong
// although the launcher says it's up to date. It clears Cache, reinstalls the realm's addons the
// player has, then hash-checks every client file against the realm's client torrent instead of
// trusting the saved resume data, downloading whatever is damaged. The sync that follows checks
// every patch's SHA-256 and replaces a bad one. The player's own addons, WTF settings, screenshots
// and logs are left alone. PAUSE stops it at any step; the next REPAIR GAME starts over.
public sealed partial class MainViewModel
{
    private bool _isRepairingGame;
    private string _repairStatus = "";
    // SyncRequiredAndShareAsync runs going on: their patch and client work mustn't overlap a repair.
    private int _syncRuns;

    public bool CanRepairGame => IsLoggedIn && ClientValid && _clientCheck != ClientCheck.NotRealmClient
        && RealmBranding.IsBuiltIn(_realmInfo) && !_isRepairingGame && !_isInstallingClient && !_isInstallingRequired
        && !_isCleaningPatches && _syncRuns == 0 && !_isRefreshingConfiguration && !_isManagingComponents
        && !IsLaunching && !IsGameRunning;

    public bool IsRepairingGame => _isRepairingGame;

    /// <summary>How the last repair went, under the button in Settings and on the home card.</summary>
    public string RepairStatus => _repairStatus;
    public bool HasRepairStatus => _repairStatus.Length > 0;

    public async Task RepairGameAsync()
    {
        var session = _session;
        if (!CanRepairGame || session is null) return;
        _isRepairingGame = true;
        _isInstallingClient = true; // the home card shows the repair; DownloadClientAsync takes over below
        _isRepairingClient = true;
        // PAUSE works from the first step: DownloadClientAsync makes its own once it starts.
        using var pause = new CancellationTokenSource();
        _installCancel = pause;
        SetRepairStatus("");
        var failedAddons = new List<string>();
        var repaired = false;
        string? stopped = null;
        try
        {
            await StopOptionalAddonUpdatesAsync();
            SetInstallStatus("Clearing the game's cache...", 0);
            foreach (var client in new[] { ClientPath, EffectiveClientPath }.Distinct(StringComparer.Ordinal))
                await Task.Run(() => PatchService.ClearClientCache(client), pause.Token);

            // Every required realm addon, and every realm addon the launcher installed, fresh from its
            // source. The player's own (personal) addons aren't the realm's to replace.
            var addons = _addons.Where(a => !a.Definition.IsPersonal
                && (a.Definition.Required || (a.IsInstalled && a.InstalledByLauncher))).ToArray();
            for (var i = 0; i < addons.Length; i++)
            {
                pause.Token.ThrowIfCancellationRequested();
                var addon = addons[i].Definition;
                SetInstallStatus($"Reinstalling the realm's addons ({i + 1} of {addons.Length}): {addon.Name}", 100.0 * i / Math.Max(1, addons.Length));
                try { await _addonInstallerService.InstallOrUpdateAsync(EffectiveClientPath, addon, pause.Token); }
                catch (Exception) when (!pause.IsCancellationRequested) { failedAddons.Add(addon.Name); }
            }
            await LoadAddonsAsync();

            SetInstallStatus("Checking every World of Warcraft file (this takes a few minutes)...", 0);
            // Held here: logging out meanwhile clears the field.
            var torrent = _clientTorrent ??= await _accountService.GetClientTorrentAsync(session, pause.Token);
            await StopAllSharingAsync();
            pause.Token.ThrowIfCancellationRequested();
            await _torrents.ForgetResumeAsync(torrent);
            _installCancel = null;
            _isInstallingClient = false;
            repaired = await DownloadClientAsync(session, null, repair: true);
            if (!repaired) stopped = _installStatus;
        }
        catch (OperationCanceledException) when (pause.IsCancellationRequested)
        {
            stopped = "";
        }
        catch (Exception ex)
        {
            // A button handler: whatever went wrong is said on the card, never thrown at the window.
            stopped = UserErrorService.Format(ex, "Repair stopped");
        }
        finally
        {
            if (ReferenceEquals(_installCancel, pause)) _installCancel = null;
            _isRepairingGame = false;
            _isInstallingClient = false;
            _isRepairingClient = false;
            // DownloadClientAsync's own "paused, TRY AGAIN" doesn't apply: a repair starts over.
            SetRepairStatus(repaired
                ? failedAddons.Count == 0
                    ? $"Repaired at {DateTime.Now:t}: every game file was checked, and the cache and the realm's addons were renewed."
                    : $"Repaired at {DateTime.Now:t}, but these addons couldn't be downloaded: {string.Join(", ", failedAddons)}."
                : stopped is { Length: > 0 } && !stopped.Contains("paused", StringComparison.OrdinalIgnoreCase)
                    ? stopped + " Run REPAIR GAME again to finish."
                    : "The repair was paused before it finished. Run REPAIR GAME again to check every file.");
            if (!repaired) SetInstallStatus("", 0);
            NotifyInstallChanged();
        }
    }

    private void SetRepairStatus(string status)
    {
        _repairStatus = status;
        OnPropertyChanged(nameof(RepairStatus));
        OnPropertyChanged(nameof(HasRepairStatus));
    }

    private void NotifyRepairChanged()
    {
        OnPropertyChanged(nameof(CanRepairGame));
        OnPropertyChanged(nameof(IsRepairingGame));
    }
}
