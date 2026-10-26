using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

/// <summary>The quick sorts on the CHARACTERS page.</summary>
public enum CharacterSort { Level, Name, Class, LastPlayed }

/// <summary>
/// Evermore fork: the CHARACTERS page. The account's characters in the order the realm's character
/// select screen shows them (<see cref="CharacterOrderService"/>). Reordering is a draft: drag a row,
/// move the picked one up or down, or sort by level, name, class or last played; nothing reaches the
/// realm until SAVE, and REVERT goes back to the realm's order. The order can't be changed while the
/// game runs: its character select screen has its own copy and would leave that behind on exit.
/// </summary>
public sealed class CharactersViewModel : ViewModelBase, IDisposable
{
    private readonly CharacterOrderService _service;
    private readonly LauncherSession _session;
    private readonly Func<IReadOnlyList<uint>, CancellationToken, Task<CharacterOrderResult>> _save;
    private readonly CancellationTokenSource _closed = new();
    private IReadOnlyList<uint> _saved = [];
    private int _loadVersion;

    private bool _isLoading;
    private bool _isSaving;
    private bool _hasListing;
    private bool _isGameRunning;
    private string _loadError = "";
    private string _statusMessage = "";
    private bool _statusIsError;
    private CharacterOrderRowView? _selectedCharacter;

    /// <param name="save">
    /// Saves an order (guids, top first). The launcher's goes through <see cref="CharacterOrderSync"/>, so
    /// it waits for, or overtakes, an order from the game on its way; null saves with <paramref name="service"/>.
    /// </param>
    public CharactersViewModel(CharacterOrderService service, LauncherSession session, bool gameRunning = false,
        Func<IReadOnlyList<uint>, CancellationToken, Task<CharacterOrderResult>>? save = null)
    {
        _service = service;
        _session = session;
        _isGameRunning = gameRunning;
        _save = save ?? ((guids, cancellationToken) => service.SaveAsync(session, guids, cancellationToken));
    }

    /// <summary>The portal says the login is no longer good (401), with its message.</summary>
    public event Action<string>? LoginExpired;
    /// <summary>The portal answered 404: the realm has no character order.</summary>
    public event Action? Unavailable;

