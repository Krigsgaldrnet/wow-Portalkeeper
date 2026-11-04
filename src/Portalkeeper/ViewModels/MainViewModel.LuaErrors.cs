using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

// Evermore fork: the game's Lua errors go to the realm (LuaErrorService). On until the player turns it
// off in Settings, and only offered when the portal says the realm collects them. Nothing here holds up
// a launch: what didn't go out goes with the next run.
public sealed partial class MainViewModel
{
    private readonly LuaErrorService _luaErrors = new();
    private readonly SemaphoreSlim _luaErrorLock = new(1, 1);
    private bool? _luaErrorsOffered;
    private bool _luaErrorStartupDone;
    private CancellationTokenSource? _luaErrorRun;
    private string _luaErrorStatus = "";
    private CancellationTokenSource? _luaErrorStatusExpiry;

    private bool LuaErrorsApply => IsLoggedIn && RealmBranding.IsBuiltIn(_realmInfo);

    /// <summary>The realm collects Lua errors and the player is logged in: show the option.</summary>
    public bool LuaErrorsOffered => LuaErrorsApply && _luaErrorsOffered == true;

    public bool SendLuaErrors
    {
        get => _savedSettings.SendLuaErrors;
        set
        {
            if (_savedSettings.SendLuaErrors == value) return;
            _savedSettings.SendLuaErrors = value;
            OnPropertyChanged();
            SaveSettings();
            // Under a running game the addon is left as it is; the next launch or the game's exit puts it right.
            if (!IsGameRunning && !IsLaunching) SyncLuaErrorAddon();
        }
    }

    /// <summary>Under the option in Settings: when errors last went out.</summary>
    public string LuaErrorsDetail => _savedSettings.LuaErrorsLastSent is { } sent ? LastSentText(sent) : "Nothing sent yet.";

    /// <summary>The footer's bright line: a combat log upload first, then Lua errors that went out.</summary>
    public string FooterStatus => HasCombatLogStatus ? CombatLogStatus : _luaErrorStatus;

    public bool HasFooterStatus => FooterStatus.Length > 0;

    private static string LastSentText(DateTimeOffset sent)
    {
        var local = sent.ToLocalTime();
        var day = local.Date == DateTime.Today ? "today"
            : local.Date == DateTime.Today.AddDays(-1) ? "yesterday"
            : local.ToString("MMM d", CultureInfo.CurrentCulture);
        return $"Last sent {day} at {local.ToString("t", CultureInfo.CurrentCulture)}.";
    }

    private void NotifyFooterStatus()
    {
        OnPropertyChanged(nameof(FooterStatus));
        OnPropertyChanged(nameof(HasFooterStatus));
        OnPropertyChanged(nameof(FooterText));
    }

    /// <summary>Shows that errors went out, then clears it after as long as a combat log's result.</summary>
    private async void ShowLuaErrorStatus(string status)
    {
        _luaErrorStatusExpiry?.Cancel();
        _luaErrorStatus = status;
        NotifyFooterStatus();
        using var expiry = new CancellationTokenSource();
        _luaErrorStatusExpiry = expiry;
        try { await Task.Delay(CombatLogResultShown, expiry.Token); }
        catch (OperationCanceledException) { return; }
        if (!ReferenceEquals(_luaErrorStatusExpiry, expiry)) return;
        _luaErrorStatus = "";
        _luaErrorStatusExpiry = null;
        NotifyFooterStatus();
    }

    /// <summary>
    /// Once per login, as soon as the login, the realm and the client are all known: asks the portal
    /// whether the realm collects Lua errors and sends what an earlier run left behind. Called after
    /// every sync, so a portal that couldn't be reached is asked again by the next one.
    /// </summary>
    private async Task StartLuaErrorsAsync()
    {
        if (_luaErrorStartupDone || !LuaErrorsApply || !ClientValid || IsGameRunning || IsLaunching) return;
        _luaErrorStartupDone = true;
        await RefreshLuaErrorOfferAsync();
        if (_luaErrorsOffered is null)
        {
            _luaErrorStartupDone = false;
            return;
        }
        await SendLuaErrorsAsync(afterGame: false);
    }

