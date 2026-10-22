using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Media;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

/// <summary>One note on the Patch Notes page: its tag ("NEW", "FIX", "DRUID"), where it belongs, and its lines.</summary>
public sealed record PatchNoteItem(string Tag, IBrush TagBrush, string Section, string? Title, IReadOnlyList<string> Lines)
{
    public bool HasTitle => Title is { Length: > 0 };
    public bool HasLines => Lines.Count > 0;
}

/// <summary>The notes of one day, newest day first on the page.</summary>
public sealed record PatchNotesDay(string Title, IReadOnlyList<PatchNoteItem> Notes);

// Evermore fork: the PATCH NOTES page. What changed on the realm lately, from the portal's
// whatsnew.php (the same notes the home screen shows since the player last played, here for the
// last 90 days, as many as the portal gives at once), grouped by day. The full notes stay on the
// website; the page links there for what didn't fit.
public sealed class PatchNotesViewModel : ViewModelBase
{
    private const int NotesAsked = 50;
    private static readonly TimeSpan Span = TimeSpan.FromDays(90);

    private readonly RealmLiveService _realmLive;
    private readonly string _fallbackUrl;
    private bool _isLoading;
    private bool _loaded;
    private string _status = "";
    private string _url;
    private int _more;
    private IReadOnlyList<PatchNotesDay> _days = [];

    public PatchNotesViewModel(RealmLiveService realmLive, string fallbackUrl)
    {
        _realmLive = realmLive;
        _fallbackUrl = fallbackUrl;
        _url = fallbackUrl;
    }

    public IReadOnlyList<PatchNotesDay> Days { get => _days; private set { if (SetProperty(ref _days, value)) NotifyContent(); } }
    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) NotifyContent(); } }
    /// <summary>Why there's nothing to show, or nothing when there is.</summary>
    public string Status { get => _status; private set { if (SetProperty(ref _status, value)) NotifyContent(); } }
    public bool HasNotes => _days.Count > 0;
    public bool ShowFirstLoad => _isLoading && !_loaded;
    public bool ShowStatus => !HasNotes && !ShowFirstLoad && _status.Length > 0;
    public bool CanRefresh => !_isLoading;
    /// <summary>The patch notes page on the website.</summary>
    public string Url => _url;
    public string MoreText => _more > 0 ? $"+{_more} more on the website" : "";
    public bool HasMore => _more > 0;

    /// <summary>Asks the portal for the notes. Whatever is shown stays while it can't answer.</summary>
    public async Task LoadAsync()
    {
        if (_isLoading) return;
        IsLoading = true;
        try
        {
            var news = await _realmLive.GetWhatsNewAsync(DateTimeOffset.UtcNow - Span, NotesAsked);
            _loaded = true;
            if (news is null)
            {
                Status = "This realm has no patch notes.";
                _more = 0;
                Days = [];
                return;
            }
            _url = news.Url is { Length: > 0 } url ? url : _fallbackUrl;
            _more = news.More;
            OnPropertyChanged(nameof(Url));
            OnPropertyChanged(nameof(MoreText));
            OnPropertyChanged(nameof(HasMore));
            Days = Group(news.Notes);
            Status = Days.Count == 0 ? "Nothing changed in the last 90 days." : "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            _loaded = true;
            if (!HasNotes) Status = "The patch notes couldn't be loaded. " + UserErrorService.Format(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>The notes by day, newest first, in the order the portal gave within a day.</summary>
    internal static IReadOnlyList<PatchNotesDay> Group(IReadOnlyList<PatchNote> notes) =>
        notes
            .GroupBy(note => note.Date.ToLocalTime().Date)
            .OrderByDescending(day => day.Key)
            .Select(day => new PatchNotesDay(
                day.Key.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture).ToUpperInvariant(),
                day.Select(ToItem).ToArray()))
            .ToArray();

    internal static PatchNoteItem ToItem(PatchNote note)
    {
        var (tag, colour) = note.Kind switch
        {
            "feature" => ("NEW", "#F6D477"),
            "fix" => ("FIX", "#3DDC84"),
            _ => ("CHANGE", "#8FB8FF")
        };
        var section = note.Sub is { Length: > 0 } sub ? note.Section + " · " + sub : note.Section;
        return new PatchNoteItem(tag, Brush.Parse(colour), section, note.Title, note.Text);
    }

    private void NotifyContent()
    {
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(ShowFirstLoad));
        OnPropertyChanged(nameof(ShowStatus));
        OnPropertyChanged(nameof(CanRefresh));
    }
}
