using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Portalkeeper.Views;

// Evermore fork: DragonUI's look, the professions-window style the realm's DragonUI gives every window
// in game: a bronze window frame, gold-filigree insets over dark marble, a warm rock ground and red
// three-slice buttons. Drawn from DragonUI's own textures (Assets/DragonUI, MIT: LICENSE-DragonUI.txt)
// with the piece rectangles and offsets of its atlas.lua, nineslice.lua and redbutton.lua.

public enum DragonFrameKind { Inset, SmallInset, Bronze }

public enum DragonGround { None, Marble, Rock }

internal static class DragonArt
{
    private static Bitmap Load(string name) =>
        new(AssetLoader.Open(new Uri("avares://Portalkeeper/Assets/DragonUI/" + name)));

    public static readonly Lazy<Bitmap> Bronze = new(() => Load("bronze-frame.png"));
    public static readonly Lazy<Bitmap> Filigree = new(() => Load("filigree-frame.png"));
    public static readonly Lazy<Bitmap> RedButton = new(() => Load("red-button.png"));
    public static readonly Lazy<Bitmap> Rock = new(() => Load("ground-rock.png"));
    public static readonly Lazy<Bitmap> Marble = new(() => Load("ground-marble.png"));
}

/// <summary>
/// A nine-slice frame around its child, with a tiled ground under it. Corners sit at DragonUI's
/// offsets from this control's edges (so Bronze reaches a little outside them) and edges stretch
/// between the corners. <see cref="Scale"/> sizes the art: 1 is DragonUI's own UI units.
/// </summary>
public sealed class DragonFrame : Decorator
{
    public static readonly StyledProperty<DragonFrameKind> KindProperty =
        AvaloniaProperty.Register<DragonFrame, DragonFrameKind>(nameof(Kind));
    public static readonly StyledProperty<DragonGround> GroundProperty =
        AvaloniaProperty.Register<DragonFrame, DragonGround>(nameof(Ground), DragonGround.Marble);
    public static readonly StyledProperty<double> GroundOpacityProperty =
        AvaloniaProperty.Register<DragonFrame, double>(nameof(GroundOpacity), 1.0);
    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<DragonFrame, double>(nameof(Scale), 1.0);

    static DragonFrame()
    {
        AffectsRender<DragonFrame>(KindProperty, GroundProperty, GroundOpacityProperty, ScaleProperty);
    }

    public DragonFrameKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public DragonGround Ground { get => GetValue(GroundProperty); set => SetValue(GroundProperty, value); }
    public double GroundOpacity { get => GetValue(GroundOpacityProperty); set => SetValue(GroundOpacityProperty, value); }
    public double Scale { get => GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }

    /// <summary>A piece of a sheet: its pixels, and its size in DragonUI units.</summary>
    private readonly record struct Piece(Rect Source, double Width, double Height);

    /// <summary>
    /// One layout: four corners with their offsets (x right, y down from the matching corner of the
    /// frame), and the four edges, which run between the corners at the edge pieces' thickness.
    /// </summary>
    private sealed record Layout(Piece TopLeft, Piece TopRight, Piece BottomLeft, Piece BottomRight,
        Piece Top, Piece Bottom, Piece Left, Piece Right,
        Point TopLeftOffset, Point TopRightOffset, Point BottomLeftOffset, Point BottomRightOffset, double GroundInset);

    private static Piece P(double l, double t, double r, double b, double w, double h) => new(new Rect(l, t, r - l, b - t), w, h);

    // UI-Frame-Bronze-* (ForeverUI\chrome-frame), NoPortraitFrameTemplate's offsets.
    private static readonly Layout BronzeLayout = new(
        P(466, 268, 656, 364, 95, 48), P(664, 268, 854, 364, 95, 48),
        P(664, 124, 854, 204, 95, 40), P(4, 388, 194, 468, 95, 40),
        P(202, 268, 458, 458, 128, 95), P(400, 4, 656, 204, 128, 100),
        P(4, 4, 194, 260, 95, 128), P(202, 4, 392, 260, 95, 128),
        new Point(-13, -16), new Point(2, -16), new Point(-13, 8), new Point(2, 8), 4);

    // UI-Frame-Filigree-* (Professions\ProfessionsUI), InsetFrameTemplate: rail pushed 2.5 out.
    private static readonly Layout InsetLayout = Filigree(16, 34, 2.5);

    // UI-Frame-FiligreeSmall-*, SmallInsetFrameTemplate: 10 units, pushed 1.5 out.
    private static readonly Layout SmallInsetLayout = Filigree(10, 21, 1.5);

    private static Layout Filigree(double corner, double edge, double push) => new(
        P(4, 4, 56, 56, corner, corner), P(166, 4, 218, 56, corner, corner),
        P(4, 166, 56, 218, corner, corner), P(166, 166, 218, 218, corner, corner),
        P(56, 4, 166, 56, edge, corner), P(56, 166, 166, 218, edge, corner),
        P(4, 56, 56, 166, corner, edge), P(166, 56, 218, 166, corner, edge),
        new Point(-push, -push), new Point(push, -push), new Point(-push, push), new Point(push, push), 0);

