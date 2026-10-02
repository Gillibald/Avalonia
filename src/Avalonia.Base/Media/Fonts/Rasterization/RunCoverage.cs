using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

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
        [ThreadStatic]
        private static TransientBuffers? t_transient;

        private byte[] _coverage = Array.Empty<byte>();
        private int[] _overlapPixels = Array.Empty<int>();
        private int[] _overlapStarts = s_noStarts;
        private byte[] _overlapCoverage = Array.Empty<byte>();
        private int _overlapCount;
        private int _stackedCount;

        /// <summary>The run mask key this coverage serves, with <see cref="RunMaskKey.CoverageTint"/>.</summary>
        public RunMaskKey Key { get; private set; }

        /// <summary>Top-left relative to the run's snapped origin pixel, device px.</summary>
        public int OffsetX { get; private set; }

        public int OffsetY { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        /// <summary>
        /// Row-major coverage, <see cref="Width"/> bytes per row: the coverage of the single
        /// glyph inking a pixel, zero where no glyph or several glyphs ink it.
        /// </summary>
        public ReadOnlySpan<byte> Coverage => _coverage.AsSpan(0, Width * Height);

        /// <summary>Indices into <see cref="Coverage"/> of the pixels several glyphs ink, ascending.</summary>
        public ReadOnlySpan<int> OverlapPixels => _overlapPixels.AsSpan(0, _overlapCount);

        /// <summary>
        /// Where the coverages of each overlap pixel start in <see cref="OverlapCoverage"/>; one
        /// more entry than <see cref="OverlapPixels"/>, the last marking the end.
        /// </summary>
        public ReadOnlySpan<int> OverlapStarts => _overlapStarts.AsSpan(0, _overlapCount + 1);

        /// <summary>The nonzero coverages of every overlap pixel, in run order.</summary>
        public ReadOnlySpan<byte> OverlapCoverage => _overlapCoverage.AsSpan(0, _stackedCount);

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
            => TryBuild(key, masks, penX, penY, null, out coverage);

        /// <summary>
        /// Builds coverage like <see cref="TryBuild(in RunMaskKey, ReadOnlySpan{GlyphMask}, ReadOnlySpan{int}, ReadOnlySpan{int}, out RunCoverage?)"/>
        /// into buffers this thread reuses, for a frame drawn once: the result holds until the
        /// next transient build on this thread, and must never be cached.
        /// </summary>
        public static bool TryBuildTransient(in RunMaskKey key, ReadOnlySpan<GlyphMask> masks, ReadOnlySpan<int> penX,
            ReadOnlySpan<int> penY, out RunCoverage? coverage)
            => TryBuild(key, masks, penX, penY, t_transient ??= new TransientBuffers(), out coverage);

        private static bool TryBuild(in RunMaskKey key, ReadOnlySpan<GlyphMask> masks, ReadOnlySpan<int> penX,
            ReadOnlySpan<int> penY, TransientBuffers? buffers, out RunCoverage? coverage)
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
            var length = width * height;
            var single = buffers is null ? new byte[length] : TransientBuffers.Grow(ref buffers.Single, length, clear: true);
            var counts = ArrayPool<byte>.Shared.Rent(single.Length);

            try
            {
                counts.AsSpan(0, length).Clear();

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
                        if (!AccumulateRow(mask.Alpha, row * mask.Width, mask.Width, single, counts,
                                (top + row) * width + left, ref overlaps))
                        {
                            return false;
                        }
                    }
                }

                coverage = buffers is null ? new RunCoverage() : buffers.Coverage;
                coverage.Key = key;
                coverage.OffsetX = minX;
                coverage.OffsetY = minY;
                coverage.Width = width;
                coverage.Height = height;
                coverage._coverage = single;

                if (overlaps == 0)
                {
                    coverage._overlapPixels = Array.Empty<int>();
                    coverage._overlapStarts = s_noStarts;
                    coverage._overlapCoverage = Array.Empty<byte>();
                    coverage._overlapCount = 0;
                    coverage._stackedCount = 0;
                }
                else
                {
                    BuildOverlaps(coverage, masks, penX, penY, counts, overlaps, buffers);
                }

                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(counts);
            }
        }

        private static readonly int[] s_noStarts = { 0 };

        /// <summary>
        /// Adds one mask row of <paramref name="length"/> coverage bytes at
        /// <paramref name="sourceOffset"/> to the run's coverage at <paramref name="start"/>:
        /// every inked pixel counts one more glyph and keeps the row's coverage byte, and
        /// <paramref name="overlaps"/> counts the pixels that reach two glyphs. Returns
        /// <c>false</c> when a pixel would count more glyphs than a byte holds.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AccumulateRow(byte[] alpha, int sourceOffset, int length, byte[] single, byte[] counts,
            int start, ref int overlaps)
        {
            var column = 0;

            if (Vector128.IsHardwareAccelerated)
            {
                // Sixteen pixels per step. Lanes past the row are masked off, so a step may read
                // and write back past the row's end as long as it stays inside all three arrays
                // (counts is at least as long as single); those lanes are stored unchanged.
                var lanes = Vector128.Create((byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);

                while (column < length && sourceOffset + column + 16 <= alpha.Length &&
                       start + column + 16 <= single.Length)
                {
                    var values = Vector128.LoadUnsafe(ref alpha[sourceOffset + column]);
                    var inked = ~Vector128.Equals(values, Vector128<byte>.Zero);

                    if (length - column < 16)
                    {
                        inked &= Vector128.LessThan(lanes, Vector128.Create((byte)(length - column)));
                    }

                    if (inked != Vector128<byte>.Zero)
                    {
                        ref var countsAt = ref counts[start + column];
                        ref var singleAt = ref single[start + column];
                        var count = Vector128.LoadUnsafe(ref countsAt);

                        if ((Vector128.Equals(count, Vector128<byte>.AllBitsSet) & inked) != Vector128<byte>.Zero)
                        {
                            return false;
                        }

                        overlaps += BitOperations.PopCount(
                            (Vector128.Equals(count, Vector128<byte>.One) & inked).ExtractMostSignificantBits());

                        // An inked lane is all ones, minus one: subtracting it counts one more glyph.
                        (count - inked).StoreUnsafe(ref countsAt);
                        Vector128.ConditionalSelect(inked, values, Vector128.LoadUnsafe(ref singleAt))
                            .StoreUnsafe(ref singleAt);
                    }

                    column += 16;
                }
            }

            for (; column < length; column++)
            {
                var value = alpha[sourceOffset + column];

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

            return true;
        }

        private static void BuildOverlaps(RunCoverage coverage, ReadOnlySpan<GlyphMask> masks, ReadOnlySpan<int> penX,
            ReadOnlySpan<int> penY, byte[] counts, int overlaps, TransientBuffers? buffers)
        {
            var width = coverage.Width;
            var length = width * coverage.Height;
            var single = coverage._coverage;
            var pixels = buffers is null ? new int[overlaps] : TransientBuffers.Grow(ref buffers.Pixels, overlaps);
            var starts = buffers is null ? new int[overlaps + 1] : TransientBuffers.Grow(ref buffers.Starts, overlaps + 1);

            // Ordinal of each overlap pixel, so the second walk finds its slot without a search.
            var ordinals = ArrayPool<int>.Shared.Rent(length);

            try
            {
                var next = 0;
                var total = 0;

                for (var index = 0; index < length; index++)
                {
                    if (counts[index] < 2)
                    {
                        continue;
                    }

                    pixels[next] = index;
                    starts[next] = total;
                    ordinals[index] = next;
                    total += counts[index];
                    single[index] = 0;
                    next++;
                }

                starts[overlaps] = total;

                var stacked = buffers is null ? new byte[total] : TransientBuffers.Grow(ref buffers.Stacked, total);
                var filled = buffers is null ? new int[overlaps] : TransientBuffers.Grow(ref buffers.Filled, overlaps, clear: true);

                for (var i = 0; i < masks.Length; i++)
                {
                    var mask = masks[i];

                    if (mask.IsEmpty)
                    {
                        continue;
                    }

                    var left = penX[i] + mask.Left - coverage.OffsetX;
                    var top = penY[i] + mask.Top - coverage.OffsetY;

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

                coverage._overlapPixels = pixels;
                coverage._overlapStarts = starts;
                coverage._overlapCoverage = stacked;
                coverage._overlapCount = overlaps;
                coverage._stackedCount = total;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(ordinals);
            }
        }

        /// <summary>
        /// The buffers and the coverage object of transient builds on one thread, grown to the
        /// largest run seen and reused by every later transient build.
        /// </summary>
        private sealed class TransientBuffers
        {
            public readonly RunCoverage Coverage = new();
            public byte[] Single = Array.Empty<byte>();
            public int[] Pixels = Array.Empty<int>();
            public int[] Starts = Array.Empty<int>();
            public byte[] Stacked = Array.Empty<byte>();
            public int[] Filled = Array.Empty<int>();

            /// <summary>
            /// <paramref name="buffer"/>, replaced by a larger one when it holds fewer than
            /// <paramref name="length"/> items, with its first <paramref name="length"/> items
            /// cleared when asked.
            /// </summary>
            public static T[] Grow<T>(ref T[] buffer, int length, bool clear = false)
            {
                if (buffer.Length < length)
                {
                    buffer = new T[Math.Max(length, buffer.Length * 2)];
                }
                else if (clear)
                {
                    buffer.AsSpan(0, length).Clear();
                }

                return buffer;
            }
        }
    }
}
