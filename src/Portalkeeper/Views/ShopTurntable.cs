using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Portalkeeper.Views;

/// <summary>
/// Evermore fork: a coin shop offer's turntable picture (a strip of <see cref="Frames"/> angles
/// side by side), drawn one frame at a time, as large as fits. It never turns on its own: with
/// <see cref="Draggable"/> the player turns it by dragging sideways.
/// </summary>
public sealed class ShopTurntable : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ShopTurntable, Bitmap?>(nameof(Source));
    public static readonly StyledProperty<int> FramesProperty =
        AvaloniaProperty.Register<ShopTurntable, int>(nameof(Frames), 1);
    public static readonly StyledProperty<bool> DraggableProperty =
        AvaloniaProperty.Register<ShopTurntable, bool>(nameof(Draggable));

    private int _frame;
    private Point? _dragStart;
    private int _dragStartFrame;

    static ShopTurntable()
    {
        AffectsRender<ShopTurntable>(SourceProperty, FramesProperty);
        AffectsMeasure<ShopTurntable>(SourceProperty, FramesProperty);
    }

    public ShopTurntable()
    {
        ClipToBounds = true;
    }

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public int Frames { get => GetValue(FramesProperty); set => SetValue(FramesProperty, value); }
    public bool Draggable { get => GetValue(DraggableProperty); set => SetValue(DraggableProperty, value); }

    /// <summary>The angle shown, 0 to <see cref="Frames"/> - 1 (wraps both ways).</summary>
    public int Frame
    {
        get => _frame;
        set
        {
            var frames = Math.Max(1, Frames);
            var frame = ((value % frames) + frames) % frames;
            if (frame == _frame) return;
            _frame = frame;
            InvalidateVisual();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // A new picture starts at its first angle (the three-quarter view).
        if (change.Property == SourceProperty) _frame = 0;
        if (change.Property == DraggableProperty)
            Cursor = Draggable ? new Cursor(StandardCursorType.SizeWestEast) : null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!Draggable || Source is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragStart = e.GetPosition(this);
        _dragStartFrame = _frame;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is not { } start) return;
        // Dragging across the whole view turns it about half way round. The frames turn the model
        // clockwise seen from above, so its near side moves left: dragging right goes back.
        var perFrame = Math.Max(4.0, Bounds.Width / (Math.Max(1, Frames) * 0.5));
        var moved = (int)Math.Round((e.GetPosition(this).X - start.X) / perFrame);
        Frame = _dragStartFrame - moved;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragStart = null;
    }

    private void EndDrag(IPointer pointer)
    {
        if (_dragStart is null) return;
        _dragStart = null;
        if (ReferenceEquals(pointer.Captured, this)) pointer.Capture(null);
    }

    private Size FrameSize()
    {
        if (Source is not { } source) return default;
        var frames = Math.Max(1, Frames);
        return new Size(source.Size.Width / frames, source.Size.Height);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var frame = FrameSize();
        if (frame.Width <= 0 || frame.Height <= 0) return default;
        // As large as the space allows, keeping the frame's shape; with no limit, its own size.
        var scale = Math.Min(
            double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width / frame.Width,
            double.IsInfinity(availableSize.Height) ? double.PositiveInfinity : availableSize.Height / frame.Height);
        return double.IsInfinity(scale) ? frame : frame * scale;
    }

    public override void Render(DrawingContext context)
    {
        if (Source is not { } source) return;
        var frame = FrameSize();
        if (frame.Width <= 0 || frame.Height <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var scale = Math.Min(Bounds.Width / frame.Width, Bounds.Height / frame.Height);
        var size = frame * scale;
        var dest = new Rect((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2, size.Width, size.Height);
        context.DrawImage(source, new Rect(_frame * frame.Width, 0, frame.Width, frame.Height), dest);
    }
}
