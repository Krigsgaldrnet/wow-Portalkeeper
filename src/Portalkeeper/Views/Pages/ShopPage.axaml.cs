using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Portalkeeper.ViewModels;

namespace Portalkeeper.Views;

/// <summary>The SHOP page of the main window; its content is the Shop view model of the MainViewModel it's bound to.</summary>
public partial class ShopPage : UserControl
{
    /// <summary>The tile column's width for a page width: three tiles across on a wide page, two on a narrow one.</summary>
    public static readonly IValueConverter TileColumnWidth = new FuncValueConverter<double, double>(width =>
        (width >= 1000 ? 3 : 2) * (148 + 10) + 12);

    private MainViewModel? _main;
    private ShopViewModel? _shop;

    public ShopPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _shop is { IsConfirming: true } shop)
            {
                shop.CancelBuy();
                e.Handled = true;
            }
        };
    }

    private void Attach(MainViewModel? main)
    {
        if (_main is not null) _main.PropertyChanged -= MainChanged;
        _main = main;
        if (_main is not null) _main.PropertyChanged += MainChanged;
        AttachShop(_main?.Shop);
    }

    private void MainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Shop)) AttachShop(_main?.Shop);
    }

    private void AttachShop(ShopViewModel? shop)
    {
        if (ReferenceEquals(_shop, shop)) return;
        if (_shop is not null) _shop.PropertyChanged -= ShopChanged;
        _shop = shop;
        if (_shop is not null) _shop.PropertyChanged += ShopChanged;
    }

    private void ShopChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The confirm step takes the keyboard too: CANCEL first, nothing behind it.
        if (e.PropertyName == nameof(ShopViewModel.IsConfirming) && _shop is { IsConfirming: true })
            Avalonia.Threading.Dispatcher.UIThread.Post(() => CancelBuyButton.Focus());
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (_shop is { } shop) await shop.LoadAsync();
    }

    private void Category_Click(object? sender, RoutedEventArgs e)
    {
        if (_shop is { } shop && (sender as Control)?.DataContext is ShopCategoryView category)
            shop.SelectedCategory = category;
    }

    private void ClearFilters_Click(object? sender, RoutedEventArgs e) => _shop?.ClearFilters();

    private void Tile_Click(object? sender, RoutedEventArgs e)
    {
        if (_shop is { } shop && (sender as Control)?.DataContext is ShopOfferView offer)
            shop.SelectedOffer = offer;
    }

    private void Buy_Click(object? sender, RoutedEventArgs e)
    {
        if (_shop is { SelectedOffer: { } offer } shop)
            shop.AskToBuy(offer);
    }

    private void CancelBuy_Click(object? sender, RoutedEventArgs e) => _shop?.CancelBuy();

    private async void ConfirmBuy_Click(object? sender, RoutedEventArgs e)
    {
        if (_shop is { } shop) await shop.ConfirmBuyAsync();
    }
}
