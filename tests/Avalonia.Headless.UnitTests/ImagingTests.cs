using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Avalonia.Headless.UnitTests;

/// <summary>
/// The imaging backend an app gets from the plain <c>UseSkia()</c> AppBuilder chain, with no
/// UseXxxImaging call: Skia's, and images decoded through it render.
/// </summary>
public class ImagingTests
{
#if NUNIT
    [AvaloniaTest]
#elif XUNIT
    [AvaloniaFact]
#endif
    public void UseSkia_Binds_Skia_As_The_Imaging_Backend()
    {
        AssertHelper.Equal("SkiaSharp", ImagingBackend.Current.Name);
    }

#if NUNIT
    [AvaloniaTest]
#elif XUNIT
    [AvaloniaFact]
#endif
    public void Png_Decoded_Through_The_Default_Backend_Renders()
    {
        using var encoded = new MemoryStream();

        using (var source = new WriteableBitmap(new PixelSize(8, 8), new Vector(96, 96), PixelFormats.Bgra8888,
                   AlphaFormat.Premul))
        {
            using (var pixels = source.Lock())
            {
                var row = new byte[8 * 4];

                for (var x = 0; x < 8; x++)
                {
                    // Opaque (R, G, B) = (200, 40, 10), stored B, G, R, A.
                    row[x * 4] = 10;
                    row[x * 4 + 1] = 40;
                    row[x * 4 + 2] = 200;
                    row[x * 4 + 3] = 255;
                }

                for (var y = 0; y < 8; y++)
                    Marshal.Copy(row, 0, pixels.Address + y * pixels.RowBytes, row.Length);
            }

            source.Save(encoded, new PngBitmapEncoder());
        }

        encoded.Position = 0;

        using var decoded = new Bitmap(encoded);
        var window = new Window
        {
            Background = Brushes.White,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = new Image { Source = decoded, Width = 8, Height = 8, Stretch = Stretch.None },
        };

        window.Show();

        using var frame = window.CaptureRenderedFrame();

        AssertHelper.NotNull(frame);

        using var rendered = frame.Lock();
        var pixel = new byte[4];

        Marshal.Copy(rendered.Address + 4 * rendered.RowBytes + 4 * 4, pixel, 0, 4);

        var rgba = rendered.Format == PixelFormats.Rgba8888;

        AssertHelper.True(rgba || rendered.Format == PixelFormats.Bgra8888, $"capture format {rendered.Format}");
        AssertHelper.Equal((byte)200, pixel[rgba ? 0 : 2]);
        AssertHelper.Equal((byte)40, pixel[1]);
        AssertHelper.Equal((byte)10, pixel[rgba ? 2 : 0]);
        window.Close();
    }
}