    /// <summary>
    /// Asks the portal whether the realm collects Lua errors. When it can't be asked (offline, portal
    /// down, login refused) what was known stays, and so does the addon.
    /// </summary>
    private async Task RefreshLuaErrorOfferAsync()
    {
        var session = _session;
        if (session is null || !LuaErrorsApply) return;
        try
        {
            var offered = await _luaErrors.IsOfferedAsync(session);
            if (!ReferenceEquals(session, _session)) return;
            _luaErrorsOffered = offered;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or LauncherApiException)
        {
            return;
        }
        OnPropertyChanged(nameof(LuaErrorsOffered));
        if (!IsGameRunning && !IsLaunching) SyncLuaErrorAddon();
    }

    private void ForgetLuaErrorOffer()
    {
        _luaErrorsOffered = null;
        _luaErrorStartupDone = false;
        OnPropertyChanged(nameof(LuaErrorsOffered));
    }

    /// <summary>
    /// Writes the launcher's error addon into the client the game runs from while the option is on and
    /// the realm collects them, and deletes it otherwise. While the portal hasn't answered yet an addon
    /// already there is left alone. What it recorded goes only when the option is off; otherwise it
    /// waits for a login and a realm that takes it.
    /// </summary>
    private void SyncLuaErrorAddon()
    {
        try
        {
            if (!ClientValid) return;
            var client = EffectiveClientPath;
            if (!SendLuaErrors) LuaErrorService.DeletePending(client);
            if (SendLuaErrors && LuaErrorsApply && _luaErrorsOffered is null) return;
            LuaErrorService.SyncAddon(client, SendLuaErrors && LuaErrorsApply && _luaErrorsOffered == true);
        }
        catch (Exception)
        {
            // The addon is a convenience; it never stands between the player and the game.
        }
    }

    /// <summary>Right before the game starts: the addon is put right, and a send still going stops.</summary>
    private void PrepareLuaErrorsForLaunch()
    {
        _luaErrorRun?.Cancel();
        SyncLuaErrorAddon();
    }

    /// <summary>
    /// A game the launcher doesn't know about, such as one started before it, holds in memory what the
    /// file says and writes it back on exit: what it recorded waits until it's gone.
    /// </summary>
    private bool GameProcessRunning(string client)
    {
        if (ClientService.FindWowExecutable(client, _realmInfo!.Client.Executable) is not { } executable) return false;
        if (OperatingSystem.IsLinux()) return RealmLaunchService.IsLinuxGameProcessRunning(executable);
        if (!OperatingSystem.IsWindows()) return false;
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }

    /// <summary>
    /// Sends what the addon recorded. Runs after the game exits (asking the portal again first, so a
    /// launcher left open notices a realm that stopped or started collecting) and once after start-up.
    /// </summary>
    private async Task SendLuaErrorsAsync(bool afterGame)
    {
        await _luaErrorLock.WaitAsync();
        try
        {
            if (afterGame)
            {
                await RefreshLuaErrorOfferAsync();
                if (!IsGameRunning && !IsLaunching) SyncLuaErrorAddon();
            }
            var session = _session;
            if (session is null || !SendLuaErrors || !LuaErrorsApply || _luaErrorsOffered != true
                || !ClientValid || IsGameRunning || IsLaunching) return;
            var client = EffectiveClientPath;
            using var run = new CancellationTokenSource();
            _luaErrorRun = run;
            var result = await Task.Run(() => GameProcessRunning(client)
                ? Task.FromResult(new LuaErrorSendResult(0, 0))
                : _luaErrors.SendPendingAsync(client, session, run.Token));
            if (result.Sent == 0) return;
            _savedSettings.LuaErrorsLastSent = DateTimeOffset.UtcNow;
            SaveSettings();
            OnPropertyChanged(nameof(LuaErrorsDetail));
            ShowLuaErrorStatus(result.Sent == 1 ? "Sent 1 Lua error to the realm" : $"Sent {result.Sent} Lua errors to the realm");
        }
        catch (Exception)
        {
            // Quiet: whatever is left is sent by the next run.
        }
        finally
        {
            _luaErrorRun = null;
            _luaErrorLock.Release();
        }
    }
}
