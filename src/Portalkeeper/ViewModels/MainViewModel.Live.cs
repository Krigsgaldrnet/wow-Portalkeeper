using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

/// <summary>One patch note on the home screen: a tag ("NEW", "FIX", "DRUID"), its headline and maybe a line under it.</summary>
public sealed record WhatsNewItem(string Tag, IBrush TagBrush, string Headline, string? Detail);

/// <summary>One world event on the home screen: its name and, for the holidays the launcher knows, what it is.</summary>
public sealed record LiveEventItem(string Name, string? About);

// Evermore fork: the home screen's right column under the realm status pill. "Live" is what the
// portal says is going on now (players online, a pending restart, world events, world bosses up), asked
// along with the realm status every 45 seconds. "What's new" is the patch notes merged since the
// player last pressed ENTER REALM, asked at start, after the game exits and every 15 minutes. Both
// come from the built-in realm's portal only, and both simply hide when it doesn't answer.
public sealed partial class MainViewModel
{
    private readonly RealmLiveService _realmLive = new();
    private readonly SemaphoreSlim _homeInfoLock = new(1, 1);
    private RealmLive? _live;
    private DateTimeOffset _liveReceived;
    private WhatsNew? _whatsNew;
    private DateTimeOffset? _whatsNewSince;
    private int _healthTicks;
    // A what's new refresh asked for while another refresh ran: that one picks it up.
    private bool _whatsNewWanted;

    // Notes asked for at once; the column shows what fits and scrolls the rest.
    private const int WhatsNewLimit = 12;
    // A live answer older than this (the portal stopped answering) isn't shown any more.
    private static readonly TimeSpan LiveShownFor = TimeSpan.FromMinutes(3);
    // Health loop ticks (45 s each) between two what's new refreshes.
    private const int WhatsNewEveryTicks = 20;

    private bool HomeInfoApplies => RealmBranding.IsBuiltIn(_realmInfo);

    // ---------------------------------------------------------
    // Live
    // ---------------------------------------------------------

    private RealmLive? Live => HomeInfoApplies && _live is not null && DateTimeOffset.UtcNow - _liveReceived < LiveShownFor ? _live : null;

    public bool HasLiveInfo => HasLivePlayers || HasLiveRestart || HasLiveEvents || HasLiveBosses;

    // Players only, not bots: once the realm is up every bot is online, so the bot count never moves,
    // and while it boots the status card counts the bots logging in (realm-status.json, every 10
    // seconds) while the portal's count is a snapshot up to 30 seconds old, asked every 45.
    public bool HasLivePlayers => Live?.Players is not null && ShowsPlayers(_realmHealthState);
    public string LivePlayersText => Live?.Players is { } p ? DescribePlayers(p) : "";

    /// <summary>
    /// Whether the portal's online count means anything: not while the realm boots (nobody can log in
    /// yet) or while its world server is down (the characters table still marks the last session online).
    /// </summary>
    internal static bool ShowsPlayers(RealmHealthState state) =>
        state is not (RealmHealthState.Booting or RealmHealthState.Offline or RealmHealthState.AuthOnly);

    /// <summary>"1 player online", "3 players online".</summary>
    internal static string DescribePlayers(RealmLivePlayers players) =>
        $"{players.Online} player{(players.Online == 1 ? "" : "s")} online";

    // A restart closer than this greys out ENTER REALM: not worth loading in for.
    private static readonly TimeSpan RestartBlocksEntryWithin = TimeSpan.FromMinutes(10);

    /// <summary>The realm restarts (or shuts down) within <see cref="RestartBlocksEntryWithin"/>.</summary>
    public bool RestartImminent => IsRestartImminent(Live?.RestartAt, DateTimeOffset.UtcNow);

    internal static bool IsRestartImminent(DateTimeOffset? restartAt, DateTimeOffset now) =>
        restartAt is { } at && at - now < RestartBlocksEntryWithin;

