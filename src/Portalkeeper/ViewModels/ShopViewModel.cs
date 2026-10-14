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

/// <summary>
/// Evermore fork: the Shop window. Spends the Tarnished Undermine Reals in the account's wallet on
/// mounts, companions and character services for one of the account's characters
/// (<see cref="CoinShopService"/>). The listing is asked for when the window opens, after every
/// purchase and on REFRESH; the portal keeps it for 15 seconds, so nothing here polls.
/// </summary>
public sealed class ShopViewModel : ViewModelBase, IDisposable
{
    private readonly CoinShopService _service;
    private readonly LauncherSession _session;
    private readonly CancellationTokenSource _closed = new();
    private readonly List<ShopOfferView> _allOffers = [];
    private int _loadVersion;

    private bool _isLoading;
    private bool _isBuying;
    private bool _hasListing;
    private string _loadError = "";
    private int _balance;
    private ShopCharacterView? _selectedCharacter;
    private ShopCategoryView? _selectedCategory;
    private string _statusMessage = "";
    private bool _statusIsError;
    private ShopOfferView? _pendingOffer;
    private ShopCharacterView? _pendingCharacter;

    public ShopViewModel(CoinShopService service, LauncherSession session)
    {
        _service = service;
        _session = session;
    }

    /// <summary>Raised when the portal says the login is no longer good (401/403), with its message.</summary>
    public event Action<string>? LoginExpired;

