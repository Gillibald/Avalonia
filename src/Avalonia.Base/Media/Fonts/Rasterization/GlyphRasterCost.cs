using System;
using System.Diagnostics;
using System.Threading;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// What rasterizing one glyph mask of a typeface costs on this device and runtime, learned
    /// from the masks it actually rasterizes: a smoothed time per glyph at a reference size,
    /// scaled to other sizes by <see cref="SizeFactor"/>. Static draws rasterize every new size
    /// and transform, so a typeface has samples before any motion asks for an estimate; JIT
    /// tiering, an interpreted or AOT runtime and the device's speed all show up in them.
    /// Updates race without a lock between threads drawing the same typeface; a lost update only
    /// delays the average by one sample.
    /// </summary>
    internal sealed class GlyphRasterCost
    {
        /// <summary>
        /// The cost per glyph at <see cref="ReferencePixelsPerEm"/> before the typeface has
        /// rasterized anything, in microseconds: about a cold CJK glyph under Mono on a phone,
        /// so text never measured counts as expensive.
        /// </summary>
        internal const double FallbackMicroseconds = 60;

        /// <summary>The size whose cost per glyph the average holds.</summary>
        internal const double ReferencePixelsPerEm = 16;

        // A faster sample weighs an eighth and a slower one a 64th, capped at four times the
        // average. The first rasterizations of a process run on code that is still compiling and
        // take several times as long, and a collection or preemption can stall any one glyph;
        // nothing makes a glyph faster than its code allows. So the average settles near the
        // warm cost within a paragraph of glyphs and stays there through occasional stalls.
        private const double FasterWeight = 1.0 / 8;
        private const double SlowerWeight = 1.0 / 64;
        private const double MaxSampleRatio = 4;

        private static readonly double s_microsecondsPerTick = 1e6 / Stopwatch.Frequency;

        private double _microseconds;

        /// <param name="seed">Not used yet.</param>
        public GlyphRasterCost(GlyphRasterCost? seed = null)
        {
        }

        [ThreadStatic]
        private static long t_ticks;

        /// <summary>
        /// The <see cref="Stopwatch"/> ticks the calling thread spent rasterizing glyph masks, a
        /// running total; a frame takes the difference around itself.
        /// </summary>
        internal static long TicksOnThread => t_ticks;

        /// <summary>
        /// The estimated time to rasterize one glyph mask at <paramref name="pixelsPerEm"/>, in
        /// microseconds.
        /// </summary>
        public double PerGlyph(double pixelsPerEm)
        {
            var average = Volatile.Read(ref _microseconds);

            return (average > 0 ? average : FallbackMicroseconds) * SizeFactor(pixelsPerEm);
        }

        /// <summary>
        /// Records that rasterizing one glyph mask at <paramref name="pixelsPerEm"/> took
        /// <paramref name="ticks"/> <see cref="Stopwatch"/> ticks.
        /// </summary>
        public void Record(double pixelsPerEm, long ticks)
        {
            var sample = ticks * s_microsecondsPerTick / SizeFactor(pixelsPerEm);
            var average = _microseconds;

            t_ticks += ticks;

            _microseconds = average > 0
                ? sample < average
                    ? average + (sample - average) * FasterWeight
                    : average + (Math.Min(sample, average * MaxSampleRatio) - average) * SlowerWeight
                : Math.Max(sample, double.Epsilon);
        }

        /// <summary>
        /// How the cost of a glyph mask grows with its size, relative to
        /// <see cref="ReferencePixelsPerEm"/>: a part per glyph (outline, hinting, setup) and a
        /// part that grows with the rows and edges of coverage. Warm x64 builds of Segoe UI and
        /// Microsoft YaHei grow from 8 to 128 pixels per em about as 1 + size / 32 does.
        /// </summary>
        internal static double SizeFactor(double pixelsPerEm)
            => (32 + Math.Max(1, pixelsPerEm)) / (32 + ReferencePixelsPerEm);
    }
}
