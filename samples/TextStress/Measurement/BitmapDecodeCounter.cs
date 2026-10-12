using System;
using System.Diagnostics;
using System.Threading;
using Avalonia;
using Avalonia.Media.Fonts.Rasterization;

namespace TextStress.Measurement
{
    /// <summary>
    /// Counts the embedded glyph images (sbix, CBDT) decoded and the time spent decoding them, by
    /// wrapping the registered <see cref="IBitmapGlyphDecoder"/>, so a run shows how often its
    /// colour glyphs decode again.
    /// </summary>
    internal sealed class BitmapDecodeCounter : IBitmapGlyphDecoder
    {
        public static long Decodes;
        public static long DecodeTicks;

        private readonly IBitmapGlyphDecoder _inner;

        private BitmapDecodeCounter(IBitmapGlyphDecoder inner) => _inner = inner;

        /// <summary>Wraps the decoder the platform registered; call after setup.</summary>
        public static void Install()
        {
            if (AvaloniaLocator.Current.GetService<IBitmapGlyphDecoder>() is { } inner and not BitmapDecodeCounter)
            {
                AvaloniaLocator.CurrentMutable.Bind<IBitmapGlyphDecoder>().ToConstant(new BitmapDecodeCounter(inner));
            }
        }

        public bool TryDecode(ReadOnlyMemory<byte> encoded, out DecodedGlyphBitmap bitmap)
        {
            var start = Stopwatch.GetTimestamp();
            var decoded = _inner.TryDecode(encoded, out bitmap);

            Interlocked.Add(ref DecodeTicks, Stopwatch.GetTimestamp() - start);
            Interlocked.Increment(ref Decodes);
            return decoded;
        }
    }
}
