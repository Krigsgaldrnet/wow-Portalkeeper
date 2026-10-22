using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Portalkeeper.ViewModels;

namespace Portalkeeper.Views;

/// <summary>The PATCH NOTES page of the main window.</summary>
public partial class PatchNotesPage : UserControl
{
    public PatchNotesPage() => InitializeComponent();

    private PatchNotesViewModel? Notes => (DataContext as MainViewModel)?.PatchNotes;

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (Notes is { } notes) await notes.LoadAsync();
    }

    private void Website_Click(object? sender, RoutedEventArgs e)
    {
        if (Notes is not { } notes) return;
        if (!System.Uri.TryCreate(notes.Url, System.UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.Exception) { /* No browser available. */ }
    }
}