    public ObservableCollection<CharacterOrderRowView> Characters { get; } = [];

    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) NotifyDraft(); } }
    public bool IsSaving { get => _isSaving; private set { if (SetProperty(ref _isSaving, value)) NotifyDraft(); } }
    public bool CanRefresh => !_isLoading && !_isSaving;

    public bool HasListing { get => _hasListing; private set { if (SetProperty(ref _hasListing, value)) NotifyContent(); } }
    public string LoadError { get => _loadError; private set { if (SetProperty(ref _loadError, value)) NotifyContent(); } }
    public bool HasLoadError => _loadError.Length > 0 && !_hasListing;
    public bool ShowFirstLoad => _isLoading && !_hasListing && _loadError.Length == 0;
    public bool HasCharacters => _hasListing && Characters.Count > 0;
    public bool HasNoCharacters => _hasListing && Characters.Count == 0;
    public string CountText => Characters.Count == 1 ? "1 character" : $"{Characters.Count} characters";

    public string StatusMessage { get => _statusMessage; private set { if (SetProperty(ref _statusMessage, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }
    public bool HasStatus => _statusMessage.Length > 0;

    /// <summary>World of Warcraft is running or starting: the order stays as it is until it's closed.</summary>
    public bool IsGameRunning => _isGameRunning;
    public string GameRunningNote => "Close World of Warcraft to change the order.";

    public CharacterOrderRowView? SelectedCharacter
    {
        get => _selectedCharacter;
        set { if (SetProperty(ref _selectedCharacter, value)) NotifyDraft(); }
    }

    public bool CanEdit => _hasListing && !_isSaving && !_isGameRunning && Characters.Count > 1;
    public bool HasUnsavedChanges => _hasListing && !Characters.Select(c => c.Guid).SequenceEqual(_saved);
    public string UnsavedText => HasUnsavedChanges ? "Unsaved changes" : "";
    public bool CanSave => HasUnsavedChanges && CanEdit && !_isLoading;
    public bool CanRevert => HasUnsavedChanges && !_isSaving;
    public bool CanMoveUp => CanEdit && _selectedCharacter is { } row && Characters.IndexOf(row) > 0;
    public bool CanMoveDown => CanEdit && _selectedCharacter is { } row && Characters.IndexOf(row) is var at && at >= 0 && at < Characters.Count - 1;

    public void SetGameRunning(bool running)
    {
        if (_isGameRunning == running) return;
        _isGameRunning = running;
        OnPropertyChanged(nameof(IsGameRunning));
        NotifyDraft();
    }

    // ---------------------------------------------------------
    // The draft
    // ---------------------------------------------------------

    public void MoveUp()
    {
        if (CanMoveUp) Move(_selectedCharacter!, Characters.IndexOf(_selectedCharacter!) - 1);
    }

    public void MoveDown()
    {
        if (CanMoveDown) Move(_selectedCharacter!, Characters.IndexOf(_selectedCharacter!) + 1);
    }

    /// <summary>Puts <paramref name="row"/> at <paramref name="index"/>; the rows between shift over. Dragging a row onto another does this.</summary>
    public bool Move(CharacterOrderRowView row, int index)
    {
        var from = Characters.IndexOf(row);
        if (!CanEdit || from < 0) return false;
        index = Math.Clamp(index, 0, Characters.Count - 1);
        if (index == from) return false;
        Characters.Move(from, index);
        SelectedCharacter = row;
        Renumber();
        return true;
    }

    /// <summary>A quick sort; saved only with SAVE.</summary>
    public void Sort(CharacterSort sort)
    {
        if (!CanEdit) return;
        var name = StringComparer.CurrentCultureIgnoreCase;
        var rows = Characters.ToList();
        IEnumerable<CharacterOrderRowView> sorted = sort switch
        {
            CharacterSort.Level => rows.OrderByDescending(r => r.Level).ThenBy(r => r.Name, name),
            CharacterSort.Name => rows.OrderBy(r => r.Name, name),
            CharacterSort.Class => rows.OrderBy(r => r.ClassName, name).ThenByDescending(r => r.Level).ThenBy(r => r.Name, name),
            // Online now is the most recent; never played goes last.
            _ => rows.OrderByDescending(r => r.Online).ThenByDescending(r => r.Character.LastPlayed ?? DateTimeOffset.MinValue).ThenBy(r => r.Name, name),
        };
        Replace(sorted.ToList());
    }

    /// <summary>Back to the order the realm has.</summary>
    public void Revert()
    {
        if (_isSaving) return;
        var byGuid = Characters.ToDictionary(c => c.Guid);
        Replace(_saved.Where(byGuid.ContainsKey).Select(g => byGuid[g]).ToList());
    }

    private void Replace(IReadOnlyList<CharacterOrderRowView> rows)
    {
        var selected = _selectedCharacter;
        Characters.Clear();
        foreach (var row in rows) Characters.Add(row);
        _selectedCharacter = selected is not null && rows.Contains(selected) ? selected : null;
        OnPropertyChanged(nameof(SelectedCharacter));
        Renumber();
    }

    private void Renumber()
    {
        for (var i = 0; i < Characters.Count; i++) Characters[i].Position = i + 1;
        NotifyDraft();
    }

    // ---------------------------------------------------------
    // The portal
    // ---------------------------------------------------------

    /// <summary>Asks the portal for the list. A save's message stays up when <paramref name="keepStatus"/>.</summary>
    public async Task LoadAsync(bool keepStatus = false)
    {
        var version = ++_loadVersion;
        IsLoading = true;
        if (!keepStatus) SetStatus("", false);
        try
        {
            var characters = await _service.GetCharactersAsync(_session, _closed.Token);
            if (version != _loadVersion) return;
            if (characters is null)
            {
                LoadError = "This realm doesn't keep a character order.";
                HasListing = false;
                Unavailable?.Invoke();
                return;
            }
            Apply(characters);
            LoadError = "";
        }
        catch (LauncherApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            if (version != _loadVersion) return;
            ShowLoadError(ex.Message, keepStatus);
            LoginExpired?.Invoke(ex.Message);
        }
        catch (LauncherApiException ex)
        {
            if (version == _loadVersion) ShowLoadError(ex.Message, keepStatus);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException
            or InvalidOperationException or NotSupportedException)
        {
            if (_closed.IsCancellationRequested || version != _loadVersion) return;
            ShowLoadError(UserErrorService.Format(ex, "Couldn't ask for your characters"), keepStatus);
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    /// <summary>SAVE: the whole order. A refusal keeps the draft.</summary>
    public async Task SaveAsync()
    {
        if (!CanSave) return;
        var guids = Characters.Select(c => c.Guid).ToArray();
        IsSaving = true;
        SetStatus("Saving the order...", false);
        CharacterOrderResult result;
        try { result = await _save(guids, _closed.Token); }
        catch (OperationCanceledException) { return; }
        finally { IsSaving = false; }

        switch (result.Outcome)
        {
            case CharacterOrderOutcome.Saved when result.Characters is { } characters:
                ++_loadVersion; // an older listing on its way is out of date
                IsLoading = false;
                Apply(characters, keepDraft: false);
                SetStatus("Saved. The character select screen shows this order the next time you log in.", false);
                return;
            case CharacterOrderOutcome.LoggedOut:
                SetStatus(result.Message, true);
                LoginExpired?.Invoke(result.Message);
                return;
            case CharacterOrderOutcome.Unavailable:
                SetStatus(result.Message, true);
                Unavailable?.Invoke();
                return;
            case CharacterOrderOutcome.NotTried:
                SetStatus(result.Message, true);
                return;
            default:
                // Refused (a character gone) or no answer: what the realm has now, draft kept.
                SetStatus(result.Message, true);
                break;
        }
        if (_closed.IsCancellationRequested) return;
        await LoadAsync(keepStatus: true);
    }

    /// <summary>The realm's list from elsewhere (an order the game left, sent after it closed).</summary>
    public void ApplyFromRealm(IReadOnlyList<OrderedCharacter> characters)
    {
        if (_isSaving) return;
        ++_loadVersion;
        IsLoading = false;
        Apply(characters);
    }

    /// <summary>
    /// The realm's list. With unsaved changes (and <paramref name="keepDraft"/>) the draft order stays
    /// for the characters still there, and new ones go at the end.
    /// </summary>
    private void Apply(IReadOnlyList<OrderedCharacter> characters, bool keepDraft = true)
    {
        var draft = keepDraft && HasUnsavedChanges ? Characters.Select(c => c.Guid).ToList() : null;
        var selected = _selectedCharacter?.Guid;
        _saved = characters.Select(c => c.Guid).ToArray();
        var rows = characters.Select(c => new CharacterOrderRowView(c)).ToList();
        if (draft is not null)
            rows = rows.OrderBy(r => draft.IndexOf(r.Guid) is var at && at >= 0 ? at : int.MaxValue).ToList();
        Characters.Clear();
        foreach (var row in rows) Characters.Add(row);
        _selectedCharacter = rows.FirstOrDefault(r => r.Guid == selected);
        HasListing = true;
        OnPropertyChanged(nameof(SelectedCharacter));
        Renumber();
        NotifyContent();
    }

    private void ShowLoadError(string message, bool afterSave)
    {
        if (!_hasListing) LoadError = message;
        else if (afterSave && HasStatus) SetStatus(_statusMessage + " The list couldn't be refreshed: " + message, _statusIsError);
        else SetStatus(message, true);
    }

    private void SetStatus(string message, bool error)
    {
        StatusIsError = error;
        StatusMessage = message;
    }

    private void NotifyDraft()
    {
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(UnsavedText));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanRevert));
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
        OnPropertyChanged(nameof(ShowFirstLoad));
    }

    private void NotifyContent()
    {
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(ShowFirstLoad));
        OnPropertyChanged(nameof(HasCharacters));
        OnPropertyChanged(nameof(HasNoCharacters));
        OnPropertyChanged(nameof(CountText));
        NotifyDraft();
    }

    public void Dispose()
    {
        _closed.Cancel();
        _closed.Dispose();
    }
}

