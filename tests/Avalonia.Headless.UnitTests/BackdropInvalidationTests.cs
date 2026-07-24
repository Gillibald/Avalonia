using System;
using Avalonia.Controls;
using Avalonia.Layout;
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

#if NUNIT
    [AvaloniaTest]
#elif XUNIT
    [AvaloniaFact]
#endif
    public void Backdrop_Partial_Refreshes_Match_A_Fresh_Render()
    {
        // Successive small changes beneath the panel, each at a different
        // spot. A partial refresh must re-filter only the changed output
        // neighborhood from freshly painted input; re-ingesting anything
        // outside the certified region reads last frame's composited output
        // (wash plus filtered content) and smears. Every frame is compared
        // against a fresh one-pass render of the same state.
        var spots = new[] { new Rect(48, 78, 14, 14), new Rect(90, 100, 14, 14), new Rect(130, 84, 14, 14) };

        static (Window Window, Border[] Squares) BuildScene(IBrush[] colors, Rect[] spots)
        {
            var host = new Canvas { Width = Size, Height = Size, Background = Brushes.White };

            var squares = new Border[spots.Length];
            for (var i = 0; i < spots.Length; i++)
            {
                squares[i] = new Border
                {
                    Background = colors[i], Width = spots[i].Width, Height = spots[i].Height,
                    [Canvas.LeftProperty] = spots[i].X, [Canvas.TopProperty] = spots[i].Y
                };
                host.Children.Add(squares[i]);
            }

            var panel = new Border
            {
                Width = PanelRect.Width,
                Height = PanelRect.Height,
                Background = new ImmutableSolidColorBrush(Colors.White, 0.25),
                BackdropEffect = new ImmutableBlurEffect(6),
                [Canvas.LeftProperty] = PanelRect.X,
                [Canvas.TopProperty] = PanelRect.Y
            };
            host.Children.Add(panel);

            var window = new Window { Content = host, Width = Size, Height = Size };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return (window, squares);
        }

        var colors = new IBrush[] { Brushes.Crimson, Brushes.DodgerBlue, Brushes.MediumSeaGreen };
        var (window, squares) = BuildScene(colors, spots);
        _ = Sample(window, PanelRect);

        var swaps = new IBrush[] { Brushes.DodgerBlue, Brushes.MediumSeaGreen, Brushes.Crimson };
        var worst = 0d;
        for (var i = 0; i < spots.Length; i++)
        {
            colors[i] = swaps[i];
            squares[i].Background = swaps[i];
            Dispatcher.UIThread.RunJobs();
            var partial = Sample(window, PanelRect);

            var (reference, _) = BuildScene((IBrush[])colors.Clone(), spots);
            var fresh = Sample(reference, PanelRect);
            worst = Math.Max(worst, Distance(partial, fresh));
        }

        AssertHelper.True(worst <= 3,
            $"A partially refreshed frame drifted {worst:F2} from a fresh one-pass render.");
    }

#if NUNIT
    [AvaloniaTest]
#elif XUNIT
    [AvaloniaFact]
#endif
    public void Backdrop_Inside_A_Cached_Subtree_Tracks_Content_Behind_It()
    {
        // The panel and the content behind it live inside a bitmap-cached
        // subtree, so the panel's filter samples the HOST surface (the cache
        // texture). Its invalidation and refresh grants must therefore track
        // the host's dirty space: a grant certified against the target's
        // region lets the capture read a cache texture whose area beneath the
        // panel still holds the previous filtered output, and re-filtering
        // that smears. The repeated toggle amplifies any such feedback; the
        // final frame must match a fresh one-pass render of the same scene.
        static (Window Window, Border Behind) BuildCachedScene(IBrush behindBrush)
        {
            var host = new Canvas { Width = Size, Height = Size, Background = Brushes.White };

            var cached = new Canvas
            {
                Width = Size, Height = Size,
                CacheMode = new BitmapCache()
            };
            host.Children.Add(cached);

            var behind = new Border
            {
                Background = behindBrush, Width = 80, Height = 60,
                [Canvas.LeftProperty] = 50.0, [Canvas.TopProperty] = 60.0
            };
            cached.Children.Add(behind);

            var panel = new Border
            {
                Width = PanelRect.Width,
                Height = PanelRect.Height,
                Background = new ImmutableSolidColorBrush(Colors.White, 0.25),
                BackdropEffect = new ImmutableBlurEffect(6),
                [Canvas.LeftProperty] = PanelRect.X,
                [Canvas.TopProperty] = PanelRect.Y
            };
            cached.Children.Add(panel);

            var window = new Window { Content = host, Width = Size, Height = Size };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return (window, behind);
        }

        var (window, behind) = BuildCachedScene(Brushes.Crimson);
        _ = Sample(window, PanelRect);

        // Alternate the content beneath; every toggle re-filters.
        for (var i = 0; i < 6; i++)
        {
            behind.Background = i % 2 == 0 ? Brushes.DodgerBlue : Brushes.Crimson;
            Dispatcher.UIThread.RunJobs();
            _ = Sample(window, PanelRect);
        }

        behind.Background = Brushes.DodgerBlue;
        Dispatcher.UIThread.RunJobs();
        var toggled = Sample(window, PanelRect);

        var (reference, _) = BuildCachedScene(Brushes.DodgerBlue);
        var fresh = Sample(reference, PanelRect);

        AssertHelper.True(Distance(toggled, fresh) <= 3,
            "The filtered result inside the cached subtree drifted from a fresh render: " +
            $"toggled {toggled} vs fresh {fresh} (delta {Distance(toggled, fresh):F2}).");
    }

