using System.Collections.Generic;
using Avalonia.Platform;

namespace Avalonia.Imaging.TestKit.Parity
{
    /// <summary>The Avalonia format and alpha format of each WPF <c>PixelFormats</c> member.</summary>
    public static class WicFormatNames
    {
        private static readonly Dictionary<string, (PixelFormat Format, AlphaFormat? Alpha)> s_formats = new()
        {
            ["BlackWhite"] = (PixelFormats.BlackWhite, AlphaFormat.Opaque),
            ["Gray2"] = (PixelFormats.Gray2, AlphaFormat.Opaque),
            ["Gray4"] = (PixelFormats.Gray4, AlphaFormat.Opaque),
            ["Gray8"] = (PixelFormats.Gray8, AlphaFormat.Opaque),
            ["Gray16"] = (PixelFormats.Gray16, AlphaFormat.Opaque),
            ["Gray32Float"] = (PixelFormats.Gray32Float, AlphaFormat.Opaque),
            // A palette may carry alpha, so the alpha format of indexed frames is not compared.
            ["Indexed1"] = (PixelFormats.Indexed1, null),
            ["Indexed2"] = (PixelFormats.Indexed2, null),
            ["Indexed4"] = (PixelFormats.Indexed4, null),
            ["Indexed8"] = (PixelFormats.Indexed8, null),
            ["Bgr555"] = (PixelFormats.Bgr555, AlphaFormat.Opaque),
            ["Bgr565"] = (PixelFormats.Bgr565, AlphaFormat.Opaque),
            ["Bgr24"] = (PixelFormats.Bgr24, AlphaFormat.Opaque),
            ["Rgb24"] = (PixelFormats.Rgb24, AlphaFormat.Opaque),
            ["Bgr32"] = (PixelFormats.Bgr32, AlphaFormat.Opaque),
            ["Bgra32"] = (PixelFormats.Bgra8888, AlphaFormat.Unpremul),
            ["Pbgra32"] = (PixelFormats.Bgra8888, AlphaFormat.Premul),
            ["Bgr101010"] = (PixelFormats.Bgr101010, AlphaFormat.Opaque),
            ["Rgb48"] = (PixelFormats.Rgb48, AlphaFormat.Opaque),
            ["Rgba64"] = (PixelFormats.Rgba64, AlphaFormat.Unpremul),
            ["Prgba64"] = (PixelFormats.Rgba64, AlphaFormat.Premul),
            ["Rgba128Float"] = (PixelFormats.Rgba128Float, AlphaFormat.Unpremul),
            ["Prgba128Float"] = (PixelFormats.Rgba128Float, AlphaFormat.Premul),
            ["Rgb128Float"] = (PixelFormats.Rgb128Float, AlphaFormat.Opaque),
            ["Cmyk32"] = (PixelFormats.Cmyk32, AlphaFormat.Opaque),
        };

        /// <summary>
        /// Looks up a WPF format name. <paramref name="alpha"/> is null when the alpha format
        /// is not part of the comparison.
        /// </summary>
        public static bool TryGet(string wpfName, out PixelFormat format, out AlphaFormat? alpha)
        {
            if (s_formats.TryGetValue(wpfName, out var entry))
            {
                (format, alpha) = entry;
                return true;
            }

            format = default;
            alpha = null;
            return false;
        }
    }
}
