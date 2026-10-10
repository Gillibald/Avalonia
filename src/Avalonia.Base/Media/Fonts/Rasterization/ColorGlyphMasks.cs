using System;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Colour masks of COLR v1 glyphs: each glyph's paint graph rasterized once per scale bucket
    /// and horizontal phase into a premultiplied BGRA <see cref="GlyphMask"/> (four channels),
    /// cached in the typeface's <see cref="GlyphTypeface.ColorMaskCache"/>. Upright runs compose
    /// their run masks from these instead of replaying the glyph's vector fills on every draw.
    /// </summary>
    /// <remarks>
    /// A mask is the glyph's cached recording replayed into a CPU raster surface of the render
    /// interface, so it holds exactly the fills, gradients, clips and composites the vector path
    /// draws, and every destination, CPU or GPU, draws the same mask bytes.
    /// </remarks>
    internal static class ColorGlyphMasks
    {
        /// <summary>
        /// Transparent pixels around the scaled ink box, so antialiasing at fractional ink edges
        /// is never cut off.
        /// </summary>
        public const int Pad = 1;

        private static readonly Func<ColorGlyphMaskKey, (GlyphTypeface Typeface, ColorGlyphRecording Recording,
            IPlatformRenderInterface RenderInterface), GlyphMask> s_build =
            static (key, state) => Build(state.Typeface, state.Recording, state.RenderInterface, key);

        /// <summary>
        /// Gets the colour mask of v1 glyph <paramref name="glyph"/> at <paramref name="scaleQ"/> and
        /// <paramref name="phase"/>, rasterizing it on a miss. <paramref name="foreground"/> is the
        /// run's text colour, used only when the paint resolves the CPAL foreground sentinel.
        /// Returns <c>false</c> when the glyph cannot draw from a mask at this size: it has no
        /// recording, its mask would exceed <see cref="GlyphMaskCache{TKey}.MaxEntryBytes"/>, or no
        /// render interface is available; the caller then draws the glyph as vectors.
        /// </summary>
        public static bool TryGetMask(GlyphTypeface typeface, ushort glyph, ushort scaleQ, byte phase,
            Color? foreground, out GlyphMask mask)
        {
            var cache = typeface.ColorMaskCache;
            var plainKey = new ColorGlyphMaskKey(glyph, scaleQ, phase);

            // Masks of paints without the sentinel are stored under the key without a foreground and
            // sentinel paints only under keys with one, so a hit needs neither the recording nor the
            // knowledge of which kind the paint is.
            if (cache.TryGet(plainKey, out mask))
            {
                return true;
            }

            var foregroundKey = foreground is { } color
                ? plainKey with { Foreground = color.ToUInt32(), HasForeground = true }
                : plainKey;

            if (foreground is not null && cache.TryGet(foregroundKey, out mask))
            {
                return true;
            }

            mask = GlyphMask.Empty;

            if (AvaloniaLocator.Current.GetService<IPlatformRenderInterface>() is not { } renderInterface ||
                typeface.GetGlyphRecording(glyph, null) is not { } recording)
            {
                return false;
            }

            var key = plainKey;

            if (recording.UsesForeground && foreground is { } text)
            {
                key = foregroundKey;
                recording = typeface.GetGlyphRecording(glyph, new GlyphDrawingOptions { Foreground = text }) ??
                    recording;
            }

            if (!TryGetPlacement(typeface, key, out _, out _, out var width, out var height) ||
                (long)width * height * 4 + 48 > GlyphMaskCache.MaxEntryBytes)
            {
                return false;
            }

            if (!recording.TryAcquire())
            {
                return false;
            }

            try
            {
                mask = cache.GetOrBuild(key, (typeface, recording, renderInterface), s_build);
            }
            finally
            {
                recording.Release();
            }

            return true;
        }

        /// <summary>
        /// The device rectangle a glyph's mask covers relative to its snapped pen: the scaled ink
        /// box (the COLR v1 clip box, else the drawing's bounds) shifted by the phase and padded by
        /// <see cref="Pad"/>. Returns <c>false</c> for a glyph without ink.
        /// </summary>
        private static bool TryGetPlacement(GlyphTypeface typeface, in ColorGlyphMaskKey key, out int left,
            out int top, out int width, out int height)
        {
            left = top = width = height = 0;

            if (!typeface.TryGetColorGlyphInkBounds(key.Glyph, out var box) || box.XMax <= box.XMin ||
                box.YMax <= box.YMin)
            {
                return false;
            }

            var scale = key.PixelsPerEm / typeface.Metrics.DesignEmHeight;
            var phase = key.PhaseOffset;

            left = (int)MathF.Floor(box.XMin * scale + phase) - Pad;
            top = (int)MathF.Floor(-box.YMax * scale) - Pad;
            width = (int)MathF.Ceiling(box.XMax * scale + phase) + Pad - left;
            height = (int)MathF.Ceiling(-box.YMin * scale) + Pad - top;

            return true;
        }

        private static unsafe GlyphMask Build(GlyphTypeface typeface, ColorGlyphRecording recording,
            IPlatformRenderInterface renderInterface, ColorGlyphMaskKey key)
        {
            if (!TryGetPlacement(typeface, key, out var left, out var top, out var width, out var height))
            {
                return GlyphMask.Empty;
            }

            GlyphRasterDiagnostics.CountColorMaskRasterization();

            var scale = (double)key.PixelsPerEm / typeface.Metrics.DesignEmHeight;

            // The recording is in font design units at the local origin (y flip included): scale it
            // to the key's size and move the pen to (phase - left, -top) inside the surface.
            var transform = Matrix.CreateScale(scale, scale) *
                Matrix.CreateTranslation(key.PhaseOffset - left, -top);

            using var target = renderInterface.CreateRenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));

            using (var impl = target.CreateDrawingContext())
            {
                impl.Clear(Colors.Transparent);

                using var context = new PlatformDrawingContext(impl, ownsImpl: false);

                context.DrawRecording(recording.Recording, transform);
            }

            using var framebuffer = target.Lock();

            var swap = framebuffer.Format == PixelFormats.Rgba8888;

            if (!swap && framebuffer.Format != PixelFormats.Bgra8888)
            {
                return GlyphMask.Empty;
            }

            var pixels = new byte[width * height * 4];

            for (var y = 0; y < height; y++)
            {
                var source = new ReadOnlySpan<byte>((byte*)framebuffer.Address + (long)y * framebuffer.RowBytes, width * 4);
                var row = pixels.AsSpan(y * width * 4, width * 4);

                source.CopyTo(row);

                if (swap)
                {
                    for (var x = 0; x < row.Length; x += 4)
                    {
                        (row[x], row[x + 2]) = (row[x + 2], row[x]);
                    }
                }
            }

            return new GlyphMask(pixels, width, height, left, top, channels: 4);
        }
    }
}