    public bool HasLiveRestart => Live?.RestartAt is { } at && at > DateTimeOffset.UtcNow.AddSeconds(-30);
    public string LiveRestartText => Live?.RestartAt is { } at ? "Realm restarts " + DescribeIn(at - DateTimeOffset.UtcNow) : "";
    public IBrush LiveRestartBrush => Brush.Parse(Live?.RestartAt is { } at && at - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(5) ? "#E5604D" : "#F2B84B");

    public bool HasLiveEvents => Live?.Events is { Count: > 0 };
    public IReadOnlyList<LiveEventItem> LiveEvents => Live?.Events is { Count: > 0 } events ? ShownEvents(events) : [];
    public string LiveEventsMoreText => Live?.Events is { Count: > 0 } events ? DescribeMoreEvents(events) : "";
    public bool HasLiveEventsMore => LiveEventsMoreText.Length > 0;

    // World bosses only: the portal's count of every rare up isn't news to a player.
    public bool HasLiveBosses => Live?.Rares?.WorldBosses is { Count: > 0 };
    public string LiveBossesText => Live?.Rares?.WorldBosses is { Count: > 0 } bosses ? DescribeBosses(bosses) : "";
    /// <summary>Green with every world boss up, yellow with some, red with none.</summary>
    public IBrush LiveBossesBrush => Brush.Parse(Live?.Rares?.WorldBosses is { Count: > 0 } bosses ? BossesColour(bosses) : "#E5604D");

    internal static string BossesColour(IReadOnlyList<RealmLiveBoss> bosses)
    {
        var up = bosses.Count(b => b.Up);
        return up == 0 ? "#E5604D" : up == bosses.Count ? "#3DDC84" : "#F2B84B";
    }
    public string RareMapUrl => RealmBranding.RareMapUrl;

    /// <summary>"in about 9 min", "in under a minute", "in about 2 h 5 min", "now".</summary>
    internal static string DescribeIn(TimeSpan left)
    {
        if (left <= TimeSpan.Zero) return "now";
        if (left < TimeSpan.FromMinutes(1)) return "in under a minute";
        if (left < TimeSpan.FromHours(1)) return $"in about {(int)Math.Ceiling(left.TotalMinutes)} min";
        return $"in about {(int)left.TotalHours} h {left.Minutes} min";
    }

    /// <summary>"3 of 8 world bosses up", "1 of 1 world boss up".</summary>
    internal static string DescribeBosses(IReadOnlyList<RealmLiveBoss> bosses) =>
        $"{bosses.Count(b => b.Up)} of {bosses.Count} world boss{(bosses.Count == 1 ? "" : "es")} up";

    // Events listed one per row; past this many the box says how many more are on.
    private const int LiveEventsShown = 4;

    /// <summary>
    /// The events as the home screen lists them: one row per holiday (the realm can run a holiday as
    /// several game events, like the fishing pools apart from the contest), at most <see cref="LiveEventsShown"/>.
    /// </summary>
    internal static IReadOnlyList<LiveEventItem> ShownEvents(IReadOnlyList<RealmLiveEvent> events) =>
        AllEvents(events).Take(LiveEventsShown).ToArray();

    /// <summary>"+2 more events on", or "" when every event fits.</summary>
    internal static string DescribeMoreEvents(IReadOnlyList<RealmLiveEvent> events)
    {
        var more = AllEvents(events).Count() - LiveEventsShown;
        return more > 0 ? $"+{more} more event{(more == 1 ? "" : "s")} on" : "";
    }

