using Avalonia;
using Avalonia.Controls;

namespace Portalkeeper.Views;

/// <summary>
/// Evermore fork: the header strip at the top of a page of the main window: the realm skyline with
/// the page's name in Cinzel gold and a line under it, and whatever the page puts at the right
/// (its Content: a wallet, a refresh button). Drawn by the template in DragonStyles.axaml.
/// </summary>
public sealed class PageBanner : ContentControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<PageBanner, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<PageBanner, string>(nameof(Subtitle), string.Empty);

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
}
