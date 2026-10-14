using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Portalkeeper.Views;

/// <summary>
/// Evermore fork: a coin shop offer's turntable picture (a strip of <see cref="Frames"/> angles
/// side by side), drawn one frame at a time, as large as fits. <see cref="Spin"/> turns it slowly
/// on its own, <see cref="SpinOnHover"/> only while the mouse is over it, and with
/// <see cref="Draggable"/> the player turns it by dragging sideways (which stops the slow turn
/// until the picture changes).
/// </summary>
public sealed class ShopTurntable : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ShopTurntable, Bitmap?>(nameof(Source));
    public static readonly StyledProperty<int> FramesProperty =
        AvaloniaProperty.Register<ShopTurntable, int>(nameof(Frames), 1);
    public static readonly StyledProperty<bool> SpinProperty =
        AvaloniaProperty.Register<ShopTurntable, bool>(nameof(Spin));
    public static readonly StyledProperty<bool> SpinOnHoverProperty =
        AvaloniaProperty.Register<ShopTurntable, bool>(nameof(SpinOnHover));
    public static readonly StyledProperty<bool> DraggableProperty =
        AvaloniaProperty.Register<ShopTurntable, bool>(nameof(Draggable));

    /// <summary>Time per frame of the slow turn: 36 frames go round in about 5 seconds.</summary>
    private static readonly TimeSpan SpinStep = TimeSpan.FromMilliseconds(140);

    private readonly DispatcherTimer _timer;
    private int _frame;
    private bool _hovered;
    private bool _attached;
    private bool _turnedByHand;
    private Point? _dragStart;
    private int _dragStartFrame;

    static ShopTurntable()
    {
        AffectsRender<ShopTurntable>(SourceProperty, FramesProperty);
        AffectsMeasure<ShopTurntable>(SourceProperty, FramesProperty);
    }

    public ShopTurntable()
    {
        _timer = new DispatcherTimer { Interval = SpinStep };
        _timer.Tick += (_, _) => Frame++;
        ClipToBounds = true;
    }

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public int Frames { get => GetValue(FramesProperty); set => SetValue(FramesProperty, value); }
    public bool Spin { get => GetValue(SpinProperty); set => SetValue(SpinProperty, value); }
    public bool SpinOnHover { get => GetValue(SpinOnHoverProperty); set => SetValue(SpinOnHoverProperty, value); }
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
        if (change.Property == SourceProperty)
        {
            _frame = 0;
            _turnedByHand = false;
        }
        if (change.Property == DraggableProperty)
            Cursor = Draggable ? new Cursor(StandardCursorType.SizeWestEast) : null;
        if (change.Property == SourceProperty || change.Property == FramesProperty || change.Property == SpinProperty
            || change.Property == SpinOnHoverProperty)
            UpdateTimer();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _timer.Stop();
    }

    private void UpdateTimer()
    {
        var turn = Source is not null && Frames > 1 && _dragStart is null && !_turnedByHand && _attached
                   && (Spin || (SpinOnHover && _hovered));
        if (turn) _timer.Start();
        else _timer.Stop();
        // A tile that stops being hovered goes back to its first angle.
        if (!turn && SpinOnHover && !Spin && !_hovered) Frame = 0;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hovered = true;
        UpdateTimer();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = false;
        UpdateTimer();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!Draggable || Source is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragStart = e.GetPosition(this);
        _dragStartFrame = _frame;
        e.Pointer.Capture(this);
        e.Handled = true;
        UpdateTimer();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is not { } start) return;
        // Dragging across the whole view turns it about half way round. The frames turn the model
        // clockwise seen from above, so its near side moves left: dragging right goes back.
        var perFrame = Math.Max(4.0, Bounds.Width / (Math.Max(1, Frames) * 0.5));
        var moved = (int)Math.Round((e.GetPosition(this).X - start.X) / perFrame);
        if (moved != 0) _turnedByHand = true;
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
        UpdateTimer();
    }

    private void EndDrag(IPointer pointer)
    {
        if (_dragStart is null) return;
        _dragStart = null;
        if (ReferenceEquals(pointer.Captured, this)) pointer.Capture(null);
        UpdateTimer();
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