    private static IEnumerable<LiveEventItem> AllEvents(IReadOnlyList<RealmLiveEvent> events) =>
        events.Select(e => DescribeEvent(e.Name)).DistinctBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A game event's name as a player knows it, and a line on what it is: "Stranglethorn Fishing
    /// Extravaganza Fishing Pools" is the Stranglethorn Fishing Extravaganza, "Darkmoon Faire (Mulgore)"
    /// the faire in Mulgore. Events the launcher doesn't know (the realm's own) keep their name, with no line.
    /// </summary>
    internal static LiveEventItem DescribeEvent(string name)
    {
        name = name.Trim();
        if (name.StartsWith("Call to Arms:", StringComparison.OrdinalIgnoreCase))
        {
            var battleground = name["Call to Arms:".Length..].Trim().TrimEnd('!');
            return new LiveEventItem("Call to Arms: " + battleground, $"Extra honor for {battleground} battles while it lasts.");
        }
        if (name.StartsWith("Darkmoon Faire", StringComparison.OrdinalIgnoreCase))
        {
            var open = name.IndexOf('(');
            var where = open > 0 && name.EndsWith(')') ? name[(open + 1)..^1].Trim() : null;
            return new LiveEventItem("Darkmoon Faire", where is { Length: > 0 }
                ? $"The faire is in {where}: games, prize tickets and Darkmoon cards."
                : "Games, prize tickets and Darkmoon cards.");
        }
        foreach (var (holiday, about) in HolidayAbout)
        {
            if (name.StartsWith(holiday, StringComparison.OrdinalIgnoreCase))
                return new LiveEventItem(holiday, about);
        }
        return new LiveEventItem(name, null);
    }

    // The holidays AzerothCore runs (game_event rows with a holiday), by the start of their name.
    private static readonly (string Holiday, string About)[] HolidayAbout =
    [
        ("Stranglethorn Fishing Extravaganza", "Fishing contest: the first to bring Riggle Bassbait in Booty Bay 40 Speckled Tastyfish wins."),
        ("Kalu'ak Fishing Derby", "Fishing contest in Dalaran: the first Blacktip Shark brought to Elder Clearwater wins."),
        ("Hallow's End", "Trick or treat at the inns, put out the fires in the towns and face the Headless Horseman."),
        ("Brewfest", "Ram races, festival brews and Coren Direbrew in Blackrock Depths."),
        ("Harvest Festival", "Honor fallen heroes at the feasts outside Orgrimmar and Ironforge."),
        ("Pirates' Day", "Booty Bay throws a party, and Dread Captain DeMeza turns guests into pirates."),
        ("Day of the Dead", "Remember the dead with Catrina at the city graveyards."),
        ("Pilgrim's Bounty", "Cook the feast and share the Bountiful Tables in the cities."),
        ("Winter Veil", "Gifts under the tree, Greatfather Winter in the capitals and the Abominable Greench."),
        ("Lunar Festival", "Honor the elders across the world and celebrate in Moonglade."),
        ("Love is in the Air", "Gifts and perfume in the cities, and Apothecary Hummel in Shadowfang Keep."),
        ("Noblegarden", "Hunt for colored eggs around the starting towns."),
        ("Children's Week", "Take an orphan from the cities on outings for a pet."),
        ("Midsummer Fire Festival", "Honor or desecrate the bonfires, and face Ahune in the Slave Pens."),
        ("Fireworks Spectacular", "Fireworks over the capital cities.")
    ];

    // ---------------------------------------------------------
    // What's new
    // ---------------------------------------------------------

    public bool HasWhatsNew => _whatsNew is not null && HomeInfoApplies;

    public string WhatsNewTitle => _whatsNewSince is null ? "RECENT CHANGES" : "NEW SINCE YOU LAST PLAYED";

    public IReadOnlyList<WhatsNewItem> WhatsNewItems => _whatsNew?.Notes.Select(ToItem).ToArray() ?? [];

    public bool HasWhatsNewItems => _whatsNew is { Notes.Count: > 0 };

    public string WhatsNewEmptyText => "Nothing new since you last played.";

    public string WhatsNewMoreText => _whatsNew is { More: > 0 } news ? $"+{news.More} more" : "";

    public string WhatsNewUrl => _whatsNew?.Url is { Length: > 0 } url ? url : RealmBranding.PatchNotesUrl;

