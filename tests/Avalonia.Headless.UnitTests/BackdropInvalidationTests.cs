using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace Avalonia.Headless.UnitTests;

/// <summary>
/// A backdrop filters the surface below it, so it has to repaint whenever the
/// content behind it changes - even though nothing about the backdrop visual
/// itself changed. These drive a recording visual behind a backdrop-filtered
/// panel and check the filtered region actually tracks it.
/// </summary>
public class BackdropInvalidationTests
{
    private const int Size = 200;

    // Mean-channel distance a band has to move before we call it a real change.
    private const double Threshold = 12;

    // The frosted panel, and the band of the frame it filters.
    private static readonly Rect PanelRect = new(40, 70, 120, 60);

    private static (Window Window, CompositionRecordingVisual Recording) BuildScene(IEffect backdrop)
    {
        var host = new Canvas { Width = Size, Height = Size, Background = Brushes.White };

        var panel = new Border
        {
            Width = PanelRect.Width,
            Height = PanelRect.Height,
            // A backdrop only shows where the control paints.
            Background = new ImmutableSolidColorBrush(Colors.White, 0.25),
            BackdropEffect = backdrop
        };
        Canvas.SetLeft(panel, PanelRect.X);
        Canvas.SetTop(panel, PanelRect.Y);

        // The moving content lives in a child visual behind the panel.
        var behind = new Canvas { Width = Size, Height = Size };
        host.Children.Add(behind);
        host.Children.Add(panel);

        var window = new Window { Content = host, Width = Size, Height = Size };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var compositor = ElementComposition.GetElementVisual(window)!.Compositor;

        var recording = DrawingRecording.Create(compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Crimson, null, new Rect(0, 0, 70, 40));
            ctx.DrawEllipse(Brushes.DodgerBlue, null, new Rect(10, 10, 50, 50));
        });

        var recordingVisual = compositor.CreateRecordingVisual();
        recordingVisual.Recording = recording;
        recordingVisual.Size = new Vector(Size, Size);
        ElementComposition.SetElementChildVisual(behind, recordingVisual);

        Dispatcher.UIThread.RunJobs();
        return (window, recordingVisual);
    }

    // A band clear of the panel. Sampling it proves the content behind really is
    // moving, so a static backdrop means stale filtering rather than a test that
    // never animated anything.
    private static readonly Rect ControlRect = new(0, 5, Size, 55);

    /// <summary>Mean colour of a band, as a cheap fingerprint of what it shows.</summary>
    private static (double R, double G, double B) Sample(Window window, Rect area)
    {
        using var frame = window.CaptureRenderedFrame()
                          ?? throw new InvalidOperationException("nothing rendered");

        return Sample(frame, area);
    }

    private static (double R, double G, double B) Sample(
        Avalonia.Media.Imaging.WriteableBitmap frame, Rect area)
    {
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

    private static (double R, double G, double B) SampleBackdrop(Window window) =>
        Sample(window, PanelRect);

    private static double Distance((double R, double G, double B) a, (double R, double G, double B) b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

#if NUNIT
    [AvaloniaTest]
#elif XUNIT
    [AvaloniaFact]
#endif
    public void Backdrop_Tracks_Recording_Moving_Behind_It()
    {
        var (window, recording) = BuildScene(new ImmutableBlurEffect(6));

        // Off to the side, then squarely behind the panel.
        recording.Offset = new Vector3D(-150, 0, 0);
        Dispatcher.UIThread.RunJobs();
        var away = SampleBackdrop(window);

        recording.Offset = new Vector3D(50, 60, 0);
        Dispatcher.UIThread.RunJobs();
        var behind = SampleBackdrop(window);

        AssertHelper.True(Distance(away, behind) > Threshold,
            $"Backdrop did not track content moving behind it: {away} vs {behind}");
    }

#if NUNIT
    [AvaloniaTest]
#elif XUNIT
    [AvaloniaFact]
#endif
    public void Backdrop_Tracks_Recording_Content_Changing_Behind_It()
    {
        var (window, recordingVisual) = BuildScene(new ImmutableBlurEffect(6));
        var compositor = recordingVisual.Compositor;

        // An animation driven by swapping the recording rather than moving the
        // visual: the geometry behind the panel changes every frame while nothing
        // about the backdrop visual itself is ever touched.
        (double R, double G, double B) first = default;
        var spread = 0d;

        for (var i = 0; i < 6; i++)
        {
            var width = 30 + i * 22;
            var frameRecording = DrawingRecording.Create(compositor, ctx =>
            {
                ctx.DrawRectangle(Brushes.Crimson, null, new Rect(30, 60, width, 80));
                ctx.DrawEllipse(Brushes.DodgerBlue, null, new Rect(40, 70, width * 0.6, 50));
            });

            recordingVisual.Recording = frameRecording;
            Dispatcher.UIThread.RunJobs();

            var sample = SampleBackdrop(window);
            if (i == 0)
                first = sample;
            else
                spread = Math.Max(spread, Distance(first, sample));
        }

        AssertHelper.True(spread > Threshold,
            $"Backdrop did not follow recording content changing behind it; spread was {spread:F2}");
    }

#if NUNIT
    [AvaloniaTest]
#elif XUNIT
    [AvaloniaFact]
#endif
    public void Backdrop_Reads_Content_Just_Outside_Its_Bounds()
    {
        // A blur reaches past the panel, so content next to it tints the filtered
        // result near that edge - the inward bleed that makes frosted glass look
        // right, and what CSS backdrop-filter does.
        //
        // This only holds because the area a backdrop reads is invalidated along
        // with the backdrop itself. Without that the surround is never repainted,
        // the filter reads a stale surface that already contains the panel's own
        // output, and re-filtering it smears outward instead.
        var (window, recordingVisual) = BuildScene(new ImmutableBlurEffect(12));
        var compositor = recordingVisual.Compositor;

        // A bar hard against the panel's top edge, still entirely outside it, and
        // only the panel's topmost rows are sampled - averaging the whole band
        // would dilute an effect that reaches a few pixels.
        var gap = new Rect(40, PanelRect.Y - 10, 120, 10);
        var topRows = new Rect(PanelRect.X, PanelRect.Y, PanelRect.Width, 10);

        recordingVisual.Recording = DrawingRecording.Create(compositor,
            ctx => ctx.DrawRectangle(Brushes.White, null, gap));
        Dispatcher.UIThread.RunJobs();
        var blank = Sample(window, topRows);

        recordingVisual.Recording = DrawingRecording.Create(compositor,
            ctx => ctx.DrawRectangle(Brushes.Black, null, gap));
        Dispatcher.UIThread.RunJobs();
        var dark = Sample(window, topRows);

        AssertHelper.True(Distance(blank, dark) > 1,
            "A black bar against the panel's top edge did not darken the filtered " +
            $"rows next to it: {blank} vs {dark}");
    }
}
