using Avalonia.Media;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

// Evermore fork: the single-screen home view (realm status pill, one status card, ENTER REALM).
// Everything here is derived from state the rest of the view model already keeps.
public sealed partial class MainViewModel
{
    public enum HomeCard { NoClient, Installing, Syncing, Playing, Ready, Attention }

    public string LauncherTitle => RealmBranding.LauncherName.ToUpperInvariant();
    public string PatchNotesUrl => RealmBranding.PatchNotesUrl;

    public string RealmStatusText => !RealmConfigured ? "Not set up" : _realmHealthState switch
    {
        RealmHealthState.Checking => "Checking",
        RealmHealthState.Online => "Online",
        RealmHealthState.AuthOnly => "Login only",
        RealmHealthState.WorldOnly => "World only",
        RealmHealthState.Offline => "Offline",
        RealmHealthState.Booting => "Booting",
        _ => "Unknown"
    };

    public bool RealmBooting => RealmConfigured && _realmHealthState == RealmHealthState.Booting;

    /// <summary>Where the boot is: the world server loading, or how many bots are in.</summary>
    public string BootProgressText => DescribeBoot(_realmBoot);

    public static string DescribeBoot(RealmBootStatus? boot) => boot switch
    {
        { Stage: "bots", BotsTarget: > 0 } => $"bots logging in ({boot.BotsOnline} of {boot.BotsTarget})",
        { Stage: "bots" } => "bots logging in",
        { Stage: "login" } => "login is opening",
        _ => "the world is loading"
    };

    public IBrush RealmStatusBrush => Brush.Parse(!RealmConfigured ? "#8E9AA5" : _realmHealthState switch
    {
        RealmHealthState.Online => "#3DDC84",
        RealmHealthState.AuthOnly or RealmHealthState.WorldOnly or RealmHealthState.Booting => "#F2B84B",
        RealmHealthState.Offline => "#E5604D",
        _ => "#8E9AA5"
    });

    public HomeCard Card =>
        _isInstallingClient ? HomeCard.Installing
        : !ClientValid || _clientCheck == ClientCheck.NotRealmClient ? HomeCard.NoClient
        : IsGameRunning || IsLaunching ? HomeCard.Playing
        : _isInstallingRequired || IsCheckingAddons ? HomeCard.Syncing
        : CanEnterRealm ? HomeCard.Ready
        : HomeCard.Attention;

    public bool CardIsNoClient => Card == HomeCard.NoClient;
    public bool CardIsInstalling => Card == HomeCard.Installing;
    public bool CardIsSyncing => Card == HomeCard.Syncing;
    public bool CardIsReady => Card == HomeCard.Ready;
    public bool CardIsAttention => Card == HomeCard.Attention;
    public bool CardShowsBusy => Card is HomeCard.Installing or HomeCard.Syncing;

    public string CardIcon => Card switch
    {
        HomeCard.Ready => RealmBooting ? "↻" : "✓",
        HomeCard.Installing or HomeCard.NoClient => "↓",
        HomeCard.Syncing => "↻",
        HomeCard.Playing => "▶",
        _ => "!"
    };

    public IBrush CardIconBrush => Brush.Parse(Card switch
    {
        HomeCard.Ready when RealmBooting => "#F2B84B",
        HomeCard.Ready or HomeCard.Playing => "#3DDC84",
        HomeCard.Attention => "#F2B84B",
        _ => "#E8B64C"
    });

    public string CardTitle => Card switch
    {
        HomeCard.Installing => _isRepairingGame ? "Repairing World of Warcraft"
            : _isRepairingClient ? "Updating World of Warcraft" : "Downloading World of Warcraft",
        HomeCard.NoClient => HasPendingClientInstall ? "Your install is paused"
            : ClientValid ? "Update your client to " + RealmBranding.LauncherName : "Install World of Warcraft",
        HomeCard.Syncing => _isInstallingRequired ? "Updating realm files" : "Checking realm files",
        HomeCard.Playing => "World of Warcraft is running",
        HomeCard.Ready => RealmBooting ? "The realm is starting up" : "Your game is up to date",
        _ => "Needs attention"
    };

    public string CardDetail => Card switch
    {
        HomeCard.Installing => InstallStatus,
        HomeCard.NoClient => HasInstallStatus ? InstallStatus
            : ClientValid ? "INSTALL WOW and pick this client's folder to update it in place (only files that differ are downloaded, "
                + "files the realm doesn't ship are deleted, addons and settings stay), or pick another folder for a fresh copy."
            : "Download the realm's client. Pick a folder with a 3.3.5a client in it to use it as the base and download only what differs.",
        HomeCard.Ready => HasRepairStatus ? RepairStatus
            : RealmBooting ? "Your game is up to date. Login opens once the realm is ready: "
                + BootProgressText + "."
            : "Patches and addons are installed and verified.",
        _ => LaunchStatus
    };

    public string EnterRealmSubText => Card switch
    {
        HomeCard.Ready => RealmBooting ? "Realm is starting up" : "Game is up to date",
        HomeCard.Playing => "Have fun out there",
        HomeCard.Syncing => _isInstallingRequired ? "Updating realm files..." : "Checking realm files...",
        HomeCard.Installing => "Downloading...",
        HomeCard.NoClient => "Install the game first",
        _ => "Not ready yet"
    };

    public string FooterText
    {
        get
        {
            var text = RealmBranding.LauncherName + " · " + ApplicationVersion;
            // A combat log upload (and how it went) takes the sharing summary's place, in its own
            // brighter line next to this one (CombatLogStatus).
            if (HasCombatLogStatus) return text + " · ";
            return SharingStatus.Length > 0 ? text + " · " + SharingStatus : text;
        }
    }

    private void NotifyHome()
    {
        OnPropertyChanged(nameof(RealmStatusText));
        OnPropertyChanged(nameof(RealmStatusBrush));
        OnPropertyChanged(nameof(RealmBooting));
        OnPropertyChanged(nameof(BootProgressText));
        OnPropertyChanged(nameof(Card));
        OnPropertyChanged(nameof(CardIsNoClient));
        OnPropertyChanged(nameof(CardIsInstalling));
        OnPropertyChanged(nameof(CardIsSyncing));
        OnPropertyChanged(nameof(CardIsReady));
        OnPropertyChanged(nameof(CardIsAttention));
        OnPropertyChanged(nameof(CardShowsBusy));
        OnPropertyChanged(nameof(CardIcon));
        OnPropertyChanged(nameof(CardIconBrush));
        OnPropertyChanged(nameof(CardTitle));
        OnPropertyChanged(nameof(CardDetail));
        OnPropertyChanged(nameof(EnterRealmSubText));
        OnPropertyChanged(nameof(FooterText));
    }
}
