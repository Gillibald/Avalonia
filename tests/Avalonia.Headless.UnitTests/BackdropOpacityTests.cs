using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace Avalonia.Headless.UnitTests;

/// <summary>
/// A backdrop belongs to the visual it is set on, so that visual's opacity fades
/// the filtered backdrop like the rest of its content - in both ways opacity is
/// applied, folded into each draw or through an offscreen layer.
/// </summary>
public class BackdropOpacityTests
{
    private const int Width = 200;
    private const int Height = 160;
    private static readonly PixelRect Glass = new(40, 40, 120, 80);

    private static (Window Window, Border Glass) BuildScene(bool fullOpacityHandling, double opacity, bool backdrop)
    {
        var host = new Canvas { Width = Width, Height = Height, Background = Brushes.White };
        RenderOptions.SetRequiresFullOpacityHandling(host, fullOpacityHandling);
        for (var x = 0; x < Width; x += 16)
        {
            host.Children.Add(new Border
            {
                Background = Brushes.Black, Width = 6, Height = Height,
                [Canvas.LeftProperty] = x, [Canvas.TopProperty] = 0
            });
        }

        host.Children.Add(new Border
        {
            Background = Brushes.Crimson, Width = 50, Height = 40,
            [Canvas.LeftProperty] = 60, [Canvas.TopProperty] = 60
        });

        var glass = new Border
        {
            // A visual that paints nothing is skipped along with its backdrop, so
            // the glass carries an invisible fill.
            Background = Brushes.Transparent,
            Width = Glass.Width, Height = Glass.Height, Opacity = opacity,
            BackdropEffect = backdrop ? new ImmutableBlurEffect(10) : null,
            [Canvas.LeftProperty] = Glass.X, [Canvas.TopProperty] = Glass.Y
        };
        host.Children.Add(glass);

        var window = new Window { Content = host, Width = Width, Height = Height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, glass);
    }

    private static byte[] Capture(Window window, out int stride)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("nothing rendered");
        using var buffer = frame.Lock();
        stride = buffer.RowBytes;
        var bytes = new byte[stride * buffer.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>
    /// The largest channel difference inside the glass from the full-strength
    /// glass composited at <paramref name="opacity"/> over the plain scene.
    /// </summary>
    private static int DifferenceFromComposite(byte[] frame, int stride, bool fullOpacityHandling, double opacity)
    {
        var full = Capture(BuildScene(fullOpacityHandling, 1, backdrop: true).Window, out var fullStride);
        var plain = Capture(BuildScene(fullOpacityHandling, 1, backdrop: false).Window, out var plainStride);

        var worst = 0;
        var contrast = 0;
        for (var y = Glass.Y; y < Glass.Bottom; y++)
        for (var x = Glass.X * 4; x < Glass.Right * 4; x++)
        {
            if (x % 4 == 3)
                continue; // alpha is opaque everywhere
            contrast = Math.Max(contrast, Math.Abs(full[y * fullStride + x] - plain[y * plainStride + x]));
            var expected = (int)Math.Round(opacity * full[y * fullStride + x] + (1 - opacity) * plain[y * plainStride + x]);
            worst = Math.Max(worst, Math.Abs(frame[y * stride + x] - expected));
        }

        AssertHelper.True(contrast > 64, $"The glass barely changes the scene ({contrast}); the test would prove nothing.");
        return worst;
    }

#if NUNIT
    [AvaloniaTest, TestCase(false), TestCase(true)]
#elif XUNIT
    [AvaloniaTheory, InlineData(false), InlineData(true)]
#endif
    public void Glass_Opacity_Fades_The_Backdrop(bool fullOpacityHandling)
    {
        var (window, glass) = BuildScene(fullOpacityHandling, 1, backdrop: true);

        var worst = 0;
        var worstAt = 0d;
        foreach (var opacity in new[] { 0.5, 0.25, 1, 0.75 })
        {
            glass.Opacity = opacity;
            Dispatcher.UIThread.RunJobs();
            var frame = Capture(window, out var stride);
            var difference = DifferenceFromComposite(frame, stride, fullOpacityHandling, opacity);
            if (difference > worst)
            {
                worst = difference;
                worstAt = opacity;
            }
        }

        AssertHelper.True(worst <= 2, $"At opacity {worstAt} the glass differed by {worst} from its faded composite.");
    }
}
