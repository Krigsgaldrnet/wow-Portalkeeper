using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Portalkeeper.ViewModels;

namespace Portalkeeper.Views;

/// <summary>
/// The CHARACTERS page of the main window; its content is the Characters view model of the
/// MainViewModel it's bound to. A row is dragged onto another to take its place (the rows between
/// shift over); Ctrl+Up / Ctrl+Down move the picked one, Ctrl+S saves. A row's star stars or unstars it.
/// </summary>
public partial class CharactersPage : UserControl
{
    private const double DragDistance = 6;
    private static readonly DataFormat<string> RowFormat = DataFormat.CreateInProcessFormat<string>("portalkeeper-character-row");

    private PointerPressedEventArgs? _press;
    private Point _pressAt;
    private CharacterOrderRowView? _dragged;

    public CharactersPage()
    {
        InitializeComponent();
        // The row takes the press for its selection: listen before it does, and to moves it handles.
        CharacterList.AddHandler(PointerPressedEvent, Row_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        CharacterList.AddHandler(PointerMovedEvent, Row_PointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        CharacterList.AddHandler(PointerReleasedEvent, Row_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        DragDrop.AddDragOverHandler(CharacterList, List_DragOver);
        DragDrop.AddDragLeaveHandler(CharacterList, List_DragLeave);
        DragDrop.AddDropHandler(CharacterList, List_Drop);
        AddHandler(KeyDownEvent, Page_KeyDown, RoutingStrategies.Tunnel);
    }

    private CharactersViewModel? ViewModel => (DataContext as MainViewModel)?.Characters;

    private static CharacterOrderRowView? RowOf(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as CharacterOrderRowView;

    // ---------------------------------------------------------
    // Dragging a row
    // ---------------------------------------------------------

    private void Row_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _press = null;
        if (ViewModel is not { CanEdit: true } || !e.GetCurrentPoint(CharacterList).Properties.IsLeftButtonPressed
            || RowOf(e.Source) is null)
            return;
        _press = e;
        _pressAt = e.GetPosition(CharacterList);
    }

    private async void Row_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || _dragged is not null) return;
        var at = e.GetPosition(CharacterList);
        if (Math.Abs(at.X - _pressAt.X) < DragDistance && Math.Abs(at.Y - _pressAt.Y) < DragDistance) return;
        _press = null;
        if (ViewModel is not { CanEdit: true } vm || RowOf(press.Source) is not { } row) return;

        _dragged = row;
        vm.SelectedCharacter = row;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(RowFormat, row.Guid.ToString()));
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        }
        finally
        {
            _dragged = null;
            ClearDropTarget(vm);
        }
    }

    private void Row_PointerReleased(object? sender, PointerReleasedEventArgs e) => _press = null;

    private void List_DragOver(object? sender, DragEventArgs e)
    {
        if (_dragged is null || ViewModel is not { CanEdit: true } vm)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        var target = RowOf(e.Source);
        foreach (var row in vm.Characters) row.IsDropTarget = target is not null && ReferenceEquals(row, target) && !ReferenceEquals(row, _dragged);
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void List_DragLeave(object? sender, DragEventArgs e)
    {
        if (ViewModel is { } vm) ClearDropTarget(vm);
    }

    private void List_Drop(object? sender, DragEventArgs e)
    {
        if (_dragged is not { } dragged || ViewModel is not { } vm) return;
        ClearDropTarget(vm);
        // Below the last row: to the end.
        var target = RowOf(e.Source);
        var index = target is not null ? vm.Characters.IndexOf(target) : vm.Characters.Count - 1;
        vm.Move(dragged, index);
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private static void ClearDropTarget(CharactersViewModel vm)
    {
        foreach (var row in vm.Characters.Where(r => r.IsDropTarget)) row.IsDropTarget = false;
    }

    // ---------------------------------------------------------
    // Buttons and keys
    // ---------------------------------------------------------

    private async void Page_KeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || !(e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) return;
        switch (e.Key)
        {
            case Key.S:
                e.Handled = true;
                await vm.SaveAsync();
                break;
            case Key.Up:
                e.Handled = true;
                vm.MoveUp();
                ShowPicked(vm);
                break;
            case Key.Down:
                e.Handled = true;
                vm.MoveDown();
                ShowPicked(vm);
                break;
        }
    }

    private void ShowPicked(CharactersViewModel vm)
    {
        if (vm.SelectedCharacter is { } row) CharacterList.ScrollIntoView(row);
    }

    private void Sort_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && sender is Button { Tag: string name } && Enum.TryParse<CharacterSort>(name, out var sort)) vm.Sort(sort);
    }

    private void Up_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        vm.MoveUp();
        ShowPicked(vm);
    }

    private void Down_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        vm.MoveDown();
        ShowPicked(vm);
    }

    private void Star_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || (sender as Control)?.DataContext is not CharacterOrderRowView row) return;
        vm.ToggleFavourite(row);
        ShowPicked(vm);
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.LoadAsync(keepStatus: vm.HasUnsavedChanges);
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.SaveAsync();
    }

    private void Revert_Click(object? sender, RoutedEventArgs e) => ViewModel?.Revert();
}