    public ObservableCollection<ShopCharacterView> Characters { get; } = [];
    public ObservableCollection<ShopCategoryView> Categories { get; } = [];
    public ObservableCollection<ShopOfferView> Offers { get; } = [];

    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) NotifyBusy(); } }
    public bool IsBuying { get => _isBuying; private set { if (SetProperty(ref _isBuying, value)) NotifyBusy(); } }
    public bool IsBusy => _isLoading || _isBuying;
    public bool CanRefresh => !IsBusy;

    public bool HasListing { get => _hasListing; private set { if (SetProperty(ref _hasListing, value)) NotifyContent(); } }
    public string LoadError { get => _loadError; private set { if (SetProperty(ref _loadError, value)) NotifyContent(); } }
    public bool HasLoadError => _loadError.Length > 0 && !_hasListing;
    /// <summary>The first listing is still on its way.</summary>
    public bool ShowFirstLoad => _isLoading && !_hasListing && _loadError.Length == 0;
    public bool ShowShop => _hasListing;
    public bool HasNoCharacters => _hasListing && Characters.Count == 0;
    public bool HasCharacters => Characters.Count > 0;
    public bool HasNoOffers => _hasListing && Offers.Count == 0;
    public bool HasBalance => _hasListing;

    public int Balance
    {
        get => _balance;
        private set
        {
            if (!SetProperty(ref _balance, value)) return;
            OnPropertyChanged(nameof(BalanceText));
            OnPropertyChanged(nameof(BalanceCaption));
            UpdateOfferStates();
        }
    }
    public string BalanceText => _balance.ToString("N0", CultureInfo.CurrentCulture);
    public string BalanceCaption => _balance == 1 ? "Tarnished Undermine Real" : "Tarnished Undermine Reals";

    public ShopCharacterView? SelectedCharacter
    {
        get => _selectedCharacter;
        set
        {
            if (!SetProperty(ref _selectedCharacter, value)) return;
            UpdateOfferStates();
        }
    }

    public ShopCategoryView? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is null || !SetProperty(ref _selectedCategory, value)) return;
            foreach (var category in Categories) category.IsSelected = ReferenceEquals(category, value);
            FilterOffers();
        }
    }

    public string StatusMessage { get => _statusMessage; private set { if (SetProperty(ref _statusMessage, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }
    public bool HasStatus => _statusMessage.Length > 0;

    public ShopOfferView? PendingOffer
    {
        get => _pendingOffer;
        private set
        {
            if (!SetProperty(ref _pendingOffer, value)) return;
            OnPropertyChanged(nameof(IsConfirming));
            OnPropertyChanged(nameof(ConfirmText));
            OnPropertyChanged(nameof(ConfirmDetail));
        }
    }
    public bool IsConfirming => _pendingOffer is not null;
    public string ConfirmText => _pendingOffer is null || _pendingCharacter is null ? ""
        : $"Buy {_pendingOffer.Name} for {_pendingOffer.PriceText} for {_pendingCharacter.Name}?";
    public string ConfirmDetail => _pendingOffer is null ? ""
        : (_balance >= _pendingOffer.Price
            ? $"You have {BalanceText}; {(_balance - _pendingOffer.Price).ToString("N0", CultureInfo.CurrentCulture)} will be left. "
            : $"You have {BalanceText}, which isn't enough for this. ")
          + _pendingOffer.Description;

    /// <summary>Asks the portal for the listing. A purchase's message stays up when <paramref name="keepStatus"/>.</summary>
    public async Task LoadAsync(bool keepStatus = false)
    {
        var version = ++_loadVersion;
        IsLoading = true;
        if (!keepStatus) SetStatus("", false);
        try
        {
            var listing = await _service.GetListingAsync(_session, _closed.Token);
            if (version != _loadVersion) return;
            if (listing is null)
            {
                LoadError = "This realm has no shop.";
                HasListing = false;
                return;
            }
            Apply(listing);
            LoadError = "";
        }
        catch (LauncherApiException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            if (version != _loadVersion) return;
            ShowLoadError(ex.Message);
            LoginExpired?.Invoke(ex.Message);
        }
        catch (LauncherApiException ex)
        {
            if (version == _loadVersion) ShowLoadError(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException
            or InvalidOperationException or NotSupportedException)
        {
            if (_closed.IsCancellationRequested || version != _loadVersion) return;
            ShowLoadError(UserErrorService.Format(ex, "Couldn't reach the shop"));
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    /// <summary>
    /// Shown where the listing would be when there's none yet; with a listing up it stays up (the
    /// balance may be a little old) and the error shows in the status line.
    /// </summary>
    private void ShowLoadError(string message)
    {
        if (_hasListing) SetStatus(message, true);
        else LoadError = message;
    }

    /// <summary>
    /// BUY on a card: the confirm step, for the character picked right now (a listing that arrives
    /// meanwhile doesn't change what was asked; the game server checks it again anyway).
    /// </summary>
    public void AskToBuy(ShopOfferView offer)
    {
        if (IsBuying || !offer.CanBuy || _selectedCharacter is null) return;
        _pendingCharacter = _selectedCharacter;
        PendingOffer = offer;
    }

    public void CancelBuy() => PendingOffer = null;

    public async Task ConfirmBuyAsync()
    {
        var offer = _pendingOffer;
        var character = _pendingCharacter;
        PendingOffer = null;
        if (offer is null || character is null || IsBuying) return;

        IsBuying = true;
        SetStatus($"Buying {offer.Name} for {character.Name}...", false);
        ShopPurchaseResult result;
        try { result = await _service.BuyAsync(_session, offer.Offer.Id, character.Guid); }
        finally { IsBuying = false; }

        switch (result.Outcome)
        {
            case ShopPurchaseOutcome.Bought:
                SetStatus(result.Message, false);
                if (result.Balance is int balance) Balance = balance;
                break;
            case ShopPurchaseOutcome.LoggedOut:
                SetStatus(result.Message, true);
                LoginExpired?.Invoke(result.Message);
                return;
            case ShopPurchaseOutcome.NotTried:
                // Turned away before the game server was asked: nothing changed, nothing to fetch.
                SetStatus(result.Message, true);
                return;
            default:
                SetStatus(result.Message, true);
                break;
        }
        if (_closed.IsCancellationRequested) return;
        await LoadAsync(keepStatus: true);
    }

    private void SetStatus(string message, bool error)
    {
        StatusIsError = error;
        StatusMessage = message;
    }

    private void Apply(ShopListing listing)
    {
        var selectedGuid = _selectedCharacter?.Guid;
        var selectedCategory = _selectedCategory?.Key;

        Characters.Clear();
        foreach (var character in listing.Characters
                     .OrderByDescending(c => c.Level).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            Characters.Add(new ShopCharacterView(character));

        _allOffers.Clear();
        _allOffers.AddRange(listing.Offers.Select(o => new ShopOfferView(o)));

        Categories.Clear();
        var keys = _allOffers.Select(o => o.Offer.Category).Distinct().OrderBy(ShopCategoryView.Order).ThenBy(k => k, StringComparer.Ordinal).ToList();
        if (keys.Count > 1) Categories.Add(new ShopCategoryView(ShopCategoryView.AllKey, _allOffers.Count));
        foreach (var key in keys) Categories.Add(new ShopCategoryView(key, _allOffers.Count(o => o.Offer.Category == key)));

        HasListing = true;
        if (_balance != listing.Balance)
        {
            _balance = listing.Balance;
            OnPropertyChanged(nameof(Balance));
            OnPropertyChanged(nameof(BalanceText));
            OnPropertyChanged(nameof(BalanceCaption));
        }

        _selectedCategory = null;
        SelectedCategory = Categories.FirstOrDefault(c => c.Key == selectedCategory) ?? Categories.FirstOrDefault();
        if (_selectedCategory is null) FilterOffers();

        // Set without the setter's guard: the cards are new and need their states either way.
        _selectedCharacter = Characters.FirstOrDefault(c => c.Guid == selectedGuid)
            ?? Characters.FirstOrDefault(c => c.Online)
            ?? Characters.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCharacter));
        UpdateOfferStates();
        NotifyContent();
    }

    private void FilterOffers()
    {
        Offers.Clear();
        var key = _selectedCategory?.Key ?? ShopCategoryView.AllKey;
        foreach (var offer in _allOffers.Where(o => key == ShopCategoryView.AllKey || o.Offer.Category == key))
            Offers.Add(offer);
        OnPropertyChanged(nameof(HasNoOffers));
    }

    private void UpdateOfferStates()
    {
        foreach (var offer in _allOffers) offer.Update(_selectedCharacter, _balance);
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(ConfirmDetail));
    }

    private void NotifyBusy()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(ShowFirstLoad));
    }

    private void NotifyContent()
    {
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(ShowFirstLoad));
        OnPropertyChanged(nameof(ShowShop));
        OnPropertyChanged(nameof(HasNoCharacters));
        OnPropertyChanged(nameof(HasCharacters));
        OnPropertyChanged(nameof(HasNoOffers));
        OnPropertyChanged(nameof(HasBalance));
    }

    public void Dispose()
    {
        if (_closed.IsCancellationRequested) return;
        _closed.Cancel();
        _closed.Dispose();
    }
}

