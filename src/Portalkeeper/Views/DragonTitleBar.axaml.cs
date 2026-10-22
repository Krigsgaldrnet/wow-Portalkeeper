using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Portalkeeper.Views;

public partial class DragonTitleBar : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<DragonTitleBar, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<bool> ShowMinimizeProperty =
        AvaloniaProperty.Register<DragonTitleBar, bool>(nameof(ShowMinimize), true);

    public DragonTitleBar()
    {
        InitializeComponent();
    }

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public bool ShowMinimize { get => GetValue(ShowMinimizeProperty); set => SetValue(ShowMinimizeProperty, value); }

    private Window? HostWindow => TopLevel.GetTopLevel(this) as Window;

    private void Band_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var window = HostWindow;
        // The buttons on the band handle their own clicks.
        if (window is null || e.Source is Button || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2 && window.CanResize)
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            window.BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window) window.WindowState = WindowState.Minimized;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => HostWindow?.Close();
}
