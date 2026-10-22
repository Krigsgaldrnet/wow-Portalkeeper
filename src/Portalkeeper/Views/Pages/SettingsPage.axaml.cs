using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Portalkeeper.ViewModels;

namespace Portalkeeper.Views;

/// <summary>The SETTINGS page of the main window (the gear in the top bar).</summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private async void ChangeRealm_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.CanSwitchRealm && Owner is { } owner)
            await new RealmSelectionWindow(vm).ShowDialog(owner);
    }

    private async void CheckForUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) await vm.CheckForUpdatesAsync();
    }

    private void ViewRelease_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ViewRelease();
    }

    private async void LocateClient_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || Owner is not { } owner) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Locate World of Warcraft 3.3.5a",
            AllowMultiple = false
        });
        if (folders.FirstOrDefault() is { } folder)
            vm.SetClientDirectory(folder.Path.LocalPath);
    }

    private async void ManageAddons_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) await vm.GoToAsync(LauncherPage.Addons);
    }

    private async void RepairGame_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        // The repair shows on the home screen's status card.
        vm.GoHome();
        vm.DismissCrash();
        await vm.RepairGameAsync();
    }

    private async void LogOut_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.GoHome();
            await vm.LogOutAsync();
        }
    }

    private async void ConstructRuntime_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.CanConstructRuntime)
            await vm.ConstructTestRuntimeAsync();
    }

    private async void LaunchRuntime_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.CanLaunchConstructedRuntime)
            await vm.LaunchConstructedRuntimeAsync();
    }

    private void ForgetRuntime_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.ForgetConstructedRuntime();
    }
}