public sealed class ShopCharacterView(ShopCharacter character)
{
    public ShopCharacter Character { get; } = character;
    public uint Guid => Character.Guid;
    public string Name => Character.Name.Length > 0 ? Character.Name : "Character " + Character.Guid;
    public bool Online => Character.Online;
    public string Detail => $"Level {Character.Level} {ArmoryNames.RaceName(Character.Race)} {ArmoryNames.ClassName(Character.Class)}";
    public IBrush ClassBrush { get; } = ArmoryNames.ClassBrush(character.Class);
}

/// <summary>A filter tab: one offer category, or all of them.</summary>
public sealed class ShopCategoryView(string key, int count) : ViewModelBase
{
    public const string AllKey = "\0all";
    private bool _isSelected;

    public string Key { get; } = key;
    public string Title { get; } = TitleOf(key);
    public int Count { get; } = count;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    /// <summary>The known categories first, in the order the shop is meant to be read.</summary>
    public static int Order(string key) => key switch { "mount" => 0, "pet" => 1, "service" => 2, _ => 3 };

    public static string TitleOf(string key) => key switch
    {
        AllKey => "ALL",
        "mount" => "MOUNTS",
        "pet" => "COMPANIONS",
        "service" => "SERVICES",
        "" => "OTHER",
        _ => key.ToUpperInvariant(),
    };
}

/// <summary>One offer card. Whether it can be bought depends on the picked character and the balance.</summary>
public sealed class ShopOfferView : ViewModelBase
{
    private string _stateText = "";
    private bool _canBuy;
    private bool _isOwned;

    public ShopOfferView(ShopOffer offer)
    {
        Offer = offer;
        AccentBrush = Brush.Parse(offer.Category switch
        {
            "mount" => "#E8B64C", "pet" => "#7CC7A0", "service" => "#9DB7F0", _ => "#C9A8E8",
        });
    }

    public ShopOffer Offer { get; }
    public string Name => Offer.Name.Length > 0 ? Offer.Name : "Offer " + Offer.Id;
    public string Description => Offer.Description;
    public int Price => Offer.Price;
    public string PriceText => Offer.Price.ToString("N0", CultureInfo.CurrentCulture) + (Offer.Price == 1 ? " coin" : " coins");
    public string PriceNumber => Offer.Price.ToString("N0", CultureInfo.CurrentCulture);
    public string CategoryTitle => ShopCategoryView.TitleOf(Offer.Category);
    // Which drawn placeholder the card shows (ShopWindow.axaml); offers carry no icon the launcher could show.
    public bool IsMount => Offer.Category == "mount";
    public bool IsPet => Offer.Category == "pet";
    public bool IsService => Offer.Category == "service";
    public bool IsOther => !IsMount && !IsPet && !IsService;
    public IBrush AccentBrush { get; }

    /// <summary>Why it can't be bought for the picked character, or empty.</summary>
    public string StateText { get => _stateText; private set { if (SetProperty(ref _stateText, value)) OnPropertyChanged(nameof(HasState)); } }
    public bool HasState => _stateText.Length > 0;
    public bool CanBuy { get => _canBuy; private set => SetProperty(ref _canBuy, value); }
    public bool IsOwned { get => _isOwned; private set => SetProperty(ref _isOwned, value); }

    public void Update(ShopCharacterView? character, int balance)
    {
        IsOwned = character is not null && Offer.Owned.Contains(character.Guid);
        var cannot = character is not null && Offer.Cannot.Contains(character.Guid);
        StateText = character is null ? "No character"
            : IsOwned ? (Offer.Category == "service" ? "Already waiting" : "Owned")
            : cannot ? "Can't use"
            : balance < Offer.Price ? $"Need {(Offer.Price - Math.Max(0, balance)).ToString("N0", CultureInfo.CurrentCulture)} more"
            : "";
        CanBuy = StateText.Length == 0;
    }
}
