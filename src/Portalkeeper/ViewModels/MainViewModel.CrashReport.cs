using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

// Evermore fork: crash reports (CrashReportService). When the game exits after writing an error report,
// a banner over the status card says so and offers to send it, or sends it straight away when the
// player ticked "Send crash reports" in Settings. Either way it points at REPAIR GAME. Only offered
// when the built-in realm's portal says it takes crash reports; the banner still shows otherwise.
public sealed partial class MainViewModel
{
    private enum CrashBanner { None, Ask, Sending, Sent, Failed, NotOffered }

    private readonly CrashReportService _crashReports = new();
    private bool? _crashReportsOffered;
    private CrashReport? _crash;
    private CrashBanner _crashBanner;
    private string _crashError = "";

    private bool CrashReportsApply => IsLoggedIn && RealmBranding.IsBuiltIn(_realmInfo);

    /// <summary>The realm takes crash reports: show the option in Settings.</summary>
    public bool CrashReportsOffered => CrashReportsApply && _crashReportsOffered == true;

    public bool SendCrashReports
    {
        get => _savedSettings.SendCrashReports;
        set
        {
            if (_savedSettings.SendCrashReports == value) return;
            _savedSettings.SendCrashReports = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public bool HasCrashBanner => _crashBanner != CrashBanner.None && _crash is not null;

    public string CrashBannerSummary => _crash?.Summary ?? "";
    public bool HasCrashBannerSummary => CrashBannerSummary.Length > 0;

    public string CrashBannerDetail => _crashBanner switch
    {
        CrashBanner.Ask => "Send the crash report to the realm? Your Windows and computer names are taken out.",
        CrashBanner.Sending => "Sending the crash report...",
        CrashBanner.Sent => "Crash report sent, thank you. If it keeps crashing, REPAIR GAME checks every game file.",
        CrashBanner.Failed => "The crash report couldn't be sent: " + _crashError,
        _ => "If it keeps crashing, REPAIR GAME checks every game file."
    };

    public bool CanSendCrashReport => _crashBanner is CrashBanner.Ask or CrashBanner.Failed;
    public bool CrashBannerOffersAlways => _crashBanner == CrashBanner.Ask;
    public bool CrashBannerOffersRepair => _crashBanner is CrashBanner.Sent or CrashBanner.NotOffered;

    /// <summary>
    /// After the game exits: picks up the error reports it wrote while it ran, and if there are any
    /// sends them or asks. Quiet when there are none, which is every normal exit.
    /// </summary>
    private async Task CheckForCrashAsync(DateTimeOffset launchedAt)
    {
        if (!ClientValid) return;
        var client = EffectiveClientPath;
        CrashReport? crash;
        try { crash = await Task.Run(() => CrashReportService.Collect(client, launchedAt)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (crash is null) return;

        _crash = crash;
        await RefreshCrashReportOfferAsync();
        if (_crashReportsOffered != true || !CrashReportsApply) SetCrashBanner(CrashBanner.NotOffered);
        else if (SendCrashReports) await SendCrashReportAsync();
        else SetCrashBanner(CrashBanner.Ask);
    }

    /// <summary>Asks the portal once per login whether the realm takes crash reports; a portal that couldn't be asked is asked again next time.</summary>
    private async Task RefreshCrashReportOfferAsync()
    {
        var session = _session;
        if (session is null || !CrashReportsApply || _crashReportsOffered is not null) return;
        try
        {
            var offered = await _crashReports.IsOfferedAsync(session);
            if (ReferenceEquals(session, _session)) _crashReportsOffered = offered;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LauncherApiException) { }
        OnPropertyChanged(nameof(CrashReportsOffered));
    }

    private void ForgetCrashReportOffer()
    {
        _crashReportsOffered = null;
        OnPropertyChanged(nameof(CrashReportsOffered));
    }

    /// <summary>SEND REPORT, ALWAYS SEND (with <paramref name="always"/>), or by itself when the option is on.</summary>
    public async Task SendCrashReportAsync(bool always = false)
    {
        if (always) SendCrashReports = true;
        var session = _session;
        if (_crash is not { } crash || session is null || _crashBanner == CrashBanner.Sending) return;
        SetCrashBanner(CrashBanner.Sending);
        try
        {
            var summary = await _crashReports.SendAsync(session, crash);
            if (summary.Length > 0 && summary != crash.Summary) _crash = crash with { Summary = summary };
            SetCrashBanner(CrashBanner.Sent);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
            or InvalidDataException or LauncherApiException)
        {
            _crashError = ex is LauncherApiException ? ex.Message : "the realm couldn't be reached. Try again in a moment.";
            SetCrashBanner(CrashBanner.Failed);
        }
    }

    /// <summary>✕ on the banner, and the next ENTER REALM.</summary>
    public void DismissCrash()
    {
        _crash = null;
        SetCrashBanner(CrashBanner.None);
    }

    private void SetCrashBanner(CrashBanner banner)
    {
        _crashBanner = banner;
        OnPropertyChanged(nameof(HasCrashBanner));
        OnPropertyChanged(nameof(CrashBannerSummary));
        OnPropertyChanged(nameof(HasCrashBannerSummary));
        OnPropertyChanged(nameof(CrashBannerDetail));
        OnPropertyChanged(nameof(CanSendCrashReport));
        OnPropertyChanged(nameof(CrashBannerOffersAlways));
        OnPropertyChanged(nameof(CrashBannerOffersRepair));
    }
}
