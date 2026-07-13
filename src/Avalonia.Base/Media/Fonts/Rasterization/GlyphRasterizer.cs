using System;
using System.Buffers;
using System.Runtime.CompilerServices;

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
    /// Stateless per call: the only transient (the coverage accumulation buffer) is pooled, so
    /// repeated rasterization allocates nothing once the pool is warm, and identical inputs
    /// produce bit-identical output (fixed operation order, no data-dependent reordering). Safe
    /// to call from any thread; the render thread and a UI-thread
    /// <c>RenderTargetBitmap.Render</c> can rasterize concurrently.
    /// </remarks>
    internal static class GlyphRasterizer
    {
        /// <summary>Maximum curve-to-chord deviation after flattening, in device pixels.</summary>
        internal const float FlattenTolerance = 0.25f;

        // Curves flatten into at most this many segments; combined with the tolerance formula this
        // is only reachable for glyphs far larger than the mask path is used for (D4 sends very
        // large sizes to the geometry fallback), so it is a defensive bound, not a quality knob.
        private const int MaxCurveSegments = 256;

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
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            if (destination.Length < width * height)
            {
                throw new ArgumentException("Destination must hold width * height bytes.", nameof(destination));
            }

            var acc = ArrayPool<float>.Shared.Rent(width * height);

            try
            {
                var cells = acc.AsSpan(0, width * height);
                cells.Clear();

                AccumulatePath(path, cells, width, height, offsetX, offsetY);
                Resolve(cells, destination, width, height, path.FillRule == Media.FillRule.EvenOdd, aliased);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(acc);
            }
        }

        private static void AccumulatePath(GlyphPathBuilder path, Span<float> cells, int width, int height,
            float offsetX, float offsetY)
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
                        AddSegment(curX, curY, x, y, cells, width, height);
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
                        FlattenQuad(curX, curY, cx, cy, x, y, cells, width, height);
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
                        FlattenCubic(curX, curY, c1X, c1Y, c2X, c2Y, x, y, cells, width, height);
                        curX = x;
                        curY = y;
                        break;
                    }

                    case GlyphPathVerb.Close:
                        AddSegment(curX, curY, startX, startY, cells, width, height);
                        curX = startX;
                        curY = startY;
                        break;
                }
            }
        }

        private static void FlattenQuad(float x0, float y0, float cx, float cy, float x1, float y1,
            Span<float> cells, int width, int height)
        {
            // Deviation of a quadratic from its chord is |p0 - 2c + p1| / 4, and uniform
            // subdivision into n pieces scales it by 1 / n² — solve for n at the tolerance.
            var ddx = x0 - 2f * cx + x1;
            var ddy = y0 - 2f * cy + y1;
            var dd = MathF.Sqrt(ddx * ddx + ddy * ddy);
            var n = 1 + (int)MathF.Sqrt(dd * (1f / (4f * FlattenTolerance)));

            if (n > MaxCurveSegments)
            {
                n = MaxCurveSegments;
            }

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

                AddSegment(prevX, prevY, nx, ny, cells, width, height);
                prevX = nx;
                prevY = ny;
            }
        }

        private static void FlattenCubic(float x0, float y0, float c1X, float c1Y, float c2X, float c2Y,
            float x1, float y1, Span<float> cells, int width, int height)
        {
            // Deviation bound from the two second differences (kurbo/Skia-style estimate),
            // scaled by 1 / n² under uniform subdivision.
            var d1X = x0 - 2f * c1X + c2X;
            var d1Y = y0 - 2f * c1Y + c2Y;
            var d2X = c1X - 2f * c2X + x1;
            var d2Y = c1Y - 2f * c2Y + y1;
            var dd = MathF.Max(
                MathF.Sqrt(d1X * d1X + d1Y * d1Y),
                MathF.Sqrt(d2X * d2X + d2Y * d2Y));
            var n = 1 + (int)MathF.Sqrt(dd * (3f / (4f * FlattenTolerance)));

            if (n > MaxCurveSegments)
            {
                n = MaxCurveSegments;
            }

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

                AddSegment(prevX, prevY, nx, ny, cells, width, height);
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

        private static void Resolve(Span<float> cells, Span<byte> destination, int width, int height,
            bool evenOdd, bool aliased)
        {
            var i = 0;

            for (var y = 0; y < height; y++)
            {
                // Accumulate per row: each closed contour's crossings sum to zero across a row, so
                // the integral returns to zero at the row's end and rows stay independent.
                var sum = 0f;

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

                    destination[i] = aliased
                        ? coverage >= 0.5f ? (byte)255 : (byte)0
                        : (byte)(coverage * 255f + 0.5f);
                }
            }
        }
    }
}