    internal static WhatsNewItem ToItem(PatchNote note)
    {
        var (tag, colour) = note.Kind switch
        {
            "feature" => ("NEW", "#F6D477"),
            "fix" => ("FIX", "#3DDC84"),
            _ => ((note.Sub ?? note.Section).ToUpperInvariant(), "#8FB8FF")
        };
        var first = note.Text.FirstOrDefault() ?? "";
        return note.Title is { Length: > 0 } title
            ? new WhatsNewItem(tag, Brush.Parse(colour), title, first.Length > 0 ? first : null)
            : new WhatsNewItem(tag, Brush.Parse(colour), first, note.Text.Count > 1 ? note.Text[1] : null);
    }

    // ---------------------------------------------------------
    // Refreshing
    // ---------------------------------------------------------

    /// <summary>
    /// Asks the portal what's going on and, with <paramref name="whatsNew"/>, for the patch notes too.
    /// Whatever can't be asked keeps its last answer (live info hides once it's a few minutes old).
    /// </summary>
    private async Task RefreshHomeInfoAsync(bool whatsNew)
    {
        if (!HomeInfoApplies)
        {
            NotifyHomeInfo(); // another realm: the column empties
            return;
        }
        if (whatsNew) _whatsNewWanted = true;
        if (!await _homeInfoLock.WaitAsync(0)) return; // one refresh at a time; it takes what was asked meanwhile
        try
        {
            try
            {
                _live = await _realmLive.GetLiveAsync();
                _liveReceived = DateTimeOffset.UtcNow;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
            {
                // The column shows the last answer until it's too old.
            }
            if (_whatsNewWanted)
            {
                _whatsNewWanted = false;
                var since = _savedSettings.LastPlayedUtc;
                try
                {
                    _whatsNew = await _realmLive.GetWhatsNewAsync(since, WhatsNewLimit);
                    _whatsNewSince = since;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException) { }
            }
        }
        finally
        {
            _homeInfoLock.Release();
            NotifyHomeInfo();
        }
    }

    /// <summary>The health loop's tick: live info every time, what's new every 15 minutes.</summary>
    private Task RefreshHomeInfoOnTickAsync() => RefreshHomeInfoAsync(whatsNew: ++_healthTicks % WhatsNewEveryTicks == 0);

    /// <summary>ENTER REALM: what's new from now on is what comes after this session.</summary>
    private void RememberPlayed(DateTimeOffset launchedAt)
    {
        _savedSettings.LastPlayedUtc = launchedAt;
        try { SaveSettings(); } catch (Exception) { }
    }

    private void NotifyHomeInfo()
    {
        OnPropertyChanged(nameof(HasLiveInfo));
        OnPropertyChanged(nameof(HasLivePlayers));
        OnPropertyChanged(nameof(LivePlayersText));
        OnPropertyChanged(nameof(HasLiveRestart));
        OnPropertyChanged(nameof(LiveRestartText));
        OnPropertyChanged(nameof(LiveRestartBrush));
        OnPropertyChanged(nameof(HasLiveEvents));
        OnPropertyChanged(nameof(LiveEvents));
        OnPropertyChanged(nameof(LiveEventsMoreText));
        OnPropertyChanged(nameof(HasLiveEventsMore));
        OnPropertyChanged(nameof(HasLiveBosses));
        OnPropertyChanged(nameof(LiveBossesText));
        OnPropertyChanged(nameof(LiveBossesBrush));
        OnPropertyChanged(nameof(HasWhatsNew));
        OnPropertyChanged(nameof(WhatsNewTitle));
        OnPropertyChanged(nameof(WhatsNewItems));
        OnPropertyChanged(nameof(HasWhatsNewItems));
        OnPropertyChanged(nameof(WhatsNewMoreText));
        OnPropertyChanged(nameof(WhatsNewUrl));
        // A restart coming up closes ENTER REALM, and one called off opens it again.
        OnPropertyChanged(nameof(RestartImminent));
        OnPropertyChanged(nameof(CanEnterRealm));
        NotifyHome();
    }
}
