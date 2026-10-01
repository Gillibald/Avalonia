using System;
using System.Buffers;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// The untinted coverage of an upright run on a raster surface, from which
    /// <see cref="GlyphMaskBlitter.BlendRunCoverage"/> blends the pixels of the run's pre-tinted
    /// mask in any colour, without composing that mask first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pre-tinted compose draws every glyph's tinted coverage over the glyphs before it, so
    /// a pixel inked by one glyph holds that glyph's tinted coverage, a function of its coverage
    /// byte and the tint alone. Those pixels keep their coverage byte here. A pixel inked by
    /// several glyphs holds a composite that depends on every coverage, their order and the
    /// tint; such a pixel keeps zero in <see cref="Coverage"/> and its coverages, in run order,
    /// in the overlap list, so a blend composes it exactly as the pre-tinted compose would.
    /// Overlaps are rare in text (kerned pairs, marks, tight italics), so the list stays short.
    /// </para>
    /// <para>
    /// Coverage counts as ink only when nonzero: every coverage table maps zero to zero, so a
    /// zero leaves the pre-tinted compose unchanged too.
    /// </para>
    /// </remarks>
    internal sealed class RunCoverage : IDisposable
    {
        private RunCoverage(in RunMaskKey key, int offsetX, int offsetY, int width, int height, byte[] coverage,
            int[] overlapPixels, int[] overlapStarts, byte[] overlapCoverage)
        {
            Key = key;
            OffsetX = offsetX;
            OffsetY = offsetY;
            Width = width;
            Height = height;
            Coverage = coverage;
            OverlapPixels = overlapPixels;
            OverlapStarts = overlapStarts;
            OverlapCoverage = overlapCoverage;
        }

        /// <summary>The run mask key this coverage serves, with <see cref="RunMaskKey.CoverageTint"/>.</summary>
        public RunMaskKey Key { get; }

        /// <summary>Top-left relative to the run's snapped origin pixel, device px.</summary>
        public int OffsetX { get; }

        public int OffsetY { get; }

        public int Width { get; }

        public int Height { get; }

        /// <summary>
        /// Row-major coverage, <see cref="Width"/> bytes per row: the coverage of the single
        /// glyph inking a pixel, zero where no glyph or several glyphs ink it.
        /// </summary>
        public byte[] Coverage { get; }

        /// <summary>Indices into <see cref="Coverage"/> of the pixels several glyphs ink, ascending.</summary>
        public int[] OverlapPixels { get; }

        /// <summary>
        /// Where the coverages of each overlap pixel start in <see cref="OverlapCoverage"/>; one
        /// more entry than <see cref="OverlapPixels"/>, the last marking the end.
        /// </summary>
        public int[] OverlapStarts { get; }

        /// <summary>The nonzero coverages of every overlap pixel, in run order.</summary>
        public byte[] OverlapCoverage { get; }

        /// <summary>Nothing to release: the coverage lives in managed arrays.</summary>
        public void Dispose()
        {
        }

        /// <summary>
        /// Builds the coverage of glyph masks placed at the given positions relative to the
        /// run's snapped origin, in run order, over their union; <paramref name="coverage"/> is
        /// <c>null</c> when no mask has ink. Returns <c>false</c> when a pixel is inked by more
        /// glyphs than an overlap can record.
        /// </summary>
        public static bool TryBuild(in RunMaskKey key, ReadOnlySpan<GlyphMask> masks, ReadOnlySpan<int> penX,
            ReadOnlySpan<int> penY, out RunCoverage? coverage)
        {
            coverage = null;

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;

            for (var i = 0; i < masks.Length; i++)
            {
                var mask = masks[i];

                if (mask.IsEmpty)
                {
                    continue;
                }

                minX = Math.Min(minX, penX[i] + mask.Left);
                minY = Math.Min(minY, penY[i] + mask.Top);
                maxX = Math.Max(maxX, penX[i] + mask.Left + mask.Width);
                maxY = Math.Max(maxY, penY[i] + mask.Top + mask.Height);
            }

            if (minX >= maxX || minY >= maxY)
            {
                return true;
            }

            var width = maxX - minX;
            var height = maxY - minY;
            var single = new byte[width * height];
            var counts = ArrayPool<byte>.Shared.Rent(single.Length);

            try
            {
                counts.AsSpan(0, single.Length).Clear();

                var overlaps = 0;

                for (var i = 0; i < masks.Length; i++)
                {
                    var mask = masks[i];

                    if (mask.IsEmpty)
                    {
                        continue;
                    }

                    var left = penX[i] + mask.Left - minX;
                    var top = penY[i] + mask.Top - minY;

                    for (var row = 0; row < mask.Height; row++)
                    {
                        var source = mask.Alpha.AsSpan(row * mask.Width, mask.Width);
                        var start = (top + row) * width + left;

                        for (var column = 0; column < source.Length; column++)
                        {
                            var value = source[column];

                            if (value == 0)
                            {
                                continue;
                            }

                            var index = start + column;
                            var count = counts[index];

                            if (count == 1)
                            {
                                overlaps++;
                            }

                            if (count == byte.MaxValue)
                            {
                                return false;
                            }

                            counts[index] = (byte)(count + 1);
                            single[index] = value;
                        }
                    }
                }

                coverage = overlaps == 0
                    ? new RunCoverage(key, minX, minY, width, height, single, Array.Empty<int>(), s_noStarts,
                        Array.Empty<byte>())
                    : BuildOverlaps(key, masks, penX, penY, minX, minY, width, height, single, counts, overlaps);

                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(counts);
            }
        }

        private static readonly int[] s_noStarts = { 0 };

        private static RunCoverage BuildOverlaps(in RunMaskKey key, ReadOnlySpan<GlyphMask> masks, ReadOnlySpan<int> penX,
            ReadOnlySpan<int> penY, int minX, int minY, int width, int height, byte[] coverage, byte[] counts,
            int overlaps)
        {
            var pixels = new int[overlaps];
            var starts = new int[overlaps + 1];

            // Ordinal of each overlap pixel, so the second walk finds its slot without a search.
            var ordinals = ArrayPool<int>.Shared.Rent(coverage.Length);

            try
            {
                var next = 0;
                var total = 0;

                for (var index = 0; index < coverage.Length; index++)
                {
                    if (counts[index] < 2)
                    {
                        continue;
                    }

                    pixels[next] = index;
                    starts[next] = total;
                    ordinals[index] = next;
                    total += counts[index];
                    coverage[index] = 0;
                    next++;
                }

                starts[overlaps] = total;

                var stacked = new byte[total];
                var filled = new int[overlaps];

                for (var i = 0; i < masks.Length; i++)
                {
                    var mask = masks[i];

                    if (mask.IsEmpty)
                    {
                        continue;
                    }

                    var left = penX[i] + mask.Left - minX;
                    var top = penY[i] + mask.Top - minY;

                    for (var row = 0; row < mask.Height; row++)
                    {
                        var source = mask.Alpha.AsSpan(row * mask.Width, mask.Width);
                        var start = (top + row) * width + left;

                        for (var column = 0; column < source.Length; column++)
                        {
                            var value = source[column];
                            var index = start + column;

                            if (value == 0 || counts[index] < 2)
                            {
                                continue;
                            }

                            var ordinal = ordinals[index];

                            stacked[starts[ordinal] + filled[ordinal]++] = value;
                        }
                    }
                }

                return new RunCoverage(key, minX, minY, width, height, coverage, pixels, starts, stacked);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(ordinals);
            }
        }
    }
}
