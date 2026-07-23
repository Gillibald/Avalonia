using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace Avalonia.Headless.UnitTests;

/// <summary>
/// An effect filters its subtree's rendered content, so a partial redraw that
/// crosses an effect visual must still feed the filter the subtree content
/// around the redrawn slice - dropping part of the input writes wrong pixels
/// inside the slice. These drive a foreign dirty slice (a sibling's change)
/// across a drop-shadowed panel and compare the slice against a fresh
/// one-pass render of the same scene.
/// </summary>
public class EffectPartialRedrawTests
{
    private const int Size = 200;

    // The drop-shadowed panel. Its own draw list is empty (no background);
    // all of its content is the child shape below.
    private static readonly Rect PanelRect = new(20, 20, 120, 120);

    // The shape inside the panel: a solid black square in the panel's top-left
    // corner, world (20,20)-(48,48). It is a child visual of the effect
    // visual, and it lies entirely outside the overlay's rect, so during a
    // redraw of only the overlay's slice the render walk culls it - while its
    // drop shadow (offset 26,26, blur 8) lands squarely inside the slice.
    // The shadow written INSIDE the overlay's rect therefore comes from
    // content OUTSIDE it, which a filter input truncated at the slice edge
    // cannot produce.
    private static readonly Rect ShapeRect = new(0, 0, 28, 28);

    // The sibling drawn over the panel, overlapping the panel's box. Its
    // background is semi-transparent so the shadow beneath stays visible in
    // the compared pixels.
    private static readonly Rect OverlayRect = new(52, 52, 30, 30);

    private static readonly ImmutableSolidColorBrush HalfRed = new(Colors.Red, 0.5);
    private static readonly ImmutableSolidColorBrush HalfBlue = new(Colors.Blue, 0.5);

    private static (Window Window, Border Overlay) BuildScene(IBrush overlayBackground, bool withEffect = true)
    {
        var host = new Canvas { Width = Size, Height = Size, Background = Brushes.White };

        var shape = new Border
        {
            Width = ShapeRect.Width,
            Height = ShapeRect.Height,
            Background = Brushes.Black
        };
        Canvas.SetLeft(shape, ShapeRect.X);
        Canvas.SetTop(shape, ShapeRect.Y);

        var shapeHost = new Canvas { Width = PanelRect.Width, Height = PanelRect.Height };
        shapeHost.Children.Add(shape);

        var panel = new Border
        {
            Width = PanelRect.Width,
            Height = PanelRect.Height,
            Child = shapeHost,
            Effect = withEffect ? new ImmutableDropShadowEffect(26, 26, 8, Colors.Black, 1) : null
        };
        Canvas.SetLeft(panel, PanelRect.X);
        Canvas.SetTop(panel, PanelRect.Y);

        var overlay = new Border
        {
            Width = OverlayRect.Width,
            Height = OverlayRect.Height,
            Background = overlayBackground
        };
        Canvas.SetLeft(overlay, OverlayRect.X);
        Canvas.SetTop(overlay, OverlayRect.Y);

        host.Children.Add(panel);
        host.Children.Add(overlay);

        var window = new Window { Content = host, Width = Size, Height = Size };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, overlay);
    }

    /// <summary>Mean colour of a band, as a cheap fingerprint of what it shows.</summary>
    private static (double R, double G, double B) Sample(Window window, Rect area)
    {
        using var frame = window.CaptureRenderedFrame()
                          ?? throw new InvalidOperationException("nothing rendered");

        using var buffer = frame.Lock();
        var stride = buffer.RowBytes;
        var bytes = new byte[stride * buffer.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);

        double r = 0, g = 0, b = 0;
        var n = 0;
        for (var y = (int)area.Y + 4; y < (int)area.Bottom - 4; y++)
        for (var x = (int)area.X + 4; x < (int)area.Right - 4; x++)
        {
            var i = y * stride + x * 4;
            b += bytes[i];
            g += bytes[i + 1];
            r += bytes[i + 2];
            n++;
        }

        return (r / n, g / n, b / n);
    }

    private static double Distance((double R, double G, double B) a, (double R, double G, double B) b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

#if NUNIT
    [AvaloniaTest]
#elif XUNIT
    [AvaloniaFact]
#endif
    public void Foreign_Slice_Redraw_Keeps_Effect_Input_Complete()
    {
        // Frame 1 renders everything. Frame 2 changes only the overlay's
        // background, so the dirty region is the overlay's rect: the shadowed
        // panel re-renders clipped to a foreign slice that contains none of
        // its own content, only its shadow.
        var (window, overlay) = BuildScene(HalfRed);
        _ = Sample(window, OverlayRect);

        overlay.Background = HalfBlue;
        Dispatcher.UIThread.RunJobs();
        var partial = Sample(window, OverlayRect);

        // Ground truth: the same scene built directly in its final state and
        // rendered in one full pass.
        var (reference, _) = BuildScene(HalfBlue);
        var full = Sample(reference, OverlayRect);

        // Geometry guard: prove the shadow really darkens the compared region
        // in a full render by comparing against the same scene without the
        // effect. If this fails the equality assertion below would only hold
        // vacuously and the test exercises nothing.
        var (noEffectWindow, _) = BuildScene(HalfBlue, withEffect: false);
        var noShadow = Sample(noEffectWindow, OverlayRect);
        AssertHelper.True(Distance(full, noShadow) > 25,
            $"The shadow does not reach the compared region ({Distance(full, noShadow):F2}); " +
            "the scene geometry is broken.");

        AssertHelper.True(Distance(partial, full) <= 3,
            "A foreign-slice redraw changed the shadowed pixels inside the slice: " +
            $"partial {partial} vs full {full} (delta {Distance(partial, full):F2}). " +
            "The effect's filter input lost the subtree content outside the slice.");
    }
}
