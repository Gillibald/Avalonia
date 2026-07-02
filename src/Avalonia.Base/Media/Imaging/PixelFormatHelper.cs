using Avalonia.Platform;

namespace Avalonia.Media.Imaging;

/// <summary>
/// Shared pixel format arithmetic.
/// </summary>
internal static class PixelFormatHelper
{
    /// <summary>
    /// Gets the minimum number of bytes one row of <paramref name="width"/> pixels
    /// occupies, rounding sub-byte formats up to whole bytes.
    /// </summary>
    public static int GetMinRowBytes(PixelFormat format, int width)
        => (int)(((long)width * format.BitsPerPixel + 7) / 8);
}
