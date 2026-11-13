using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Avalonia.Headless.UnitTests;

/// <summary>
/// A window's last rendered frame, copied out so it can be compared with a
/// frame taken later or from another window.
/// </summary>
/// <remarks>
/// Comparisons are per pixel. A mean over a region cannot tell filtered from
/// unfiltered content: a blur keeps the region's mean colour and only moves
/// its edges.
/// </remarks>
internal sealed class RenderedFrame
{
    private readonly byte[] _bytes;
    private readonly int _stride;
    private readonly int _width;
    private readonly int _height;

    private RenderedFrame(byte[] bytes, int stride, int width, int height)
    {
        _bytes = bytes;
        _stride = stride;
        _width = width;
        _height = height;
    }

    public static RenderedFrame Capture(Window window)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("nothing rendered");
        using var buffer = frame.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        return new RenderedFrame(bytes, buffer.RowBytes, buffer.Size.Width, buffer.Size.Height);
    }

    /// <summary>The largest difference of any channel of any pixel inside <paramref name="area"/>.</summary>
    public static int MaxDifference(RenderedFrame a, RenderedFrame b, Rect area)
    {
        var left = Math.Max(0, (int)Math.Floor(area.X));
        var top = Math.Max(0, (int)Math.Floor(area.Y));
        var right = Math.Min(Math.Min(a._width, b._width), (int)Math.Ceiling(area.Right));
        var bottom = Math.Min(Math.Min(a._height, b._height), (int)Math.Ceiling(area.Bottom));

        var worst = 0;
        for (var y = top; y < bottom; y++)
        for (var x = left * 4; x < right * 4; x++)
            worst = Math.Max(worst, Math.Abs(a._bytes[y * a._stride + x] - b._bytes[y * b._stride + x]));
        return worst;
    }

    public static int MaxDifference(Window a, Window b, Rect area) =>
        MaxDifference(Capture(a), Capture(b), area);
}
