using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Scanline coverage rasterizer for glyph contours: fills a path captured by
    /// <see cref="GlyphPathBuilder"/> into an 8-bit alpha mask using analytic per-cell area
    /// accumulation (exact-area antialiasing — no supersampling, no edge lists). Curves are
    /// flattened adaptively to <see cref="FlattenTolerance"/>; winding follows the path's
    /// <see cref="GlyphPathBuilder.FillRule"/>.
    /// </summary>
    /// <remarks>
    /// Stateless per call: the only transients (the coverage accumulation buffer, and the
    /// crossing queue of the vector paths) are pooled, so repeated rasterization allocates
    /// nothing once the pools are warm, and identical inputs produce bit-identical output (fixed
    /// operation order, no data-dependent reordering). The vector paths perform the scalar
    /// path's floating-point operations, in its order, for every cell, so every
    /// <see cref="GlyphRasterizerPath"/> produces the same bytes. Safe to call from any thread;
    /// the render thread and a UI-thread <c>RenderTargetBitmap.Render</c> can rasterize
    /// concurrently.
    /// </remarks>
    internal static partial class GlyphRasterizer
    {
        /// <summary>Maximum curve-to-chord deviation after flattening, in device pixels.</summary>
        internal const float FlattenTolerance = 0.25f;

        // Curves flatten into at most this many segments. The tolerance formulas reach it only at a
        // second difference of about 65000 px (quadratic) or 21700 px (cubic); a curve whose
        // control points stay inside a glyph mask (at most MaxMaskSize px square) has a second
        // difference of at most twice the mask diagonal, about 11600 px. So it is a defensive
        // bound against hostile outlines, not a quality knob.
        private const int MaxCurveSegments = 256;

        /// <summary>
        /// Which instruction set accumulates and resolves coverage, apart from the small masks
        /// <see cref="EffectivePath"/> moves to the scalar path. Every path produces the same bytes.
        /// </summary>
        internal static GlyphRasterizerPath Path { get; set; } = DetectPath();

        /// <summary>Whether this machine can run <paramref name="path"/>.</summary>
        internal static bool IsSupported(GlyphRasterizerPath path) => path switch
        {
            GlyphRasterizerPath.Vector256 => Avx2.IsSupported,
            GlyphRasterizerPath.Vector128 => Sse41.IsSupported,
#if NET9_0_OR_GREATER
            GlyphRasterizerPath.Portable => Vector128.IsHardwareAccelerated,
#else
            GlyphRasterizerPath.Portable => false,
#endif
            _ => true,
        };

        private static GlyphRasterizerPath DetectPath()
            => IsSupported(GlyphRasterizerPath.Vector256) ? GlyphRasterizerPath.Vector256
                : IsSupported(GlyphRasterizerPath.Vector128) ? GlyphRasterizerPath.Vector128
                : IsSupported(GlyphRasterizerPath.Portable) ? GlyphRasterizerPath.Portable
                : GlyphRasterizerPath.Scalar;

        /// <summary>
        /// Masks of fewer cells than this rasterize on the scalar path while
        /// <see cref="Path"/> is <see cref="GlyphRasterizerPath.Portable"/>: there the portable
        /// path's per-mask cost (crossing queue, vector resolve) exceeds what its four lanes save.
        /// The value is the crossover under browser WebAssembly AOT, where the portable path is the
        /// default. The SSE and AVX2 paths are unaffected.
        /// </summary>
        internal const int PortableMinimumCells = 96;

        /// <summary>
        /// The path that rasterizes a mask of <paramref name="width"/> by <paramref name="height"/>
        /// cells while <paramref name="selected"/> is the selected path.
        /// </summary>
        internal static GlyphRasterizerPath EffectivePath(GlyphRasterizerPath selected, int width, int height)
            => selected == GlyphRasterizerPath.Portable && (long)width * height < PortableMinimumCells
                ? GlyphRasterizerPath.Scalar
                : selected;

        /// <summary>
        /// Rasterizes <paramref name="path"/> into an alpha mask of <paramref name="width"/> ×
        /// <paramref name="height"/> cells. <paramref name="offsetX"/>/<paramref name="offsetY"/>
        /// translate the captured points into mask-local space (mask placement plus any subpixel
        /// phase). When <paramref name="aliased"/> is true, coverage is thresholded at one half
        /// instead of producing antialiased levels.
        /// </summary>
        /// <remarks>
        /// The destination is fully overwritten (row-major, stride == width); it does not need to
        /// be cleared beforehand. Coverage outside the mask is clipped: geometry left of the mask
        /// still contributes winding (a shape straddling the left edge fills correctly from
        /// column zero), geometry right of it is dropped.
        /// </remarks>
        public static void Rasterize(GlyphPathBuilder path, int width, int height,
            float offsetX, float offsetY, bool aliased, Span<byte> destination)
            => Rasterize(path, width, height, offsetX, offsetY, aliased, destination, width);

        /// <summary>
        /// Rasterizes like <see cref="Rasterize(GlyphPathBuilder, int, int, float, float, bool, Span{byte})"/>
        /// into rows <paramref name="destinationStride"/> bytes apart, such as a rectangle of a
        /// larger image. Bytes between the rows are left untouched.
        /// </summary>
        public static void Rasterize(GlyphPathBuilder path, int width, int height,
            float offsetX, float offsetY, bool aliased, Span<byte> destination, int destinationStride)
            => Rasterize(EffectivePath(Path, width, height), path, width, height, offsetX, offsetY, aliased,
                destination, destinationStride);

        /// <summary>
        /// Rasterizes like <see cref="Rasterize(GlyphPathBuilder, int, int, float, float, bool, Span{byte}, int)"/>
        /// on exactly <paramref name="vectorPath"/>, whatever the mask size; tests compare the paths
        /// through it.
        /// </summary>
        internal static void Rasterize(GlyphRasterizerPath vectorPath, GlyphPathBuilder path, int width, int height,
            float offsetX, float offsetY, bool aliased, Span<byte> destination, int destinationStride)
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            if (destinationStride < width)
            {
                throw new ArgumentOutOfRangeException(nameof(destinationStride));
            }

            if (destination.Length < (long)destinationStride * (height - 1) + width)
            {
                throw new ArgumentException("Destination must hold height rows of width bytes at the stride.",
                    nameof(destination));
            }

            var acc = ArrayPool<float>.Shared.Rent(width * height + CrossingQueue.SinkCells);
            var evenOdd = path.FillRule == Media.FillRule.EvenOdd;

            try
            {
                var cells = acc.AsSpan(0, width * height);

                if (vectorPath == GlyphRasterizerPath.Scalar || !IsSupported(vectorPath))
                {
                    cells.Clear();

                    var segments = new ScalarSegments();

                    AccumulatePath(path, ref segments, cells, width, height, offsetX, offsetY);
                    Resolve(cells, destination, width, height, destinationStride, evenOdd, aliased);
                }
                else
                {
                    // The queue's deposits that a crossing does not use land in cells past the
                    // mask, which the resolve never reads.
                    var buffer = acc.AsSpan(0, width * height + CrossingQueue.SinkCells);
                    var wide = vectorPath == GlyphRasterizerPath.Vector256;
                    var segments = new QueuedSegments(t_crossings ??= new CrossingQueue(), wide);

                    buffer.Clear();
                    AccumulatePath(path, ref segments, buffer, width, height, offsetX, offsetY);
                    segments.Queue.Finish(buffer, width, height, wide);
                    ResolveVectorized(cells, destination, width, height, destinationStride, evenOdd, aliased,
                        vectorPath);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(acc);
            }
        }

        /// <summary>Receives the line segments a path flattens into.</summary>
        private interface ISegmentSink
        {
            void Add(float x0, float y0, float x1, float y1, Span<float> cells, int width, int height);
        }

        /// <summary>Deposits every segment's coverage as it arrives.</summary>
        private struct ScalarSegments : ISegmentSink
        {
            public void Add(float x0, float y0, float x1, float y1, Span<float> cells, int width, int height)
                => AddSegment(x0, y0, x1, y1, cells, width, height);
        }

        private static void AccumulatePath<TSink>(GlyphPathBuilder path, ref TSink sink, Span<float> cells, int width,
            int height, float offsetX, float offsetY)
            where TSink : struct, ISegmentSink
        {
            var verbs = path.Verbs;
            var points = path.Points;

            var p = 0;
            float startX = 0, startY = 0;
            float curX = 0, curY = 0;

            for (var v = 0; v < verbs.Length; v++)
            {
                switch ((GlyphPathVerb)verbs[v])
                {
                    case GlyphPathVerb.MoveTo:
                        startX = curX = points[p++] + offsetX;
                        startY = curY = points[p++] + offsetY;
                        break;

                    case GlyphPathVerb.LineTo:
                    {
                        var x = points[p++] + offsetX;
                        var y = points[p++] + offsetY;
                        sink.Add(curX, curY, x, y, cells, width, height);
                        curX = x;
                        curY = y;
                        break;
                    }

                    case GlyphPathVerb.QuadTo:
                    {
                        var cx = points[p++] + offsetX;
                        var cy = points[p++] + offsetY;
                        var x = points[p++] + offsetX;
                        var y = points[p++] + offsetY;
                        FlattenQuad(ref sink, curX, curY, cx, cy, x, y, cells, width, height);
                        curX = x;
                        curY = y;
                        break;
                    }

                    case GlyphPathVerb.CubicTo:
                    {
                        var c1X = points[p++] + offsetX;
                        var c1Y = points[p++] + offsetY;
                        var c2X = points[p++] + offsetX;
                        var c2Y = points[p++] + offsetY;
                        var x = points[p++] + offsetX;
                        var y = points[p++] + offsetY;
                        FlattenCubic(ref sink, curX, curY, c1X, c1Y, c2X, c2Y, x, y, cells, width, height);
                        curX = x;
                        curY = y;
                        break;
                    }

                    case GlyphPathVerb.Close:
                        sink.Add(curX, curY, startX, startY, cells, width, height);
                        curX = startX;
                        curY = startY;
                        break;
                }
            }
        }

        /// <summary>
        /// The number of uniform pieces a quadratic flattens into: deviation of a quadratic
        /// from its chord is |p0 - 2c + p1| / 4, and uniform subdivision into n pieces scales
        /// it by 1 / n², so n is solved for at the tolerance.
        /// </summary>
        internal static int QuadSegmentCount(float x0, float y0, float cx, float cy, float x1, float y1)
        {
            var ddx = x0 - 2f * cx + x1;
            var ddy = y0 - 2f * cy + y1;
            var dd = MathF.Sqrt(ddx * ddx + ddy * ddy);
            var n = 1 + (int)MathF.Sqrt(dd * (1f / (4f * FlattenTolerance)));

            return n > MaxCurveSegments ? MaxCurveSegments : n;
        }

        /// <summary>
        /// The number of uniform pieces a cubic flattens into: a deviation bound from the two
        /// second differences (kurbo/Skia-style estimate), scaled by 1 / n² under uniform
        /// subdivision.
        /// </summary>
        internal static int CubicSegmentCount(float x0, float y0, float c1X, float c1Y, float c2X, float c2Y,
            float x1, float y1)
        {
            var d1X = x0 - 2f * c1X + c2X;
            var d1Y = y0 - 2f * c1Y + c2Y;
            var d2X = c1X - 2f * c2X + x1;
            var d2Y = c1Y - 2f * c2Y + y1;
            var dd = MathF.Max(
                MathF.Sqrt(d1X * d1X + d1Y * d1Y),
                MathF.Sqrt(d2X * d2X + d2Y * d2Y));
            var n = 1 + (int)MathF.Sqrt(dd * (3f / (4f * FlattenTolerance)));

            return n > MaxCurveSegments ? MaxCurveSegments : n;
        }

        private static void FlattenQuad<TSink>(ref TSink sink, float x0, float y0, float cx, float cy, float x1,
            float y1, Span<float> cells, int width, int height)
            where TSink : struct, ISegmentSink
        {
            var n = QuadSegmentCount(x0, y0, cx, cy, x1, y1);

            float prevX = x0, prevY = y0;

            for (var i = 1; i <= n; i++)
            {
                float nx, ny;

                if (i == n)
                {
                    // Land exactly on the endpoint so adjoining segments share coordinates
                    // bit-for-bit (no winding seams from floating-point drift).
                    nx = x1;
                    ny = y1;
                }
                else
                {
                    var t = i / (float)n;
                    var mt = 1f - t;
                    var a = mt * mt;
                    var b = 2f * mt * t;
                    var c = t * t;
                    nx = a * x0 + b * cx + c * x1;
                    ny = a * y0 + b * cy + c * y1;
                }

                sink.Add(prevX, prevY, nx, ny, cells, width, height);
                prevX = nx;
                prevY = ny;
            }
        }

        private static void FlattenCubic<TSink>(ref TSink sink, float x0, float y0, float c1X, float c1Y, float c2X,
            float c2Y, float x1, float y1, Span<float> cells, int width, int height)
            where TSink : struct, ISegmentSink
        {
            var n = CubicSegmentCount(x0, y0, c1X, c1Y, c2X, c2Y, x1, y1);

            float prevX = x0, prevY = y0;

            for (var i = 1; i <= n; i++)
            {
                float nx, ny;

                if (i == n)
                {
                    nx = x1;
                    ny = y1;
                }
                else
                {
                    var t = i / (float)n;
                    var mt = 1f - t;
                    var a = mt * mt * mt;
                    var b = 3f * mt * mt * t;
                    var c = 3f * mt * t * t;
                    var d = t * t * t;
                    nx = a * x0 + b * c1X + c * c2X + d * x1;
                    ny = a * y0 + b * c1Y + c * c2Y + d * y1;
                }

                sink.Add(prevX, prevY, nx, ny, cells, width, height);
                prevX = nx;
                prevY = ny;
            }
        }

        /// <summary>
        /// Accumulates one line segment's signed coverage deltas. Cells hold d(coverage)/dx per
        /// row; <see cref="Resolve"/> integrates along x to recover winding.
        /// </summary>
        private static void AddSegment(float x0, float y0, float x1, float y1,
            Span<float> cells, int width, int height)
        {
            // Horizontal segments sweep no scanlines; malformed coordinates (a hostile font that
            // slipped through the walkers' own guards) are dropped rather than propagated.
            if (y0 == y1 || !float.IsFinite(x0) || !float.IsFinite(y0) || !float.IsFinite(x1) || !float.IsFinite(y1))
            {
                return;
            }

            float dir;

            if (y0 < y1)
            {
                dir = 1f;
            }
            else
            {
                (x0, x1) = (x1, x0);
                (y0, y1) = (y1, y0);
                dir = -1f;
            }

            if (y1 <= 0f || y0 >= height)
            {
                return;
            }

            var dxdy = (x1 - x0) / (y1 - y0);

            if (y0 < 0f)
            {
                x0 += dxdy * -y0;
                y0 = 0f;
            }

            if (y1 > height)
            {
                x1 = x0 + dxdy * (height - y0);
                y1 = height;
            }

            var iy0 = (int)y0;
            var iyEnd = (int)MathF.Ceiling(y1);

            if (iyEnd > height)
            {
                iyEnd = height;
            }

            for (var iy = iy0; iy < iyEnd; iy++)
            {
                var ya = MathF.Max(y0, iy);
                var yb = MathF.Min(y1, iy + 1);
                var dy = yb - ya;

                if (dy <= 0f)
                {
                    continue;
                }

                var xa = x0 + dxdy * (ya - y0);
                var xb = x0 + dxdy * (yb - y0);

                AccumulateRow(cells, iy * width, width, xa, xb, dir * dy);
            }
        }

        /// <summary>
        /// Deposits <paramref name="area"/> as coverage deltas for a crossing that sweeps from
        /// <paramref name="xa"/> to <paramref name="xb"/> within one scanline slab. Crossings left
        /// of the mask land on column zero (the row fills from its left edge); the part of a sweep
        /// beyond the right edge only affects cells outside the mask and is dropped.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AccumulateRow(Span<float> cells, int rowBase, int width, float xa, float xb, float area)
        {
            if (xa > xb)
            {
                (xa, xb) = (xb, xa);
            }

            if (xb <= 0f)
            {
                cells[rowBase] += area;
                return;
            }

            if (xa >= width)
            {
                return;
            }

            if (xa < 0f)
            {
                var f = -xa / (xb - xa);
                cells[rowBase] += area * f;
                area *= 1f - f;
                xa = 0f;
            }

            if (xb > width)
            {
                area *= (width - xa) / (xb - xa);
                xb = width;
            }

            var run = xb - xa;

            if (run < 1f / 1024f)
            {
                // Effectively a vertical crossing: split between the two neighboring cells by the
                // fractional position.
                var x = 0.5f * (xa + xb);
                var ix = (int)x;

                if (ix >= width)
                {
                    ix = width - 1;
                }

                var fr = x - ix;
                cells[rowBase + ix] += area * (1f - fr);

                if (ix + 1 < width)
                {
                    cells[rowBase + ix + 1] += area * fr;
                }

                return;
            }

            var invRun = 1f / run;
            var ix0 = (int)xa;
            var ix1 = (int)xb;

            if (ix1 >= width)
            {
                ix1 = width - 1;
            }

            for (var ix = ix0; ix <= ix1; ix++)
            {
                var cx0 = MathF.Max(xa, ix);
                var cx1 = MathF.Min(xb, ix + 1);
                var w01 = cx1 - cx0;

                if (w01 <= 0f)
                {
                    continue;
                }

                var subArea = area * (w01 * invRun);
                var xMid = 0.5f * (cx0 + cx1) - ix;

                cells[rowBase + ix] += subArea * (1f - xMid);

                if (ix + 1 < width)
                {
                    cells[rowBase + ix + 1] += subArea * xMid;
                }
            }
        }

        private static void Resolve(Span<float> cells, Span<byte> destination, int width, int height, int stride,
            bool evenOdd, bool aliased)
        {
            var i = 0;

            for (var y = 0; y < height; y++)
            {
                // Accumulate per row: each closed contour's crossings sum to zero across a row, so
                // the integral returns to zero at the row's end and rows stay independent.
                var sum = 0f;
                var row = destination.Slice(y * stride, width);

                for (var x = 0; x < width; x++, i++)
                {
                    sum += cells[i];

                    float coverage;

                    if (evenOdd)
                    {
                        // Triangle wave: winding 0→0, 1→1, 2→0, … with linear AA ramps between.
                        var t = sum - 2f * MathF.Round(sum * 0.5f);
                        coverage = MathF.Abs(t);
                    }
                    else
                    {
                        coverage = MathF.Min(MathF.Abs(sum), 1f);
                    }

                    row[x] = aliased
                        ? coverage >= 0.5f ? (byte)255 : (byte)0
                        : (byte)(coverage * 255f + 0.5f);
                }
            }
        }
    }
}