/// <summary>One row of the CHARACTERS page.</summary>
public sealed class CharacterOrderRowView(OrderedCharacter character) : ViewModelBase
{
    private int _position;
    private bool _isDropTarget;

    public OrderedCharacter Character { get; } = character;
    public uint Guid => Character.Guid;
    public string Name => Character.Name.Length > 0 ? Character.Name : "Character " + Character.Guid;
    public int Level => Character.Level;
    public string LevelText => Character.Level.ToString(CultureInfo.InvariantCulture);
    public string ClassName { get; } = ArmoryNames.ClassName(character.Class);
    public string Detail => $"{ArmoryNames.RaceName(Character.Race)} {ClassName}";
    public IBrush ClassBrush { get; } = ArmoryNames.ClassBrush(character.Class);
    public bool Online => Character.Online;
    public string LastPlayedText => Online ? "Online now" : LastPlayed(Character.LastPlayed, DateTimeOffset.Now);

    /// <summary>1 is the top of the character select screen.</summary>
    public int Position { get => _position; set { if (SetProperty(ref _position, value)) OnPropertyChanged(nameof(PositionText)); } }
    public string PositionText => _position.ToString(CultureInfo.InvariantCulture);
    /// <summary>A dragged row is over this one.</summary>
    public bool IsDropTarget { get => _isDropTarget; set => SetProperty(ref _isDropTarget, value); }

    public static string LastPlayed(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } played) return "Never played";
        var days = (now.LocalDateTime.Date - played.ToLocalTime().Date).Days;
        return days switch
        {
            <= 0 => "Played today",
            1 => "Played yesterday",
            < 30 => $"Played {days} days ago",
            _ => "Played " + played.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture),
        };
    }
}