    public override void Render(DrawingContext context)
    {
        var (layout, sheet) = Kind switch
        {
            DragonFrameKind.Bronze => (BronzeLayout, DragonArt.Bronze.Value),
            DragonFrameKind.SmallInset => (SmallInsetLayout, DragonArt.Filigree.Value),
            _ => (InsetLayout, DragonArt.Filigree.Value),
        };
        var s = Scale;
        var w = Bounds.Width;
        var h = Bounds.Height;

        if (Ground != DragonGround.None && w > 0 && h > 0)
        {
            var ground = Ground == DragonGround.Rock ? DragonArt.Rock.Value : DragonArt.Marble.Value;
            var tile = ground.Size.Width * (Ground == DragonGround.Rock ? 0.5 : 1);
            var brush = new ImageBrush(ground)
            {
                TileMode = TileMode.Tile,
                Stretch = Stretch.Fill,
                DestinationRect = new RelativeRect(0, 0, tile, tile, RelativeUnit.Absolute),
                Opacity = GroundOpacity,
            };
            var inset = layout.GroundInset * s;
            context.FillRectangle(brush, new Rect(inset, inset, Math.Max(0, w - 2 * inset), Math.Max(0, h - 2 * inset)));
        }

        // Corners: placed from the frame's corners at their offsets.
        var tl = new Rect(layout.TopLeftOffset.X * s, layout.TopLeftOffset.Y * s, layout.TopLeft.Width * s, layout.TopLeft.Height * s);
        var tr = new Rect(w + layout.TopRightOffset.X * s - layout.TopRight.Width * s, layout.TopRightOffset.Y * s,
            layout.TopRight.Width * s, layout.TopRight.Height * s);
        var bl = new Rect(layout.BottomLeftOffset.X * s, h + layout.BottomLeftOffset.Y * s - layout.BottomLeft.Height * s,
            layout.BottomLeft.Width * s, layout.BottomLeft.Height * s);
        var br = new Rect(w + layout.BottomRightOffset.X * s - layout.BottomRight.Width * s,
            h + layout.BottomRightOffset.Y * s - layout.BottomRight.Height * s, layout.BottomRight.Width * s, layout.BottomRight.Height * s);

        // Edges run between the corners, at the edge pieces' own thickness.
        Draw(context, sheet, layout.Top, new Rect(tl.Right, tl.Top, tr.Left - tl.Right, layout.Top.Height * s));
        Draw(context, sheet, layout.Bottom, new Rect(bl.Right, bl.Bottom - layout.Bottom.Height * s, br.Left - bl.Right, layout.Bottom.Height * s));
        Draw(context, sheet, layout.Left, new Rect(tl.Left, tl.Bottom, layout.Left.Width * s, bl.Top - tl.Bottom));
        Draw(context, sheet, layout.Right, new Rect(tr.Right - layout.Right.Width * s, tr.Bottom, layout.Right.Width * s, br.Top - tr.Bottom));
        Draw(context, sheet, layout.TopLeft, tl);
        Draw(context, sheet, layout.TopRight, tr);
        Draw(context, sheet, layout.BottomLeft, bl);
        Draw(context, sheet, layout.BottomRight, br);
    }

    private static void Draw(DrawingContext context, Bitmap sheet, Piece piece, Rect target)
    {
        if (target.Width > 0 && target.Height > 0) context.DrawImage(sheet, piece.Source, target);
    }
}

/// <summary>
/// DragonUI's red button (retail's "128-RedButton"): two caps (the left slice, mirrored for the right)
/// that scale with the height, a stretched middle, a darker pressed row, a drained disabled look and
/// an added glow on hover. Reads its state from the button it is drawn in.
/// </summary>
public sealed class RedButtonChrome : Control
{
    // Source rectangles on red-button.png (DragonUI's UI\redbutton3slice, 256 x 256).
    private static readonly Rect NormalCap = new(4, 76, 57, 64), NormalMid = new(4, 148, 32, 64);
    private static readonly Rect PushedCap = new(136, 76, 57, 64), PushedMid = new(84, 148, 32, 64);
    private static readonly Rect DisabledCap = new(70, 76, 57, 64), DisabledMid = new(44, 148, 32, 64);
    // The hover glow: one strip holding cap, middle and cap; a cap's share is 114 of 441.
    private static readonly Rect Glow = new(4, 4, 221, 64);
    private const double GlowCap = 221.0 * 114 / 441;

    private Button? _button;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _button = TemplatedParent as Button;
        if (_button is not null) _button.PropertyChanged += ButtonChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_button is not null) _button.PropertyChanged -= ButtonChanged;
        _button = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void ButtonChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsPointerOverProperty || e.Property == Button.IsPressedProperty || e.Property == IsEffectivelyEnabledProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var sheet = DragonArt.RedButton.Value;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var enabled = _button?.IsEffectivelyEnabled ?? true;
        var pressed = enabled && _button?.IsPressed == true;
        var (cap, mid) = !enabled ? (DisabledCap, DisabledMid) : pressed ? (PushedCap, PushedMid) : (NormalCap, NormalMid);

        // The caps keep the art's proportions; they never take more than 80% of the width.
        var capWidth = Math.Min(h * 57 / 64, w * 0.4);
        using (context.PushOpacity(enabled ? 1 : 0.55))
            Slices(context, sheet, cap, mid, w, h, capWidth);

        if (enabled && _button?.IsPointerOver == true)
        {
            using (context.PushRenderOptions(new RenderOptions { BitmapBlendingMode = BitmapBlendingMode.Plus }))
            using (context.PushOpacity(0.8))
                Slices(context, sheet, new Rect(Glow.X, Glow.Y, GlowCap, Glow.Height),
                    new Rect(Glow.X + GlowCap, Glow.Y, Glow.Width - 2 * GlowCap, Glow.Height), w, h, capWidth);
        }
    }

    private static void Slices(DrawingContext context, Bitmap sheet, Rect cap, Rect mid, double w, double h, double capWidth)
    {
        context.DrawImage(sheet, cap, new Rect(0, 0, capWidth, h));
        if (w > 2 * capWidth) context.DrawImage(sheet, mid, new Rect(capWidth, 0, w - 2 * capWidth, h));
        // The right cap is the left one mirrored.
        using (context.PushTransform(Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(w, 0)))
            context.DrawImage(sheet, cap, new Rect(0, 0, capWidth, h));
    }
}