#if NUNIT
    [AvaloniaTest]
#elif XUNIT
    [AvaloniaFact]
#endif
    public void Backdrop_Result_Is_Stable_While_Only_Content_Above_It_Changes()
    {
        // The panel's own child paints above the point where the filter samples
        // the surface, so its changes reuse the retained filtered result. A
        // stale or feedback-smeared cache would drift the panel band away from
        // its first frame here; a real change beneath must still refresh it.
        var host = new Canvas { Width = Size, Height = Size, Background = Brushes.White };

        var caret = new Border
        {
            Width = 20, Height = 20, Background = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var panel = new Border
        {
            Width = PanelRect.Width,
            Height = PanelRect.Height,
            Background = new ImmutableSolidColorBrush(Colors.White, 0.25),
            BackdropEffect = new ImmutableBlurEffect(6),
            Child = caret
        };
        Canvas.SetLeft(panel, PanelRect.X);
        Canvas.SetTop(panel, PanelRect.Y);

        var behind = new Canvas { Width = Size, Height = Size };
        host.Children.Add(behind);
        host.Children.Add(panel);

        var window = new Window { Content = host, Width = Size, Height = Size };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var compositor = ElementComposition.GetElementVisual(window)!.Compositor;
        var recordingVisual = compositor.CreateRecordingVisual();
        recordingVisual.Recording = DrawingRecording.Create(compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Crimson, null, new Rect(20, 60, 90, 80));
            ctx.DrawEllipse(Brushes.DodgerBlue, null, new Rect(60, 80, 70, 50));
        });
        recordingVisual.Size = new Vector(Size, Size);
        ElementComposition.SetElementChildVisual(behind, recordingVisual);
        Dispatcher.UIThread.RunJobs();

        // The panel spans (40,70)-(160,130) and the child hugs its right edge,
        // so this band on the left only ever shows the filtered backdrop.
        var band = new Rect(44, 74, 60, 50);
        var caretRect = new Rect(140, 90, 20, 20);

        var first = Sample(window, band);
        var caretFirst = Sample(window, caretRect);
        var caretMoved = 0d;
        var drift = 0d;

        for (var i = 0; i < 6; i++)
        {
            caret.Background = i % 2 == 0 ? Brushes.White : Brushes.Black;
            Dispatcher.UIThread.RunJobs();

            drift = Math.Max(drift, Distance(first, Sample(window, band)));
            caretMoved = Math.Max(caretMoved, Distance(caretFirst, Sample(window, caretRect)));
        }

        AssertHelper.True(caretMoved > Threshold,
            $"The child never visibly changed (spread {caretMoved:F2}); the test exercised nothing.");
        AssertHelper.True(drift < 2,
            $"The filtered band drifted {drift:F2} while only content above the backdrop changed.");

        recordingVisual.Recording = DrawingRecording.Create(compositor, ctx =>
            ctx.DrawRectangle(Brushes.MediumSeaGreen, null, new Rect(0, 0, Size, Size)));
        Dispatcher.UIThread.RunJobs();

        AssertHelper.True(Distance(first, Sample(window, band)) > Threshold,
            "A change behind the panel did not refresh the filtered result.");
    }
}
