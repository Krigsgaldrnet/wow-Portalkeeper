using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

// A game the launcher didn't start: from a shortcut, or by a launcher that has since closed. While one
// runs from the client folder IsGameRunning is true, so everything that holds back for the launcher's
// own game holds back for this one too; downloads, patch and addon installs already going are stopped
// when it's seen. Once it exits the realm is checked again, which picks up whatever waited.
public sealed partial class MainViewModel
{
    private static readonly TimeSpan OutsideGameLookEvery = TimeSpan.FromSeconds(5);
    private const string OutsideGameStatus =
        "World of Warcraft is running, started outside Portalkeeper. Updates to your game wait until you close it, then carry on by themselves.";

    private readonly RunningGameTracker _outsideGame = new();
    private bool _outsideGameRunning;
    private bool _checkAfterOutsideGame;
    // A client download or repair stopped, or not started, because of it: said on the card instead of "paused".
    private bool _stoppedForOutsideGame;
    private string? _installTarget;
    // Taken by patch, addon and cleanup work as it starts; cancelled when a game is seen. Never reset, only
    // replaced, so work that took an old one can't miss that it was cancelled.
    private CancellationTokenSource _outsideGameStop = new();

    /// <summary>The first look comes before anything else starts, so a launcher opened under a game writes nothing.</summary>
    private void StartOutsideGameWatch()
    {
        ApplyOutsideGame(_outsideGame.Observe(RunningGameDetector.IsRunningFrom(FoldersGameMayRunFrom(), GameExecutableName)));
        _ = RunOutsideGameWatchAsync();
    }

    private async Task RunOutsideGameWatchAsync()
    {
        using var timer = new PeriodicTimer(OutsideGameLookEvery);
        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                if (!_isGameRunning)
                {
                    var folders = FoldersGameMayRunFrom();
                    var executable = GameExecutableName;
                    var seen = await Task.Run(() => RunningGameDetector.IsRunningFrom(folders, executable));
                    // The launcher's own game may have started meanwhile: then what this found may be that.
                    if (!_isGameRunning) ApplyOutsideGame(_outsideGame.Observe(seen));
                }
                CheckAfterOutsideGameIfDue();
            }
            catch (Exception ex) { Trace.WriteLine("Portalkeeper: watching for a running game failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// Looks right before client files are written, since the watch can be a few seconds behind. Only ever
    /// starts a hold: finding nothing here doesn't end one, that's for the watch's own looks. This also runs
    /// while ENTER REALM prepares: the launcher's own game starts and sets _isGameRunning in one step, so
    /// until then whatever runs from the client is somebody else's.
    /// </summary>
    private async Task LookForOutsideGameAsync()
    {
        if (_isGameRunning || _outsideGameRunning) return;
        var folders = FoldersGameMayRunFrom();
        var executable = GameExecutableName;
        var seen = await Task.Run(() => RunningGameDetector.IsRunningFrom(folders, executable));
        if (seen == true && !_isGameRunning) ApplyOutsideGame(_outsideGame.Observe(true));
    }

    /// <summary>For ENTER REALM's preparing steps, which go on to write and then open a second game.</summary>
    private async Task ThrowIfOutsideGameAsync()
    {
        await LookForOutsideGameAsync();
        if (_outsideGameRunning) throw new InvalidOperationException(OutsideGameStatus);
    }

    private string GameExecutableName => _realmInfo?.Client.Executable ?? "Wow.exe";

    private List<string> FoldersGameMayRunFrom()
    {
        var folders = new List<string>();
        if (ClientValid)
        {
            folders.Add(ClientPath);
            // Not EffectiveClientPath: it hashes the isolated runtime's files, and a runtime that fails
            // those checks is exactly one a game may still run from. A realm whose runtime can't be worked
            // out mustn't stop the look at the client itself.
            try { folders.Add(ComponentInspectionPath); }
            catch (Exception ex) { Trace.WriteLine("Portalkeeper: no runtime folder to watch: " + ex.Message); }
        }
        if (_savedSettings.PendingClientInstallPath is { Length: > 0 } pending) folders.Add(pending);
        if (_installTarget is { } target) folders.Add(target);
        return folders;
    }

    private void ApplyOutsideGame(bool running)
    {
        if (running == _outsideGameRunning)
        {
            // Something else may have written over it just before the hold began.
            if (running) LaunchStatus = OutsideGameStatus;
            return;
        }
        _outsideGameRunning = running;
        NotifyGameRunningChanged();
        if (running)
        {
            LaunchStatus = OutsideGameStatus;
            var stop = _outsideGameStop;
            _outsideGameStop = new CancellationTokenSource();
            stop.Cancel();
            _optionalUpdateCancel?.Cancel();
            _combatLogRun?.Cancel();
            if (_isInstallingClient)
            {
                _stoppedForOutsideGame = true;
                CancelClientInstall();
            }
            _ = PauseSharingForGameAsync();
            return;
        }
        _syncStatus = "";
        LaunchStatus = "";
        UpdateLaunchReadinessStatus();
        AllowClientUpdateRetry(); // an update it stopped carries on now, not after the usual wait
        _ = ResumeSharingAfterGameAsync();
        // What the launcher's own game's exit does for the player's files, minus the crash banner, which
        // goes with a launch it made.
        _ = UploadCombatLogsAsync(afterGame: true);
        _ = SendPendingCharacterOrderAsync(afterGame: true);
        _checkAfterOutsideGame = true;
        CheckAfterOutsideGameIfDue();
    }

    /// <summary>The refresh ends with the full sync. If something still runs, the next look tries again.</summary>
    private void CheckAfterOutsideGameIfDue()
    {
        if (!_checkAfterOutsideGame || IsGameRunning || IsLaunching || _isManagingComponents || _isRefreshingConfiguration
            || _isInstallingClient)
            return;
        _checkAfterOutsideGame = false;
        _ = RediscoverRealmConfigurationAsync();
    }
}
