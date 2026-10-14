using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Portalkeeper.ViewModels;

namespace Portalkeeper.Views;

public partial class ShopWindow : Window
{
    /// <summary>The tile column's width for a window width: three tiles across in a wide window, two in a narrow one.</summary>
    public static readonly IValueConverter TileColumnWidth = new FuncValueConverter<double, double>(width =>
        (width >= 1060 ? 3 : 2) * (148 + 10) + 12);

    public ShopWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => { if (DataContext is ShopViewModel vm) await vm.LoadAsync(); };
        // A purchase on its way keeps the window open when the player closes it: its answer (or "it
        // may have gone through") is only shown here. Quitting the app or the OS shutting down isn't
        // held up, and a login that ran out closes it from code.
        Closing += (_, e) =>
        {
            if (!e.IsProgrammatic && e.CloseReason == WindowCloseReason.WindowClosing
                && DataContext is ShopViewModel { IsBuying: true })
                e.Cancel = true;
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ShopViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    // The confirm step takes the keyboard too: CANCEL first, nothing behind it.
                    if (e.PropertyName == nameof(ShopViewModel.IsConfirming) && vm.IsConfirming)
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => CancelBuyButton.Focus());
                };
        };
        Closed += (_, _) => (DataContext as ShopViewModel)?.Dispose();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is ShopViewModel { IsConfirming: true } vm)
            {
                vm.CancelBuy();
                e.Handled = true;
            }
        };
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShopViewModel vm) await vm.LoadAsync();
    }

    private void Category_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShopViewModel vm && (sender as Control)?.DataContext is ShopCategoryView category)
            vm.SelectedCategory = category;
    }

    private void Tile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShopViewModel vm && (sender as Control)?.DataContext is ShopOfferView offer)
            vm.SelectedOffer = offer;
    }

    private void Buy_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShopViewModel { SelectedOffer: { } offer } vm)
            vm.AskToBuy(offer);
    }

    private void CancelBuy_Click(object? sender, RoutedEventArgs e) => (DataContext as ShopViewModel)?.CancelBuy();

    private async void ConfirmBuy_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShopViewModel vm) await vm.ConfirmBuyAsync();
    }
}
