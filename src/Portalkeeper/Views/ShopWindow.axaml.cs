using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Portalkeeper.ViewModels;

namespace Portalkeeper.Views;

public partial class ShopWindow : Window
{
    public ShopWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => { if (DataContext is ShopViewModel vm) await vm.LoadAsync(); };
        // A purchase on its way keeps the window open: its answer (or "it may have gone through")
        // is only shown here. A login that ran out closes it from code.
        Closing += (_, e) =>
        {
            if (!e.IsProgrammatic && DataContext is ShopViewModel { IsBuying: true }) e.Cancel = true;
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

    private void Buy_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShopViewModel vm && (sender as Control)?.DataContext is ShopOfferView offer)
            vm.AskToBuy(offer);
    }

    private void CancelBuy_Click(object? sender, RoutedEventArgs e) => (DataContext as ShopViewModel)?.CancelBuy();

    private async void ConfirmBuy_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShopViewModel vm) await vm.ConfirmBuyAsync();
    }
}
