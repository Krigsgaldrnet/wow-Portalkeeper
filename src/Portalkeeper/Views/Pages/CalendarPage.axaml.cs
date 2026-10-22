using Avalonia.Controls;
using Avalonia.Interactivity;
using Portalkeeper.ViewModels;

namespace Portalkeeper.Views;

/// <summary>The CALENDAR page of the main window; its content is the Calendar view model of the MainViewModel it's bound to.</summary>
public partial class CalendarPage : UserControl
{
    public CalendarPage() => InitializeComponent();

    private CalendarViewModel? Calendar => (DataContext as MainViewModel)?.Calendar;

    private void Previous_Click(object? sender, RoutedEventArgs e) => Calendar?.PreviousMonth();

    private void Next_Click(object? sender, RoutedEventArgs e) => Calendar?.NextMonth();

    private void Day_Click(object? sender, RoutedEventArgs e)
    {
        if (Calendar is { } calendar && sender is Button { DataContext: CalendarDayCell day })
            calendar.SelectDay(day.Date);
    }
}
